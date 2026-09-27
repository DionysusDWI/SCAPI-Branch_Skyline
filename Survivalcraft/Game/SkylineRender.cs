using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using Engine;
using GameEntitySystem;

namespace Game {
    /// <summary>
    /// SCAPI Skyline（v0.1.0）：**多层渲染优化（家具 LOD）** + 三层球形统计。
    ///
    /// 命名（用户要求）：
    ///   * 中文：**占位替代距离**（简称 **占替距**）
    ///   * 代码：**MBD = Minimum Box-substitution Distance**，符号 `d_box`
    ///   * 实测曲线（标准视角 100% / 竖直 FOV 80° / 720p）：`d_box(E) = 39.6·E^0.300`（R²=0.939）
    ///     等价判据：暴露面投影 ≤ **70 px²**（≈8×8 像素）
    ///
    /// v0.1.0 起**真的参与渲染**（`Enabled` 默认 true）：
    ///   * 每个家具实例算暴露度 E（朝向相机的面里最大者），得到 `d_box(E)`；
    ///     分散暴露（≥2 个暴露面）乘保守分布因子 1.4（实测多窗口比单窗口更显眼）；
    ///   * 超出 `d_box` 的实例渲染成**主材质方盒**（`SkylineFurniture.DominantMaterial`；
    ///     实测用引擎默认贴图槽在 96 m 仍有可见差异，必须取设计主材质）；
    ///   * 区块级三层状态机（`Full / Mixed / Boxed`）避免逐实例重建：
    ///     相机距离 &gt; 区块内最大 `d_box` → 整块占位；&lt; 最小 `d_box` → 整块全精度；
    ///     之间 → 逐实例判定。跨档时**只对该区块**触发一次几何重建（带 5% 迟滞 + 每 tick 预算）。
    ///
    /// 三层球形统计（只读，`Survey()`）：占位球 / 视觉球（二维→三维椭球）/ 加载球。
    /// </summary>
    public static class SkylineRender {
        // ---------------- 曲线与常量 ----------------

        public const float BoxPixelArea = 70f;
        public const float MbdA = 39.6f;
        public const float MbdB = 0.300f;
        public const float ReferenceScreenHeight = 720f;
        public const float ReferenceFovDegrees = 80f;

        /// <summary>分散暴露的保守因子（v0.0.9 实测：同样总面积拆成 4 个窗口时差异面积约 ×2，
        /// 折算成距离 ≈ ×1.4）。当实例有 ≥2 个暴露面时乘上它。</summary>
        public const float DistributionFactor = 1.4f;

        /// <summary>状态切换迟滞（5%）：避免相机在边界抖动时反复重建几何。</summary>
        public const float Hysteresis = 0.05f;

        public static float Mbd(float exposure) =>
            MbdA * MathF.Pow(MathUtils.Clamp(exposure, 1f / 65536f, 1f), MbdB);

        public static float MbdFromPixelArea(float exposure, float screenHeightPx = ReferenceScreenHeight,
                                            float fovDegrees = ReferenceFovDegrees, float pixelArea = BoxPixelArea) {
            float scale = screenHeightPx / (2f * MathF.Tan(MathUtils.DegToRad(fovDegrees) * 0.5f));
            return scale * MathF.Sqrt(MathUtils.Clamp(exposure, 1f / 65536f, 1f) / MathF.Max(pixelArea, 0.01f));
        }

        // ---------------- 开关 ----------------

        /// <summary>是否按 `d_box` 把远处家具替换成主材质方盒（v0.1.0 起默认开）。</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>统计开关（只读计数）。</summary>
        public static bool StatsEnabled { get; set; } = true;

        /// <summary>每个 tick 允许触发的区块重建数（防止跨档时一次重建风暴）。</summary>
        public static int MaxRebakesPerTick { get; set; } = 1;

        /// <summary>动态档位检查间隔（秒）。</summary>
        public static float TickIntervalSeconds { get; set; } = 0.5f;

        // ---------------- 统计 ----------------

        static int m_fullInstances;
        static int m_boxInstances;
        static int m_rebakes;
        static int m_surveys;
        static double m_nextTick;
        static int m_roundRobin;

        public static void ResetStats() {
            m_fullInstances = m_boxInstances = m_rebakes = 0;
        }

