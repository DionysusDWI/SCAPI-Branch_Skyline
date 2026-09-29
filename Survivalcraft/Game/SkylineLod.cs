using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <summary>
        /// 精细层（8 m）覆盖到"视距 × 本系数"为止，之后交给 16 m 粗层。
        ///
        /// **[v0.1.62] 2.0 → 4.0**：这是"远景 LOD 太平"真正能改的那一根杠杆（实测，确定性 `GBufferCapture`，
        /// 强制重建后逐档对比）：
        ///
        /// | 系数 | 8 m 层覆盖到 | 覆盖率 | 平均亮度 | 索引总数 |
        /// |---|---|---|---|---|
        /// | 2.0（旧） | 256 m | 16,833 | 44.40 | 30,438 |
        /// | **4.0（新）** | **512 m** | **17,475** | **46.90** | 51,642 |
        /// | 6.0 | 768 m | 17,475 | 47.07 | 69,684 |
        /// | 8.0 | 1024 m | 17,475 | 47.09 | 88,332 |
        ///
        /// 覆盖率与亮度在 **4.0 就饱和**（再往上只涨几何不涨画面）→ 取 4.0：**几何 ×1.7 换 +3.8% 覆盖率 / +2.5 亮度**；
        /// 帧率实测无差异（都在 30 附近）。旧的 2.0 会让 256 m 以外全是 16 m 平台，正是"太平"的来源。
        /// </summary>
        public static float FineRangeFactor { get; set; } = 4.0f;

        // ===== v0.1.62：LOD 采样"所有裸露在外的方块"（里程碑 1.6 的另一半）=====
        /// <summary>
        /// [v0.1.62] 采集时是否**额外**找"第二层表面"（裸露但不是列顶的那一层）。
        ///
        /// **默认关** —— 这是实测出来的结论，不是省事：实现完成并跑过确定性 A/B
        /// （`GBufferCapture` + `LodSecondSurface(false/true)`）后测到：
        /// 多画 385 个四边形 / 3,210 个索引，而 **G-buffer 覆盖率一模一样（16,833）**、亮度只差 **0.06/255**。
        /// 原因是结构性的：`Height2` 按定义**低于** `Height`（`SecondMinDrop ≥ 2`），
        /// 而每格的主表面是一整块 16 m 的四边形 —— 下层那张面**必然被上层完全盖住**。
        /// 也就是说：**在"每格一个高度"的 LOD 里，"采样所有裸露表面"不可能看得见**；
        /// 真正能看见的杠杆是**格子精细度**（见 `FineRangeFactor` 的实测）。
        /// 开关与数据保留：等 LOD 换成更细的格子（或每格多高度场）之后，这个数据立刻就能用上。
        /// </summary>
        public static bool SecondSurfaceEnabled { get; set; }
        /// <summary>[v0.1.62] 第二层表面至少要在主表面下方这么多格（否则两层太近，画出来是重复的面）。</summary>
        public static int SecondMinDrop { get; set; } = 2;
        /// <summary>[v0.1.62] 从主表面往下最多找这么多格（够覆盖树冠下方的地面；再深就不找了）。</summary>
        public static int SecondSearchDepth { get; set; } = 40;
        /// <summary>[v0.1.62] 第二层表面与主表面之间**至少**要隔这么多格空气（= 中间是真空腔，才叫"第二层"）。
        /// 1 会把"台阶/斜坡"也算成第二层，2 才对应"树冠之下的地面"。</summary>
        public static int SecondGap { get; set; } = 2;
        /// <summary>[v0.1.62] 有第二层表面的**单元数**（普查，判据）。</summary>
        public static int CellsWithSecond { get; private set; }
        /// <summary>[v0.1.62] 第二层顶面四边形数（上一次重建）。</summary>
        public static int SecondQuads { get; private set; }
        /// <summary>[v0.1.62] 第二层裙边四边形数（上一次重建）。</summary>
        public static int SecondWallQuads { get; private set; }

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
            // [v0.1.62] **第二层表面**：裸露但**不是列顶**的那层表面（树冠底面之下的地面、檐下的墙顶、
            // 雪层台阶…）。用户口径 1.6："LOD 不应当只采样最上层方块，而是采样所有裸露在外的方块"。
            // 没有第二层时 `HasSecond = false`，网格与 v0.1.61 逐位一致。
            public short Height2;
            public ushort Value2;
            public byte Light2;
            public bool HasSecond;
            /// <summary>
            /// [v0.1.100] 里程碑 2.3 第三项：**地表上方空气格的光照**（单元内取最大）。
            ///
            /// 为什么必须单独采：Survivalcraft 里**实心方块自己的 light 位通常是 0**，光活在
            /// **相邻的空气格**里（v0.1.82 已经吃过一次这个亏）。所以只采"顶面方块"的 light
            /// ⇒ LOD 里**永远看不到固定光源（火把/灯）造成的亮斑**。
            /// 这里在采集时额外读每列 `top+1` 那一格的光，单元内取**最大**（亮斑就该是"最大"而不是中位），
            /// 于是"火把照亮一片地"在 LOD 上会表现为一块比周围亮的斑块。
            /// </summary>
            public byte LightAir;
        }

        static readonly Dictionary<long, Cell> m_cells = [];
        static readonly Dictionary<long, Cell> m_cellsFine = [];        // 8 m 精细层
        static readonly Dictionary<long, Cell> m_cellsNear = [];        // 4 m 近环细层（交接带专用）

        // ===== [v0.1.98] 里程碑 2.2：**加载距离之外统一到 32³ 一档** =====
        /// <summary>
        /// 用户口径（本轮 goal 2.2）："目前 LOD 分辨率分级在视觉上过于明显 … **先将所有区块加载距离之外的
        /// 区块 LOD 都固定为 32³**，后续再根据性能问题和双模型核对放大视觉分辨来确定不同 LOD 分级的转换边界"。
        ///
        /// 为什么要有这一档：现在视距外是**三档拼接**（近环 4 m / 精细 8 m / 粗 16 m），
        /// 三档的交界就是"分级过于明显"的来源。本开关打开时，**只画一档**（32 m 单元），
        /// 于是"加载距离之外"在视觉上只有一个分辨率，才谈得上用放大截图去**定边界**。
        ///
        /// 32 m 单元 = **4 个 16 m 粗单元**（一个区块 = 一个 16 m 单元 ⇒ 2×2 区块），
        /// 高度/材质**按 4 个子单元取中位**（与单格口径一致，见 `MedianInto`），
        /// 第二层表面这一档不参与（它默认就是关的，且 32 m 格上"下层必被上层盖住"，见 `SecondSurfaceEnabled` 的实测）。
        /// 关掉 = 逐位回到 v0.1.97 的三档行为（A/B 用）。
        /// </summary>
        public static bool UniformBeyondLoaded { get; set; } = true;

        static float m_lastSkipRadius = 200f;
        /// <summary>[v0.1.110] 最近一次重建时 LOD 的**内边界半径**（米）—— 抖动淡出用它当起点。</summary>
        public static float LastSkipRadius => m_lastSkipRadius;

        /// <summary>[v0.1.110] **DH 的 `overdrawPrevention` 同规格开关**（0..1，默认 **0 = 保持现状**）。
        ///
        /// DH 的语义（源码 `core/util/RenderUtil.java`）：近裁剪面 = `overdrawPrevention × 原版视距`，
        /// 默认 **0.4** —— 也就是 **LOD 从视距的 40% 就开始画**，再用 `ditherDhFade` 的抖动淡出
        /// 把"相机附近"那一带化掉。好处：**原版区块缺失/未加载时不会露洞**（LOD 已在下面兜住）。
        ///
        /// 本分支的差别（必须如实）：我们的 LOD 在那个带里仍是 **32³ 粗档**（`UniformBeyondLoaded` 默认开），
        /// 粗档高度是"4 个子单元的中位"，在起伏地形上可能**高于真实地面** ⇒ 会"穿地"。
        /// 所以本开关默认 0，且**必须与 `SkylineLodLook.DitherFadeStartMetres` 一起用**（抖动淡出掩护重叠带）。
        /// </summary>
        public static float OverdrawPrevention { get; set; }
        /// <summary>[v0.1.98] 统一档的单元边长 = CellShift + 本值（默认 4+1 = 5 ⇒ **32 m**）。</summary>
        public static int UniformExtraShift { get; set; } = 1;

        // ===== [v0.1.130] 里程碑 2.6：**外围区块合并阶梯**（Distant Horizons 的"越远越粗"）=====
        /// <summary>
        /// 用户口径（目标 2.6）："随着距离渐远，将 2³、4³、8³ 及以上个区块合并成一个 LOD 块渲染，
        /// 每个 LOD 块保持 32³ 分辨率，合并后大 LOD 精度就自然下降到原来 1/2、1/4、1/8 及以下，
        /// 这在构建外围 LOD 的时候较为高效。"
        ///
        /// **关（默认）** = v0.1.98 起的"加载距离之外只画一档 32 m"（已验收的出厂行为，逐位不变）；
        /// **开** = 按 DH 的档位边界把 LOD 分成 **32 / 64 / 128 m** 三档：
        /// 合并 2³ / 4³ / 8³ 个 16 m 单元，每档仍然只出**一张 32³ 采样**的表面网格
        /// （合并后等效精度 1 m / 2 m / 4 m，正好对应 DH 的 detail level 1 / 2 / 3）。
        /// 外围因此可以在同样的单元预算下把绘制半径推得更远。
        /// </summary>
        public static bool MergeLadderEnabled { get; set; }

        /// <summary>
        /// [v0.1.130] 合并阶梯的两条边界（米）直接取 DH 的 `unit × 16 × base^level`（MEDIUM ⇒ 384 / 768）。
        ///
        /// 取的是 `level = 1` 与 `level = 2` 的**起点**：DH 的 `DhLevelForDistance` 在 192 / 384 / 768 m
        /// 分别跨到 1 / 2 / 3 级，而我们的三档把 `clamp(level, 1, 3)` 当作档号 ⇒
        /// 第一档 = level 0~1（小于 384 m）、第二档 = level 2、第三档 = level 3 及其以上。
        /// </summary>
        public static (float B1, float B2) MergeBoundaries() {
            (float quadraticBase, int unit) = SkylineCubeShellStore.DhQualityParams();
            float unitMetres = unit * SkylineCubeShellStore.McChunkWidthBlocks;   // 方块数 = 米数
            return (unitMetres * MathF.Pow(quadraticBase, 1), unitMetres * MathF.Pow(quadraticBase, 2));
        }

        /// <summary>
        /// [v0.1.132] **距离 → 合并档号**（1 = 32 m / 2 = 64 m / 3 = 128 m）。
        ///
        /// ⚠️ 这里踩过一次坑，写清楚：DH 的 `detailLevel = floor(log_base(d / (unit×16)))` 在
        /// `d = 384 m`（MEDIUM）处给出的是 **1**、在 `768 m` 处给出 **2** ⇒ 与"第一档 &lt; 384、
        /// 第二档 384~768、第三档 ≥ 768"相比**整整差一档**（v0.1.130 就是直接 clamp(level,1,3)，
        /// 结果第一档吃掉了 [0,768)、第三档直到 1536 m 才开始 ⇒ 外环整圈没被画，
        /// 同半径下画出来的单元从 112 掉到 23，是 v0.1.132 的放大截图核对抓出来的）。
        /// 正确的换算是 `tier = clamp(level + 1, 1, 3)`：level 0 → 32 m、level 1 → 64 m、level ≥ 2 → 128 m。
        /// </summary>
        public static int TierForDistance(float distanceMetres) =>
            Math.Clamp(SkylineCubeShellStore.DhLevelForDistance(distanceMetres) + 1, 1, 3);

        static readonly Dictionary<long, Cell> m_cellsM2 = [];   // 64 m 档（合并 4³）
        static readonly Dictionary<long, Cell> m_cellsM3 = [];   // 128 m 档（合并 8³）
        static int m_ladderTier1Cells, m_ladderTier2Cells, m_ladderTier3Cells;

        /// <summary>
        /// [v0.1.100] 里程碑 2.3 第三项：**固定光源的亮度斑块**（默认开）。
        /// 开 = 顶面基色取 `max(实心方块自身 light, **上方空气格 light**)`（单元内取最大）；
        /// 关 = 逐位回到 v0.1.99（只读实心方块自身 light，于是 LOD 里看不到火光造成的亮斑）。
        /// 依据：Survivalcraft 里实心方块自身的 light 位通常是 0、光活在相邻空气格（v0.1.82 的教训）。
        /// </summary>
        public static bool LodAirLightPatch { get; set; } = true;
        static int m_airLightBrightenedCells;
        public static int AirLightBrightenedCells => m_airLightBrightenedCells;

        /// <summary>
        /// [v0.1.100] **只读探针**：回读某格 LOD 单元的 `Light`（实心方块自身光照）、`LightAir`（上方空气格光照）
        /// 与"实际用于顶面基色的生效值" —— 用来**零噪声地**证明"固定光源/天空光的亮度斑块"真的接上了
        /// （比拿画面差分判方向可靠：本轮实测场景自身抖动可达 19 万像素）。
        /// 统一档（`UniformBeyondLoaded`）时查 32 m 表，否则查 16 m 粗表。参数是世界坐标格。
        /// </summary>
        public static string AirLightProbe(int worldX, int worldZ) {
            int shift = CellShift + (UniformBeyondLoaded ? Math.Clamp(UniformExtraShift, 0, 3) : 0);
            long key = Key(worldX >> shift, worldZ >> shift);
            Dictionary<long, Cell> dict = UniformBeyondLoaded ? m_cells32 : m_cells;
            JsonObject o = new();
            if (!dict.TryGetValue(key, out Cell c)) {
                o["ok"] = false;
                o["err"] = "cell not found";
                o["cellShift"] = shift;
                return o.ToJsonString();
            }
            int eff = LodAirLightPatch ? Math.Max(c.Light, c.LightAir) : c.Light;
            o["ok"] = true;
            o["cellShift"] = shift;
            o["cell"] = new JsonArray(worldX >> shift, worldZ >> shift);
            o["light"] = (int)c.Light;
            o["lightAir"] = (int)c.LightAir;
            o["effective"] = eff;
            o["brightened"] = c.LightAir > c.Light;
            o["patchEnabled"] = LodAirLightPatch;
            return o.ToJsonString();
        }

        /// <summary>
        /// [v0.1.98] 里程碑 2.3 的**可断言证据**："按最小体素步进阴影"与"按整块单元步进阴影"
        /// 到底有没有差别 —— 有差别的格子数与差值总和。为什么需要它：这条要求
        /// （"分辨率是该 LOD 的最小体素，而不是整个 LOD 区块作为一个整体参与"）如果只是改了代码，
        /// 在画面上可能看不出来；量化之后才有判据（差值恒 0 = 改了等于没改）。
        /// </summary>
        public static long MinVoxelShadowDiffCells { get; private set; }
        public static long MinVoxelShadowDiffSum255 { get; private set; }
        public static long MinVoxelShadowCompared { get; private set; }

        static readonly Dictionary<long, Cell> m_cells32 = [];          // 32 m 统一档
        // 分组用的临时表（值元组，**不分配数组**；每次重建前 Clear）
        static readonly Dictionary<long, (int n, long p0, long p1, long p2, long p3, int air)> m_groupScratch = [];
        static int m_harvestCursor;
        static bool m_dirty = true;
        static double m_nextRebuild;
        static double m_nextCloudShadowRebuild;   // [v0.1.99] 云影周期性重建的时间点

        /// <summary>
        /// [v0.1.51] 让 LOD 立刻重建一次网格 —— 壳仓新增/淘汰壳之后调用（壳一多，LOD 要让位的那片就变了，
        /// 不立刻重建的话最多要等 `MeshRebuildSeconds`（默认 0.5 s）才生效）。
        /// </summary>
        public static void RequestRebuild() {
            m_dirty = true;
            m_nextRebuild = 0;
        }
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
        // [v0.1.60] 三套网格各自的**精确顶点数**（原来只有索引数，顶点字节只能靠 indexCount/3 估）。
        // 顶点属性格式（SkylineLodVertex，28 B）与老格式（TerrainVertex，20 B）的常驻字节就靠这两个数算。
        static int m_vertexCount, m_vertexCountFine, m_vertexCountNear;

        // ===== [v0.1.74] 网格重建的**复用缓冲**（里程碑 2.2：不要"只增不减"）=====
        //
        // 为什么必须有：重建一次网格要几千~几万项 —— `keys`/`tops`/`walls` 三个 List、
        // 顶点数组（TerrainVertex 20 B 或 SkylineLodVertex 28 B）、索引数组（short/int），
        // 加起来一次就是**几个 MB**，全都 **>85 KB ⇒ 落在大对象堆（LOH）上**。
        // 而**LOH 默认不压缩**（`LargeObjectHeapCompactionMode.Default`），于是"边走边重建"
        // 会把 LOH 的高水位一路抬上去：实测走动时 **LOH 70 → 299 MiB**，而且强制 GC 也收不回来
        // （收回来的是垃圾，收不回来的是**高水位/空闲段**）。这解释了 v0.1.70~v0.1.73 一直没归因掉的
        // "移动时存活堆增长"（`notes/152 §6`），也是"LOD 关掉后斜率从 +420 掉到 +78 MiB/960 m"的那 340 MiB。
        //
        // 修法：把这三类缓冲做成**静态复用**（每次 Clear/EnsureCapacity），重建不再产生 MB 级垃圾。
        static readonly List<long> m_keysScratch = [];
        static readonly List<(int x0, int z0, float yHigh, float yLow, int side, int value)> m_wallsScratch = [];
        static readonly List<(long Key, int Height, int Value, byte Light)> m_topsScratch = [];
        static TerrainVertex[] m_vertexScratch;
        static SkylineLodVertex[] m_attrVertexScratch;
        static short[] m_indexScratch;
        static int[] m_index32Scratch;

        static void EnsureCapacity<T>(ref T[] array, int count) {
            if (array != null && array.Length >= count) {
                return;
            }
            int capacity = array == null ? Math.Max(count, 1024) : Math.Max(count, array.Length * 2);
            array = new T[capacity];
        }

        // side 编号与侧壁代码一致：0=+Z、1=-Z、2=+X、3=-X
        static readonly int[] s_sideDx = [0, 0, 1, -1];
        static readonly int[] s_sideDz = [1, -1, 0, 0];

        static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

        // ===== [v0.1.46] 交接带"主动铺满" =====

        static Vector3 m_lastNearMarkCenter;
        static bool m_hasNearMarkCenter;

        // ===== [v0.1.47] 卸载前"最后一采"（4.2：玩家进入→离开加载范围都要刷新一次 LOD）=====

        static TerrainChunk m_forceHarvestChunk;
        static ResampleReason m_forceHarvestReason;
        static long m_refreshedOnUnload;
        /// <summary>因"区块即将离开加载范围"而采样的次数（诊断）。</summary>
        public static long RefreshedOnUnload => m_refreshedOnUnload;

        /// <summary>
        /// [v0.1.48] LOD 单元的**材质贴图槽**（4.4：门/栅栏等非完整方块按材质表现，而不是按碰撞箱/建模）：
        ///   * 完整方块（`CubeBlock`，含树叶这类 alpha-tested 立方体）→ **顶面**（保持原来的地貌观感）；
        ///   * 非完整方块（门/栅栏/栅栏门/植物/火把…）→ **侧面**（那才是它渲染用的材质面）；
        ///   * 取槽失败时回退顶面。
        /// 关掉 `skyline.LodMaterialAware` 即回到 v0.1.47 的"一律顶面"。
        /// </summary>
        public static int MaterialTextureSlot(Block block, int value) {
            try {
                if (block is CubeBlock) {
                    return block.GetFaceTextureSlot(4, value);
                }
                int side = block.GetFaceTextureSlot(0, value);
                return side >= 0 ? side : block.GetFaceTextureSlot(4, value);
            }
            catch {
                try {
                    return block.GetFaceTextureSlot(4, value);
                }
                catch {
                    return 0;
                }
            }
        }

        /// <summary>[v0.1.48] 材质选择诊断：给一个 contents，报它是不是完整方块、顶面/侧面槽、
        /// 以及当前开关下**实际会选**哪一个（4.4 的判据）。</summary>
        public static string MaterialProbe(int contents) {
            JsonObject result = new();
            try {
                if (contents < 0 || contents >= BlocksManager.Blocks.Length || BlocksManager.Blocks[contents] == null) {
                    result["ok"] = false;
                    result["err"] = $"bad contents {contents}";
                    return result.ToJsonString();
                }
                Block block = BlocksManager.Blocks[contents];
                int value = Terrain.MakeBlockValue(contents, 15, 0);
                result["ok"] = true;
                result["contents"] = contents;
                result["block"] = block.GetType().Name;
                result["isCubeBlock"] = block is CubeBlock;
                result["slotCount"] = block.GetTextureSlotCount(value);
                foreach (int face in new[] { 0, 4 }) {
                    try {
                        result[face == 4 ? "topSlot" : "sideSlot"] = block.GetFaceTextureSlot(face, value);
                    }
                    catch (Exception e) {
                        result[face == 4 ? "topSlot" : "sideSlot"] = "err:" + e.GetType().Name;
                    }
                }
                result["chosenSlot"] = MaterialTextureSlot(block, value);
                result["materialAware"] = SkylineRuntime.LodMaterialAware;
                result["note"] = "完整方块取顶面；非完整方块取侧面（= 它的材质面）";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>[v0.1.48] **4.4 全方块材质审计**：扫一遍所有 contents，统计
        /// 非完整方块（非 `CubeBlock`）里"顶面槽 ≠ 侧面槽"的个数与样例、以及槽位异常（&lt;=0）的个数。
        /// 这是"非完整方块在 LOD 里该用哪个材质槽"的**数据依据**（不是拍脑袋）。</summary>
        public static string MaterialAudit() {
            JsonObject result = new();
            try {
                int total = 0, cubeBlocks = 0, nonCubeBlocks = 0, slotMismatch = 0, badSlot = 0;
                JsonArray examples = [];
                JsonArray badExamples = [];
                for (int contents = 1; contents < BlocksManager.Blocks.Length; contents++) {
                    Block block = BlocksManager.Blocks[contents];
                    if (block == null || block is AirBlock) {
                        continue;
                    }
                    total++;
                    bool cube = block is CubeBlock;
                    if (cube) {
                        cubeBlocks++;
                        continue;
                    }
                    nonCubeBlocks++;
                    int value = Terrain.MakeBlockValue(contents, 15, 0);
                    int top, side;
                    try {
                        top = block.GetFaceTextureSlot(4, value);
                        side = block.GetFaceTextureSlot(0, value);
                    }
                    catch {
                        badSlot++;
                        continue;
                    }
                    if (top <= 0 || side <= 0) {
                        badSlot++;
                        if (badExamples.Count < 12) {
                            badExamples.Add(new JsonObject {
                                ["contents"] = contents,
                                ["block"] = block.GetType().Name,
                                ["topSlot"] = top,
                                ["sideSlot"] = side
                            });
                        }
                    }
                    if (top != side) {
                        slotMismatch++;
                        if (examples.Count < 12) {
                            examples.Add(new JsonObject {
                                ["contents"] = contents,
                                ["block"] = block.GetType().Name,
                                ["topSlot"] = top,
                                ["sideSlot"] = side
                            });
                        }
                    }
                }
                result["ok"] = true;
                result["totalBlocks"] = total;
                result["cubeBlocks"] = cubeBlocks;
                result["nonCubeBlocks"] = nonCubeBlocks;
                result["nonCubeTopDiffersFromSide"] = slotMismatch;
                result["badSlotCount"] = badSlot;
                result["examples"] = examples;
                result["badExamples"] = badExamples;
                result["materialAware"] = SkylineRuntime.LodMaterialAware;
                result["note"] = "非完整方块若顶面槽≠侧面槽，则 LOD 的材质选择会真的改变观感；相等则规则等价";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.47] 区块**即将被卸载**（离开加载范围）时先把它采进 LOD —— 4.2 的要求：
        /// "让玩家进入→离开区块加载范围的过程中刷新一次 LOD 区块状态"，避免"改完走远，LOD 还是旧的"。
        /// 调用点：`TerrainUpdater.AllocateAndFreeChunks`（**主线程**，与 LOD 的采集/重建同线程，无并发风险）。
        /// </summary>
        public static void NotifyChunkUnloading(TerrainChunk chunk) {
            if (!Enabled || chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                return;
            }
            try {
                m_forceHarvestChunk = chunk;
                m_forceHarvestReason = ResampleReason.Unloading;
                Harvest();
                m_refreshedOnUnload++;
                // [v0.1.51] 4.3 第三步的壳采集**不在这里**：卸载是逐个 Free 的，
                // 轮到本区块时兄弟区块可能已经没了 → 采不到完整的 32³ 立方体。
                // 改为在 `TerrainUpdater.AllocateAndFreeChunks` 里做一次"预扫"
                // （见那里的 `SkylineCubeShellStore.OnChunksLeavingRange`）。
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
            finally {
                m_forceHarvestChunk = null;
            }
        }
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
                // [v0.1.51] 壳的生产路径：按预算采集 + 建模（采集在卸载钩子里排队）
                SkylineCubeShellStore.Tick();
                // [v0.1.60] 手动生成 LOD：分帧推进任务表（默认没有任务时是空操作）
                SkylineLodManualBuild.Tick();
                // [v0.1.73] 里程碑 2.2 收官：区域仓每 0.5 s 做一次"按需回读 + 超出半径的写盘移除"
                RegionStoreTick();
                // [v0.1.75] 采样戳按距离裁剪（`m_stamps` 是"按走过的地方"增长的表）——
                // 它在 `SkylineLodRefresh.cs` 里，但那个文件也是 `partial class SkylineLod`，所以直接调。
                PruneStamps();
                Harvest();
                // [v0.1.99] 里程碑 2.3：**云影要跟着云走**。云影是烘进顶点色的低频项，
                //   而云的相位随 `Time.RealTime` 变 —— 所以开着云影时按 `RefreshSeconds`（默认 2 s）
                //   主动请一次重建；实测重建在毫秒级，代价可接受（见 notes/178）。
                if (SkylineLodCloudShadow.Enabled && SkylineLodCloudShadow.RefreshSeconds > 0f
                    && now >= m_nextCloudShadowRebuild) {
                    m_nextCloudShadowRebuild = now + MathF.Max(SkylineLodCloudShadow.RefreshSeconds, 0.25f);
                    m_dirty = true;
                }
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

        // ============================================================================================
        // [v0.1.129] 里程碑 3.2：**离开加载范围的那一刻给 LOD 单元补最后一次采样**
        // ============================================================================================
        //
        // 问题（实测，见 notes/229）：`NotifyChunkValid` 在区块刚 Valid 时把它的 16 m 单元标脏，
        // 但玩家一路走过去时，排在队列后面的单元**可能在轮到自己之前就随区块一起被卸载**：
        //   * 脏队列取到它 → 区块已不在加载范围 ⇒ 重排在物理上不可能 ⇒ 无限循环；
        //   * 于是 `LodDescribe` 的 `dirty=921/q=921` 静止两分钟不降（里程碑 3.2"加载全部 LOD 区块"卡在这里）。
        // 修法：卸载钩子（`TerrainUpdater.AllocateAndFreeChunks` 的预扫，主线程、数据还在）里
        // **把该单元采进 LOD 再销账**；已经有样本的单元直接销账（数据已在手上，只是不新鲜）。

        /// <summary>[v0.1.129] 每次卸载预扫最多补采几个 16 m 单元（防"走远一次释放一整圈"卡帧）。</summary>
        public static int CellCapturePerCall { get; set; } = 128;

        /// <summary>[v0.1.129] 卸载前补采成功累计（个）。</summary>
        public static long CellsCapturedOnUnload { get; private set; }

        /// <summary>[v0.1.129] 因预算不足被跳过的补采次数（这些单元留到玩家回来再采）。</summary>
        public static long CellCaptureSkippedOverBudget { get; private set; }

        /// <summary>[v0.1.129] 最近一次卸载预扫的耗时（毫秒，含补采）。</summary>
        public static float CellCaptureLastMs { get; private set; }

        /// <summary>
        /// [v0.1.129] 卸载预扫入口（`TerrainUpdater` 调用，与壳采集同一处、同在主线程）。
        /// 只处理"脏的或从没采过"的单元 —— 其余单元本来就已经在 LOD 里，不必碰。
        /// </summary>
        internal static void OnChunksLeavingRange(List<TerrainChunk> leaving) {
            if (!Enabled || leaving == null || leaving.Count == 0) {
                return;
            }
            try {
                long t0 = Stopwatch.GetTimestamp();
                int budget = Math.Max(0, CellCapturePerCall);
                foreach (TerrainChunk chunk in leaving) {
                    if (chunk == null) {
                        continue;
                    }
                    long key = Key(chunk.Origin.X >> CellShift, chunk.Origin.Y >> CellShift);
                    if (m_cells.ContainsKey(key)) {
                        // 已经有样本：区块马上消失 ⇒ 脏标记没有意义了（重采不可能），直接销账。
                        SatisfyDirty(key);
                        continue;
                    }
                    if (budget <= 0) {
                        CellCaptureSkippedOverBudget++;
                        continue;
                    }
                    if (CaptureLeavingCell(chunk, key)) {
                        budget--;
                        CellsCapturedOnUnload++;
                    }
                }
                CellCaptureLastMs = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            catch (Exception e) {
                m_lastError = "unloadCapture: " + e.Message;
            }
        }

        /// <summary>
        /// 把一个即将卸载的区块采成它所属的 16 m LOD 单元（主表面中位高度 + 众数材质 + 空气格光照），
        /// 复用与 `Harvest` 完全相同的 `MedianInto` 口径，保证两条路采出来的数据一致。
        /// 第二层表面（`HasSecond`）留空：卸载路径只保证"这一格在 LOD 里有正确的主表面"，
        /// 树冠下的地面这一层由加载期间的常规采集负责（如实记录，不假装等价）。
        /// </summary>
        static bool CaptureLeavingCell(TerrainChunk chunk, long key) {
            if (chunk.ThreadState < TerrainChunkState.Valid) {
                return false;
            }
            Span<long> samples = stackalloc long[TerrainChunk.Size * TerrainChunk.Size];
            int count = 0;
            int airLight = 0;
            for (int x = 0; x < TerrainChunk.Size; x++) {
                for (int z = 0; z < TerrainChunk.Size; z++) {
                    int top = chunk.GetTopHeightFast(x, z);
                    if (top < TerrainChunk.MinHeight) {
                        continue;                       // 空列
                    }
                    if (top < TerrainChunk.HeightMinusOne) {
                        int light = Terrain.ExtractLight(chunk.GetCellValueFast(x, top + 1, z));
                        if (light > airLight) {
                            airLight = light;
                        }
                    }
                    samples[count++] = ((long)top << 32) | (uint)chunk.GetCellValueFast(x, top, z);
                }
            }
            if (count == 0) {
                return false;
            }
            Span<int> top0 = [int.MaxValue];
            Span<int> value0 = [0];
            Span<byte> light0 = [15];
            MedianInto(samples, count, top0, value0, light0, 0);
            if (top0[0] == int.MaxValue) {
                return false;
            }
            m_cells[key] = new Cell {
                Height = (short)top0[0], Value = (ushort)value0[0], Light = light0[0],
                LightAir = (byte)airLight, HasSecond = false
            };
            m_harvestedCells++;
            m_dirty = true;
            MarkCoarseRegionDirty(key);
            SatisfyDirty(key);
            return true;
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
            // [v0.1.62] 第二层表面的采样缓冲（与主表面同一套中位/众数口径，只是样本集不同）
            Span<long> coarseSecond = stackalloc long[TerrainChunk.Size * TerrainChunk.Size];
            Span<long> fineSecond0 = stackalloc long[64];
            Span<long> fineSecond1 = stackalloc long[64];
            Span<long> fineSecond2 = stackalloc long[64];
            Span<long> fineSecond3 = stackalloc long[64];
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
                // [v0.1.47] 卸载前的"最后一采"优先（4.2：进入→离开加载范围都要刷新一次）
                if (m_forceHarvestChunk != null) {
                    chunk = m_forceHarvestChunk;
                    reason = m_forceHarvestReason;
                    m_forceHarvestChunk = null;              // 只采一次
                }
                else if (dirtyUsed < dirtyBudget && TryTakeDirtyChunk(terrain, out chunk, out reason)) {
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
                int cSecond = 0, s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int coarseAirLight = 0;      // [v0.1.100] 单元内"地表上方空气格"的最大光照
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
                        // [v0.1.100] 里程碑 2.3 第三项：读**顶面上方那一格（空气）**的光照并取单元内最大
                        if (top < TerrainChunk.HeightMinusOne) {
                            int above = chunk.GetCellValueFast(x, top + 1, z);
                            int airLight = Terrain.ExtractLight(above);
                            if (airLight > coarseAirLight) {
                                coarseAirLight = airLight;
                            }
                        }
                        long packed = ((long)top << 32) | (uint)chunk.GetCellValueFast(x, top, z);
                        coarseSamples[cCount++] = packed;
                        // [v0.1.62] **第二层表面**：从主表面往下找"下一块上面是空气的实心块"，
                        // 且它与主表面之间至少隔 `SecondGap` 格空气。树冠之下的地面、檐下的墙顶就是它。
                        // 找不到（实心一直连到主表面）就不算 —— 那本来就是同一个表面。
                        if (SecondSurfaceEnabled) {
                            int airRun = 0;
                            int floorY = Math.Max(top - SecondSearchDepth, TerrainChunk.MinHeight);
                            for (int yy = top - 1; yy >= floorY; yy--) {
                                int vv = chunk.GetCellValueFast(x, yy, z);
                                if (Terrain.ExtractContents(vv) == 0) {
                                    airRun++;
                                    continue;
                                }
                                // 走到第一个实心块时**不能直接停**：主表面（树冠顶）下面往往还有好几层实心树叶，
                                // 真正要找的是"**上面攒够 SecondGap 格空气**的那个实心块"= 树冠之下的地面。
                                // 空气不够 → 这一块属于主表面本身，重置计数继续往下。
                                if (airRun < SecondGap) {
                                    airRun = 0;
                                    continue;
                                }
                                if (top - yy >= SecondMinDrop) {
                                    long packed2 = ((long)yy << 32) | (uint)vv;
                                    coarseSecond[cSecond++] = packed2;
                                    switch ((x >> 3) | ((z >> 3) << 1)) {
                                        case 0: fineSecond0[s0++] = packed2; break;
                                        case 1: fineSecond1[s1++] = packed2; break;
                                        case 2: fineSecond2[s2++] = packed2; break;
                                        default: fineSecond3[s3++] = packed2; break;
                                    }
                                }
                                break;                        // 已经找到"主表面 → 空气 → 实心"这一组，再深就是更下面的层
                            }
                        }
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
                // [v0.1.62] 第二层表面：同一个中位/众数口径，只是样本集不同。
                // **要求"够多列有第二层"才认**（否则单列噪声会在远处糊出一层假表面）。
                Span<int> coarseTop2 = [int.MaxValue];
                Span<int> coarseValue2 = [0];
                Span<byte> coarseLight2 = [15];
                if (cSecond >= Math.Max(4, cCount / 8)) {
                    MedianInto(coarseSecond, cSecond, coarseTop2, coarseValue2, coarseLight2, 0);
                }
                bool coarseHasSecond = coarseTop2[0] != int.MaxValue && bestTop - coarseTop2[0] >= SecondMinDrop;
                Span<int> fineTop2 = [int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue];
                Span<int> fineValue2 = [0, 0, 0, 0];
                Span<byte> fineLight2 = [15, 15, 15, 15];
                Span<int> fineSecondCount = [s0, s1, s2, s3];
                if (s0 >= 4) {
                    MedianInto(fineSecond0, s0, fineTop2, fineValue2, fineLight2, 0);
                }
                if (s1 >= 4) {
                    MedianInto(fineSecond1, s1, fineTop2, fineValue2, fineLight2, 1);
                }
                if (s2 >= 4) {
                    MedianInto(fineSecond2, s2, fineTop2, fineValue2, fineLight2, 2);
                }
                if (s3 >= 4) {
                    MedianInto(fineSecond3, s3, fineTop2, fineValue2, fineLight2, 3);
                }
                if (bestTop != int.MaxValue) {
                    m_cells[key] = new Cell {
                        Height = (short)bestTop, Value = (ushort)bestValue, Light = coarseLight[0],
                        LightAir = (byte)coarseAirLight,          // [v0.1.100] 空气格光照（单元内最大）
                        Height2 = (short)(coarseHasSecond ? coarseTop2[0] : 0),
                        Value2 = (ushort)(coarseHasSecond ? coarseValue2[0] : 0),
                        Light2 = coarseHasSecond ? coarseLight2[0] : (byte)15,
                        HasSecond = coarseHasSecond
                    };
                    m_harvestedCells++;
                    m_dirty = true;
                    MarkCoarseRegionDirty(key);      // [v0.1.73] 该区域标脏（保存时按区域整块写出）
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
                        Height = (short)fineTop[k], Value = (ushort)fineValue[k], Light = fineLight[k],
                        Height2 = (short)(fineTop2[k] == int.MaxValue ? 0 : fineTop2[k]),
                        Value2 = (ushort)fineValue2[k],
                        Light2 = fineLight2[k],
                        HasSecond = fineSecondCount[k] >= 4 && fineTop2[k] != int.MaxValue
                                    && fineTop[k] - fineTop2[k] >= SecondMinDrop
                    };
                    m_dirty = true;
                    MarkFineRegionDirty(fkey);       // [v0.1.73] 精细层同样按区域标脏
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
        static float SelfShadowFactor(Dictionary<long, Cell> dict, int cx, int cz, int height, int cellSize,
                                      Vector3 sun,
                                      Dictionary<long, Cell> shadowDict = null, int shadowCellSize = 0) {
            // [v0.1.98] 里程碑 2.3：**阴影的最小分辨必须是 LOD 的最小体素，而不是"整块 LOD 单元"**。
            //   用户口径原文："注意分辨率是该 LOD 的最小体素，而不是整个 LOD 区块作为一个整体参与"。
            //   32 m 统一档的**网格**是 32 m 一格，但**阴影**应当用手里最细的那份采样（16 m 的粗单元层）来步进 ——
            //   否则同一块 32 m 假平地上，站在格子中心还是格子边缘得到的明暗完全一样，边界会是"整块跳变"。
            //   `shadowDict == null` 时行为与 v0.1.97 逐位一致（A/B 用）。
            Dictionary<long, Cell> marchDict = shadowDict ?? dict;
            int marchSize = shadowCellSize > 0 ? shadowCellSize : cellSize;
            sun = sun.LengthSquared() > 1e-8f ? Vector3.Normalize(sun) : Vector3.UnitY;
            float step = marchSize * 0.5f;
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
                int nx = (int)MathF.Floor(x / marchSize);
                int nz = (int)MathF.Floor(z / marchSize);
                if (!marchDict.TryGetValue(Key(nx, nz), out Cell cell)) {
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
            // [v0.1.78] 同样显式传固定光方向（判据不依赖当前时刻）
            Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
            float shadowed = SelfShadowFactor(dict, 0, 0, low, cellSize, sun);
            float lit = SelfShadowFactor(dict, 0, -2, low, cellSize, sun);
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
        static Vector3 SlopeNormal(Dictionary<long, Cell> dict, int cx, int cz, int height, int cellSize) {
            int hx0 = NeighborHeight(dict, cx - 1, cz, height);
            int hx1 = NeighborHeight(dict, cx + 1, cz, height);
            int hz0 = NeighborHeight(dict, cx, cz - 1, height);
            int hz1 = NeighborHeight(dict, cx, cz + 1, height);
            float ddx = (hx1 - hx0) / (2f * cellSize);
            float ddz = (hz1 - hz0) / (2f * cellSize);
            return Vector3.Normalize(new Vector3(-ddx, 1f, -ddz));
        }

        /// <summary>
        /// [v0.1.77] 坡向增益公式 —— **着色器里逐字同式**（这就是"坡向明暗迁到 GPU"的那条公式）：
        /// `lit = dot(n, sun) / dot(+Y, sun)`，`gain = lerp(1, clamp(lit, 0.35, 1), strength)`，
        /// 再按日照量淡出。注意：游戏本身用**两盏镜像方向光**（DirectionToLight1/2），水平坡向会互相抵消 ——
        /// 直接用 `CalculateLighting` 得到的增益在实测里恒为 1（看不出起伏），
        /// 所以这里只取**一盏太阳**（DirectionToLight1），与 Dawnlight/Iris 的单向太阳一致。
        /// </summary>
        /// <summary>
        /// [v0.1.78] 坡向明暗 / 自阴影使用的**太阳方向**：统一走 v0.1.65 的真太阳
        /// `SkylineRuntime.TrackedLightDirection()`（关掉 `SunTracking` 即回固定光）。
        /// 用户口径（逐字）："**太阳追踪功能很重要，不要让光源点偏离太阳**" ——
        /// v0.1.15~v0.1.77 的 LOD 坡向/自阴影一直用游戏本体的**固定**方向光，
        /// 于是"影子跟着太阳转、坡向不跟着转"这两件事互相矛盾；本版把它们统一到同一个真值。
        /// </summary>
        public static Vector3 SlopeSunDirection() => SkylineRuntime.TrackedLightDirection();

        static float SlopeGainFromNormal(Vector3 normal) => SlopeGainFromNormal(normal, SlopeSunDirection());

        /// <summary>坡向增益（**显式传太阳**的版本，自检用它保持"不依赖当前时刻"的确定性）。</summary>
        static float SlopeGainFromNormal(Vector3 normal, Vector3 sun) {
            sun = sun.LengthSquared() > 1e-8f ? Vector3.Normalize(sun) : Vector3.UnitY;
            float gain = SlopeGainPure(normal, sun);
            m_slopeStatMin = MathF.Min(m_slopeStatMin, gain);   // v0.1.15：诊断——全网格累计（几个浮点运算）
            m_slopeStatMax = MathF.Max(m_slopeStatMax, gain);
            m_slopeStatSum += gain;
            m_slopeStatCount++;
            return gain;
        }

        /// <summary>[v0.1.78] 坡向增益的**纯函数**版本（不碰统计量）—— 探针与 A/B 用它，保证只读。</summary>
        static float SlopeGainPure(Vector3 normal, Vector3 sun) {
            sun = sun.LengthSquared() > 1e-8f ? Vector3.Normalize(sun) : Vector3.UnitY;
            float lit = Vector3.Dot(normal, sun) / MathF.Max(Vector3.Dot(Vector3.UnitY, sun), 0.0001f);
            return MathUtils.Lerp(1f, MathUtils.Clamp(lit, 0.35f, 1f), SlopeShadingStrength);
        }

        /// <summary>[v0.1.15/v0.1.77] 单个单元的坡向增益（法线取自 <see cref="SlopeNormal"/>）。</summary>
        static float SlopeLightGain(Dictionary<long, Cell> dict, int cx, int cz, int height, int cellSize,
                                    Vector3 sun) =>
            SlopeGainFromNormal(SlopeNormal(dict, cx, cz, height, cellSize), sun);

        /// <summary>[v0.1.15] 坡向明暗的**确定性自检**（不依赖世界地形）：
        /// 平地 → 增益 = 1；十格高的坡：**背光侧**增益 &lt; 1（更暗）、**迎光侧**被夹到 1（不炸亮）、
        /// 两侧差异明显（说明确实是"单向太阳"而不是两盏镜像光抵消）。</summary>
        /// <summary>
        /// [v0.1.78] 坡向/自阴影**当前**使用的太阳方向（只读；给 `LodSurvey` 与回归清单断言用）。
        /// `dotWithTracked` 恒为 1 —— 这就是"不要让光源点偏离太阳"的可断言形式。
        /// </summary>
        public static JsonObject SlopeSunInfo() {
            Vector3 dir = SlopeSunDirection();
            Vector3 tracked = SkylineRuntime.TrackedLightDirection();
            return new JsonObject {
                ["tracking"] = SkylineRuntime.SunTracking,
                ["useMoonAtNight"] = SkylineRuntime.SunUseMoonAtNight,
                ["source"] = "SkylineRuntime.TrackedLightDirection()（与天上那个太阳同式；关掉 SunTracking 即回固定光）",
                ["dir"] = new JsonArray(dir.X, dir.Y, dir.Z),
                ["tracked"] = new JsonArray(tracked.X, tracked.Y, tracked.Z),
                ["dotWithTracked"] = Math.Round((double)Vector3.Dot(dir, tracked), 6),
                ["elevationDeg"] = Math.Round(MathF.Asin(MathUtils.Clamp(dir.Y, -1f, 1f)) * 180f / MathF.PI, 2),
                ["deviationDegFromFixedLight"] = Math.Round(
                    MathF.Acos(MathUtils.Clamp(Vector3.Dot(dir,
                        Vector3.Normalize(LightingManager.DirectionToLight1)), -1f, 1f)) * 180f / MathF.PI, 2)
            };
        }

        /// <summary>
        /// [v0.1.78] **坡向太阳探针**（只读）：对当前 LOD 单元字典里一个稳定抽样的子集，
        /// 分别用**追踪到的太阳**与**游戏固定方向光**算坡向增益，报均值/差异。
        /// 用途：①量化"统一到真太阳"改了多少（固定光那列就是 v0.1.77 的行为）；
        /// ②把世界时刻拨到不同值再调它，就能看到坡向**跟着太阳转**（见 `heightlab/skyline-v0178-lod-sun.py`）。
        /// </summary>
        public static JsonObject SlopeSunProbe(int maxCells) {
            JsonObject result = new();
            try {
                int limit = Math.Clamp(maxCells <= 0 ? 512 : maxCells, 1, 20000);
                long[] keys = new long[m_cells.Count];
                m_cells.Keys.CopyTo(keys, 0);
                Array.Sort(keys);
                int n = Math.Min(limit, keys.Length);
                Vector3 tracked = SlopeSunDirection();
                Vector3 fixedSun = Vector3.Normalize(LightingManager.DirectionToLight1);
                double sumTracked = 0, sumFixed = 0, sumAbsDelta = 0;
                float minT = 2f, maxT = -1f;
                for (int i = 0; i < n; i++) {
                    long key = keys[i];
                    int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                    Cell cell = m_cells[key];
                    Vector3 normal = SlopeNormal(m_cells, cx, cz, cell.Height, CellSize);
                    float gT = SlopeGainPure(normal, tracked);
                    float gF = SlopeGainPure(normal, fixedSun);
                    sumTracked += gT;
                    sumFixed += gF;
                    sumAbsDelta += MathF.Abs(gT - gF);
                    minT = MathF.Min(minT, gT);
                    maxT = MathF.Max(maxT, gT);
                }
                result["ok"] = true;
                result["cells"] = n;
                result["cellsTotal"] = m_cells.Count;
                result["strength"] = Math.Round(SlopeShadingStrength, 4);
                result["sunAmount"] = Math.Round(m_sunAmount, 4);
                result["trackedDir"] = new JsonArray(tracked.X, tracked.Y, tracked.Z);
                result["fixedDir"] = new JsonArray(fixedSun.X, fixedSun.Y, fixedSun.Z);
                result["meanGainTracked"] = n > 0 ? Math.Round(sumTracked / n, 5) : 0.0;
                result["meanGainFixed"] = n > 0 ? Math.Round(sumFixed / n, 5) : 0.0;
                result["meanAbsDelta"] = n > 0 ? Math.Round(sumAbsDelta / n, 5) : 0.0;
                result["minGainTracked"] = n > 0 ? Math.Round(minT, 5) : 0.0;
                result["maxGainTracked"] = n > 0 ? Math.Round(maxT, 5) : 0.0;
                result["note"] = "meanGainFixed 那一列就是 v0.1.77 及以前的行为（固定方向光）；"
                    + "两者差多少，就是\"把坡向统一到真太阳\"改了的外观量。";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result;
        }

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
            // [v0.1.78] 自检**显式传固定光方向**：判据必须不随"现在几点"变化（确定性的前提）。
            Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
            float gFlat = SlopeLightGain(flat, 0, 0, height, cellSize, sun);
            float gEast = SlopeLightGain(eastStep, 0, 0, height, cellSize, sun);
            float gWest = SlopeLightGain(westStep, 0, 0, height, cellSize, sun);
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
            // [v0.1.62] 第二层表面的统计每次重建归零（普查在下面按字典实数一遍）
            SecondQuads = 0;
            SecondWallQuads = 0;
            CellsWithSecond = 0;
            foreach (Cell c2 in m_cells.Values) {
                if (c2.HasSecond) {
                    CellsWithSecond++;
                }
            }
            foreach (Cell c2 in m_cellsFine.Values) {
                if (c2.HasSecond) {
                    CellsWithSecond++;
                }
            }
            // [v0.1.110] DH 的 `overdrawPrevention`：默认 0 ⇒ 保持"视距内不画"（v0.1.0 的修复）；
            // 设为 0.4（DH 默认）⇒ LOD 从**视距的 40%** 就开始画，靠 `SkylineLodLook` 的抖动淡出掩护重叠带。
            float skipRadius = UniformBeyondLoaded && OverdrawPrevention > 0.001f
                ? MathF.Max(visualRange * Math.Clamp(OverdrawPrevention, 0f, 1f), FineSize * 0.5f)
                : visualRange + FineSize * 0.5f;                              // 视距内不画（v0.1.0 修复）
            m_lastSkipRadius = skipRadius;   // [v0.1.110] 供抖动淡出取"LOD 的实际内边界"
            float fineRange = MathF.Max(visualRange * FineRangeFactor, skipRadius + FineSize * 4f);
            // [v0.1.45] 近环 4 m 层：只覆盖 [skipRadius, skipRadius+NearBandMetres]
            float nearRange = skipRadius + NearBandMetres;
            // [v0.1.70] 里程碑 2.2：**LOD 单元也要及时移除**（壳有滑动窗口、LOD 之前没有）。
            // 只在"网格范围之外"动手（×LodCellReleaseFactor），所以不需要置脏。
            if (LodCellReleaseEnabled) {
                PruneFarCells(m_cells, CellShift, CellSize, RadiusMetres * LodCellReleaseFactor,
                    out int releasedCoarse);
                PruneFarCells(m_cellsFine, FineShift, FineSize, fineRange * LodCellReleaseFactor,
                    out int releasedFine);
                m_lodCellsReleasedLastCoarse = releasedCoarse;
                m_lodCellsReleasedLastFine = releasedFine;
                m_lodCellsReleasedCoarse += releasedCoarse;
                m_lodCellsReleasedFine += releasedFine;
            }
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
            if (UniformBeyondLoaded) {
                // [v0.1.98] 里程碑 2.2：**加载距离之外只画一档 32 m**（消除三档交界那种"分级过于明显"）。
                //   近环/精细两档**不建网格**（并清空），统一档覆盖 `[skipRadius, RadiusMetres]`。
                if (MergeLadderEnabled) {
                    // [v0.1.130] 里程碑 2.6：**合并阶梯**（32 / 64 / 128 m 三档，边界取 DH 的 L2/L3）。
                    (float b1, float b2) = MergeBoundaries();
                    BuildMergedTier(1, m_cells32);
                    BuildMergedTier(2, m_cellsM2);
                    BuildMergedTier(3, m_cellsM3);
                    m_ladderTier1Cells = m_cells32.Count;
                    m_ladderTier2Cells = m_cellsM2.Count;
                    m_ladderTier3Cells = m_cellsM3.Count;
                    PruneFarCells(m_cells32, CellShift + 1, CellSize << 1,
                        MathF.Min(b1, RadiusMetres) * LodCellReleaseFactor, out _);
                    PruneFarCells(m_cellsM2, CellShift + 2, CellSize << 2,
                        MathF.Min(b2, RadiusMetres) * LodCellReleaseFactor, out _);
                    PruneFarCells(m_cellsM3, CellShift + 3, CellSize << 3,
                        RadiusMetres * LodCellReleaseFactor, out _);
                    RebuildMeshCore(m_cells32, CellShift + 1, skipRadius, MathF.Min(b1, RadiusMetres), 0,
                        m_cells, CellSize);
                    if (RadiusMetres > b1) {
                        RebuildMeshCore(m_cellsM2, CellShift + 2, MathF.Max(b1, skipRadius),
                            MathF.Min(b2, RadiusMetres), 1, m_cells, CellSize);
                    }
                    else {
                        ClearLayer(1);
                    }
                    if (RadiusMetres > b2) {
                        RebuildMeshCore(m_cellsM3, CellShift + 3, MathF.Max(b2, skipRadius),
                            RadiusMetres, 2, m_cells, CellSize);
                    }
                    else {
                        ClearLayer(2);
                    }
                    if (m_cellsFine.Count > 0) {
                        m_cellsFine.Clear();
                    }
                    if (m_cellsNear.Count > 0) {
                        m_cellsNear.Clear();
                    }
                }
                else {
                    BuildUniformCells();
                }
                if (m_indexCountFine > 0 || m_cellsFine.Count > 0) {
                    if (!MergeLadderEnabled) {
                        m_cellsFine.Clear();
                        m_indexCountFine = 0;
                        m_cellsInMeshFine = 0;
                        Utilities.Dispose(ref m_vbFine);
                        Utilities.Dispose(ref m_ibFine);
                    }
                }
                if (!MergeLadderEnabled && m_cellsNear.Count > 0) {
                    m_cellsNear.Clear();
                }
                if (!MergeLadderEnabled) {
                    PruneFarCells(m_cells32, CellShift + UniformExtraShift, CellSize << UniformExtraShift,
                        RadiusMetres * LodCellReleaseFactor, out _);
                    // 里程碑 2.3：32 m 的**网格**配 16 m 的**阴影最小体素**（`m_cells` 就是手里最细的采样）
                    RebuildMeshCore(m_cells32, CellShift + UniformExtraShift, skipRadius, RadiusMetres, 0,
                        m_cells, CellSize);
                }
            }
            else {
                RebuildMeshCore(m_cellsFine, FineShift, MathF.Max(skipRadius, nearRange), fineRange, 1);
                RebuildMeshCore(m_cells, CellShift, fineRange, RadiusMetres, 0);
            }
            m_rebuilds++;
            m_dirty = false;
            // v0.1.0 修复保留：相机未就位（建出空网格）时保持 dirty，等相机就位后重建。
            if (m_indexCount == 0 && m_indexCountFine == 0 && m_indexCountNear == 0
                && (m_cells.Count > 0 || m_cellsFine.Count > 0 || m_cellsNear.Count > 0)) {
                m_dirty = true;
            }
        }

        // ===== [v0.1.70] 里程碑 2.2：LOD 单元也要**及时移除** =====

        /// <summary>
        /// [v0.1.70] 是否把"超出绘制半径 ×系数"的 LOD 单元从内存里放掉。**默认关**。
        ///
        /// 为什么默认关（实测教训，别想当然开）：LOD 单元表**就是"走过就记住"的轨迹缓存** ——
        /// 视距只有 128 m，而 LOD 要画 [512, 1024] m 那一圈，那些数据**只能**来自"玩家曾经走近时采下来的"。
        /// 把它按距离放掉 = **把远景丢掉**：实测在同一个位置，放掉之后 `cellsInMesh` 从 148 掉到 **0**
        /// （地平线远景整片消失），而它**本身小到可以忽略**（12.3 km 走行后粗层 6,442 + 精细层 25,768，
        /// 量级 ~1.6 MB，约 0.13 MB/km）。
        /// ⇒ 换来的是"一个本来就不存在的内存问题"，代价是可见的远景。**要用请显式打开**。
        /// 真正的下一步是**把 LOD 单元落盘 + 按需回读**（Distant Horizons 的做法），那才既省内存又不丢远景。
        /// </summary>
        public static bool LodCellReleaseEnabled { get; set; }

        /// <summary>[v0.1.70] 释放半径相对**绘制半径**的倍数（默认 1.25，与壳滑动窗口同一口径）。</summary>
        public static float LodCellReleaseFactor { get; set; } = 1.25f;

        static long m_lodCellsReleasedCoarse;
        static long m_lodCellsReleasedFine;
        static int m_lodCellsReleasedLastCoarse;
        static int m_lodCellsReleasedLastFine;

        /// <summary>累计被释放的粗层/精细层单元数（诊断）。</summary>
        public static long LodCellsReleasedCoarse => m_lodCellsReleasedCoarse;

        public static long LodCellsReleasedFine => m_lodCellsReleasedFine;

        /// <summary>
        /// 把超出 `maxDist` 的单元丢掉。**为什么不置 m_dirty**：调用点给的距离是
        /// `绘制半径 × LodCellReleaseFactor`（>1），被丢掉的单元本来就不在网格里，
        /// 置脏只会引发"重建→置脏→重建"的死循环（而且每走一步就白烧一次全量重建）。
        /// </summary>
        static void PruneFarCells(Dictionary<long, Cell> dict, int shift, float size, float maxDist,
                                  out int removed) {
            removed = 0;
            if (dict.Count == 0 || maxDist <= 0f) {
                return;
            }
            Vector3 camera = CameraViewPosition();
            float maxSq = maxDist * maxDist;
            List<long> remove = null;
            foreach (long key in dict.Keys) {
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float dx = (cx << shift) + size * 0.5f - camera.X;
                float dz = (cz << shift) + size * 0.5f - camera.Z;
                if (dx * dx + dz * dz > maxSq) {
                    (remove ??= []).Add(key);
                }
            }
            if (remove == null) {
                return;
            }
            foreach (long key in remove) {
                dict.Remove(key);
            }
            removed = remove.Count;
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

        /// <summary>
        /// [v0.1.98] 里程碑 2.2：把 16 m 粗单元按 `UniformExtraShift`（默认 2×2 ⇒ **32 m**）合成**统一档**。
        ///
        /// 口径与单格**完全同一套**（`MedianInto`：高度取中位、材质取众数）——
        /// 所以"32 m 一档"和原来的"16 m 一档"不是两套观感，只是格子更大；
        /// 这样"加载距离之外"在画面上只有**一个**分辨率，才谈得上用放大截图去定分级边界。
        /// </summary>
        static void BuildUniformCells() {
            m_cells32.Clear();
            m_groupScratch.Clear();
            MinVoxelShadowCompared = 0;      // [v0.1.98] 每次重建重新统计
            MinVoxelShadowDiffCells = 0;
            MinVoxelShadowDiffSum255 = 0;
            int extra = Math.Clamp(UniformExtraShift, 1, 3);
            foreach (KeyValuePair<long, Cell> kv in m_cells) {
                int cx = (int)(kv.Key >> 32);
                int cz = (int)(uint)kv.Key;
                long gkey = Key(cx >> extra, cz >> extra);
                AggregateGroup(gkey, kv.Value);
            }
            FlushGroups(m_cells32);
        }

        // ============================================================================================
        // [v0.1.130] 里程碑 2.6：**合并阶梯**（32 / 64 / 128 m 三档，档由 DH 的档位公式给出）
        // ============================================================================================

        /// <summary>把一个 16 m 单元并进当前分组表（高度取中位、材质取众数、空气格光照取最大）。</summary>
        static void AggregateGroup(long gkey, in Cell cell) {
            m_groupScratch.TryGetValue(gkey, out (int n, long p0, long p1, long p2, long p3, int air) g);
            long packed = ((long)cell.Height << 32) | cell.Value;
            g.air = Math.Max(g.air, cell.LightAir);      // [v0.1.100] 空气格光照取**组内最大**
            switch (g.n) {
                case 0: g.p0 = packed; break;
                case 1: g.p1 = packed; break;
                case 2: g.p2 = packed; break;
                default: g.p3 = packed; break;      // 组满 4 个后多余的忽略（中位对少数样本稳健）
            }
            if (g.n < 4) {
                g.n++;
            }
            m_groupScratch[gkey] = g;
        }

        /// <summary>把分组表按**同一套 `MedianInto` 口径**落成合并后的单元表。</summary>
        static void FlushGroups(Dictionary<long, Cell> dest) {
            Span<long> samples = stackalloc long[4];
            Span<int> top = stackalloc int[1];
            Span<int> val = stackalloc int[1];
            Span<byte> light = stackalloc byte[1];
            foreach (KeyValuePair<long, (int n, long p0, long p1, long p2, long p3, int air)> kv in m_groupScratch) {
                (int n, long p0, long p1, long p2, long p3, int air) g = kv.Value;
                if (g.n <= 0) {
                    continue;
                }
                samples[0] = g.p0;
                samples[1] = g.p1;
                samples[2] = g.p2;
                samples[3] = g.p3;
                top[0] = int.MaxValue;
                val[0] = 0;
                light[0] = 15;
                MedianInto(samples, g.n, top, val, light, 0);
                if (top[0] == int.MaxValue) {
                    continue;
                }
                dest[kv.Key] = new Cell {
                    Height = (short)top[0], Value = (ushort)val[0], Light = light[0],
                    LightAir = (byte)Math.Clamp(g.air, 0, 255)
                };
            }
            m_groupScratch.Clear();
        }

        /// <summary>
        /// 把 16 m 单元按**它自己到相机的水平距离**分进第 `tier` 档（1 = 32 m、2 = 64 m、3 = 128 m）。
        /// 档位判据直接用 DH 的 `LodQuadTree.calcDetailLevelFromDistance`（`SkylineCubeShellStore.DhLevelForDistance`），
        /// 所以边界就是 DH 的 192 / 384 / 768 / 1536 m 绝对距离，减到我们这三档即 clamp 到 [1,3]。
        /// </summary>
        static void BuildMergedTier(int tier, Dictionary<long, Cell> dest) {
            dest.Clear();
            m_groupScratch.Clear();
            Vector3 camera = CameraViewPosition();
            int shift = Math.Clamp(tier, 1, 3);
            foreach (KeyValuePair<long, Cell> kv in m_cells) {
                int cx = (int)(kv.Key >> 32);
                int cz = (int)(uint)kv.Key;
                float wx = (cx << CellShift) + CellSize * 0.5f;
                float wz = (cz << CellShift) + CellSize * 0.5f;
                float dx = wx - camera.X, dz = wz - camera.Z;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (TierForDistance(d) != tier) {
                    continue;
                }
                AggregateGroup(Key(cx >> shift, cz >> shift), kv.Value);
            }
            FlushGroups(dest);
        }

        /// <summary>清空某一层的网格缓冲（层号：0 主 / 1 精细 / 2 近环；合并阶梯时 1/2 被复用为 64/128 m 档）。</summary>
        static void ClearLayer(int layer) => SetLayerMesh(layer, null, null, 0, 0, 0);

        static void RebuildMeshCore(Dictionary<long, Cell> dict, int cellShift,
                                    float minDist, float maxDist, int layer,
                                    Dictionary<long, Cell> shadowDict = null, int shadowCellSize = 0) {
            if (layer == 0) {
                SkylineLodCloudShadow.ResetStats();     // [v0.1.99] 云影统计按"最粗那一层"重建一次
                m_airLightBrightenedCells = 0;          // [v0.1.100] 亮斑统计同样按最粗那层重建一次
            }
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

            m_keysScratch.Clear();
            List<long> keys = m_keysScratch;
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
                    if (!dict.TryGetValue(key, out Cell cell)) {
                        continue;
                    }
                    float dx = (cx << cellShift) + cellSize * 0.5f - camera.X;
                    float dz = (cz << cellShift) + cellSize * 0.5f - camera.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 <= minSq || d2 > maxSq) {
                        continue;
                    }
                    // [v0.1.51] 32³ 表面壳**已经接管**的立方体 → 现有 LOD 层让位。
                    // 否则两层在同一片地上互相穿插（壳是逐列真高、LOD 是 16 m 中位高），画面会发花。
                    // 判定与 `SkylineCubeShellStore.Draw` 用**同一套带规则 + 同一个立方体中心**，所以不会出现
                    // "LOD 让位了、壳却没画"的洞。
                    if (SkylineCubeShellStore.RestrictLod
                        && SkylineCubeShellStore.HasShellInBand(cx << cellShift, cell.Height, cz << cellShift,
                            camera.X, camera.Z)) {
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
            m_wallsScratch.Clear();
            List<(int x0, int z0, float yHigh, float yLow, int side, int value)> walls = m_wallsScratch;
            // [v0.1.62] **第二层表面的顶面表**：与主表面合成同一张表，走同一套材质/光照管线
            // （没有第二层单元的格时，这张表 == keys，逐位回 v0.1.61）。
            m_topsScratch.Clear();
            List<(long Key, int Height, int Value, byte Light)> tops = m_topsScratch;
            int secondSheets = 0;
            foreach (long key in keys) {
                Cell c0 = dict[key];
                // [v0.1.100] 里程碑 2.3 第三项：**固定光源的亮度斑块** ——
                //   顶面基色用"实心方块自身的 light"是错的（几乎恒 0），光活在**上方空气格**里。
                //   这里改成 `max(自身 light, 上方空气格 light)`（单元内已取最大）⇒ 火把/灯照到的地方出亮斑。
                byte effLight = c0.Light;
                if (LodAirLightPatch) {
                    byte withAir = (byte)Math.Max(c0.Light, c0.LightAir);
                    if (withAir > effLight) {
                        effLight = withAir;
                        m_airLightBrightenedCells++;
                    }
                }
                tops.Add((key, c0.Height, c0.Value, effLight));
                if (SecondSurfaceEnabled && c0.HasSecond) {
                    tops.Add((key, c0.Height2, c0.Value2, c0.Light2));
                    secondSheets++;
                }
            }
            int secondWallCount = 0;
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
                // [v0.1.62] 第二层表面的**裙边**：从第二层顶面向下接到"邻居的第二层顶面或自己的主表面"，
                // 把树冠/檐下那层表面的轮廓封闭起来（否则远处看是悬空的平板）。
                if (SecondSurfaceEnabled && cell.HasSecond) {
                    float yHigh2 = cell.Height2 + 1f;
                    float yOwn = cell.Height + 1f;
                    for (int side = 0; side < 4; side++) {
                        long nk2 = Key(cx + s_sideDx[side], cz + s_sideDz[side]);
                        float yLow2 = yOwn;
                        if (dict.TryGetValue(nk2, out Cell nb2) && nb2.HasSecond) {
                            yLow2 = MathF.Max(yOwn, nb2.Height2 + 1f);
                        }
                        if (yHigh2 - yLow2 < 0.5f) {
                            continue;
                        }
                        walls.Add((cx << cellShift, cz << cellShift, yHigh2, yLow2, side, cell.Value2));
                        secondWallCount++;
                    }
                }
            }

            int indexCount = tops.Count * 6 + walls.Count * 12;
            int vertexCount = tops.Count * 4 + walls.Count * 4;
            if (vertexCount == 0) {
                // 空网格：由外层 RebuildMesh 统一决定是否保持 dirty（v0.1.0 修复的逻辑移到外层）。
                SetLayerMesh(layer, null, null, 0, 0, 0);
                return;
            }
            // [v0.1.60] 里程碑 1.3：远景 LOD 的**顶点格式**可切到 `SkylineLodVertex`（28 B，
            // 在原 20 B 之后**追加**法线与材质 id）。前 20 B 与 `TerrainVertex` 逐位相同，
            // 而引擎 `GLWrapper.ApplyShaderAndBuffers` 是**按语义**从"顶点缓冲自带声明"里取偏移与步长
            // （`Shader.GetVertexAttribData`），所以游戏 `Opaque` 着色器照样能画这个 buffer ——
            // 这正是"加属性而不是换格式"。关掉开关即逐位回到 v0.1.59 的 20 B 路径。
            bool attr = SkylineRuntime.LodVertexAttributes;
            bool bigIndices = vertexCount > 65535;
            // [v0.1.74] 全部改用**静态复用缓冲**（见字段处的注释：这些数组是 MB 级 LOH 垃圾的主要来源）。
            TerrainVertex[] bakedVertices = null;
            SkylineLodVertex[] attrVertices = null;
            short[] indices = null;
            int[] indices32 = null;
            if (attr) {
                EnsureCapacity(ref m_attrVertexScratch, vertexCount);
                attrVertices = m_attrVertexScratch;
            }
            else {
                EnsureCapacity(ref m_vertexScratch, vertexCount);
                bakedVertices = m_vertexScratch;
            }
            if (bigIndices) {
                EnsureCapacity(ref m_index32Scratch, indexCount);
                indices32 = m_index32Scratch;
            }
            else {
                EnsureCapacity(ref m_indexScratch, indexCount);
                indices = m_indexScratch;
            }
            var light = new Color((byte)220, (byte)220, (byte)220);
            // [v0.1.78] 这一次重建用的太阳方向（**一次取好**，循环里不再重复取）：坡向明暗与自阴影共用，
            // 也就是"影子跟着太阳转"和"坡向跟着太阳转"用同一个真值。
            Vector3 lodSun = SlopeSunDirection();
            int vi = 0, ii = 0, built = 0;
            // 写一个顶点：属性路径多写面法线（`face` 沿用 CellFace 编号 0=+Z 1=+X 2=-Z 3=-X 4=+Y 5=-Y）
            // 与材质 id（方块值）。非属性路径与 v0.1.59 逐位一致。
            void PutByNormal(int i, float px, float py, float pz, Color c, float u, float v,
                             Vector3 normal, bool slopeTop, int materialId) {
                if (attr) {
                    SkylineLodVertex.Setup(px, py, pz, c, u, v, normal, slopeTop, materialId, ref attrVertices[i]);
                }
                else {
                    BlockGeometryGenerator.SetupVertex(px, py, pz, c, u, v, ref bakedVertices[i]);
                }
            }
            // 立面：法线 = 面法线，标志位 w=0（着色器走**六面因子**公式）
            void Put(int i, float px, float py, float pz, Color c, float u, float v, int face, int materialId) =>
                PutByNormal(i, px, py, pz, c, u, v, SkylineLodVertex.FaceNormal(face), false, materialId);
            // [v0.1.62] 这里遍历的是 `tops`（主表面 + 第二层表面），**同一套材质/光照/坡向/自阴影管线**，
            // 只是高度与材质取自各自那一层。
            foreach ((long key, int topHeight, int topValue, byte topLight) in tops) {
                int cx = (int)(key >> 32), cz = (int)(key & 0xFFFFFFFF);
                float x0 = cx << cellShift, z0 = cz << cellShift;
                float y = topHeight + 1f;
                // [v0.1.44] 基色改用**采集时的光照值**（0..15 → 0..255，与游戏光照→顶点色的口径一致）。
                // 关掉开关即回到常数 220（= 光 13），用于 A/B（`skyline.LodLightFromSamples`）。
                Color cellBase = SkylineRuntime.LodLightFromSamples
                    ? new Color((byte)(topLight * 17), (byte)(topLight * 17), (byte)(topLight * 17))
                    : light;
                int contents = Terrain.ExtractContents(topValue);
                int value = topValue;
                Block block = BlocksManager.Blocks[contents];
                int slotCount = Math.Max(block.GetTextureSlotCount(value), 1);
                // [v0.1.48] 4.4：非完整方块（门/栅栏/栅栏门…）在 LOD 里按**材质**表现，
                // 而不是按碰撞箱/建模取"顶面"（顶面会把它们画成细边/怪条）。
                int slot = SkylineRuntime.LodMaterialAware
                    ? MaterialTextureSlot(block, value)
                    : block.GetFaceTextureSlot(4, value);
                float u0 = (slot % slotCount) / (float)slotCount;
                float v0 = (slot / slotCount) / (float)slotCount;
                float du = 1f / slotCount;
                // [v0.1.15] 坡向明暗：远景低模原来一律 flat 光（220,220,220），起伏完全看不出来。
                // 这里用**相邻单元高度**估该单元法线，再套游戏自己的 `LightingManager.CalculateLighting`
                // （环境光 + 两盏方向光），得到"与游戏光照模型一致"的明暗系数 —— 这是把 LOD 接进光照的
                // 第一步（里程碑 5 的阴影/G-buffer 之前的最小可用版本，见 notes/85）。
                // [v0.1.77] 顶面法线 = **坡面法线**（相邻单元高度的梯度）：CPU 烘焙与 GPU 顶点属性
                // 取的是**同一个来源**，所以 `lod-attr-selfcheck` 能把两条路径比到量化误差。
                Vector3 topNormal = SlopeNormal(dict, cx, cz, topHeight, cellSize);
                bool gpuShade = attr && SkylineRuntime.LodAttrShaderOn;
                Color cellLight = cellBase;
                if (SlopeShadingStrength > 0f || SelfShadowStrength > 0f || SkylineLodCloudShadow.Enabled) {
                    float gain = 1f;
                    // [v0.1.99] 里程碑 2.3：**云层阴影**（与体积云 shader 同源的低频项）——
                    //   用户口径里的"云层阴影"：云遮住太阳 → 地面变暗。它是低频的，烘进顶点色即可，
                    //   而且这样 CPU 烘焙/GPU 体积着色两条路径**自动一致**（与自阴影同一做法）。
                    if (SkylineLodCloudShadow.Enabled) {
                        float cloudFactor = SkylineLodCloudShadow.Factor(
                            x0 + cellSize * 0.5f, topHeight, z0 + cellSize * 0.5f, lodSun, m_sunAmount);
                        SkylineLodCloudShadow.Note(cloudFactor);
                        gain *= SkylineLodCloudShadow.ShadeFactor(cloudFactor);
                    }
                    // 坡向明暗：**GPU 路径不在 CPU 烘焙**（着色器按顶点法线逐片元算同一个式子），否则会算两遍。
                    if (SlopeShadingStrength > 0f && !gpuShade) {
                        // v0.1.19：坡向明暗按日照量淡出
                        gain *= MathUtils.Lerp(1f, SlopeGainFromNormal(topNormal, lodSun), m_sunAmount);
                    }
                    if (SelfShadowStrength > 0f) {
                        // v0.1.19：LOD 自阴影（CPU 射线步进，见 notes/88 §5 的第 1 条路线）。
                        // [v0.1.77] 这是**可见性**计算（要在高度场里步进），CPU/GPU 两条路径**都**按当前口径
                        // 烘焙进顶点色 —— 着色器负责的是"按法线算明暗"，不是"算可见性"。
                    // [v0.1.98] 阴影按"最小体素"步进（32 m 统一档传 16 m 的 `m_cells`；其余档 shadowDict=null）
                    float shadow = SelfShadowFactor(dict, cx, cz, topHeight, cellSize, lodSun,
                        shadowDict, shadowCellSize);
                    if (shadowDict != null && shadowCellSize > 0 && shadowCellSize < cellSize) {
                        // 诊断：同一格再按"整块单元"步进一次，量化两者的差别（只有 32 m 档会走这里）
                        float shadowCellLevel = SelfShadowFactor(dict, cx, cz, topHeight, cellSize, lodSun);
                        MinVoxelShadowCompared++;
                        int delta = (int)MathF.Round(MathF.Abs(shadow - shadowCellLevel) * 255f);
                        if (delta > 0) {
                            MinVoxelShadowDiffCells++;
                            MinVoxelShadowDiffSum255 += delta;
                        }
                    }
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
                PutByNormal(vi, x0, y, z0, cellLight, u0, v0, topNormal, true, value);
                PutByNormal(vi + 1, x0 + cellSize, y, z0, cellLight, u0 + du, v0, topNormal, true, value);
                PutByNormal(vi + 2, x0 + cellSize, y, z0 + cellSize, cellLight, u0 + du, v0 + du, topNormal, true, value);
                PutByNormal(vi + 3, x0, y, z0 + cellSize, cellLight, u0, v0 + du, topNormal, true, value);
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
                // [v0.1.60] 侧壁的面编号（CellFace：0=+Z 1=+X 2=-Z 3=-X），用于属性路径的法线与
                // CPU 烘焙面明暗。`side` 的顺序来自 s_sideDx/s_sideDz：0=+Z、1=-Z、2=+X、3=-X。
                int wallFace = side switch { 0 => 0, 1 => 2, 2 => 1, _ => 3 };
                // GPU 体积着色器会**自己**按法线算明暗 → 这条路不再把面因子烘焙进顶点色（否则明暗算两遍）。
                bool gpuShade = attr && SkylineRuntime.LodAttrShaderOn;
                Color wallColor = (gpuShade || !SkylineFaceShading.Enabled)
                    ? light
                    : SkylineFaceShading.Apply(light, wallFace);
                switch (side) {
                    case 0:      // +Z 面（z1）: (x0,z1) (x1,z1)
                        Put(vi, x0, yHigh, z1, wallColor, wu, wv, wallFace, wallValue);
                        Put(vi + 1, x1, yHigh, z1, wallColor, wu + wd, wv, wallFace, wallValue);
                        Put(vi + 2, x1, yLow, z1, wallColor, wu + wd, wv + wd, wallFace, wallValue);
                        Put(vi + 3, x0, yLow, z1, wallColor, wu, wv + wd, wallFace, wallValue);
                        break;
                    case 1:      // -Z 面（z0）
                        Put(vi, x1, yHigh, z0, wallColor, wu, wv, wallFace, wallValue);
                        Put(vi + 1, x0, yHigh, z0, wallColor, wu + wd, wv, wallFace, wallValue);
                        Put(vi + 2, x0, yLow, z0, wallColor, wu + wd, wv + wd, wallFace, wallValue);
                        Put(vi + 3, x1, yLow, z0, wallColor, wu, wv + wd, wallFace, wallValue);
                        break;
                    case 2:      // +X 面（x1）
                        Put(vi, x1, yHigh, z0, wallColor, wu, wv, wallFace, wallValue);
                        Put(vi + 1, x1, yHigh, z1, wallColor, wu + wd, wv, wallFace, wallValue);
                        Put(vi + 2, x1, yLow, z1, wallColor, wu + wd, wv + wd, wallFace, wallValue);
                        Put(vi + 3, x1, yLow, z0, wallColor, wu, wv + wd, wallFace, wallValue);
                        break;
                    default:     // -X 面（x0）
                        Put(vi, x0, yHigh, z1, wallColor, wu, wv, wallFace, wallValue);
                        Put(vi + 1, x0, yHigh, z0, wallColor, wu + wd, wv, wallFace, wallValue);
                        Put(vi + 2, x0, yLow, z0, wallColor, wu + wd, wv + wd, wallFace, wallValue);
                        Put(vi + 3, x0, yLow, z1, wallColor, wu, wv + wd, wallFace, wallValue);
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
            // 顶点缓冲用**哪一种声明**决定了 engine 侧每顶点读多少字节（步长从声明取）。
            var vb = attr
                ? new VertexBuffer(SkylineLodVertex.VertexDeclaration, vertexCount)
                : new VertexBuffer(TerrainVertex.VertexDeclaration, vertexCount);
            if (attr) {
                vb.SetData(attrVertices, 0, vertexCount);
            }
            else {
                vb.SetData(bakedVertices, 0, vertexCount);
            }
            var ib = new IndexBuffer(bigIndices ? IndexFormat.ThirtyTwoBits : IndexFormat.SixteenBits, ii);
            if (bigIndices) {
                ib.SetData(indices32, 0, ii);
            }
            else {
                ib.SetData(indices, 0, ii);
            }
            // [v0.1.62] "单元数"仍按**主表面单元**报（`keys.Count`），第二层表面的顶面数单独统计
            if (layer == 0) {
                SecondQuads = secondSheets;
                SecondWallQuads = secondWallCount;
            }
            else if (layer == 1) {
                SecondQuads += secondSheets;
                SecondWallQuads += secondWallCount;
            }
            SetLayerMesh(layer, vb, ib, ii, keys.Count, vertexCount);
        }

        /// <summary>[v0.1.45] 把一套网格写回对应层（0=粗 16 m、1=细 8 m、2=近环 4 m）。</summary>
        static void SetLayerMesh(int layer, VertexBuffer vb, IndexBuffer ib, int indexCount, int cellsInMesh,
                                 int vertexCount) {
            switch (layer) {
                case 2:
                    Utilities.Dispose(ref m_vbNear);
                    Utilities.Dispose(ref m_ibNear);
                    m_vbNear = vb;
                    m_ibNear = ib;
                    m_indexCountNear = indexCount;
                    m_cellsInMeshNear = cellsInMesh;
                    m_vertexCountNear = vertexCount;
                    break;
                case 1:
                    Utilities.Dispose(ref m_vbFine);
                    Utilities.Dispose(ref m_ibFine);
                    m_vbFine = vb;
                    m_ibFine = ib;
                    m_indexCountFine = indexCount;
                    m_cellsInMeshFine = cellsInMesh;
                    m_vertexCountFine = vertexCount;
                    break;
                default:
                    Utilities.Dispose(ref m_vb);
                    Utilities.Dispose(ref m_ib);
                    m_vb = vb;
                    m_ib = ib;
                    m_indexCount = indexCount;
                    m_cellsInMesh = cellsInMesh;
                    m_vertexCount = vertexCount;
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

        /// <summary>当前相机视点（壳仓的距离分档也用它，保证两边同一基准）。</summary>
        public static Vector3 CameraViewPosition() {
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
            // [v0.1.60] 里程碑 1.3：属性着色器路径 —— 三层网格都带**面法线 + 材质 id**，
            // 明暗改成在 GPU 上按法线算（`SkylineLodVolume`，与 CPU 烘焙面因子同一条公式）。
            // [v0.1.77] 着色器没准备好时**回落到 Opaque**（返回 false），不让远景整层消失。
            if (SkylineRuntime.LodAttrShaderOn && SkylineRuntime.LodVertexAttributes
                && DrawWithAttributeShader(camera)) {
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
                    .SetValue(SkylineRuntime.FogBand(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity), "lod"));
                // v0.1.1：**与真实地形共用同一条视图雾曲线**。原来自算 [0.55R, R] 的雾带，
                // 而原版真实地形在 `视距 × 0.8` 处就已 100% 雾化——两者交界"地形全雾消失 /
                // LOD 无雾跳出"，交接感明显（用户反馈）。现在 `SkylineAtmosphere.AdjustHazeSpan`
                // 已把视图雾的跨度拉远到 LOD 半径的 90%，这里直接采用 sky 的 (start, density)，
                // 两个渲染层在交界处与全程都连续。
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(SkylineRuntime.HazeStartDensity(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity), "lod"));
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
            // [v0.1.73] 区域仓模式：只写**变脏的区域**（每个区域整块重写），不重写整表。
            if (RegionStoreEnabled) {
                SaveDirtyRegions();
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
            // [v0.1.73] 区域仓模式：只把**相机附近**的区域读进内存（远处留在盘上、按需回读）。
            ResetRegionState();
            if (RegionStoreEnabled) {
                ScanKnownRegions();
                MigrateLegacyFile();
                LoadNearbyRegions();
                m_dirty = true;
                Log.Information($"SkylineLod: region store loaded {m_cells.Count} cells (+{m_cellsFine.Count} fine)"
                    + $" residentRegions={m_residentRegions.Count} knownRegions={m_knownRegions.Count}");
                return;
            }
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

        /// <summary>
        /// [v0.1.130] 里程碑 2.6 只读表：**合并阶梯的三档**（32 / 64 / 128 m）、边界、以及每档的
        /// 单元数与网格量。每档的"等效精度"= 16 m 单元边长 ÷ 合并倍数（1 m / 2 m / 4 m），
        /// 与 DH 的 detail level 1 / 2 / 3 对齐；每档仍然只出一张 32³ 采样的表面网格。
        /// </summary>
        public static string LodMergeTable() {
            (float b1, float b2) = MergeBoundaries();
            JsonArray tiers = [
                new JsonObject {
                    ["tier"] = 1, ["merge"] = "2³", ["cellMetres"] = CellSize << 1,
                    ["voxelMetres"] = 1.0, ["bandMetres"] = new JsonArray(0.0, Math.Round(b1, 1)),
                    ["cells"] = m_cells32.Count, ["cellsInMesh"] = m_cellsInMesh,
                    ["indices"] = m_indexCount,
                },
                new JsonObject {
                    ["tier"] = 2, ["merge"] = "4³", ["cellMetres"] = CellSize << 2,
                    ["voxelMetres"] = 2.0, ["bandMetres"] = new JsonArray(Math.Round(b1, 1), Math.Round(b2, 1)),
                    ["cells"] = m_cellsM2.Count, ["cellsInMesh"] = m_cellsInMeshFine,
                    ["indices"] = m_indexCountFine,
                },
                new JsonObject {
                    ["tier"] = 3, ["merge"] = "8³", ["cellMetres"] = CellSize << 3,
                    ["voxelMetres"] = 4.0, ["bandMetres"] = new JsonArray(Math.Round(b2, 1), (double)RadiusMetres),
                    ["cells"] = m_cellsM3.Count, ["cellsInMesh"] = m_cellsInMeshNear,
                    ["indices"] = m_indexCountNear,
                }
            ];
            return new JsonObject {
                ["enabled"] = MergeLadderEnabled,
                ["uniformMode"] = UniformBeyondLoaded && !MergeLadderEnabled,
                ["radiusMetres"] = (double)RadiusMetres,
                ["boundariesMetres"] = new JsonArray(Math.Round(b1, 1), Math.Round(b2, 1)),
                ["tiers"] = tiers,
                ["distanceHistogram"] = MergeDistanceHistogram(),
                ["note"] = "档位边界取 DH 的 unit×16×base^level（MEDIUM = 384/768 m 绝对距离）；"
                           + "每档 32³ 采样，等效精度 1/2/4 m（= DH 的 detail level 1/2/3）"
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.132] 诊断：把 `m_cells` 按"到相机的距离"分桶，并给出**每桶会被分到哪一档**
        /// （档号 = `clamp(DhLevelForDistance(d), 1, 3)`）。用来钉死"阶梯臂在同半径下画得比统一档少"
        /// 到底是**分配**的问题还是**网格band**的问题 —— 光看两端计数是分不出来的。
        /// </summary>
        public static string MergeDistanceHistogram() {
            (float b1, float b2) = MergeBoundaries();
            float[] edges = [192f, b1, b2, 1536f, 3072f, float.MaxValue];
            string[] names = ["0-192", "192-384", "384-768", "768-1536", "1536-3072", "3072+"];
            int[] counts = new int[names.Length];
            int[] tier1 = new int[names.Length];
            int[] tier2 = new int[names.Length];
            int[] tier3 = new int[names.Length];
            Vector3 camera = CameraViewPosition();
            foreach (KeyValuePair<long, Cell> kv in m_cells) {
                int cx = (int)(kv.Key >> 32);
                int cz = (int)(uint)kv.Key;
                float wx = (cx << CellShift) + CellSize * 0.5f;
                float wz = (cz << CellShift) + CellSize * 0.5f;
                float dx = wx - camera.X, dz = wz - camera.Z;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                int bin = 0;
                while (bin < edges.Length - 1 && d > edges[bin]) {
                    bin++;
                }
                counts[bin]++;
                switch (TierForDistance(d)) {
                    case 1: tier1[bin]++; break;
                    case 2: tier2[bin]++; break;
                    default: tier3[bin]++; break;
                }
            }
            JsonArray rows = [];
            for (int i = 0; i < names.Length; i++) {
                rows.Add(new JsonObject {
                    ["band"] = names[i], ["cells16m"] = counts[i],
                    ["tier1"] = tier1[i], ["tier2"] = tier2[i], ["tier3"] = tier3[i],
                });
            }
            return rows.ToJsonString();
        }

        public static string Describe() =>
            $"lod:enabled={Enabled} cells={m_cells.Count}(+{m_cellsFine.Count}f+{m_cellsNear.Count}n) "
            + $"inMesh={m_cellsInMesh}(+{m_cellsInMeshFine}f+{m_cellsInMeshNear}n) "
            + $"indices={m_indexCount}(+{m_indexCountFine}f+{m_indexCountNear}n) "
            + $"radius={RadiusMetres:0}m cell={CellSize}/{FineSize}/{NearSize} rebuilds={m_rebuilds} "
            + $"unloadCapture={CellsCapturedOnUnload}(skip={CellCaptureSkippedOverBudget},{CellCaptureLastMs:0.0}ms) "
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
            // [v0.1.62] 精细层**实际**覆盖多远（与 `RebuildMesh` 同一公式；不是简单的 视距×系数）
            float viewRangeNow = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float skipRadiusNow = viewRangeNow + FineSize * 0.5f;
            float fineRangeNow = MathF.Max(viewRangeNow * FineRangeFactor, skipRadiusNow + FineSize * 4f);
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
                // [v0.1.62] 第二层表面（"所有裸露在外的方块"的另一半）
                ["secondSurface"] = SecondSurfaceEnabled,
                ["cellsWithSecond"] = CellsWithSecond,
                ["secondQuads"] = SecondQuads,
                ["secondWallQuads"] = SecondWallQuads,
                ["secondMinDrop"] = SecondMinDrop,
                ["secondGap"] = SecondGap,
                ["secondSearchDepth"] = SecondSearchDepth,
                // [v0.1.60] 顶点格式与字节：属性格式（SkylineLodVertex，28 B）还是老格式（TerrainVertex，20 B）
                ["vertexAttributes"] = SkylineRuntime.LodVertexAttributes,
                ["vertexStride"] = SkylineRuntime.LodVertexAttributes ? SkylineLodVertex.Stride : 20,
                ["attrShader"] = SkylineRuntime.LodAttrShaderOn,
                // [v0.1.78] 坡向/自阴影用的光源方向（= 真太阳；`dotWithTracked` 恒为 1）
                ["slopeSun"] = SlopeSunInfo(),
                ["meshVertices"] = m_vertexCount + m_vertexCountFine + m_vertexCountNear,
                ["meshVertexBytes"] = (long)(m_vertexCount + m_vertexCountFine + m_vertexCountNear)
                    * (SkylineRuntime.LodVertexAttributes ? SkylineLodVertex.Stride : 20),
                ["nearLayerSwitch"] = "skyline.LodNearLayerEnabled / skyline.LodNearBandMark() 立刻铺满",
                ["refreshedOnUnload"] = m_refreshedOnUnload,
                ["radiusMetres"] = RadiusMetres,
                ["fineRangeFactor"] = FineRangeFactor,
                ["fineRangeMetres"] = Math.Round(fineRangeNow, 1),
                ["cellSizeBlocks"] = CellSize,
                ["fineCellSizeBlocks"] = FineSize,
                // [v0.1.98] 里程碑 2.2：加载距离之外**统一一档**（默认 32 m）
                ["uniformBeyondLoaded"] = UniformBeyondLoaded,
                ["uniformExtraShift"] = UniformExtraShift,
                ["uniformCellSizeBlocks"] = CellSize << Math.Clamp(UniformExtraShift, 0, 3),
                ["uniformCells"] = m_cells32.Count,
                ["uniformCellsInMesh"] = UniformBeyondLoaded ? m_cellsInMesh : 0,
                ["uniformMeshIndices"] = UniformBeyondLoaded ? m_indexCount : 0,
                // [v0.1.130] 里程碑 2.6：合并阶梯
                ["mergeLadderEnabled"] = MergeLadderEnabled,
                ["mergeBoundariesMetres"] = new JsonArray(
                    Math.Round(MergeBoundaries().B1, 1), Math.Round(MergeBoundaries().B2, 1)),
                ["merged64Metres"] = CellSize << 2,
                ["merged128Metres"] = CellSize << 3,
                // [v0.1.98] 里程碑 2.3：阴影是否真的按"最小体素"参与（与"整块步进"的差别量化）
                ["minVoxelShadowCompared"] = MinVoxelShadowCompared,
                ["minVoxelShadowDiffCells"] = MinVoxelShadowDiffCells,
                ["minVoxelShadowDiffSum255"] = MinVoxelShadowDiffSum255,
                ["minVoxelShadowSizeBlocks"] = UniformBeyondLoaded
                    ? (CellSize << Math.Clamp(UniformExtraShift, 0, 3)) : CellSize,
                ["minVoxelShadowMarchBlocks"] = CellSize,
                // [v0.1.99] 里程碑 2.3：云层阴影（与体积云 shader 同源）
                ["cloudShadowEnabled"] = SkylineLodCloudShadow.Enabled,
                ["cloudShadowDepth"] = SkylineLodCloudShadow.Depth,
                ["cloudShadowSteps"] = SkylineLodCloudShadow.Steps,
                ["cloudShadowSampledCells"] = SkylineLodCloudShadow.SampledCells,
                ["cloudShadowAffectedCells"] = SkylineLodCloudShadow.AffectedCells,
                ["cloudShadowMinFactor"] = Math.Round(SkylineLodCloudShadow.LastMinFactor, 4),
                ["cloudShadowAvgFactor"] = Math.Round(SkylineLodCloudShadow.LastAvgFactor, 4),
                // [v0.1.100] 里程碑 2.3 第三项：固定光源的亮度斑块（采"上方空气格光照"）
                ["airLightPatch"] = LodAirLightPatch,
                ["airLightBrightenedCells"] = m_airLightBrightenedCells,
                ["coveredAreaKm2"] = Math.Round(covered / 1_000_000f, 4),
                ["harvestedCells"] = m_harvestedCells,
                ["meshRebuilds"] = m_rebuilds,
                ["cellReleaseEnabled"] = LodCellReleaseEnabled,
                ["cellReleaseFactor"] = (double)LodCellReleaseFactor,
                ["cellsReleasedCoarse"] = m_lodCellsReleasedCoarse,
                ["cellsReleasedFine"] = m_lodCellsReleasedFine,
                ["cellsReleasedLastCoarse"] = m_lodCellsReleasedLastCoarse,
                ["cellsReleasedLastFine"] = m_lodCellsReleasedLastFine,
                // [v0.1.73] 区域仓（LOD 单元落盘 + 按需回读）
                ["regionStoreEnabled"] = RegionStoreEnabled,
                ["regionMetres"] = Math.Round(RegionMetres, 1),
                ["regionKeepMetres"] = (double)RegionKeepMetres,
                ["regionsResident"] = RegionsResident,
                ["regionsKnown"] = RegionsKnown,
                ["regionsDirty"] = RegionsDirty,
                ["regionsLoadedTotal"] = RegionsLoadedTotal,
                ["regionsSavedTotal"] = RegionsSavedTotal,
                ["regionsEvictedTotal"] = RegionsEvictedTotal,
                ["regionLastError"] = m_regionLastError,
                ["loadedChunks"] = loadedColumns,
                // [v0.1.129] 里程碑 3.2：卸载前补采（脏队列排空的关键路径）
                ["cellsCapturedOnUnload"] = CellsCapturedOnUnload,
                ["cellCaptureSkippedOverBudget"] = CellCaptureSkippedOverBudget,
                ["cellCapturePerCall"] = CellCapturePerCall,
                ["cellCaptureLastMs"] = Math.Round(CellCaptureLastMs, 2),
                ["refresh"] = RefreshSurveyJson(),
                ["lastError"] = m_lastError
            }.ToJsonString();
        }
    }
}
