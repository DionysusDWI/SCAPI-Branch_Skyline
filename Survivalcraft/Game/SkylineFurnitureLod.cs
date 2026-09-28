using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.63：**家具 LOD（里程碑 1.5）** —— 每个家具设计（design index = 用户说的"家具 ID"）
    /// 一组**逐级降低分辨率**的几何，按距离选级。
    ///
    /// 与既有机制的衔接：
    ///   * `SkylineFurniture.TryReserve`（顶点预算）+ `SkylineRender.ShouldBoxInstance`（每实例按距离/曝光决定
    ///     **全精度 vs 方盒**）—— 那是**二元**的；
    ///   * 本类在两者之间插入 **1/2、1/4 分辨率**两级 → "全精度 → 粗 → 方盒"变成**连续**，
    ///     于是"远一点就整块变方盒"的跳变被抹掉（视觉无差异的关键）。
    ///   * 粗级几何**按设计缓存**（同一设计的全部实例共用一套），不随距离反复生成。
    ///
    /// **选级规则（可测、可证伪）**：一个体素在屏幕上的**投影尺寸 ≤ 1 px** 时，比它更细的分辨率在画面上不可分。
    /// 像素/弧度 ≈ (屏宽/2) / tan(fov/2)；于是"需要的最小分辨率" `res ≥ pxPerRad × 安全系数 / 距离`。
    /// 取满足 `res` 的**最粗**一级；若连最粗一级都不满足（即已经比 1 px 还细）→ 交给既有的方盒路径（更省）。
    /// </summary>
    public static class SkylineFurnitureLod {
        /// <summary>总开关（默认 **关**，等实测把切换距离标定好再默认开）。关掉即逐位回到 v0.1.62 的二元行为。</summary>
        public static bool Enabled { get; set; }

        /// <summary>安全系数：`res ≥ pxPerRad × 安全系数 / 距离`。取 2 表示"投影到 0.5 px 才允许降一级"。</summary>
        public static float Safety { get; set; } = 1f;

        /// <summary>低于这么多顶点的设计不做 LOD（本来就不贵，分级反而多一次缓存与分支）。</summary>
        public static int MinVerticesToLod { get; set; } = 2000;

        /// <summary>最多降几级（1 = 只做 1/2；2 = 还做 1/4）。</summary>
        public static int MaxLevel { get; set; } = 2;

        /// <summary>
        /// 级别 1 / 级别 2 的分界距离占**渲染半径**的比例（默认 0.34 / 0.67）。
        ///
        /// 分界距离用的**参考分辨率**（默认 28 = 本工程里最大的家具设计分辨率；实测 214 个设计里只有 3 个
        /// 超过 `MinVerticesToLod`，全是 res 28）。用于**区块级**的"该不该重建几何"判断；
        /// 单个实例真正取哪一档，仍用**它自己的分辨率**算（两者同一套公式）。
        /// </summary>
        public static int ReferenceResolution { get; set; } = 28;

        /// <summary>
        /// 第 `level` 级从多远开始"够用"：**这一级的体素投影 ≤ 1 px × 安全系数**。
        /// `LevelDistance(l) = 像素/弧度 × Safety / (参考分辨率 >> l)`。
        ///
        /// **为什么必须是这个规则**（两条实测教训，别再改回去）：
        /// ① 先用"占渲染半径的比例"分段（0.34/0.67）→ **在 60 m 就把一件细棋盘家具降成实心块**，
        ///   而 60 m 处它的花纹远没到亚像素，画面确实变了；
        /// ② **任何 2× 降采样对"周期 2 的棋盘"都会退化成均匀块**（这是混叠，不是实现 bug：
        ///   design 25 的 14³ 降采样实测 **2744/2744 全实心**）——所以"降到 1/2 是否视觉无差异"
        ///   完全取决于"花纹在屏幕上是否已经亚像素"。
        /// `Safety` 默认 **1**（= 1 px）：res 28 的设计 L1 = `998/14 ≈ 71 m`、L2 = `998/7 ≈ 143 m`，
        /// 落在/贴着 128 m 渲染半径内 —— 与"细化到亚像素"一致；取 2 会把 L1 推到 142 m（渲染半径外，永远用不上）。
        /// </summary>
        public static float LevelDistance(int level) {
            if (level <= 0) {
                return 0f;
            }
            int res = Math.Max(ReferenceResolution >> Math.Min(level, 2), 2);
            return PixelsPerRadian() * MathF.Max(Safety, 0.25f) / res;
        }

        /// <summary>按距离给出"该用哪一级"（0..MaxLevel）。仅用于**决定要不要重建区块几何**；
        /// 真正取哪一档几何仍由 `GeometryFor` 按该实例自己的分辨率算（两者口径一致）。</summary>
        public static int LevelForDistance(float distance) {
            if (!Enabled || distance < 1f) {
                return 0;
            }
            for (int l = MaxLevel; l >= 1; l--) {
                if (distance >= LevelDistance(l)) {
                    return l;
                }
            }
            return 0;
        }

        // ---- 统计（判据从这里出） ----
        public static int CachedDesigns { get; private set; }
        public static long Level1Uses { get; private set; }
        public static long Level2Uses { get; private set; }
        public static long FullUses { get; private set; }
        public static long SkippedTooSmall { get; private set; }
        public static long BuildFailures { get; private set; }
        public static double LastBuildMs { get; private set; }

        sealed class LevelSet {
            public int SourceResolution;
            public readonly FurnitureGeometry[] Geometries = new FurnitureGeometry[3];
            public readonly int[] Vertices = new int[3];
            /// <summary>该级降采样后**非空体素**的个数（判据：与源成正比说明降采样没丢东西）。</summary>
            public readonly int[] SolidVoxels = new int[3];
            /// <summary>源设计的非空体素数。</summary>
            public int SourceSolidVoxels;
        }

        static readonly Dictionary<int, LevelSet> m_sets = [];

        public static void ResetStats() {
            Level1Uses = Level2Uses = FullUses = SkippedTooSmall = BuildFailures = 0;
        }

        /// <summary>[v0.1.63] 这个设计有没有可用的分级（顶点数够、分辨率够粗一级）——
        /// `ShouldBoxInstance` 用它决定"要不要把方盒让位给分级"。贵的设计才让位；小设计行为不变。</summary>
        public static bool HasLevels(FurnitureDesign design) {
            return design != null
                && design.Resolution >= FurnitureDesign.MinResolution * 2
                && SkylineFurniture.CountDesignVertices(design) >= MinVerticesToLod;
        }

        /// <summary>
        /// 给一个家具实例选几何。返回 **null** = 用原设计（第 0 级）；否则返回对应的粗级几何。
        /// `level` 输出实际级别（0/1/2）。不做方盒决策 —— 那仍然由 `SkylineRender.ShouldBoxInstance` 负责。
        /// </summary>
        public static FurnitureGeometry GeometryFor(FurnitureDesign design, SubsystemTerrain subsystemTerrain,
                                                   float distance, out int level) {
            level = 0;
            if (!Enabled || design == null || subsystemTerrain == null || distance < 1f) {
                return null;
            }
            int fullVertices = SkylineFurniture.CountDesignVertices(design);
            if (fullVertices < MinVerticesToLod) {
                SkippedTooSmall++;
                return null;
            }
            LevelSet set = GetSet(design, subsystemTerrain);
            // 从最粗往下挑第一个"距离够远、可以用它的"级别（0 = 原设计）
            for (int l = MaxLevel; l >= 1; l--) {
                if (set.Geometries[l] == null) {
                    continue;
                }
                if (distance >= LevelDistance(l)) {
                    level = l;
                    if (l == 1) {
                        Level1Uses++;
                    }
                    else {
                        Level2Uses++;
                    }
                    return set.Geometries[l];
                }
            }
            FullUses++;
            return null;
        }

        /// <summary>
        /// 像素/弧度 —— **必须与 `BasePerspectiveCamera` 同式**。
        ///
        /// [v0.1.99 修正] 旧实现写死 `fovY = 1.0472 rad`（**60°**）+ 写死 `16/9` 长宽比，
        /// 而那台相机用的是 `80f * SettingsManager.ViewAngle`（竖直 80°）。
        /// 在 2048×1113 的窗口上旧式给出 `pixelsPerRadian ≈ 997.7`，
        /// 正解是 `556.5 / tan(40°) ≈ 663.2` —— **高估 50%**，
        /// 后果是"家具该降级却不降级"（`LevelDistance = pxPerRad/res`：L1 71.3 m vs 47.4 m、
        /// L2 142.5 m vs 94.7 m），白花几何。用户口径要求视场角**固定 100%（80°）**，
        /// 所以这里直接按**竖直视场**算，不再经过长宽比（针孔相机两个轴的焦距像素数相同：
        /// `(W/2)/tan(fovX/2) ≡ (H/2)/tan(fovY/2)`，用竖直更不容易写错）。
        /// </summary>
        static float PixelsPerRadian() {
            float height = 720f;
            try {
                height = Math.Max(Window.Size.Y, 200f);
            }
            catch {
                // ignored（无窗口环境）
            }
            float viewAngle = Math.Clamp(SettingsManager.ViewAngle, 0.25f, 2f);
            float fovY = MathUtils.DegToRad(80f * viewAngle);
            return height * 0.5f / MathF.Tan(fovY * 0.5f);
        }

        static LevelSet GetSet(FurnitureDesign design, SubsystemTerrain subsystemTerrain) {
            if (m_sets.TryGetValue(design.Index, out LevelSet cached)
                && cached.SourceResolution == design.Resolution) {
                return cached;                                // 槽位会被回收复用 → 用分辨率做校验
            }
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            LevelSet set = new() { SourceResolution = design.Resolution };
            int[] values = design.m_values;
            int res = design.Resolution;
            if (values != null && res >= FurnitureDesign.MinResolution * 2) {
                set.SolidVoxels[0] = CountSolid(values);
                set.SourceSolidVoxels = set.SolidVoxels[0];
                for (int l = 1; l <= MaxLevel; l++) {
                    int nr = res >> l;
                    if (nr < FurnitureDesign.MinResolution * 2) {
                        break;
                    }
                    try {
                        int[] coarse = Downsample(values, res, 1 << l);
                        FurnitureDesign d = new(subsystemTerrain);
                        d.SetValues(nr, coarse);
                        d.CreateGeometry();
                        set.Geometries[l] = d.Geometry;
                        set.Vertices[l] = CountGeometry(d.Geometry);
                        set.SolidVoxels[l] = CountSolid(coarse);
                    }
                    catch (Exception e) {
                        BuildFailures++;
                        Log.Warning($"SkylineFurnitureLod level {l} for design {design.Index}: {e.Message}");
                        break;
                    }
                }
            }
            m_sets[design.Index] = set;
            CachedDesigns = m_sets.Count;
            LastBuildMs = watch.Elapsed.TotalMilliseconds;
            return set;
        }

        /// <summary>
        /// 体素降采样：每 `div³` 个格子合成一格，**多数表决**决定这一格是不是实心
        /// （实心数 ≥ 一半才算实心），实心时材质取这些实心格里**出现次数最多的方块值**（众数）。
        ///
        /// **为什么必须"多数"而不是"只要有非空就填实"**（第一版就是这么写的，实测踩到）：
        /// design 25 是密度约 50% 的设计（res 28 里有 10,976 个实心体素），
        /// "有非空就填"会让 14³ 的**每一格**都变成实心（`level1SolidVoxels = 2744 = 14³`），
        /// 于是生成的是一个**实心立方体**（只剩外壳 24 个顶点）—— 分辨率降了，但**形状全没了**，
        /// 跟"视觉无差异"正好相反。多数表决能保住"薄板/格栅/柱子"这类结构（恰半数的板会保留）。
        /// **索引约定必须是设计自己的那一套：`x + y*res + z*res²`** ——
        /// 依据 `FurnitureDesign.CreateTorchPoints()`：`BoundingBox(new Vector3(k, j, i)/Resolution, ...)`，
        /// 而取值处是 `m_values[k + j*Resolution + i*Resolution*Resolution]` → **x=k, y=j, z=i**。
        /// （第一版我按 `TerrainChunk` 的 `x + z*R + y*R²` 写，结果把设计**打散**成 24 个顶点 —— 实测踩到，记在这里。）
        /// </summary>
        static int[] Downsample(int[] src, int res, int div) {
            int nr = res / div;
            int[] dst = new int[nr * nr * nr];
            for (int y = 0; y < nr; y++) {
                for (int z = 0; z < nr; z++) {
                    for (int x = 0; x < nr; x++) {
                        int best = 0;
                        int bestCount = 0;
                        int solidCount = 0;
                        for (int dy = 0; dy < div; dy++) {
                            int py = y * div + dy;
                            for (int dz = 0; dz < div; dz++) {
                                int pz = z * div + dz;
                                int rowBase = py * res + pz * res * res;
                                for (int dx = 0; dx < div; dx++) {
                                    int v = src[(x * div + dx) + rowBase];
                                    if (v == 0 || Terrain.ExtractContents(v) == 0) {
                                        continue;
                                    }
                                    solidCount++;
                                    int count = 0;
                                    // 数同值出现次数（div³ ≤ 64，朴素计数足够）
                                    for (int ey = 0; ey < div; ey++) {
                                        for (int ez = 0; ez < div; ez++) {
                                            int qz = z * div + ez;
                                            int qBase = (y * div + ey) * res + qz * res * res;
                                            for (int ex = 0; ex < div; ex++) {
                                                if (src[(x * div + ex) + qBase] == v) {
                                                    count++;
                                                }
                                            }
                                        }
                                    }
                                    if (count > bestCount) {
                                        bestCount = count;
                                        best = v;
                                    }
                                }
                            }
                        }
                        // 多数表决：实心数 ≥ 一半才保留这一格（保住形状与密度，而不是糊成一个实心块）
                        bool solid = solidCount * 2 >= div * div * div;
                        dst[x + y * nr + z * nr * nr] = solid ? best : 0;
                    }
                }
            }
            return dst;
        }

        static int CountGeometry(FurnitureGeometry geometry) {
            if (geometry == null) {
                return 0;
            }
            int count = 0;
            for (int i = 0; i < 6; i++) {
                count += geometry.SubsetOpaqueByFace[i]?.Vertices.Count ?? 0;
                count += geometry.SubsetAlphaTestByFace[i]?.Vertices.Count ?? 0;
                count += geometry.SubsetTransparentByFace[i]?.Vertices.Count ?? 0;
            }
            return count;
        }

        /// <summary>[v0.1.63] 一个体素数组里的非空体素数（诊断用）。</summary>
        static int CountSolid(int[] values) {
            if (values == null) {
                return 0;
            }
            int n = 0;
            for (int i = 0; i < values.Length; i++) {
                if (Terrain.ExtractContents(values[i]) != 0) {
                    n++;
                }
            }
            return n;
        }

        public static JsonObject Describe() {
            JsonArray designs = [];
            foreach (KeyValuePair<int, LevelSet> kv in m_sets) {
                designs.Add(new JsonObject {
                    ["design"] = kv.Key,
                    ["sourceResolution"] = kv.Value.SourceResolution,
                    ["sourceSolidVoxels"] = kv.Value.SourceSolidVoxels,
                    ["level1Vertices"] = kv.Value.Vertices[1],
                    ["level1SolidVoxels"] = kv.Value.SolidVoxels[1],
                    ["level2Vertices"] = kv.Value.Vertices[2],
                    ["level2SolidVoxels"] = kv.Value.SolidVoxels[2]
                });
                if (designs.Count >= 24) {
                    break;
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["safety"] = Safety,
                ["minVerticesToLod"] = MinVerticesToLod,
                ["maxLevel"] = MaxLevel,
                ["referenceResolution"] = ReferenceResolution,
                ["pixelsPerRadian"] = Math.Round(PixelsPerRadian(), 1),
                ["visualRadiusMetres"] = Math.Round(SkylineRender.VisualSphereRadius, 1),
                ["level1DistanceMetres"] = Math.Round(LevelDistance(1), 1),
                ["level2DistanceMetres"] = Math.Round(LevelDistance(2), 1),
                ["cachedDesigns"] = CachedDesigns,
                ["fullUses"] = FullUses,
                ["level1Uses"] = Level1Uses,
                ["level2Uses"] = Level2Uses,
                ["skippedTooSmall"] = SkippedTooSmall,
                ["buildFailures"] = BuildFailures,
                ["lastBuildMs"] = Math.Round(LastBuildMs, 3),
                ["designs"] = designs,
                ["note"] = "每个 design index（= 家具 ID）一组逐级降分辨率的几何（1/2、1/4），按距离选级；"
                    + "选级规则：`需要的分辨率 res ≥ 像素/弧度 × 安全系数 / 距离`（投影 ≤ 1 px 就允许降级）"
            };
        }
    }

    /// <summary>桥：`skyline.FurnitureLod*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>[v0.1.63] 家具逐级降分辨率 LOD 开关（里程碑 1.5）。</summary>
        public static string FurnitureLod(bool enabled) {
            SkylineFurnitureLod.Enabled = enabled;
            JsonObject result = SkylineFurnitureLod.Describe();
            // 家具几何是**烘进区块网格**的：**两个方向都必须重建"烘过家具"的区块**，否则画面不变。
            //   * **开**：旧实现不重建 ⇒ `LodLevel` 全是 0、一个都挑不到，**"打开开关当下没有任何变化"**
            //     （本轮标定实测到的真缺陷，见 notes/223）；
            //   * **关**：旧实现只挑 `LodLevel != 0` 的区块 —— 但"开着开关、恰好落在 0 级"的区块
            //     （比如 60 m 处因为 LOD 开着所以**不让位给方盒**）它的 `LodLevel` 也是 0，
            //     于是关掉后**方盒让位状态残留**，A/B 量到的不是"关掉的效果"（60 m 实测 115 px 假差）。
            // ⇒ 两个方向统一用 `InvalidateFurnitureChunks(true)`（只碰 `InstanceCount > 0` 的区块）。
            result["rebakedChunks"] = SkylineRender.InvalidateFurnitureChunks(true);
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.125] **只重建给定方框内的家具区块**（标定/取证用）：`FurnitureLod` 的全量重建会牵动
        /// 整个视距内的区块，标定时没必要；而且"重建范围"本身要被写进证据里。
        /// </summary>
        public static string FurnitureLodRebakeRegion(int x1, int y1, int z1, int x2, int y2, int z2) {
            JsonObject result = SkylineFurnitureLod.Describe();
            result["rebakedChunks"] = SkylineRender.InvalidateFurnitureChunks(
                true, Math.Min(x1, x2), Math.Min(z1, z2), Math.Max(x1, x2), Math.Max(z1, z2));
            return result.ToJsonString();
        }

        /// <summary>[v0.1.63] 强制把所有非 0 级别的家具区块重建一次几何（取证用；返回重建数）。</summary>
        public static string FurnitureLodRebake() {
            JsonObject result = SkylineFurnitureLod.Describe();
            result["rebakedChunks"] = SkylineRender.InvalidateFurnitureLodChunks();
            return result.ToJsonString();
        }

        /// <summary>[v0.1.63] 家具 LOD 参数：安全系数 / 触发的最小顶点数 / 最大级数（1~2）。</summary>
        public static string FurnitureLodParams(float safety, int minVertices, int maxLevel) {
            SkylineFurnitureLod.Safety = Math.Clamp(safety, 1f, 16f);
            SkylineFurnitureLod.MinVerticesToLod = Math.Max(0, minVertices);
            SkylineFurnitureLod.MaxLevel = Math.Clamp(maxLevel, 1, 2);
            return SkylineFurnitureLod.Describe().ToJsonString();
        }

        /// <summary>[v0.1.63] 家具 LOD 状态与逐设计顶点数（判据）。</summary>
        public static string FurnitureLodInfo() => SkylineFurnitureLod.Describe().ToJsonString();

        public static string FurnitureLodResetStats() {
            SkylineFurnitureLod.ResetStats();
            return SkylineFurnitureLod.Describe().ToJsonString();
        }
    }
}