        // ---------------- 区块级 LOD 状态 ----------------

        enum LodState { Mixed = 0, Full = 1, Boxed = 2 }

        sealed class ChunkInfo {
            public LodState State = LodState.Mixed;
            public float MinDb = float.MaxValue;
            public float MaxDb;
            public int InstanceCount;
        }

        static readonly Dictionary<long, ChunkInfo> m_chunks = [];

        static long Key(Point2 coords) => ((long)coords.X << 32) ^ (uint)coords.Y;

        static ChunkInfo GetInfo(long key) {
            if (!m_chunks.TryGetValue(key, out ChunkInfo info)) {
                info = new ChunkInfo();
                m_chunks[key] = info;
            }
            return info;
        }

        public static void ResetChunkLod() => m_chunks.Clear();

        /// <summary>一次烘焙开始时重置该区块的实例统计（由 TerrainUpdater 在 stage==0 调用）。</summary>
        public static void BeginChunkStage(TerrainChunk chunk, int stage) {
            if (!Enabled || chunk == null) {
                return;
            }
            if (stage == 0) {
                ChunkInfo info = GetInfo(Key(chunk.Coords));
                info.MinDb = float.MaxValue;
                info.MaxDb = 0f;
                info.InstanceCount = 0;
            }
        }

        static void RecordInstance(TerrainChunk chunk, float db) {
            ChunkInfo info = GetInfo(Key(chunk.Coords));
            info.MinDb = MathF.Min(info.MinDb, db);
            info.MaxDb = MathF.Max(info.MaxDb, db);
            info.InstanceCount++;
        }

        // ---------------- 暴露度 ----------------

        static readonly int[] s_faceDx = [0, 0, 0, 0, 1, -1];
        static readonly int[] s_faceDy = [0, 0, 1, -1, 0, 0];
        static readonly int[] s_faceDz = [1, -1, 0, 0, 0, 0];

        static float NeighborCoverage(Terrain terrain, SubsystemFurnitureBlockBehavior furniture,
                                     int x, int y, int z, int face) {
            int value = terrain.GetCellValueFast(x + s_faceDx[face], y + s_faceDy[face], z + s_faceDz[face]);
            int contents = Terrain.ExtractContents(value);
            if (contents == 0) {
                return 0f;
            }
            if (contents == 227) {
                FurnitureDesign design = furniture?.GetDesign(FurnitureBlock.GetDesignIndex(Terrain.ExtractData(value)));
                if (design == null) {
                    return 0.5f;
                }
                int resolution = design.Resolution;
                int covered = 0;
                for (int a = 0; a < resolution; a++) {
                    for (int b = 0; b < resolution; b++) {
                        int vx, vy, vz;
                        switch (face) {
                            case 0: vx = a; vy = b; vz = 0; break;
                            case 1: vx = a; vy = b; vz = resolution - 1; break;
                            case 2: vx = a; vy = 0; vz = b; break;
                            case 3: vx = a; vy = resolution - 1; vz = b; break;
                            case 4: vx = 0; vy = a; vz = b; break;
                            default: vx = resolution - 1; vy = a; vz = b; break;
                        }
                        if (design.GetValue(vx + vy * resolution + vz * resolution * resolution) != 0) {
                            covered++;
                        }
                    }
                }
                return covered / (float)(resolution * resolution);
            }
            Block block = BlocksManager.Blocks[contents];
            return block.IsTransparent_(value) ? 0.5f : 1f;
        }

        /// <summary>暴露度：朝向相机的面里最大的"未被邻居覆盖"比例；同时给出暴露面个数（分布因子用）。</summary>
        public static float InstanceExposure(Terrain terrain, SubsystemFurnitureBlockBehavior furniture,
                                            int x, int y, int z, Vector3 camera, out int exposedFaces) {
            float best = 0f;
            exposedFaces = 0;
            for (int face = 0; face < 6; face++) {
                float cx = x + 0.5f + s_faceDx[face] * 0.5f;
                float cy = y + 0.5f + s_faceDy[face] * 0.5f;
                float cz = z + 0.5f + s_faceDz[face] * 0.5f;
                float dot = s_faceDx[face] * (camera.X - cx) + s_faceDy[face] * (camera.Y - cy)
                          + s_faceDz[face] * (camera.Z - cz);
                if (dot <= 0f) {
                    continue;
                }
                float exposure = 1f - NeighborCoverage(terrain, furniture, x, y, z, face);
                if (exposure > 0.02f) {
                    exposedFaces++;
                }
                best = MathF.Max(best, exposure);
            }
            return best;
        }

