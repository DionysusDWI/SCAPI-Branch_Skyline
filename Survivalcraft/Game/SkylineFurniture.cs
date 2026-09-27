using System.Collections.Generic;

namespace Game {
    /// <summary>
    /// SCAPI Skyline（v0.0.9）：**高复杂度家具的几何预算守卫**。
    ///
    /// 为什么需要它（2026-09-27 实测，`notes/59` §9 / `data/sessions/skyline-v009/hi-furniture/ramp.json`）：
    ///   * 家具几何在**每个实例**上重复烘进区块顶点缓冲（`FurnitureBlock.GenerateTerrainVertices`），
    ///     一个 16³ 区块里放 N 件家具 = N × 单件顶点数（**乘法**）。
    ///   * 实测样品：分辨率 28 的 3D 棋盘设计（10,976 非空体素、贪心网格无法合并）
    ///     单件 ≈ 263,424 顶点 ≈ **6.5 MB 显存 + ~21 MB 托管内存 + ~10 ms 生成时间**。
    ///   * 单区块塞满 16³（4096 件）外推 ≈ **26 GB 显存 + 86 GB 内存** → 必然把机器打崩
    ///     （实测 256 件时物理内存已到 35.2 GB、进程工作集 14.6 GB）。
    ///
    /// 机制：给"每次 <see cref="TerrainUpdater.GenerateChunkVertices"/>（一个阶段的半个区块）里
    /// 家具贡献的顶点数"设上限。预算内的家具照常烘自身几何；超预算的家具**退化成方盒**（默认，
    /// 保留可见的占位块）或直接不画（`FallbackBox=false`），并计入统计。
    /// 这样"病态高密度高复杂度家具"从"打崩机器"变成"有界的降级 + 可查询的计数"。
    ///
    /// 注意：这是**安全阀**，不是精度方案——要真正让高复杂度家具既精确又不爆，需要把家具改成
    /// 实例化渲染（设计网格渲染一次、实例只带变位/朝向），那是渲染管线级改动（见 notes/59 §9 的后续路线）。
    /// </summary>
    public static class SkylineFurniture {
        /// <summary>总开关（默认开）。关掉就完全恢复"每件家具都烘全精度几何"的行为。</summary>
        public static bool BudgetEnabled { get; set; } = true;

        /// <summary>单个几何生成阶段内，家具允许贡献的顶点数上限（默认 2,000,000）。</summary>
        public static int MaxStageFurnitureVertices { get; set; } = 2000000;

        /// <summary>超预算的家具是否退化成"方盒占位"（默认 true；false = 完全不画）。</summary>
        public static bool FallbackBox { get; set; } = true;

        /// <summary>A/B 调试开关：强制**所有**家具走方盒占位（默认关）。用于"占位方盒一致性距离"测量。</summary>
        public static bool ForceBox { get; set; }

        /// <summary>A/B 调试开关：只把**指定设计索引**强制成方盒（默认 -1 = 关）。
        /// 用它可以只替换"目标家具"而保留遮挡屏的窗孔几何（`ForceBox` 会把屏也变成方盒，窗口就没了）。</summary>
        public static int ForceBoxDesign { get; set; } = -1;

        /// <summary>每次 BeginStage 重新计数的本阶段统计。</summary>
        static string m_stageName = "";
        static int m_stageVertices;
        static int m_stageKept;
        static int m_stageCulled;
        static int m_totalCulled;
        static int m_totalBoxes;

        /// <summary>设计索引 → (分辨率, 顶点数)。设计槽会被回收复用，所以带上分辨率做校验。</summary>
        static readonly Dictionary<int, (int Resolution, int Vertices)> m_designVertices = [];

        /// <summary>设计索引 → 主材质方块值（占位盒用它上色，见 notes/64 §4.1 的实测结论：
        /// 用引擎默认贴图槽会在 96 m 仍有可见差异，必须取设计的主材质）。</summary>
        static readonly Dictionary<int, int> m_designDominant = [];

        public static void BeginStage(string name) {
            m_stageName = name;
            m_stageVertices = 0;
            m_stageKept = 0;
            m_stageCulled = 0;
        }

        /// <summary>一个家具设计的顶点数（六面 × 透明/半透明/不透明子网格之和，带缓存）。</summary>
        public static int CountDesignVertices(FurnitureDesign design) {
            if (design == null) {
                return 0;
            }
            int resolution = design.Resolution;
            if (m_designVertices.TryGetValue(design.Index, out (int Resolution, int Vertices) cached)
                && cached.Resolution == resolution) {
                return cached.Vertices;
            }
            FurnitureGeometry geometry = design.Geometry;
            int count = 0;
            if (geometry != null) {
                for (int i = 0; i < 6; i++) {
                    count += geometry.SubsetOpaqueByFace[i]?.Vertices.Count ?? 0;
                    count += geometry.SubsetAlphaTestByFace[i]?.Vertices.Count ?? 0;
                    count += geometry.SubsetTransparentByFace[i]?.Vertices.Count ?? 0;
                }
            }
            m_designVertices[design.Index] = (resolution, count);
            return count;
        }

        /// <summary>设计里出现次数最多的体素材质（完整方块值），带缓存。空设计返回 0。</summary>
        public static int DominantMaterial(FurnitureDesign design) {
            if (design == null) {
                return 0;
            }
            if (m_designDominant.TryGetValue(design.Index, out int cached)) {
                return cached;
            }
            int resolution = design.Resolution;
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < resolution * resolution * resolution; i++) {
                int value = design.GetValue(i);
                if (value == 0) {
                    continue;
                }
                counts.TryGetValue(value, out int n);
                counts[value] = n + 1;
            }
            int best = 0, bestCount = -1;
            foreach (KeyValuePair<int, int> pair in counts) {
                if (pair.Value > bestCount) {
                    bestCount = pair.Value;
                    best = pair.Key;
                }
            }
            m_designDominant[design.Index] = best;
            return best;
        }

        /// <summary>true = 允许按设计烘几何；false = 超预算（调用方退化成方盒/跳过）。</summary>
        public static bool TryReserve(FurnitureDesign design) {
            if (!BudgetEnabled) {
                return true;
            }
            int count = CountDesignVertices(design);
            if (m_stageVertices + count <= MaxStageFurnitureVertices) {
                m_stageVertices += count;
                m_stageKept++;
                return true;
            }
            m_stageCulled++;
            m_totalCulled++;
            return false;
        }

        public static void NoteFallbackBox() => m_totalBoxes++;

        public static void ResetStats() {
            m_totalCulled = 0;
            m_totalBoxes = 0;
            m_designVertices.Clear();
            m_designDominant.Clear();
        }

        public static string Describe() =>
            $"furnitureBudget={BudgetEnabled} maxStageVerts={MaxStageFurnitureVertices} box={FallbackBox} forceBox={ForceBox} "
            + $"forceBoxDesign={ForceBoxDesign} "
            + $"stage[{m_stageName}] verts={m_stageVertices} kept={m_stageKept} culled={m_stageCulled} "
            + $"totalCulled={m_totalCulled} boxes={m_totalBoxes} designsCached={m_designVertices.Count}";
    }
}
