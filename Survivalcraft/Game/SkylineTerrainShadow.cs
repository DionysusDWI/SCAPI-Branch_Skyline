using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.30：**近景地形的真阴影（CPU 第一刀）** —— 里程碑 5（Iris 真接入）路线上
    /// "把阴影判定接进游戏自己的地形渲染"这一步：
    /// 只读网格访问器 v0.1.11 → 太阳视角 pass v0.1.18 → LOD 自阴影 v0.1.19 → **地形顶点光照阴影（本版）**。
    ///
    /// 做法：区块几何生成完成（InvalidVertices2 → Valid 之前）时，对每个 `TerrainVertex` 用
    /// `SkylineLod.TerrainShadowSample(x,y,z)`（LOD 高度场射线步进）取遮挡判定，命中则把顶点颜色
    /// 按 `TerrainShadowStrength` 压暗。**不改方块数据、不改光照数据** —— 只改"这一次生成出来的
    /// 顶点颜色"，因此重建一次即完全恢复（可 A/B、可回滚）。
    ///
    /// 边界（如实告知）：阴影分辨率 = LOD 高度场（细 8 m / 粗 16 m），所以是**远景尺度**的硬阴影，
    /// 不是逐像素阴影贴图；近处小物件不会投影。逐像素阴影要等 GPU 阴影贴图（`notes/88 §5` 路线 2）。
    ///
    /// 开关：`skyline.TerrainShadowEnabled`（默认关）、`skyline.TerrainShadowStrength`（默认 0.45）。
    /// 诊断：`skyline.TerrainShadowDescribe()`；A/B 辅助：`skyline.TerrainShadowApplyLoadedChunks()`。
    /// </summary>
    public static partial class SkylineRuntime {
        public static bool TerrainShadowEnabled { get; set; }

        public static float TerrainShadowStrength { get; set; } = 0.45f;

        // ===== [v0.1.31] 随太阳重烘焙（补齐 v0.1.30 的已知局限：阴影按"烘焙时的太阳"固定）=====
        /// <summary>太阳转过阈值角度后自动重烘焙（默认开；仅在 `TerrainShadowEnabled` 打开时生效）。</summary>
        public static bool TerrainShadowRebakeEnabled { get; set; } = true;

        /// <summary>太阳方向变化超过该角度（度）才重烘焙。</summary>
        public static float TerrainShadowSunThresholdDegrees { get; set; } = 4f;

        /// <summary>两次重烘焙之间的最小间隔（秒；防止太阳快速移动时每帧重建）。</summary>
        public static float TerrainShadowMinRebakeSeconds { get; set; } = 8f;

        /// <summary>重烘焙的半径（米）：只重建相机周围这个范围内的已加载区块。</summary>
        public static int TerrainShadowRebakeRadius { get; set; } = 256;

        static Vector3 m_terrainShadowBakedSun = Vector3.UnitY;
        static double m_terrainShadowLastRebakeTime;
        static long m_terrainShadowRebakes;
        static int m_terrainShadowLastRebakeChunks;

        static long m_terrainShadowChunks;
        static long m_terrainShadowVertices;
        static long m_terrainShadowShadowed;
        static double m_terrainShadowLastMs;
        static double m_terrainShadowTotalMs;

        public static string TerrainShadowDescribe() =>
            $"terrainShadow enabled={TerrainShadowEnabled} strength={TerrainShadowStrength:0.##} "
            + $"chunks={m_terrainShadowChunks} vertices={m_terrainShadowVertices} "
            + $"shadowed={m_terrainShadowShadowed} lastMs={m_terrainShadowLastMs:0.0} "
            + $"totalMs={m_terrainShadowTotalMs:0.0} "
            + $"rebake={TerrainShadowRebakeEnabled} thresholdDeg={TerrainShadowSunThresholdDegrees:0.#} "
            + $"rebakes={m_terrainShadowRebakes} lastRebakeChunks={m_terrainShadowLastRebakeChunks} "
            + $"sunDot={Vector3.Dot(TerrainShadowSun(), m_terrainShadowBakedSun):0.###}";

        /// <summary>当前太阳方向（指向光源的单位向量）。</summary>
        static Vector3 TerrainShadowSun() {
            Vector3 sun = LightingManager.DirectionToLight1;
            return sun.LengthSquared() > 1e-6f ? Vector3.Normalize(sun) : Vector3.UnitY;
        }

        /// <summary>
        /// [v0.1.31] 每帧由 `SkylineRuntime.Tick()` 调用：太阳转过阈值角度后，把相机周围的
        /// 已加载区块几何**强制重建**一次（`forceGeometryRegeneration=true`，否则切片哈希相同会被跳过），
        /// 让顶点阴影跟着太阳走。限频 + 半径限制；默认随 `TerrainShadowEnabled` 生效。
        /// </summary>
        public static void TerrainShadowTick() {
            if (!TerrainShadowEnabled || !TerrainShadowRebakeEnabled) {
                return;
            }
            if (Time.RealTime - m_terrainShadowLastRebakeTime < Math.Max(1f, TerrainShadowMinRebakeSeconds)) {
                return;
            }
            Vector3 sun = TerrainShadowSun();
            float cosThreshold = MathF.Cos(MathUtils.DegToRad(Math.Clamp(TerrainShadowSunThresholdDegrees, 0.5f, 45f)));
            if (Vector3.Dot(sun, m_terrainShadowBakedSun) >= cosThreshold) {
                return;
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            TerrainUpdater updater = subsystemTerrain?.TerrainUpdater;
            Terrain terrain = subsystemTerrain?.Terrain;
            Camera camera = GetCamera();
            if (updater == null || terrain == null || camera == null) {
                return;
            }
            Vector2 center = camera.ViewPosition.XZ;
            float radius2 = (float)TerrainShadowRebakeRadius * TerrainShadowRebakeRadius;
            int marked = 0;
            foreach (TerrainChunk chunk in terrain.AllocatedChunks) {
                if (chunk == null || Vector2.DistanceSquared(center, chunk.Center) > radius2) {
                    continue;
                }
                updater.DowngradeChunkNeighborhoodState(chunk.Coords, 0, TerrainChunkState.InvalidVertices1, true);
                marked++;
            }
            m_terrainShadowBakedSun = sun;
            m_terrainShadowLastRebakeTime = Time.RealTime;
            m_terrainShadowRebakes++;
            m_terrainShadowLastRebakeChunks = marked;
        }

        /// <summary>
        /// 对**一个区块已生成的顶点**施加阴影（同一批顶点不要重复调用，否则会叠加压暗）。
        /// 由 `TerrainUpdater` 在 InvalidVertices2（两次顶点生成都做完）时调用；
        /// A/B 时可用 `TerrainShadowApplyLoadedChunks()` 对已加载区块补一次。
        /// </summary>
        public static void ApplyTerrainShadow(TerrainChunk chunk) {
            if (chunk == null) {
                return;
            }
            double start = Time.RealTime;
            float keep = MathUtils.Clamp(1f - TerrainShadowStrength, 0.05f, 1f);
            int vertices = 0;
            int shadowed = 0;
            m_terrainShadowBakedSun = TerrainShadowSun();     // 记录"这次烘焙用的太阳"
            var memo = new Dictionary<long, float>(512);
            for (int slice = 0; slice < chunk.ChunkSliceGeometries.Length; slice++) {
                TerrainGeometry geometry = chunk.ChunkSliceGeometries[slice];
                if (geometry == null) {
                    continue;
                }
                ApplyToGeometry(geometry, keep, memo, ref vertices, ref shadowed);
                if (geometry.Draws != null) {
                    foreach (KeyValuePair<Texture2D, TerrainGeometry> draw in geometry.Draws) {
                        if (draw.Value != null && draw.Value != geometry) {
                            ApplyToGeometry(draw.Value, keep, memo, ref vertices, ref shadowed);
                        }
                    }
                }
            }
            double ms = (Time.RealTime - start) * 1000.0;
            m_terrainShadowChunks++;
            m_terrainShadowVertices += vertices;
            m_terrainShadowShadowed += shadowed;
            m_terrainShadowLastMs = ms;
            m_terrainShadowTotalMs += ms;
        }

        static void ApplyToGeometry(TerrainGeometry geometry, float keep, Dictionary<long, float> memo,
            ref int vertices, ref int shadowed) {
            if (geometry?.Subsets == null) {
                return;
            }
            foreach (TerrainGeometrySubset subset in geometry.Subsets) {
                TerrainGeometryDynamicArray<TerrainVertex> list = subset?.Vertices;
                if (list == null) {
                    continue;
                }
                int count = list.Count;
                for (int i = 0; i < count; i++) {
                    TerrainVertex vertex = list[i];
                    long key = PackVertexKey(vertex.X, vertex.Y, vertex.Z);
                    if (!memo.TryGetValue(key, out float factor)) {
                        factor = SkylineLod.TerrainShadowSample(vertex.X, vertex.Y, vertex.Z);
                        if (memo.Count < 8192) {
                            memo[key] = factor;
                        }
                    }
                    vertices++;
                    if (factor < 0.5f) {
                        Color color = vertex.Color;
                        color.R = (byte)(color.R * keep);
                        color.G = (byte)(color.G * keep);
                        color.B = (byte)(color.B * keep);
                        vertex.Color = color;
                        list[i] = vertex;
                        shadowed++;
                    }
                }
            }
        }

        static long PackVertexKey(float x, float y, float z) {
            long ix = (long)MathF.Round(x) & 0x1FFFFF;
            long iy = (long)MathF.Round(y) & 0x1FFFFF;
            long iz = (long)MathF.Round(z) & 0x1FFFFF;
            return (ix << 42) | (iy << 21) | iz;
        }

        /// <summary>A/B 辅助：对当前已加载且 Valid 的区块补一次阴影（即时对比截图用）。</summary>
        public static string TerrainShadowApplyLoadedChunks() {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            if (terrain == null) {
                return "terrainShadow: no terrain";
            }
            TerrainChunk[] chunks = terrain.AllocatedChunks;
            int applied = 0;
            double start = Time.RealTime;
            foreach (TerrainChunk chunk in chunks) {
                if (chunk != null && chunk.State == TerrainChunkState.Valid) {
                    ApplyTerrainShadow(chunk);
                    applied++;
                }
            }
            return $"terrainShadow applied={applied} ms={(Time.RealTime - start) * 1000.0:0.0} "
                + $"({TerrainShadowDescribe()})";
        }
    }
}