        public static float InstanceExposure(Terrain terrain, SubsystemFurnitureBlockBehavior furniture,
                                            int x, int y, int z, Vector3 camera) =>
            InstanceExposure(terrain, furniture, x, y, z, camera, out _);

        // ---------------- 渲染决策 ----------------

        static Vector3 CameraPosition() {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(true);
            ComponentPlayer player = players != null && players.ComponentPlayers.Count > 0
                ? players.ComponentPlayers[0] : null;
            Camera camera = player?.GameWidget?.ActiveCamera;
            return camera?.ViewPosition ?? Vector3.Zero;
        }

        /// <summary>家具实例的替换决策（由 FurnitureBlock 在烘焙时调用）。</summary>
        public static bool ShouldBoxInstance(int designIndex, int x, int y, int z) {
            if (!Enabled) {
                return false;
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            Terrain terrain = subsystemTerrain?.Terrain;
            if (terrain == null) {
                return false;
            }
            TerrainChunk chunk = terrain.GetChunkAtCell(x, z);
            if (chunk == null) {
                return false;
            }
            Vector3 camera = CameraPosition();
            Vector3 center = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            float distance = Vector3.Distance(camera, center);
            float exposure = InstanceExposure(terrain, subsystemTerrain.SubsystemFurnitureBlockBehavior,
                                              x, y, z, camera, out int exposedFaces);
            float db = Mbd(exposure) * (exposedFaces >= 2 ? DistributionFactor : 1f);
            RecordInstance(chunk, db);
            ChunkInfo info = GetInfo(Key(chunk.Coords));
            bool boxed;
            switch (info.State) {
                case LodState.Boxed: boxed = true; break;
                case LodState.Full: boxed = false; break;
                default: boxed = distance >= db; break;
            }
            if (StatsEnabled) {
                if (boxed) {
                    m_boxInstances++;
                }
                else {
                    m_fullInstances++;
                }
            }
            return boxed;
        }

        /// <summary>动态档位检查（由 SkylineRuntime.Tick 每帧调用，内部按 TickIntervalSeconds 限频）。</summary>
        public static void Tick() {
            if (!Enabled || m_chunks.Count == 0) {
                return;
            }
            double now = Time.RealTime;
            if (now < m_nextTick) {
                return;
            }
            m_nextTick = now + MathF.Max(TickIntervalSeconds, 0.1f);
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain == null) {
                return;
            }
            Vector3 camera = CameraPosition();
            float visual = VisualSphereRadius;
            var keys = new List<long>(m_chunks.Keys);
            int budget = Math.Max(MaxRebakesPerTick, 1);
            for (int i = 0; i < keys.Count && budget > 0; i++) {
                m_roundRobin = (m_roundRobin + 1) % Math.Max(keys.Count, 1);
                long key = keys[m_roundRobin];
                ChunkInfo info = m_chunks[key];
                if (info.InstanceCount == 0 || info.MinDb == float.MaxValue) {
                    continue;
                }
                Point2 coords = new Point2((int)(key >> 32), (int)(key & 0xFFFFFFFF));
                TerrainChunk chunk = subsystemTerrain.Terrain.GetChunkAtCoords(coords.X, coords.Y);
                if (chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                    continue;
                }
                // v0.1.0 修复（2026-09-27 实测）：区块状态机原来**只用水平距离**——高空俯视时
                // "水平 12 m / 垂直 776 m" 的家具区块被判 Full（全精度），多层 LOD 在高空场景
                // 完全失效（实测 full=35,736 / box≈4,300）。现在用区块中心列的顶部高度补上
                // 竖直分量（地面玩家 dy≈0，行为不变；8 号列的 top 采样足够便宜）。
                float horiz = Vector2.Distance(
                    camera.XZ, new Vector2(coords.X * TerrainChunk.Size + 8f,
                                           coords.Y * TerrainChunk.Size + 8f));
                int topY = chunk.GetTopHeightFast(TerrainChunk.Size / 2, TerrainChunk.Size / 2);
                float dy = MathF.Max(0f, MathF.Abs(camera.Y - (topY + 1f)) - 16f);
                float distance = MathF.Sqrt(horiz * horiz + dy * dy);
                if (distance > visual * 1.5f) {
                    continue;
                }
                LodState desired;
                if (distance > info.MaxDb * (1f + Hysteresis)) {
                    desired = LodState.Boxed;
                }
                else if (distance < info.MinDb * (1f - Hysteresis)) {
                    desired = LodState.Full;
                }
                else {
                    desired = LodState.Mixed;
                }
                if (desired != info.State) {
                    info.State = desired;
                    subsystemTerrain.TerrainUpdater.DowngradeChunkNeighborhoodState(
                        chunk.Coords, 0, TerrainChunkState.InvalidVertices1, true);
                    m_rebakes++;
                    budget--;
                }
            }
        }

