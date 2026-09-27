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
    public static partial class SkylineLod {
        public const int CellShift = 4;                 // 16 格/单元
        public const int CellSize = 1 << CellShift;
        // v0.1.1：**精细层**——视距外近环用 8 m 单元（用户反馈"128 格视距下 LOD 非常粗糙"）。
        // 采集时每个 16 m 单元同时填 4 个 8 m 子单元；重建时近环用细网格、远环用粗网格。
        public const int FineShift = 3;                 // 8 格/单元
        public const int FineSize = 1 << FineShift;
        // [v0.1.45] **近环细层**（4 m 单元）：只填"视距外的交接带"那一圈，
        // 用来压低"8 m 单元只有一个高度"造成的起伏损失（审计口径见 notes/116/117）。
        public const int NearShift = 2;                 // 4 格/单元
        public const int NearSize = 1 << NearShift;
        /// <summary>[v0.1.45] 近环细层的带宽（米）：从 `视距+FineSize/2` 起往外这么宽。</summary>
        public static float NearBandMetres { get; set; } = 48f;

        public static bool Enabled { get; set; } = true;
        public static float RadiusMetres { get; set; } = 1024f;
        public static int ChunksPerTick { get; set; } = 2;
        public static float MeshRebuildSeconds { get; set; } = 3f;
        public static int MaxCells { get; set; } = 24000;
        /// <summary>精细层（8 m）覆盖到"视距 × 本系数"为止，之后交给 16 m 粗层。</summary>
        public static float FineRangeFactor { get; set; } = 2.0f;

        // ===== v0.1.5：光影接口预适配（Dawnlight / Iris 式管线）=====
        /// <summary>
        /// 外部光影包接管 LOD 绘制时置 true：`Draw` 不再走内置的地形 shader 路径，
        /// 改调用 <see cref="CustomDraw"/>（若为 null 则回退内置路径）。
        /// 设计背景见 notes/73：Dawnlight 用 MonoMod hook 游戏渲染管线，Iris 用
        /// gbuffers_/composite_ 阶段约定；这里给两者都留"一个明确的接管点 + 一份 LOD 元数据"。
        /// </summary>
        public static bool ExternalShaderHooked { get; set; }

        /// <summary>外部光影包的 LOD 绘制回调（相机参数；内部需自行设置 shader 与状态）。</summary>
        public static System.Action<Camera> CustomDraw { get; set; }

        /// <summary>当前 LOD 网格的元数据（给光影包用：层、单元尺寸、可见半径、索引数）。</summary>
        public static string MeshMetadata() {
            return new System.Text.Json.Nodes.JsonObject {
                ["coarseCells"] = m_cellsInMesh,
                ["coarseIndices"] = m_indexCount,
                ["coarseCellSize"] = CellSize,
                ["fineCells"] = m_cellsInMeshFine,
                ["fineIndices"] = m_indexCountFine,
                ["fineCellSize"] = FineSize,
                ["radiusMetres"] = RadiusMetres,
                ["fineRangeMetres"] = RadiusMetres * FineRangeFactor,
                ["externalHooked"] = ExternalShaderHooked
            }.ToJsonString();
        }

        sealed class Cell {
            public short Height;
            public ushort Value;
            /// <summary>[v0.1.44] 采集时该顶面方块的光照值（0..15）。近景地形顶点色由光照决定，
            /// LOD 一直用常数基色 → 交界处出现"亮度台阶"（交接带口径，见 notes/117）。</summary>
            public byte Light;
        }

        static readonly Dictionary<long, Cell> m_cells = [];
        static readonly Dictionary<long, Cell> m_cellsFine = [];        // 8 m 精细层
        static readonly Dictionary<long, Cell> m_cellsNear = [];        // 4 m 近环细层（交接带专用）
        static int m_harvestCursor;
        static bool m_dirty = true;
        static double m_nextRebuild;
        static double m_nextSave;
        static string m_worldDir;
        static VertexBuffer m_vb;
        static IndexBuffer m_ib;
        static int m_indexCount;
        static int m_cellsInMesh;
        static VertexBuffer m_vbFine;
        static IndexBuffer m_ibFine;
        static int m_indexCountFine;
        static int m_cellsInMeshFine;
        // [v0.1.45] 4 m 近环层（交接带）
        static VertexBuffer m_vbNear;
        static IndexBuffer m_ibNear;
        static int m_indexCountNear;
        static int m_cellsInMeshNear;
        static int m_harvestedCells;
        static int m_rebuilds;
        static string m_lastError = "";

        // side 编号与侧壁代码一致：0=+Z、1=-Z、2=+X、3=-X
        static readonly int[] s_sideDx = [0, 0, 1, -1];
        static readonly int[] s_sideDz = [1, -1, 0, 0];

        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        // ===== [v0.1.46] 交接带"主动铺满" =====

        static Vector3 m_lastNearMarkCenter;
        static bool m_hasNearMarkCenter;
        /// <summary>相机移动超过这个距离（米）就把交接带重新标脏一次。</summary>
        public static float NearMarkMoveThreshold { get; set; } = 24f;
        public static long NearMarkedCells { get; private set; }

        /// <summary>[v0.1.46] 交接带（近环层覆盖的那一圈）里所有 16 m 单元主动标脏，让它们立刻重采。
        /// 返回标记的单元数；开关关掉时返回 0。</summary>
        public static int MarkNearBandDirty() {
            if (!SkylineRuntime.LodNearLayerEnabled) {
                return 0;
            }
            Vector3 camera = CameraViewPosition();
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float bandStart = MathF.Max(viewRange + FineSize * 0.5f - 8f - CellSize, 0f);
            float bandEnd = viewRange + FineSize * 0.5f + NearBandMetres + 16f + CellSize;
            int cellSize = CellSize;
            int cx0 = (int)MathF.Floor((camera.X - bandEnd) / cellSize);
            int cx1 = (int)MathF.Floor((camera.X + bandEnd) / cellSize);
            int cz0 = (int)MathF.Floor((camera.Z - bandEnd) / cellSize);
            int cz1 = (int)MathF.Floor((camera.Z + bandEnd) / cellSize);
            float startSq = bandStart * bandStart;
            float endSq = bandEnd * bandEnd;
            int marked = 0;
            for (int cx = cx0; cx <= cx1; cx++) {
                for (int cz = cz0; cz <= cz1; cz++) {
                    float dx = (cx << CellShift) + CellSize * 0.5f - camera.X;
                    float dz = (cz << CellShift) + CellSize * 0.5f - camera.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < startSq || d2 > endSq) {
                        continue;
                    }
                    MarkDirty(Key(cx, cz));      // 同 partial 类（SkylineLodRefresh.cs）
                    marked++;
                }
            }
            NearMarkedCells += marked;
            return marked;
        }

        /// <summary>相机移动够了就重标一次（由 Tick 调用）。</summary>
        static void NearBandTick() {
            if (!SkylineRuntime.LodNearLayerEnabled) {
                return;
            }
            Vector3 camera = CameraViewPosition();
            if (m_hasNearMarkCenter
                && Vector3.DistanceSquared(camera, m_lastNearMarkCenter)
                    < NearMarkMoveThreshold * NearMarkMoveThreshold) {
                return;
            }
            MarkNearBandDirty();
            m_lastNearMarkCenter = camera;
            m_hasNearMarkCenter = true;
        }

        public static int CellCount => m_cells.Count;

        public static void Reset() {
            m_cells.Clear();
            m_cellsFine.Clear();
            m_cellsNear.Clear();
            ResetRefreshState();          // v0.1.8：采样戳/脏集合与单元数据同生命周期
            m_indexCount = 0;
            m_cellsInMesh = 0;
            m_indexCountFine = 0;
            m_cellsInMeshFine = 0;
            m_indexCountNear = 0;
            m_cellsInMeshNear = 0;
            m_harvestedCells = 0;
            Utilities.Dispose(ref m_vb);
            Utilities.Dispose(ref m_ib);
            Utilities.Dispose(ref m_vbFine);
            Utilities.Dispose(ref m_ibFine);
            Utilities.Dispose(ref m_vbNear);
            Utilities.Dispose(ref m_ibNear);
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
                // [v0.1.46] 近环层"立刻铺满"：相机移动一定距离就把交接带里的单元主动标脏，
                // 否则近环只覆盖"恰好被轮转采样到"的区块（v0.1.45 实测 20 s 窗口只有 53% 覆盖）。
                NearBandTick();
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
            // v0.1.6：采样缓冲移出循环（CA2014——stackalloc 不能放在循环体内，会累积栈）
            Span<long> coarseSamples = stackalloc long[TerrainChunk.Size * TerrainChunk.Size];
            Span<long> fine0 = stackalloc long[64];
            Span<long> fine1 = stackalloc long[64];
            Span<long> fine2 = stackalloc long[64];
            Span<long> fine3 = stackalloc long[64];
            // v0.1.8：脏重采（推通道）**不挤占**轮转采集的预算。原来两者共用一个预算，
            // 一旦脏队列被"世界加载期的一批内容变更 / 大编辑"堆起来就要排队好几秒
            // （12:45 实测：目标单元 7.2 s 才刷新，超出 1 s 判据）。现在：
            //   本轮 = 最多 DirtyChunksPerTick 个脏重采 + 最多 ChunksPerTick 个轮转采集。
            int rotationBudget = Math.Max(ChunksPerTick, 1);
            int dirtyBudget = EffectiveDirtyBudget;      // v0.1.16：队列越长处理越多（上限 DirtyChunksMaxPerTick）
            int rotationUsed = 0;
            int dirtyUsed = 0;
            for (int n = 0; n < rotationBudget + dirtyBudget; n++) {
                TerrainChunk chunk;
                ResampleReason reason;
                if (dirtyUsed < dirtyBudget && TryTakeDirtyChunk(terrain, out chunk, out reason)) {
                    dirtyUsed++;
                }
                else if (rotationUsed < rotationBudget) {
                    rotationUsed++;
                    m_harvestCursor = (m_harvestCursor + 1) % chunks.Length;
                    chunk = chunks[m_harvestCursor];
                    if (chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                        continue;
                    }
                    // v0.1.8：旧逻辑"粗层+细层都在字典里就跳过"→ 采过就永不重采（LOD 失效根因）。
                    reason = NeedsResample(chunk);
                    if (reason == ResampleReason.None) {
                        continue;
                    }
                }
                else {
                    break;
                }
                // v0.1.0 改进（2026-09-27）：一个 16 m 单元原来只取"第一列"的高度，
                // 采样落到树冠上时整格被抬高成"浮空平板"（取证 data/sessions/skyline-v010/lod-verify/post3）。
                // 现在扫**该区块的全部 256 列取最低顶面**（= 单元内的地形低点），材质跟着该列取。
                int cx0 = chunk.Origin.X >> CellShift;
                int cz0 = chunk.Origin.Y >> CellShift;
                long key = Key(cx0, cz0);
                // v0.1.6：采样从"最低顶面"改为**中位高度**——"最低"会被单个深坑拉低
                // （整片地形画成下沉平板），中位对树冠/坑洞都稳健。一次扫描把每列的
                // (height<<32|value) 打包进 5 组样本（粗层 256 + 4 个细子块各 64），
                // 各自排序取中位。Span<long>.Sort() 用默认比较（先 height 后 value），无 lambda。
                int cCount = 0, f0 = 0, f1 = 0, f2 = 0, f3 = 0;
                // [v0.1.45] 近环细层（4 m）只填"视距外的交接带"这一圈：先判断本区块在不在带里
                bool fillNear = false;
                if (SkylineRuntime.LodNearLayerEnabled) {
                    Vector3 camera = CameraViewPosition();
                    float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                        ?? SettingsManager.VisibilityRange;
                    float bandStart = viewRange + FineSize * 0.5f - 8f;
                    float bandEnd = bandStart + NearBandMetres + 16f;
                    float dx = chunk.Origin.X + TerrainChunk.Size * 0.5f - camera.X;
                    float dz = chunk.Origin.Y + TerrainChunk.Size * 0.5f - camera.Z;
                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    fillNear = dist >= bandStart && dist <= bandEnd;
                }
                Span<int> nearCounts = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        int top = chunk.GetTopHeightFast(x, z);
                        if (top < TerrainChunk.MinHeight) {
                            continue;                       // 空列
                        }
                        long packed = ((long)top << 32) | (uint)chunk.GetCellValueFast(x, top, z);
                        coarseSamples[cCount++] = packed;
                        if (fillNear) {
                            int sub = ((x >> 2) & 3) + ((z >> 2) & 3) * 4;
                            if (nearCounts[sub] < 16) {
                                m_nearScratch[sub * 16 + nearCounts[sub]++] = packed;
                            }
                        }
                        switch ((x >> 3) | ((z >> 3) << 1)) {
                            case 0: fine0[f0++] = packed; break;
                            case 1: fine1[f1++] = packed; break;
                            case 2: fine2[f2++] = packed; break;
                            default: fine3[f3++] = packed; break;
                        }
                    }
                }
                Span<int> coarseTop = [int.MaxValue];
                Span<int> coarseValue = [0];
                Span<byte> coarseLight = [15];
                MedianInto(coarseSamples, cCount, coarseTop, coarseValue, coarseLight, 0);
                int bestTop = coarseTop[0];
                int bestValue = coarseValue[0];
                Span<int> fineTop = [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue];
                Span<int> fineValue = [0, 0, 0, 0];
                Span<byte> fineLight = [15, 15, 15, 15];
                MedianInto(fine0, f0, fineTop, fineValue, fineLight, 0);
                MedianInto(fine1, f1, fineTop, fineValue, fineLight, 1);
                MedianInto(fine2, f2, fineTop, fineValue, fineLight, 2);
                MedianInto(fine3, f3, fineTop, fineValue, fineLight, 3);
                if (bestTop != int.MaxValue) {
                    m_cells[key] = new Cell {
                        Height = (short)bestTop, Value = (ushort)bestValue, Light = coarseLight[0]
                    };
                    m_harvestedCells++;
                    m_dirty = true;
                }
                else if (RemoveEmptiedCells && m_cells.Remove(key)) {
                    m_dirty = true;                       // 整格被清空（默认关；见 SkylineLodRefresh.RemoveEmptiedCells）
                }
                for (int k = 0; k < 4; k++) {
                    long fkey = Key(cx0 * 2 + (k & 1), cz0 * 2 + (k >> 1));
                    if (fineTop[k] == int.MaxValue) {
                        if (RemoveEmptiedCells && m_cellsFine.Remove(fkey)) {
                            m_dirty = true;
                        }
                        continue;
                    }
                    m_cellsFine[fkey] = new Cell {
                        Height = (short)fineTop[k], Value = (ushort)fineValue[k], Light = fineLight[k]
                    };
                    m_dirty = true;
                }
                // [v0.1.45] 近环细层：16 个 4 m 子单元，各自取中位（样本数 ≤16）
                if (fillNear) {
                    int nx0 = chunk.Origin.X >> NearShift;
                    int nz0 = chunk.Origin.Y >> NearShift;
                    for (int k = 0; k < 16; k++) {
                        int nearSampleCount = nearCounts[k];
                        long nkey = Key(nx0 + (k & 3), nz0 + (k >> 2));
                        if (nearSampleCount == 0) {
                            if (RemoveEmptiedCells && m_cellsNear.Remove(nkey)) {
                                m_dirty = true;
                            }
                            continue;
                        }
                        Span<int> nTop = [int.MaxValue];
                        Span<int> nValue = [0];
                        Span<byte> nLight = [15];
                        Span<long> group = m_nearScratch.AsSpan(k * 16, nearSampleCount);
                        MedianInto(group, nearSampleCount, nTop, nValue, nLight, 0);
                        m_cellsNear[nkey] = new Cell {
                            Height = (short)nTop[0], Value = (ushort)nValue[0], Light = nLight[0]
                        };
                        m_dirty = true;
                    }
                }
                RecordSample(chunk, reason);          // v0.1.8：登记采样戳 + 清脏标记
            }
        }

        // ---------------- 网格 ----------------

        /// <summary>[v0.1.15] 坡向明暗强度：0 = 关闭（回到 v0.1.14 的 flat 光），1 = 完全按法线明暗。</summary>
        public static float SlopeShadingStrength { get; set; } = 0.45f;

        /// <summary>
        /// [v0.1.19] **LOD 自阴影**强度：向太阳方向在 LOD 高度场里做射线步进（步长 = 单元/2，
        /// 最远 64 步 ≈ 512 m），被更高的单元（山脊、高台）挡住就压暗。0 = 关闭。
        /// 这是"阴影阶段"的 CPU 侧最短路径版本（`notes/88 §5` 建议先做的那条）：
        /// 零 shader 改动、只在网格重建时算一次（≈1000~2000 单元 × ≤64 步的字典查询）。
        /// </summary>
        public static float SelfShadowStrength { get; set; } = 0.35f;

        /// <summary>[v0.1.19] 自阴影射线在竖直方向上的偏置（格），避免自我遮挡（浮点/取整噪声）。</summary>
        public static float SelfShadowBias { get; set; } = 0.75f;

        static int m_selfShadowedCells;
        static int m_selfShadowSampled;

        /// <summary>[v0.1.19] 诊断：最近一次网格重建里被判为"在阴影中"的单元数/采样数。</summary>
        public static string SelfShadowStats() =>
            $"selfShadow strength={SelfShadowStrength:0.##} shadowed={m_selfShadowedCells}/{m_selfShadowSampled}"
            + $" sun={m_sunAmount:0.##}"
            + (m_selfShadowSampled > 0
                ? $" ({100.0 * m_selfShadowedCells / m_selfShadowSampled:0.#}%)"
                : "");

        /// <summary>
        /// 向太阳方向步进，判断该单元是否被 LOD 高度场里的更高地形遮挡。
        /// 返回 1（无遮挡）或 1-strength（在阴影中）。字典里没有的单元按"无数据"跳过（不算遮挡）。
        /// </summary>
        static float SelfShadowFactor(Dictionary<long, Cell> dict, int cx, int cz, int height, int cellSize) {
            Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
            float step = cellSize * 0.5f;
            float x = cx * cellSize + cellSize * 0.5f;
            float y = height + 1f;
            float z = cz * cellSize + cellSize * 0.5f;
            for (int i = 0; i < 64; i++) {
                x += sun.X * step;
                y += sun.Y * step;
                z += sun.Z * step;
                if (y > TerrainChunk.HeightMinusOne) {
                    break;
                }
                int nx = (int)MathF.Floor(x / cellSize);
                int nz = (int)MathF.Floor(z / cellSize);
                if (!dict.TryGetValue(Key(nx, nz), out Cell cell)) {
                    continue;                                  // 未采集 → 不判遮挡（避免假阴影）
                }
                if (cell.Height + 1f > y + SelfShadowBias) {
                    return MathUtils.Clamp(1f - SelfShadowStrength, 0.05f, 1f);
                }
            }
            return 1f;
        }

        /// <summary>[v0.1.19] 自阴影确定性自检：一堵高墙的背光侧应判为阴影、迎光侧不判。</summary>
        public static string SelfShadowSelfCheck() {
            int cellSize = CellSize;
            int low = 70, high = 100;
            var dict = new Dictionary<long, Cell>();
            // 在原点周围铺一圈低地
            for (int dx = -8; dx <= 8; dx++) {
                for (int dz = -8; dz <= 8; dz++) {
                    dict[Key(dx, dz)] = new Cell { Height = (short)low };
                }
            }
            // 太阳方向 ≈ (+0.27,+0.57,+0.78)（朝 +x/+z 升起）：**迎光侧**（+z 一格）放高墙，
            // 射线从原点出发第 3 步（z≈27 格）正落在该格、此时高度 ≈85 < 100 → 应判为阴影；
            // 而背光侧那一格（-z 方向）不看这堵墙 → 应判为受光。
            dict[Key(0, 1)] = new Cell { Height = (short)high };
            float shadowed = SelfShadowFactor(dict, 0, 0, low, cellSize);
            float lit = SelfShadowFactor(dict, 0, -2, low, cellSize);
            bool ok = shadowed < 0.999f && lit >= 0.999f;
            return $"selfShadowSelfCheck strength={SelfShadowStrength:0.##} "
                + $"behindWall={shadowed:0.###} sunSide={lit:0.###} ok={ok}";
        }

        /// <summary>
        /// [v0.1.30] **近景地形的阴影采样**：把任意世界坐标交给同一套 LOD 高度场射线步进
        /// （细网格 8 m 优先，退回粗网格 16 m）。返回 1 = 受光、0 = 被遮挡；强度由调用方决定。
        /// 供 `SkylineRuntime.ApplyTerrainShadow` 在区块几何生成后给顶点光照乘系数用。
        /// </summary>
        public static float TerrainShadowSample(float x, float y, float z) {
            Dictionary<long, Cell> dict = null;
            int cellSize = CellSize;
            if (m_cellsFine.Count > 0) {
                dict = m_cellsFine;
                cellSize = FineSize;
            }
            else if (m_cells.Count > 0) {
                dict = m_cells;
            }
            if (dict == null) {
                return 1f;
            }
            Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
            float step = cellSize * 0.5f;
            for (int i = 0; i < 64; i++) {
                x += sun.X * step;
                y += sun.Y * step;
                z += sun.Z * step;
                if (y > TerrainChunk.HeightMinusOne) {
                    break;
                }
                int nx = (int)MathF.Floor(x / cellSize);
                int nz = (int)MathF.Floor(z / cellSize);
                if (!dict.TryGetValue(Key(nx, nz), out Cell cell)) {
                    continue;                                  // 未采集 → 不判遮挡（避免假阴影）
                }
                if (cell.Height + 1f > y + SelfShadowBias) {
                    return 0f;
                }
            }
            return 1f;
        }

        /// <summary>
        /// [v0.1.15] 用相邻单元高度估"该单元顶面法线"，再套 `LightingManager.CalculateLighting`
        /// 得到相对"平地"的明暗系数（平地 = 1）。相邻单元缺失时按"同高"处理（不产生假坡度）。
        /// </summary>
        static float SlopeLightGain(Dictionary<long, Cell> dict, int cx, int cz, int height, int cellSize) {
            int hx0 = NeighborHeight(dict, cx - 1, cz, height);
            int hx1 = NeighborHeight(dict, cx + 1, cz, height);
            int hz0 = NeighborHeight(dict, cx, cz - 1, height);
            int hz1 = NeighborHeight(dict, cx, cz + 1, height);
            float ddx = (hx1 - hx0) / (2f * cellSize);
            float ddz = (hz1 - hz0) / (2f * cellSize);
            Vector3 normal = Vector3.Normalize(new Vector3(-ddx, 1f, -ddz));
            // 注意：游戏本身用**两盏镜像方向光**（DirectionToLight1/2），水平坡向会互相抵消 ——
            // 直接用 `CalculateLighting` 得到的增益在实测里恒为 1（看不出起伏）。
            // 所以这里只取**一盏太阳**（DirectionToLight1）做"坡向明暗"，与 Dawnlight/Iris 的单向太阳一致。
            Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
            float lit = Vector3.Dot(normal, sun) / MathF.Max(Vector3.Dot(Vector3.UnitY, sun), 0.0001f);
            float gain = MathUtils.Lerp(1f, MathUtils.Clamp(lit, 0.35f, 1f), SlopeShadingStrength);
            m_slopeStatMin = MathF.Min(m_slopeStatMin, gain);   // v0.1.15：诊断——全网格累计（几个浮点运算）
            m_slopeStatMax = MathF.Max(m_slopeStatMax, gain);
            m_slopeStatSum += gain;
            m_slopeStatCount++;
            return gain;
        }

        /// <summary>[v0.1.15] 坡向明暗的**确定性自检**（不依赖世界地形）：
        /// 平地 → 增益 = 1；十格高的坡：**背光侧**增益 &lt; 1（更暗）、**迎光侧**被夹到 1（不炸亮）、
        /// 两侧差异明显（说明确实是"单向太阳"而不是两盏镜像光抵消）。</summary>
        public static string SlopeShadingSelfCheck() {
            var flat = new Dictionary<long, Cell>();
            var eastStep = new Dictionary<long, Cell>();
            var westStep = new Dictionary<long, Cell>();
            int cellSize = CellSize;
            int height = 70;
            flat[Key(0, 0)] = new Cell { Height = (short)height };
            for (int d = -1; d <= 1; d++) {
                flat[Key(d, 0)] = new Cell { Height = (short)height };
                flat[Key(0, d)] = new Cell { Height = (short)height };
                eastStep[Key(d, 0)] = new Cell { Height = (short)(d >= 0 ? height + 10 : height) };
                eastStep[Key(0, d)] = new Cell { Height = (short)height };
                westStep[Key(d, 0)] = new Cell { Height = (short)(d <= 0 ? height + 10 : height) };
                westStep[Key(0, d)] = new Cell { Height = (short)height };
            }
            eastStep[Key(1, 1)] = new Cell { Height = (short)(height + 10) };
            westStep[Key(-1, 1)] = new Cell { Height = (short)(height + 10) };
            float gFlat = SlopeLightGain(flat, 0, 0, height, cellSize);
            float gEast = SlopeLightGain(eastStep, 0, 0, height, cellSize);
            float gWest = SlopeLightGain(westStep, 0, 0, height, cellSize);
            float darker = MathF.Min(gEast, gWest);
            float brighter = MathF.Max(gEast, gWest);
            bool ok = MathF.Abs(gFlat - 1f) < 0.001f && darker < 0.96f
                && brighter >= 0.999f && (brighter - darker) > 0.02f;
            return $"slopeShadingSelfCheck strength={SlopeShadingStrength:0.##} "
                + $"flat={gFlat:0.###} eastStep={gEast:0.###} westStep={gWest:0.###} ok={ok}";
        }

        static int NeighborHeight(Dictionary<long, Cell> dict, int cx, int cz, int fallback) =>
            dict.TryGetValue(Key(cx, cz), out Cell cell) ? cell.Height : fallback;

        // v0.1.15：坡向明暗的**可验证诊断**（只统计"网格里最后经过的那个 x 列"，成本可忽略）。
        static float m_slopeStatMin;
        static float m_slopeStatMax;
        static double m_slopeStatSum;
        static int m_slopeStatCount;

        /// <summary>[v0.1.19] 当前"日照量"（0=夜 1=正午），来自 `SubsystemSky.SkyLightValue`：
        /// 坡向明暗与自阴影都是"太阳"效应，夜里应淡出（近景会随光照变暗，远景不能还亮着）。</summary>
        static float m_sunAmount = 1f;

        /// <summary>[v0.1.19] 诊断：当前日照量。</summary>
        public static float SunAmount => m_sunAmount;

        /// <summary>诊断：坡向明暗增益的 min/max/mean（最后一次网格重建里抽样的那一列）。</summary>
        public static string SlopeShadingStats() =>
            m_slopeStatCount == 0
                ? "slopeShading: no samples"
                : $"slopeShading strength={SlopeShadingStrength:0.##} samples={m_slopeStatCount} "
                  + $"gain min={m_slopeStatMin:0.###} mean={m_slopeStatSum / m_slopeStatCount:0.###} max={m_slopeStatMax:0.###}";

        /// <summary>v0.1.6：把一组打包样本（height 在高 32 位、value 在低 32 位）排序取中位，
        /// 写入细层的高度/材质槽。</summary>
        // [v0.1.43] 材质众数统计用的复用容器（不每次分配）
        static readonly Dictionary<int, int> m_contentCounts = [];
        static readonly long[] m_valueScratch = new long[TerrainChunk.Size * TerrainChunk.Size];
        /// <summary>[v0.1.45] 近环细层采样暂存：16 个 4 m 子单元 × 最多 16 列。</summary>
        static readonly long[] m_nearScratch = new long[16 * 16];

        static void MedianInto(Span<long> samples, int count, Span<int> tops, Span<int> values, Span<byte> lights, int index) {
            if (count <= 0) {
                return;
            }
            Span<long> sorted = samples.Slice(0, count);
            sorted.Sort();
            long mid = sorted[count / 2];
            tops[index] = (int)(mid >> 32);
            // [v0.1.43] **材质取众数**：原来取"中位高度那一列"的方块，单元内雪/石/草混杂时
            // 那一列可能只是少数派 → 整格颜色跑偏（交接带实测材质命中率 83.6%）。
            // 现在统计 content 频次取最高者，再从该 content 的样本里挑"中位高度"作代表；
            // **高度仍是整体中位**，几何不变（只改材质选择）。
            m_contentCounts.Clear();
            for (int i = 0; i < count; i++) {
                int contents = Terrain.ExtractContents((int)(uint)sorted[i]);
                m_contentCounts.TryGetValue(contents, out int c);
                m_contentCounts[contents] = c + 1;
            }
            int modeContents = -1;
            int modeCount = -1;
            foreach (KeyValuePair<int, int> kv in m_contentCounts) {
                if (kv.Value > modeCount || (kv.Value == modeCount && kv.Key < modeContents)) {
                    modeCount = kv.Value;
                    modeContents = kv.Key;
                }
            }
            int reps = 0;
            for (int i = 0; i < count; i++) {
                if (Terrain.ExtractContents((int)(uint)sorted[i]) == modeContents) {
                    m_valueScratch[reps++] = sorted[i];
                }
            }
            long representative = reps > 0 ? m_valueScratch[reps / 2] : mid;
            values[index] = (int)(uint)representative;
            // [v0.1.44] 光照跟着代表样本走（打包时低 32 位保留了完整 value，含 light 位）
            lights[index] = (byte)Terrain.ExtractLight((int)(uint)representative);
        }

        /// <summary>v0.1.1：重建入口——精细层（8 m，近环）+ 粗层（16 m，远环）两套网格。</summary>
        static void RebuildMesh() {
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
            float visualRange = sky?.VisibilityRange ?? SettingsManager.VisibilityRange;
            // v0.1.19：昼夜调制——坡向明暗/自阴影是"太阳"效应，夜里（SkyLightValue→0）应淡出，
            // 否则远景观感与近景（由格子光照驱动、会随昼夜变暗）不一致。
            m_sunAmount = MathUtils.Saturate((sky?.SkyLightValue ?? 15) / 15f);
            float skipRadius = visualRange + FineSize * 0.5f;                  // 视距内不画（v0.1.0 修复）
            float fineRange = MathF.Max(visualRange * FineRangeFactor, skipRadius + FineSize * 4f);
            // [v0.1.45] 近环 4 m 层：只覆盖 [skipRadius, skipRadius+NearBandMetres]
            float nearRange = skipRadius + NearBandMetres;
            if (SkylineRuntime.LodNearLayerEnabled) {
                PruneNearCells(skipRadius + NearBandMetres + 32f);
                RebuildMeshCore(m_cellsNear, NearShift, skipRadius, nearRange, 2);
            }
            else if (m_indexCountNear > 0 || m_cellsNear.Count > 0) {
                m_cellsNear.Clear();
                m_indexCountNear = 0;
                m_cellsInMeshNear = 0;
                Utilities.Dispose(ref m_vbNear);
                Utilities.Dispose(ref m_ibNear);
            }
            RebuildMeshCore(m_cellsFine, FineShift, MathF.Max(skipRadius, nearRange), fineRange, 1);
            RebuildMeshCore(m_cells, CellShift, fineRange, RadiusMetres, 0);
            m_rebuilds++;
            m_dirty = false;
            // v0.1.0 修复保留：相机未就位（建出空网格）时保持 dirty，等相机就位后重建。
            if (m_indexCount == 0 && m_indexCountFine == 0 && m_indexCountNear == 0
                && (m_cells.Count > 0 || m_cellsFine.Count > 0 || m_cellsNear.Count > 0)) {
                m_dirty = true;
            }
        }

        /// <summary>[v0.1.45] 玩家走远后清掉带外的近环单元（近环层不落盘、纯临时）。</summary>
        static void PruneNearCells(float maxDist) {
            if (m_cellsNear.Count == 0) {
                return;
            }
            Vector3 camera = CameraViewPosition();
            float maxSq = maxDist * maxDist;
            List<long> remove = null;
            foreach (long key in m_cellsNear.Keys) {
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float dx = (cx << NearShift) + NearSize * 0.5f - camera.X;
                float dz = (cz << NearShift) + NearSize * 0.5f - camera.Z;
                if (dx * dx + dz * dz > maxSq) {
                    (remove ??= []).Add(key);
                }
            }
            if (remove != null) {
                foreach (long key in remove) {
                    m_cellsNear.Remove(key);
                }
                m_dirty = true;
            }
        }

        static void RebuildMeshCore(Dictionary<long, Cell> dict, int cellShift,
                                    float minDist, float maxDist, int layer) {
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain == null) {
                return;
            }
            Vector3 camera = CameraViewPosition();
            int cellSize = 1 << cellShift;
            int radiusCells = Math.Max(1, (int)(maxDist / cellSize));
            int ccx = (int)MathF.Floor(camera.X / cellSize);
            int ccz = (int)MathF.Floor(camera.Z / cellSize);
            float minSq = minDist * minDist;
            float maxSq = maxDist * maxDist;

            var keys = new List<long>();
            if (layer == 0) {                                // v0.1.15：坡向明暗诊断只在粗层重置（避免细层覆盖）
                m_slopeStatMin = float.MaxValue;
                m_slopeStatMax = float.MinValue;
                m_slopeStatSum = 0.0;
                m_slopeStatCount = 0;
                m_selfShadowedCells = 0;                     // v0.1.19：自阴影诊断同样只在粗层重置
                m_selfShadowSampled = 0;
            }
            for (int cx = ccx - radiusCells; cx <= ccx + radiusCells; cx++) {
                for (int cz = ccz - radiusCells; cz <= ccz + radiusCells; cz++) {
                    long key = Key(cx, cz);
                    if (!dict.ContainsKey(key)) {
                        continue;
                    }
                    float dx = (cx << cellShift) + cellSize * 0.5f - camera.X;
                    float dz = (cz << cellShift) + cellSize * 0.5f - camera.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 <= minSq || d2 > maxSq) {
                        continue;
                    }
                    keys.Add(key);
                }
            }
            if (keys.Count > MaxCells) {
                keys.Sort((a, b) => Dist2(a, camera, cellShift).CompareTo(Dist2(b, camera, cellShift)));
                keys.RemoveRange(MaxCells, keys.Count - MaxCells);
            }

            // 侧壁（v0.1.0 改进）：相邻单元高度差 ≥ 0.5 m 时，从**高的一侧**向下画一圈
            // 双面"裙边墙"，消除浮空平板之间的断层/黑洞（post3 取证）。
            var walls = new List<(int x0, int z0, float yHigh, float yLow, int side, int value)>();
            foreach (long key in keys) {
                Cell cell = dict[key];
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float yHigh = cell.Height + 1f;
                for (int side = 0; side < 4; side++) {
                    long nk = Key(cx + s_sideDx[side], cz + s_sideDz[side]);
                    if (!dict.TryGetValue(nk, out Cell neighbor)) {
                        continue;                          // 缺邻居：不画（避免无边长裙）
                    }
                    float yLow = neighbor.Height + 1f;
                    if (yHigh - yLow < 0.5f) {
                        continue;
                    }
                    walls.Add((cx << cellShift, cz << cellShift, yHigh, yLow, side, cell.Value));
                }
            }

            int indexCount = keys.Count * 6 + walls.Count * 12;
            int vertexCount = keys.Count * 4 + walls.Count * 4;
            if (vertexCount == 0) {
                // 空网格：由外层 RebuildMesh 统一决定是否保持 dirty（v0.1.0 修复的逻辑移到外层）。
                SetLayerMesh(layer, null, null, 0, 0);
                return;
            }
            var vertices = new TerrainVertex[vertexCount];
            short[] indices = new short[indexCount];
            bool bigIndices = vertexCount > 65535;
            var indices32 = bigIndices ? new int[indexCount] : null;
            var light = new Color((byte)220, (byte)220, (byte)220);
            int vi = 0, ii = 0, built = 0;
            foreach (long key in keys) {
                Cell cell = dict[key];
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float x0 = cx << cellShift, z0 = cz << cellShift;
                float y = cell.Height + 1f;
                // [v0.1.44] 基色改用**采集时的光照值**（0..15 → 0..255，与游戏光照→顶点色的口径一致）。
                // 关掉开关即回到常数 220（= 光 13），用于 A/B（`skyline.LodLightFromSamples`）。
                Color cellBase = SkylineRuntime.LodLightFromSamples
                    ? new Color((byte)(cell.Light * 17), (byte)(cell.Light * 17), (byte)(cell.Light * 17))
                    : light;
                int contents = Terrain.ExtractContents(cell.Value);
                int value = cell.Value;
                Block block = BlocksManager.Blocks[contents];
                int slotCount = Math.Max(block.GetTextureSlotCount(value), 1);
                int slot = block.GetFaceTextureSlot(4, value);      // 顶面
                float u0 = (slot % slotCount) / (float)slotCount;
                float v0 = (slot / slotCount) / (float)slotCount;
                float du = 1f / slotCount;
                // [v0.1.15] 坡向明暗：远景低模原来一律 flat 光（220,220,220），起伏完全看不出来。
                // 这里用**相邻单元高度**估该单元法线，再套游戏自己的 `LightingManager.CalculateLighting`
                // （环境光 + 两盏方向光），得到"与游戏光照模型一致"的明暗系数 —— 这是把 LOD 接进光照的
                // 第一步（里程碑 5 的阴影/G-buffer 之前的最小可用版本，见 notes/85）。
                Color cellLight = cellBase;
                if (SlopeShadingStrength > 0f || SelfShadowStrength > 0f) {
                    float gain = SlopeShadingStrength > 0f
                        ? SlopeLightGain(dict, cx, cz, cell.Height, cellSize)
                        : 1f;
                    gain = MathUtils.Lerp(1f, gain, m_sunAmount);      // v0.1.19：坡向明暗同样按日照量淡出
                    if (SelfShadowStrength > 0f) {
                        // v0.1.19：LOD 自阴影（CPU 射线步进，见 notes/88 §5 的第 1 条路线）
                        float shadow = SelfShadowFactor(dict, cx, cz, cell.Height, cellSize);
                        // 昼夜调制：夜里把"坡向/阴影"偏差按日照量收回 1（= 不再有斜阳感）
                        gain *= MathUtils.Lerp(1f, shadow, m_sunAmount);
                        if (layer == 0) {
                            m_selfShadowSampled++;
                            if (shadow < 0.999f) {
                                m_selfShadowedCells++;
                            }
                        }
                    }
                    cellLight = new Color(
                        (byte)MathUtils.Clamp(cellBase.R * gain, 0f, 255f),
                        (byte)MathUtils.Clamp(cellBase.G * gain, 0f, 255f),
                        (byte)MathUtils.Clamp(cellBase.B * gain, 0f, 255f),
                        cellBase.A
                    );
                }
                BlockGeometryGenerator.SetupVertex(x0, y, z0, cellLight, u0, v0, ref vertices[vi]);
                BlockGeometryGenerator.SetupVertex(x0 + cellSize, y, z0, cellLight, u0 + du, v0, ref vertices[vi + 1]);
                BlockGeometryGenerator.SetupVertex(x0 + cellSize, y, z0 + cellSize, cellLight, u0 + du, v0 + du, ref vertices[vi + 2]);
                BlockGeometryGenerator.SetupVertex(x0, y, z0 + cellSize, cellLight, u0, v0 + du, ref vertices[vi + 3]);
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
                float x0 = wx, z0 = wz, x1 = wx + cellSize, z1 = wz + cellSize;
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
            var vb = new VertexBuffer(TerrainVertex.VertexDeclaration, vertexCount);
            vb.SetData(vertices, 0, vertexCount);
            var ib = new IndexBuffer(bigIndices ? IndexFormat.ThirtyTwoBits : IndexFormat.SixteenBits, ii);
            if (bigIndices) {
                ib.SetData(indices32, 0, ii);
            }
            else {
                ib.SetData(indices, 0, ii);
            }
            SetLayerMesh(layer, vb, ib, ii, built);
        }

        /// <summary>[v0.1.45] 把一套网格写回对应层（0=粗 16 m、1=细 8 m、2=近环 4 m）。</summary>
        static void SetLayerMesh(int layer, VertexBuffer vb, IndexBuffer ib, int indexCount, int cellsInMesh) {
            switch (layer) {
                case 2:
                    Utilities.Dispose(ref m_vbNear);
                    Utilities.Dispose(ref m_ibNear);
                    m_vbNear = vb;
                    m_ibNear = ib;
                    m_indexCountNear = indexCount;
                    m_cellsInMeshNear = cellsInMesh;
                    break;
                case 1:
                    Utilities.Dispose(ref m_vbFine);
                    Utilities.Dispose(ref m_ibFine);
                    m_vbFine = vb;
                    m_ibFine = ib;
                    m_indexCountFine = indexCount;
                    m_cellsInMeshFine = cellsInMesh;
                    break;
                default:
                    Utilities.Dispose(ref m_vb);
                    Utilities.Dispose(ref m_ib);
                    m_vb = vb;
                    m_ib = ib;
                    m_indexCount = indexCount;
                    m_cellsInMesh = cellsInMesh;
                    break;
            }
        }

        static float Dist2(long key, Vector3 camera, int cellShift) {
            int cellSize = 1 << cellShift;
            int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
            float dx = (cx << cellShift) + cellSize * 0.5f - camera.X;
            float dz = (cz << cellShift) + cellSize * 0.5f - camera.Z;
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
            if (!Enabled || (m_indexCount == 0 && m_indexCountFine == 0)) {
                return;
            }
            // v0.1.5：光影包接管点——Dawnlight/Iris 式管线自行绘制 LOD（见 notes/73）。
            if (ExternalShaderHooked && CustomDraw != null) {
                try {
                    CustomDraw(camera);
                }
                catch (Exception e) {
                    m_lastError = e.Message;
                    Log.Warning($"SkylineLod.CustomDraw: {e.Message}");
                }
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
                    .SetValue(SkylineRuntime.FogBand(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity)));
                // v0.1.1：**与真实地形共用同一条视图雾曲线**。原来自算 [0.55R, R] 的雾带，
                // 而原版真实地形在 `视距 × 0.8` 处就已 100% 雾化——两者交界"地形全雾消失 /
                // LOD 无雾跳出"，交接感明显（用户反馈）。现在 `SkylineAtmosphere.AdjustHazeSpan`
                // 已把视图雾的跨度拉远到 LOD 半径的 90%，这里直接采用 sky 的 (start, density)，
                // 两个渲染层在交界处与全程都连续。
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(SkylineRuntime.HazeStartDensity(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity)));
                shader.GetParameter("u_texture", true).SetValue(
                    subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                // v0.1.1：先画粗层（远环），再画精细层（近环）
                if (m_vb != null && m_ib != null && m_indexCount > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vb, m_ib, 0, m_indexCount);
                }
                if (m_vbFine != null && m_ibFine != null && m_indexCountFine > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vbFine, m_ibFine, 0, m_indexCountFine);
                }
                // [v0.1.45] 近环 4 m 层最后画（离相机最近，盖在 8 m 层之上）
                if (m_vbNear != null && m_ibNear != null && m_indexCountNear > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vbNear, m_ibNear, 0, m_indexCountNear);
                }
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
                    writer.Write(3);                       // 版本 3：粗层 + 精细层两段，每项多一个 Light 字节
                    writer.Write(m_cells.Count);
                    foreach (KeyValuePair<long, Cell> pair in m_cells) {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Height);
                        writer.Write(pair.Value.Value);
                        writer.Write(pair.Value.Light);
                    }
                    writer.Write(m_cellsFine.Count);
                    foreach (KeyValuePair<long, Cell> pair in m_cellsFine) {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value.Height);
                        writer.Write(pair.Value.Value);
                        writer.Write(pair.Value.Light);
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
            m_cellsFine.Clear();
            ResetRefreshState();          // v0.1.8：切世界/重载时不带旧采样戳与脏集合
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
                        byte cellLight = version >= 3 ? reader.ReadByte() : (byte)15;
                        m_cells[key] = new Cell { Height = height, Value = value, Light = cellLight };
                    }
                    if (version >= 2) {
                        int countFine = reader.ReadInt32();
                        for (int i = 0; i < countFine && i < MaxCells * 8; i++) {
                            long key = reader.ReadInt64();
                            short height = reader.ReadInt16();
                            ushort value = reader.ReadUInt16();
                            byte cellLight = version >= 3 ? reader.ReadByte() : (byte)15;
                            m_cellsFine[key] = new Cell { Height = height, Value = value, Light = cellLight };
                        }
                    }
                }
                m_dirty = true;
                Log.Information($"SkylineLod: loaded {m_cells.Count} cells (+{m_cellsFine.Count} fine)");
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
        }

        // ---------------- 状态 ----------------

        public static string Describe() =>
            $"lod:enabled={Enabled} cells={m_cells.Count}(+{m_cellsFine.Count}f+{m_cellsNear.Count}n) "
            + $"inMesh={m_cellsInMesh}(+{m_cellsInMeshFine}f+{m_cellsInMeshNear}n) "
            + $"indices={m_indexCount}(+{m_indexCountFine}f+{m_indexCountNear}n) "
            + $"radius={RadiusMetres:0}m cell={CellSize}/{FineSize}/{NearSize} rebuilds={m_rebuilds} "
            + $"err={(m_lastError.Length > 0 ? m_lastError : "-")} " + RefreshDescribe();

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
                ["fineCells"] = m_cellsFine.Count,
                ["fineCellsInMesh"] = m_cellsInMeshFine,
                ["fineMeshIndices"] = m_indexCountFine,
                ["nearCells"] = m_cellsNear.Count,
                ["nearCellsInMesh"] = m_cellsInMeshNear,
                ["nearMeshIndices"] = m_indexCountNear,
                ["nearBandMetres"] = Math.Round(NearBandMetres, 1),
                ["nearMarkedCells"] = NearMarkedCells,
                ["nearLayerEnabled"] = SkylineRuntime.LodNearLayerEnabled,
                ["nearLayerSwitch"] = "skyline.LodNearLayerEnabled / skyline.LodNearBandMark() 立刻铺满",
                ["radiusMetres"] = RadiusMetres,
                ["cellSizeBlocks"] = CellSize,
                ["fineCellSizeBlocks"] = FineSize,
                ["coveredAreaKm2"] = Math.Round(covered / 1_000_000f, 4),
                ["harvestedCells"] = m_harvestedCells,
                ["meshRebuilds"] = m_rebuilds,
                ["loadedChunks"] = loadedColumns,
                ["refresh"] = RefreshSurveyJson(),
                ["lastError"] = m_lastError
            }.ToJsonString();
        }
    }
}
