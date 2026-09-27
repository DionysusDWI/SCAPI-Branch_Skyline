using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline（v0.1.0）：**超视距 LOD 层**（学习 Distant Horizons 的机制）。
    ///
    /// 机制（DH 的核心思路，按我们的约束落地）：
    ///   1. **随加载采集**：每当一个区块列被加载/合法化，就把它的"顶面高度 + 顶面方块"写进一张**粗网格**
    ///      （单元 = 16×16 格）。玩家走过的地方因此会被永久记下来——这正是 DH 的 LOD 数据库生成模型
    ///      （加载即采样、采样即持久化），不需要去直接解析存档。
    ///   2. **持久化**：粗网格定期落盘到世界目录（`SkylineLod.bin`），重启后仍在。
    ///   3. **渲染**：把粗网格里**半径为 `RadiusMetres`（默认 1024 m）**内的单元生成一层低模
    ///      （每个单元一个 16×16 顶面四边形 + 高度落差处的裙边），用**地形自己的不透明 shader**
    ///      与图集渲染，雾/雾带参数与地形一致 → 视距之外仍有内容，且与视距内无缝衔接。
    ///   4. **默认开启**，可用 `SkylineRuntime.LodEnabled` 关闭（关闭后完全不渲染、也不采集）。
    ///
    /// 已知限制（写进 notes/66）：单元内只保留"一个采样列"的高度与材质（粗糙）；跨单元只做裙边、不做真正的
    /// 多级 LOD 树；尚未与 `SkylineRender` 的占位球联动（家具在远景层里不单独渲染）。
    /// </summary>
    public static class SkylineLod {
        public const int CellShift = 4;                 // 16 格/单元
        public const int CellSize = 1 << CellShift;

        public static bool Enabled { get; set; } = true;
        public static float RadiusMetres { get; set; } = 1024f;
        public static int ChunksPerTick { get; set; } = 2;
        public static float MeshRebuildSeconds { get; set; } = 3f;
        public static int MaxCells { get; set; } = 24000;

        sealed class Cell {
            public short Height;
            public ushort Value;
        }

        static readonly Dictionary<long, Cell> m_cells = [];
        static int m_harvestCursor;
        static bool m_dirty = true;
        static double m_nextRebuild;
        static double m_nextSave;
        static string m_worldDir;
        static VertexBuffer m_vb;
        static IndexBuffer m_ib;
        static int m_indexCount;
        static int m_cellsInMesh;
        static int m_harvestedCells;
        static int m_rebuilds;
        static string m_lastError = "";

        // side 编号与侧壁代码一致：0=+Z、1=-Z、2=+X、3=-X
        static readonly int[] s_sideDx = [0, 0, 1, -1];
        static readonly int[] s_sideDz = [1, -1, 0, 0];

        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        public static int CellCount => m_cells.Count;

        public static void Reset() {
            m_cells.Clear();
            m_indexCount = 0;
            m_cellsInMesh = 0;
            m_harvestedCells = 0;
            Utilities.Dispose(ref m_vb);
            Utilities.Dispose(ref m_ib);
            m_dirty = true;
        }

        // ---------------- 采集 ----------------

        public static void Tick() {
            if (!Enabled) {
                return;
            }
            double now = Time.RealTime;
            try {
                // v0.1.0 修复（2026-09-27 跨世界测试发现）：**每帧先确认世界目录**。
                // 原来 `FilePath()` 只在 Save（≤60 s 一次）里被调用 → 换世界后旧世界的
                // 内存数据会被带到新世界（无 bin 时 Load 还不清空，见下），并可能被写进
                // 新世界的 SkylineLod.bin（实测 World 的 bin 被污染成 1873 个 AgentLab 坐标）。
                FilePath();
                Harvest();
                if (m_dirty && now >= m_nextRebuild) {
                    RebuildMesh();
                    m_nextRebuild = now + MathF.Max(MeshRebuildSeconds, 0.5f);
                }
                if (now >= m_nextSave) {
                    Save();
                    m_nextSave = now + 60.0;
                }
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLod.Tick: {e.Message}");
            }
        }

        static void Harvest() {
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            Terrain terrain = subsystemTerrain?.Terrain;
            if (terrain == null) {
                return;
            }
            TerrainChunk[] chunks = terrain.AllocatedChunks;
            if (chunks.Length == 0) {
                return;
            }
            for (int n = 0; n < Math.Max(ChunksPerTick, 1); n++) {
                m_harvestCursor = (m_harvestCursor + 1) % chunks.Length;
                TerrainChunk chunk = chunks[m_harvestCursor];
                if (chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                    continue;
                }
                // v0.1.0 改进（2026-09-27）：一个 16 m 单元原来只取"第一列"的高度，
                // 采样落到树冠上时整格被抬高成"浮空平板"（取证 data/sessions/skyline-v010/lod-verify/post3）。
                // 现在扫**该区块的全部 256 列取最低顶面**（= 单元内的地形低点），材质跟着该列取。
                int cx0 = chunk.Origin.X >> CellShift;
                int cz0 = chunk.Origin.Y >> CellShift;
                long key = Key(cx0, cz0);
                if (m_cells.ContainsKey(key)) {
                    continue;
                }
                int bestTop = int.MaxValue;
                int bestValue = 0;
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        int top = chunk.GetTopHeightFast(x, z);
                        if (top < TerrainChunk.MinHeight || top >= bestTop) {
                            continue;
                        }
                        bestTop = top;
                        bestValue = chunk.GetCellValueFast(x, top, z);
                    }
                }
                if (bestTop != int.MaxValue) {
                    m_cells[key] = new Cell { Height = (short)bestTop, Value = (ushort)bestValue };
                    m_harvestedCells++;
                    m_dirty = true;
                }
            }
        }

        // ---------------- 网格 ----------------

        static void RebuildMesh() {
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain == null) {
                return;
            }
            Vector3 camera = CameraViewPosition();
            int radiusCells = Math.Max(1, (int)(RadiusMetres / CellSize));
            int ccx = (int)MathF.Floor(camera.X / CellSize);
            int ccz = (int)MathF.Floor(camera.Z / CellSize);

            // v0.1.0 修复（2026-09-27 实测）：**跳过视距内的单元**。
            // 视距内（平面 `VisibilityRange`）由真实几何渲染；若 LOD 也画同位置的 16 m 粗平面，
            // 两者深度相当 → LOD 后画覆盖真实几何，近处细节被抹平（取证：
            // data/sessions/skyline-v010/lod-verify/pre，on/off 差异 50,903 px、单一斑块 40,780 px）。
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
            float visualRange = sky?.VisibilityRange ?? SettingsManager.VisibilityRange;
            float skipRadius = visualRange + CellSize * 0.5f;   // 半格余量，避免接缝裂缝
            float skipRadiusSq = skipRadius * skipRadius;

            var keys = new List<long>();
            for (int cx = ccx - radiusCells; cx <= ccx + radiusCells; cx++) {
                for (int cz = ccz - radiusCells; cz <= ccz + radiusCells; cz++) {
                    long key = Key(cx, cz);
                    if (!m_cells.ContainsKey(key)) {
                        continue;
                    }
                    float dx = (cx << CellShift) + CellSize * 0.5f - camera.X;
                    float dz = (cz << CellShift) + CellSize * 0.5f - camera.Z;
                    if (dx * dx + dz * dz <= skipRadiusSq) {
                        continue;
                    }
                    keys.Add(key);
                }
            }
            if (keys.Count > MaxCells) {
                keys.Sort((a, b) => {
                    float da = Dist2(a, camera);
                    float db = Dist2(b, camera);
                    return da.CompareTo(db);
                });
                keys.RemoveRange(MaxCells, keys.Count - MaxCells);
            }

            // 侧壁（v0.1.0 改进）：相邻单元高度差 ≥ 0.5 m 时，从**高的一侧**向下画一圈
            // 双面"裙边墙"，消除浮空平板之间的断层/黑洞（post3 取证）。
            var walls = new List<(int x0, int z0, float yHigh, float yLow, int side, int value)>();
            foreach (long key in keys) {
                Cell cell = m_cells[key];
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float yHigh = cell.Height + 1f;
                for (int side = 0; side < 4; side++) {
                    long nk = Key(cx + s_sideDx[side], cz + s_sideDz[side]);
                    if (!m_cells.TryGetValue(nk, out Cell neighbor)) {
                        continue;                          // 缺邻居：不画（避免无边长裙）
                    }
                    float yLow = neighbor.Height + 1f;
                    if (yHigh - yLow < 0.5f) {
                        continue;
                    }
                    walls.Add((cx << CellShift, cz << CellShift, yHigh, yLow, side, cell.Value));
                }
            }

            int indexCount = keys.Count * 6 + walls.Count * 12;
            int vertexCount = keys.Count * 4 + walls.Count * 4;
            if (vertexCount == 0) {
                m_indexCount = 0;
                m_cellsInMesh = 0;
                // v0.1.0 修复：世界加载早期相机还在原点（CameraViewPosition=Zero），按相机过滤会
                // 建出"空网格"；原来这里直接把 m_dirty 清掉 → 相机就位后也不再重建（实测
                // inMesh=0 直到有新采集）。改为"还有数据就保持 dirty"，下一轮（相机已就位）重建。
                m_dirty = m_cells.Count > 0;
                return;
            }
            var vertices = new TerrainVertex[vertexCount];
            short[] indices = new short[indexCount];
            bool bigIndices = vertexCount > 65535;
            var indices32 = bigIndices ? new int[indexCount] : null;
            var light = new Color((byte)220, (byte)220, (byte)220);
            int vi = 0, ii = 0, built = 0;
            foreach (long key in keys) {
                Cell cell = m_cells[key];
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float x0 = cx << CellShift, z0 = cz << CellShift;
                float y = cell.Height + 1f;
                int contents = Terrain.ExtractContents(cell.Value);
                int value = cell.Value;
                Block block = BlocksManager.Blocks[contents];
                int slotCount = Math.Max(block.GetTextureSlotCount(value), 1);
                int slot = block.GetFaceTextureSlot(4, value);      // 顶面
                float u0 = (slot % slotCount) / (float)slotCount;
                float v0 = (slot / slotCount) / (float)slotCount;
                float du = 1f / slotCount;
                BlockGeometryGenerator.SetupVertex(x0, y, z0, light, u0, v0, ref vertices[vi]);
                BlockGeometryGenerator.SetupVertex(x0 + CellSize, y, z0, light, u0 + du, v0, ref vertices[vi + 1]);
                BlockGeometryGenerator.SetupVertex(x0 + CellSize, y, z0 + CellSize, light, u0 + du, v0 + du, ref vertices[vi + 2]);
                BlockGeometryGenerator.SetupVertex(x0, y, z0 + CellSize, light, u0, v0 + du, ref vertices[vi + 3]);
                if (bigIndices) {
                    indices32[ii] = vi; indices32[ii + 1] = vi + 1; indices32[ii + 2] = vi + 2;
                    indices32[ii + 3] = vi; indices32[ii + 4] = vi + 2; indices32[ii + 5] = vi + 3;
                }
                else {
                    indices[ii] = (short)vi; indices[ii + 1] = (short)(vi + 1); indices[ii + 2] = (short)(vi + 2);
                    indices[ii + 3] = (short)vi; indices[ii + 4] = (short)(vi + 2); indices[ii + 5] = (short)(vi + 3);
                }
                vi += 4;
                ii += 6;
                built++;
            }
            foreach ((int wx, int wz, float yHigh, float yLow, int side, int wallValue) in walls) {
                float x0 = wx, z0 = wz, x1 = wx + CellSize, z1 = wz + CellSize;
                Block wallBlock = BlocksManager.Blocks[Terrain.ExtractContents(wallValue)];
                int wallSlotCount = Math.Max(wallBlock.GetTextureSlotCount(wallValue), 1);
                int wallSlot = wallBlock.GetFaceTextureSlot(1, wallValue);       // 侧面
                float wu = (wallSlot % wallSlotCount) / (float)wallSlotCount;
                float wv = (wallSlot / wallSlotCount) / (float)wallSlotCount;
                float wd = 1f / wallSlotCount;
                switch (side) {
                    case 0:      // +Z 面（z1）: (x0,z1) (x1,z1)
                        BlockGeometryGenerator.SetupVertex(x0, yHigh, z1, light, wu, wv, ref vertices[vi]);
                        BlockGeometryGenerator.SetupVertex(x1, yHigh, z1, light, wu + wd, wv, ref vertices[vi + 1]);
                        BlockGeometryGenerator.SetupVertex(x1, yLow, z1, light, wu + wd, wv + wd, ref vertices[vi + 2]);
                        BlockGeometryGenerator.SetupVertex(x0, yLow, z1, light, wu, wv + wd, ref vertices[vi + 3]);
                        break;
                    case 1:      // -Z 面（z0）
                        BlockGeometryGenerator.SetupVertex(x1, yHigh, z0, light, wu, wv, ref vertices[vi]);
                        BlockGeometryGenerator.SetupVertex(x0, yHigh, z0, light, wu + wd, wv, ref vertices[vi + 1]);
                        BlockGeometryGenerator.SetupVertex(x0, yLow, z0, light, wu + wd, wv + wd, ref vertices[vi + 2]);
                        BlockGeometryGenerator.SetupVertex(x1, yLow, z0, light, wu, wv + wd, ref vertices[vi + 3]);
                        break;
                    case 2:      // +X 面（x1）
                        BlockGeometryGenerator.SetupVertex(x1, yHigh, z0, light, wu, wv, ref vertices[vi]);
                        BlockGeometryGenerator.SetupVertex(x1, yHigh, z1, light, wu + wd, wv, ref vertices[vi + 1]);
                        BlockGeometryGenerator.SetupVertex(x1, yLow, z1, light, wu + wd, wv + wd, ref vertices[vi + 2]);
                        BlockGeometryGenerator.SetupVertex(x1, yLow, z0, light, wu, wv + wd, ref vertices[vi + 3]);
                        break;
                    default:     // -X 面（x0）
                        BlockGeometryGenerator.SetupVertex(x0, yHigh, z1, light, wu, wv, ref vertices[vi]);
                        BlockGeometryGenerator.SetupVertex(x0, yHigh, z0, light, wu + wd, wv, ref vertices[vi + 1]);
                        BlockGeometryGenerator.SetupVertex(x0, yLow, z0, light, wu + wd, wv + wd, ref vertices[vi + 2]);
                        BlockGeometryGenerator.SetupVertex(x0, yLow, z1, light, wu, wv + wd, ref vertices[vi + 3]);
                        break;
                }
                // 双面：正反两个绕序（各 6 索引）
                if (bigIndices) {
                    indices32[ii] = vi; indices32[ii + 1] = vi + 1; indices32[ii + 2] = vi + 2;
                    indices32[ii + 3] = vi; indices32[ii + 4] = vi + 2; indices32[ii + 5] = vi + 3;
                    indices32[ii + 6] = vi + 2; indices32[ii + 7] = vi + 1; indices32[ii + 8] = vi;
                    indices32[ii + 9] = vi + 3; indices32[ii + 10] = vi + 2; indices32[ii + 11] = vi;
                }
                else {
                    indices[ii] = (short)vi; indices[ii + 1] = (short)(vi + 1); indices[ii + 2] = (short)(vi + 2);
                    indices[ii + 3] = (short)vi; indices[ii + 4] = (short)(vi + 2); indices[ii + 5] = (short)(vi + 3);
                    indices[ii + 6] = (short)(vi + 2); indices[ii + 7] = (short)(vi + 1); indices[ii + 8] = (short)vi;
                    indices[ii + 9] = (short)(vi + 3); indices[ii + 10] = (short)(vi + 2); indices[ii + 11] = (short)vi;
                }
                vi += 4;
                ii += 12;
            }
            Utilities.Dispose(ref m_vb);
            Utilities.Dispose(ref m_ib);
            m_vb = new VertexBuffer(TerrainVertex.VertexDeclaration, vertexCount);
            m_vb.SetData(vertices, 0, vertexCount);
            m_ib = new IndexBuffer(bigIndices ? IndexFormat.ThirtyTwoBits : IndexFormat.SixteenBits, ii);
            if (bigIndices) {
                m_ib.SetData(indices32, 0, ii);
            }
            else {
                m_ib.SetData(indices, 0, ii);
            }
            m_indexCount = ii;
            m_cellsInMesh = built;
            m_rebuilds++;
            m_dirty = false;
        }

        static float Dist2(long key, Vector3 camera) {
            int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
            float dx = (cx << CellShift) + CellSize * 0.5f - camera.X;
            float dz = (cz << CellShift) + CellSize * 0.5f - camera.Z;
            return dx * dx + dz * dz;
        }

        // ---------------- 渲染 ----------------

        static Vector3 CameraViewPosition() {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(true);
            ComponentPlayer player = players != null && players.ComponentPlayers.Count > 0
                ? players.ComponentPlayers[0] : null;
            Camera camera = player?.GameWidget?.ActiveCamera;
            return camera?.ViewPosition ?? Vector3.Zero;
        }

        public static void Draw(Camera camera) {
            if (!Enabled || m_vb == null || m_ib == null || m_indexCount == 0) {
                return;
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
            if (subsystemTerrain == null || sky == null || TerrainRenderer.m_opaqueShader == null) {
                return;
            }
            try {
                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                Vector3 v = new Vector3(MathF.Floor(viewPosition.X), 0f, MathF.Floor(viewPosition.Z));
                Matrix matrix = Matrix.CreateTranslation(v - viewPosition)
                    * camera.ViewMatrix.OrientationMatrix * camera.ProjectionMatrix;
                Shader shader = TerrainRenderer.m_opaqueShader;
                shader.GetParameter("u_origin", true).SetValue(new Vector2(v.X, v.Z));
                shader.GetParameter("u_viewProjectionMatrix", true).SetValue(matrix);
                shader.GetParameter("u_viewPosition", true).SetValue(viewPosition);
                shader.GetParameter("u_samplerState", true).SetValue(
                    SettingsManager.TerrainMipmapsEnabled
                        ? new SamplerState {
                            AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                            FilterMode = TextureFilterMode.PointMipLinear, MaxLod = 4f
                        }
                        : new SamplerState {
                            AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                            FilterMode = TextureFilterMode.Point, MaxLod = 0f
                        });
                shader.GetParameter("u_fogYMultiplier", true).SetValue(sky.VisibilityRangeYMultiplier);
                shader.GetParameter("u_fogColor", true).SetValue(new Vector3(sky.ViewFogColor));
                shader.GetParameter("u_fogBottomTopDensity", true)
                    .SetValue(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity));
                // v0.1.1：**与真实地形共用同一条视图雾曲线**。原来自算 [0.55R, R] 的雾带，
                // 而原版真实地形在 `视距 × 0.8` 处就已 100% 雾化——两者交界"地形全雾消失 /
                // LOD 无雾跳出"，交接感明显（用户反馈）。现在 `SkylineAtmosphere.AdjustHazeSpan`
                // 已把视图雾的跨度拉远到 LOD 半径的 90%，这里直接采用 sky 的 (start, density)，
                // 两个渲染层在交界处与全程都连续。
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity));
                shader.GetParameter("u_texture", true).SetValue(
                    subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vb, m_ib, 0, m_indexCount);
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLod.Draw: {e.Message}");
            }
        }

        // ---------------- 持久化 ----------------

        static string FilePath() {
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (info == null || string.IsNullOrEmpty(info.DirectoryName)) {
                return null;
            }
            if (m_worldDir != info.DirectoryName) {
                m_worldDir = info.DirectoryName;
                Load();
            }
            return Storage.CombinePaths(info.DirectoryName, "SkylineLod.bin");
        }

        public static void Save() {
            string path = FilePath();
            if (path == null) {
                return;
            }
            try {
                using (var stream = Storage.OpenFile(path, OpenFileMode.Create)) {
                    var writer = new BinaryWriter(stream);
                    writer.Write(1);                       // 版本
                    writer.Write(m_cells.Count);
                    foreach (KeyValuePair<long, Cell> pair in m_cells) {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Height);
                        writer.Write(pair.Value.Value);
                    }
                }
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
        }

        public static void Load() {
            string path = FilePath();
            // v0.1.0 修复：**无条件先清空**——文件不存在（新世界/没有 LOD 数据的世界）时也必须清掉
            // 上一个世界的内存数据（原来 `return` 在 `Clear` 之前，导致跨世界污染）。
            m_cells.Clear();
            if (path == null || !Storage.FileExists(path)) {
                m_dirty = true;
                return;
            }
            try {
                using (var stream = Storage.OpenFile(path, OpenFileMode.Read)) {
                    var reader = new BinaryReader(stream);
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count && i < MaxCells * 4; i++) {
                        long key = reader.ReadInt64();
                        short height = reader.ReadInt16();
                        ushort value = reader.ReadUInt16();
                        m_cells[key] = new Cell { Height = height, Value = value };
                    }
                }
                m_dirty = true;
                Log.Information($"SkylineLod: loaded {m_cells.Count} cells");
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
        }

        // ---------------- 状态 ----------------

        public static string Describe() =>
            $"lod:enabled={Enabled} cells={m_cells.Count} inMesh={m_cellsInMesh} indices={m_indexCount} "
            + $"radius={RadiusMetres:0}m cell={CellSize} rebuilds={m_rebuilds} "
            + $"err={(m_lastError.Length > 0 ? m_lastError : "-")}";

        public static string Survey() {
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            Terrain terrain = subsystemTerrain?.Terrain;
            int loadedColumns = 0;
            if (terrain != null) {
                foreach (TerrainChunk chunk in terrain.AllocatedChunks) {
                    if (chunk != null && chunk.ThreadState >= TerrainChunkState.Valid) {
                        loadedColumns++;
                    }
                }
            }
            float covered = m_cells.Count * (CellSize * (float)CellSize);
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["cells"] = m_cells.Count,
                ["cellsInMesh"] = m_cellsInMesh,
                ["meshIndices"] = m_indexCount,
                ["radiusMetres"] = RadiusMetres,
                ["cellSizeBlocks"] = CellSize,
                ["coveredAreaKm2"] = Math.Round(covered / 1_000_000f, 4),
                ["harvestedCells"] = m_harvestedCells,
                ["meshRebuilds"] = m_rebuilds,
                ["loadedChunks"] = loadedColumns,
                ["lastError"] = m_lastError
            }.ToJsonString();
        }
    }
}