        // ---------------- 视觉球 / 加载球 ----------------

        public static float VisualSphereRadius {
            get {
                SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
                return sky?.VisibilityRange ?? SettingsManager.VisibilityRange;
            }
        }

        public static float VisualSphereYMultiplier {
            get {
                SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
                return sky?.VisibilityRangeYMultiplier ?? 1f;
            }
        }

        public static float LoadingSphereRadius => VisualSphereRadius;

        public static bool InsideVisualSphere(Vector3 point, Vector3 center) {
            float radius = VisualSphereRadius;
            float m = MathF.Max(VisualSphereYMultiplier, 0.0001f);
            float dx = point.X - center.X;
            float dz = point.Z - center.Z;
            float dy = (point.Y - center.Y) / m;
            return dx * dx + dy * dy + dz * dz <= radius * radius;
        }

        // ---------------- 只读统计 ----------------

        public static string Survey(int radiusColumns = 2) {
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain == null) {
                return "{\"ok\":false,\"err\":\"no terrain\"}";
            }
            Terrain terrain = subsystemTerrain.Terrain;
            Vector3 camera = CameraPosition();
            float visual = VisualSphereRadius;
            float m = MathF.Max(VisualSphereYMultiplier, 0.0001f);

            Point2 centerCell = Terrain.ToCell(camera.XZ);
            int centerCx = centerCell.X >> TerrainChunk.SizeBits;
            int centerCz = centerCell.Y >> TerrainChunk.SizeBits;
            int columns = 0, skipped = 0, furnitureCells = 0, boxCandidates = 0;
            float exposureSum = 0f, maxExposure = 0f, savingsVertices = 0f;
            for (int cx = centerCx - radiusColumns; cx <= centerCx + radiusColumns; cx++) {
                for (int cz = centerCz - radiusColumns; cz <= centerCz + radiusColumns; cz++) {
                    TerrainChunk chunk = terrain.GetChunkAtCoords(cx, cz);
                    if (chunk == null || chunk.ThreadState < TerrainChunkState.InvalidLight) {
                        skipped++;
                        continue;
                    }
                    columns++;
                    for (int x = 0; x < TerrainChunk.Size; x++) {
                        for (int z = 0; z < TerrainChunk.Size; z++) {
                            for (int y = TerrainChunk.MinHeight; y <= TerrainChunk.HeightMinusOne; y++) {
                                int value = chunk.GetCellValueFast(x, y, z);
                                if (Terrain.ExtractContents(value) != 227) {
                                    continue;
                                }
                                FurnitureDesign design = subsystemTerrain.SubsystemFurnitureBlockBehavior
                                    ?.GetDesign(FurnitureBlock.GetDesignIndex(Terrain.ExtractData(value)));
                                if (design == null) {
                                    continue;
                                }
                                int wx = chunk.Origin.X + x, wz = chunk.Origin.Y + z;
                                furnitureCells++;
                                float exposure = InstanceExposure(terrain, subsystemTerrain.SubsystemFurnitureBlockBehavior,
                                                                  wx, y, wz, camera, out int exposedFaces);
                                exposureSum += exposure;
                                maxExposure = MathF.Max(maxExposure, exposure);
                                float db = Mbd(exposure) * (exposedFaces >= 2 ? DistributionFactor : 1f);
                                float distance = Vector3.Distance(camera, new Vector3(wx + 0.5f, y + 0.5f, wz + 0.5f));
                                if (distance >= db) {
                                    boxCandidates++;
                                    savingsVertices += MathF.Max(0f, SkylineFurniture.CountDesignVertices(design) - 24f);
                                }
                            }
                        }
                    }
                }
            }

            int allocated = 0, planar = 0, sphere3d = 0, loading = 0;
            foreach (TerrainChunk chunk in terrain.AllocatedChunks) {
                allocated++;
                int minBottom = TerrainChunk.HeightMinusOne, maxTop = TerrainChunk.MinHeight;
                for (int sx = 0; sx < TerrainChunk.Size; sx++) {
                    for (int sz = 0; sz < TerrainChunk.Size; sz++) {
                        long shaft = chunk.GetShaftValueFast(sx, sz);
                        int bottom = Terrain.ExtractBottomHeight(shaft);
                        int top = Terrain.ExtractTopHeight(shaft);
                        if (top >= bottom) {
                            minBottom = Math.Min(minBottom, bottom);
                            maxTop = Math.Max(maxTop, top);
                        }
                    }
                }
                if (maxTop < minBottom) {
                    minBottom = maxTop = 0;
                }
                if (Vector2.Distance(camera.XZ, chunk.Center) <= visual) {
                    planar++;
                }
                float dy = camera.Y < minBottom ? minBottom - camera.Y
                    : (camera.Y > maxTop ? camera.Y - maxTop : 0f);
                float dxz = Vector2.Distance(camera.XZ, chunk.Center);
                float effective = MathF.Sqrt(dxz * dxz + (dy / m) * (dy / m));
                if (effective <= visual) {
                    sphere3d++;
                }
                if (effective <= LoadingSphereRadius) {
                    loading++;
                }
            }

            m_surveys++;
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["mbd"] = new JsonObject {
                    ["name"] = "MBD（占替距 / Minimum Box-substitution Distance，d_box）",
                    ["formula"] = $"d_box(E) = {MbdA} * E^{MbdB}",
                    ["boxPixelArea"] = BoxPixelArea,
                    ["distributionFactor"] = DistributionFactor
                },
                ["furnitureLod"] = new JsonObject {
                    ["fullInstances"] = m_fullInstances, ["boxInstances"] = m_boxInstances,
                    ["chunkRebakes"] = m_rebakes, ["trackedChunks"] = m_chunks.Count
                },
                ["placeholderSphere"] = new JsonObject {
                    ["columnsScanned"] = columns, ["columnsSkippedNotLoaded"] = skipped,
                    ["furnitureCells"] = furnitureCells,
                    ["avgExposure"] = Math.Round(furnitureCells > 0 ? exposureSum / furnitureCells : 0f, 4),
                    ["maxExposure"] = Math.Round(maxExposure, 4),
                    ["boxCandidates"] = boxCandidates,
                    ["estimatedVertexSavings"] = Math.Round(savingsVertices, 0)
                },
                ["visualSphere"] = new JsonObject {
                    ["radiusMetres"] = Math.Round(visual, 1), ["yMultiplier"] = Math.Round(m, 3),
                    ["chunksPlanar2D"] = planar, ["chunksSphere3D"] = sphere3d,
                    ["allocatedChunks"] = allocated
                },
                ["loadingSphere"] = new JsonObject {
                    ["radiusMetres"] = Math.Round(LoadingSphereRadius, 1), ["chunksToLoad"] = loading,
                    ["estimatedMB"] = loading * 2
                },
                ["stats"] = new JsonObject { ["surveys"] = m_surveys }
            }.ToJsonString();
        }

        public static string Describe() =>
            $"render:enabled={Enabled} MBD d_box(E)= {MbdA}*E^{MbdB} (<= {BoxPixelArea}px2, distFactor {DistributionFactor}) "
            + $"full={m_fullInstances} box={m_boxInstances} rebakes={m_rebakes} trackedChunks={m_chunks.Count} "
            + $"visualR={VisualSphereRadius:0.#}m m={VisualSphereYMultiplier:0.##} loadingR={LoadingSphereRadius:0.#}m";
    }
}
