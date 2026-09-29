using Engine;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— 面向"创意建筑"的运行时开关与常量（**新挂载的模块**，非 mod）。
    ///
    /// 设计口径（v0.0.3）：
    ///   * 世界竖直范围 = <see cref="TerrainChunk.MinHeight"/> .. <see cref="TerrainChunk.HeightMinusOne"/>
    ///     （当前 -1024..1023，共 2048 层）。
    ///   * **生存余量** <see cref="SurvivalMargin"/> = 64：建筑范围上下各留 64 格，
    ///     角色在 [BuildMinY-64, BuildMaxY+64] = [-1088, 1087] 内不掉血、不缺氧；
    ///     超出这个范围才按原版逻辑扣虚空伤害/缺氧。
    ///   * <see cref="FreeViewMode"/>（取景模式）：打开后**完全不受高度/深度伤害限制**，
    ///     为将来"超限高度飞行取景/漫游"做底层准备（本版暂不加 UI 按钮）。
    ///
    /// 这些静态成员可以被外部直接调用，例如 AgentBridge：
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime.FreeViewMode","value":true}
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime","member":"Describe","args":[]}
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>建筑范围之外、仍允许角色生存的余量（格）。</summary>
        public const int SurvivalMargin = 64;

        /// <summary>取景模式：不受高度/深度伤害与缺氧限制（默认关闭）。</summary>
        public static bool FreeViewMode { get; set; }

        /// <summary>可建造的最低层。</summary>
        public static int BuildMinY => TerrainChunk.MinHeight;

        /// <summary>可建造的最高层。</summary>
        public static int BuildMaxY => TerrainChunk.HeightMinusOne;

        /// <summary>角色生存的下限（含 64 格余量）。</summary>
        public static int SurvivalMinY => TerrainChunk.MinHeight - SurvivalMargin;

        /// <summary>角色生存的上限（含 64 格余量）。</summary>
        public static int SurvivalMaxY => TerrainChunk.HeightMinusOne + SurvivalMargin;

        /// <summary>该 y 是否在建筑范围内。</summary>
        public static bool IsInsideBuildRange(int y) => y >= BuildMinY && y <= BuildMaxY;

        /// <summary>该 y 是否允许角色存活（取景模式恒为真）。</summary>
        public static bool IsInsideSurvivalRange(float y) =>
            FreeViewMode || (y >= SurvivalMinY && y <= SurvivalMaxY);

        /// <summary>一行状态，便于桥/日志读取。</summary>
        public static string Describe() =>
            $"Skyline build=[{BuildMinY},{BuildMaxY}] survival=[{SurvivalMinY},{SurvivalMaxY}] freeView={FreeViewMode} "
            + $"residency={ChunkResidencyMode} regions={Regions.Count} v={m_residencyVersion} "
            + SkylineFurniture.Describe() + " " + SkylineRender.Describe() + " " + SkylineLod.Describe();

        // ==========================================================================================
        // v0.0.9：高复杂度家具的几何预算（实现在 Game/SkylineFurniture.cs，这里只做转发，
        // 好处是桥不用改代码就能通过既有 `skyline` 根读写——桥根是 typeof(SkylineRuntime)）
        // ==========================================================================================

        public static bool FurnitureBudgetEnabled {
            get => SkylineFurniture.BudgetEnabled;
            set => SkylineFurniture.BudgetEnabled = value;
        }

        public static int FurnitureMaxStageVertices {
            get => SkylineFurniture.MaxStageFurnitureVertices;
            set => SkylineFurniture.MaxStageFurnitureVertices = value;
        }

        public static bool FurnitureFallbackBox {
            get => SkylineFurniture.FallbackBox;
            set => SkylineFurniture.FallbackBox = value;
        }

        /// <summary>A/B 调试：强制所有家具渲染成方盒占位（默认关）。</summary>
        public static bool FurnitureForceBox {
            get => SkylineFurniture.ForceBox;
            set => SkylineFurniture.ForceBox = value;
        }

        /// <summary>A/B 调试：只把指定设计索引的家具强制成方盒（-1 = 关）。</summary>
        public static int FurnitureForceBoxDesign {
            get => SkylineFurniture.ForceBoxDesign;
            set => SkylineFurniture.ForceBoxDesign = value;
        }

        public static string FurnitureDescribe() => SkylineFurniture.Describe();

        public static void FurnitureResetStats() => SkylineFurniture.ResetStats();

        // ==========================================================================================
        // v0.0.9 探索：三层球形渲染原型（实现在 Game/SkylineRender.cs）
        //   * RenderEnabled 默认 **false**：关着时只有只读统计，渲染逐位等于现状
        //   * 三层球 = 占位球（MBD/占替距） + 视觉球（视距，二维→三维） + 加载球（被波及的区块都要加载）
        // ==========================================================================================

        /// <summary>是否真的按三层球把远处被埋藏家具替换成占位方盒（默认关）。</summary>
        public static bool RenderEnabled {
            get => SkylineRender.Enabled;
            set => SkylineRender.Enabled = value;
        }

        /// <summary>只读勘察：三层球在当前相机下的规模与占位球能省多少顶点（不改变渲染）。</summary>
        public static string RenderSurvey() => SkylineRender.Survey();

        public static string RenderSurvey(int radiusColumns) => SkylineRender.Survey(radiusColumns);

        public static string RenderDescribe() => SkylineRender.Describe();

        /// <summary>每个 tick 允许的 LOD 区块重建数（默认 1，防跨档重建风暴）。</summary>
        public static int RenderMaxRebakesPerTick {
            get => SkylineRender.MaxRebakesPerTick;
            set => SkylineRender.MaxRebakesPerTick = value;
        }

        public static void RenderResetStats() => SkylineRender.ResetStats();

        public static void RenderResetChunkLod() => SkylineRender.ResetChunkLod();

        // ==========================================================================================
        // v0.1.0：超视距 LOD 层（实现在 Game/SkylineLod.cs，学习 Distant Horizons 的"加载即采样 + 持久化 + 视距外渲染"）
        // ==========================================================================================

        public static bool LodEnabled {
            get => SkylineLod.Enabled;
            set => SkylineLod.Enabled = value;
        }

        public static float LodRadiusMetres {
            get => SkylineLod.RadiusMetres;
            set => SkylineLod.RadiusMetres = value;
        }

        /// <summary>
        /// [v0.1.98] 里程碑 2.2：**加载距离之外的 LOD 统一一档**（默认开，单元 32 m）。
        /// 开 = 只画这一档（近环 4 m / 精细 8 m 两档不建网格），画面上只有一个分辨率，
        /// 才能用放大截图 + 双模型去**定分级边界**；关 = 逐位回到 v0.1.97 的三档（A/B 用）。
        /// </summary>
        public static bool LodUniformBeyond {
            get => SkylineLod.UniformBeyondLoaded;
            set {
                if (SkylineLod.UniformBeyondLoaded != value) {
                    SkylineLod.UniformBeyondLoaded = value;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>
        /// [v0.1.98] 里程碑 2.2：**壳带统一单档**（默认 16 = 一个 16 m 立方体一格；`0` = 关，回到 1/2/4/8/16 分级）。
        /// 用户口径"LOD 分辨率分级在视觉上过于明显"指的正是壳带里那一串档位 —— 先统一，再用放大截图 + 双模型定边界。
        /// </summary>
        public static int CubeShellUniformStep {
            get => SkylineCubeShellStore.UniformStep;
            set {
                int v = Math.Clamp(value, 0, SkylineCubeShellStore.CubeSize);
                if (SkylineCubeShellStore.UniformStep != v) {
                    SkylineCubeShellStore.UniformStep = v;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>
        /// [v0.1.101] 里程碑 2.2 的下一步：**档位按"屏幕像素"定**（`false` = 逐位回到绝对米数阶梯；默认 **关**）。
        /// 这是把"分级边界"从拍脑袋的米数变成**可核对数字**的那一步（用户口径："基于实际渲染粒度去算观感"）。
        /// </summary>
        public static bool CubeShellPixelTiers {
            get => SkylineCubeShellStore.PixelAwareTiers;
            set {
                if (SkylineCubeShellStore.PixelAwareTiers != value) {
                    SkylineCubeShellStore.PixelAwareTiers = value;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>[v0.1.101] 细一档小于这么多像素（屏幕上）才允许降档。</summary>
        public static float CubeShellPixelThreshold {
            get => SkylineCubeShellStore.PixelThreshold;
            set => SkylineCubeShellStore.PixelThreshold = Math.Clamp(value, 0.5f, 32f);
        }

        /// <summary>[v0.1.101] 档位像素表（只读）：把"分级边界"变成可核对的数字。</summary>
        public static string CubeShellTierPixelTable(string distancesMetres = "48,96,192,384,768,1024")
            => SkylineCubeShellStore.TierPixelTable(distancesMetres);

        /// <summary>[v0.1.98] 统一档的额外位移（默认 1 ⇒ 单元 32 m）。改动会立刻重建 LOD。</summary>
        public static int LodUniformExtraShift {
            get => SkylineLod.UniformExtraShift;
            set {
                int v = Math.Clamp(value, 0, 3);
                if (SkylineLod.UniformExtraShift != v) {
                    SkylineLod.UniformExtraShift = v;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>
        /// [v0.1.130] 里程碑 2.6：**外围区块合并阶梯**（默认 **关** = v0.1.98 起的"加载距离之外一档 32 m"）。
        /// 开 = 按 DH 的档位边界分成 32 / 64 / 128 m 三档（合并 2³/4³/8³ 个 16 m 单元，
        /// 每档仍是 32³ 采样，等效精度 1/2/4 m），把绘制半径推到 2048 m 时外围仍只有粗档。
        /// </summary>
        public static bool LodMergeLadder {
            get => SkylineLod.MergeLadderEnabled;
            set {
                if (SkylineLod.MergeLadderEnabled != value) {
                    SkylineLod.MergeLadderEnabled = value;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>[v0.1.130] 里程碑 2.6 只读表：三档的边长/边界/单元数/网格量。</summary>
        public static string LodMergeTable() => SkylineLod.LodMergeTable();

        /// <summary>[v0.1.131] 里程碑 2.5：原版**天空染色**（朝霞/晚霞/霾）替换成中性昼光渐变。</summary>
        public static bool AmbienceNeutralSky {
            get => SkylineAtmosphere.NeutralSkyTintRemoved;
            set => SkylineAtmosphere.NeutralSkyTintRemoved = value;
        }

        /// <summary>[v0.1.131] 里程碑 2.5：原版**降水粒子**（雨/雪柱）开关。</summary>
        public static bool AmbiencePrecipitation {
            get => SkylineAtmosphere.VanillaPrecipitationEnabled;
            set => SkylineAtmosphere.VanillaPrecipitationEnabled = value;
        }

        /// <summary>[v0.1.131] 里程碑 2.5 账本：两项移除的调用/替换计数。</summary>
        public static string AmbienceStatus() => SkylineAtmosphere.AmbienceStatus();

        /// <summary>
        /// [v0.1.133] 审计缺陷 B-01 的自检：**合并聚合的排列不变性**
        /// （n = 4/16/64/65 个合成样本 × 原序/倒序/确定性洗牌 三种顺序，输出必须完全相同）。
        /// </summary>
        public static string LodMergeAggregationSelfCheck() => SkylineLod.MergeAggregationSelfCheck();

        /// <summary>[v0.1.133] 空间覆盖口径（审计缺陷 A-01）：逐档"已采样面积 vs 真正进网格面积"。</summary>
        public static string LodDrawnCoverage() => SkylineLod.LodDrawnCoverage();

        /// <summary>
        /// [v0.1.133] **只给审计用的落盘/回读探针**：调用一次即 `SkylineLod.Load()`（清空内存 LOD 单元，
        /// 再从区域仓读取相机附近的区域）。用途：验证区域格式 v2 的字段往返（`LightAir` 与第二层表面不再丢）。
        /// 可回滚：LOD 单元由"加载中的区块 + 区域仓"两条既有通道重建，下一次轮转采集会补齐。
        /// </summary>
        public static void LodReloadNow() => SkylineLod.Load();

        /// <summary>[v0.1.134] 审计 B-02 的事实基础：列出某坐标所属 LOD 合并块的全部源 16 m 格。</summary>
        public static string LodBlockProvenance(int worldX, int worldZ) =>
            SkylineLod.LodBlockProvenance(worldX, worldZ);

        /// <summary>[v0.1.135] 壳层立方体的**表面体素壳**只读回读（B-02 第二份事实基础）。</summary>
        public static string ShellVoxelProbe(int cx, int cy, int cz) =>
            SkylineCubeShellStore.ShellVoxelProbe(cx, cy, cz);

        /// <summary>[v0.1.135] 编辑失效化壳层的开关与计数（A/B + 代价观察）。</summary>
        public static bool ShellInvalidateOnEdit {
            get => SkylineCubeShellStore.InvalidateOnEdit;
            set => SkylineCubeShellStore.InvalidateOnEdit = value;
        }

        public static long ShellInvalidatedByEdit => SkylineCubeShellStore.InvalidatedByEdit;

        /// <summary>
        /// **[v0.1.156] 把"壳接管后 LOD 让位"暴露成可写**（桥侧原先是只读的
        /// `CubeShellSurvey.restrictLod`）。
        ///
        /// 为什么要它：`LodSurvey` 报的 `uniformCells`（统一档**表**里的格数）与
        /// `uniformCellsInMesh`（真正**进网格**的格数）长期对不上（实测快照 347/2039、723/3072、1437/2680）。
        /// 读代码后把范围收敛到"**表 → 网格**"这一步：`RebuildMeshCore` 里有一条默认开启的规则 ——
        /// **`RestrictLod` 与 `SkylineCubeShellStore.HasShellInBand(...)` 同时成立就 `continue`**
        /// （即"壳接管了，LOD 让位"）。
        /// 但原先**没有写入口**（`set RestrictLod` 报 "not a writable property/field"），
        /// 于是这条"是不是壳让位造成的"**做不了 A/B**（`notes/272 §8.3`）。
        /// 本属性就是那个入口：`skyline.RestrictLod = false` → LOD 不再让位（两层叠着画，
        /// 观感上会发花，但**记账口径**立刻可比）。默认值不变（`true`）。
        /// </summary>
        public static bool RestrictLod {
            get => SkylineCubeShellStore.RestrictLod;
            set => SkylineCubeShellStore.RestrictLod = value;
        }

        /// <summary>**[v0.1.156 · CC P2 `104218Z`] 壳仓压实策略（可写）**：
        /// 死记录占比阈值（默认 0.30）与文件绝对上限（默认 512 MiB）。
        /// 为什么要它：旧口径 `pending*2 &lt; max(64, fileRecords)` 在 37 万条时恒真 ⇒ 永远 append、
        /// 墓碑永不回收（实测 1.42 GB vs 活动记录理论 ~26~56 MB）。</summary>
        public static double CubeShellCompactDeadRatio {
            get => SkylineCubeShellStore.CompactDeadRatio;
            set => SkylineCubeShellStore.CompactDeadRatio = Math.Clamp(value, 0.0, 1.0);
        }

        public static long CubeShellCompactMaxBytes {
            get => SkylineCubeShellStore.CompactMaxBytes;
            set => SkylineCubeShellStore.CompactMaxBytes = Math.Max(1024L * 1024, value);
        }

        /// <summary>[v0.1.136 · CC 审计 P2] 区域单元格式的内存往返自检（含 HasSecond=true 合成样本）。</summary>
        public static string RegionCellRoundTripSelfCheck() =>
            SkylineLod.RegionCellRoundTripSelfCheck();

        /// <summary>[v0.1.136 · CC 审计 P3] 读取失败的坏区域数（读完即从 known 摘掉）。</summary>
        public static long RegionsCorrupt => SkylineLod.RegionsCorrupt;

        // ===== [v0.1.137] 里程碑 5.2：Chunky 式区块预加载（虚拟加载点） =====
        /// <summary>开始预加载一块区域（中心世界坐标 + 半径区块数）。</summary>
        public static string ChunkPreloadStart(float centerX, float centerZ, int radiusChunks) =>
            SkylineChunkPreloader.Start(centerX, centerZ, radiusChunks);

        /// <summary>预加载进度/ETA/帧开销账本。</summary>
        public static string ChunkPreloadStatus() => SkylineChunkPreloader.Status();

        /// <summary>取消预加载（摘掉虚拟加载点）。</summary>
        public static string ChunkPreloadCancel() => SkylineChunkPreloader.Cancel();

        /// <summary>释放预加载（已完成时用；未完成则等同取消）。</summary>
        public static string ChunkPreloadRelease() => SkylineChunkPreloader.Release();

        /// <summary>[v0.1.138] 更新器逐 pass 耗时账本（只读，决定"并行哪一段"）。</summary>
        public static string TerrainUpdateStats() =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater?.DescribeStatistics()
            ?? "{\"err\":\"no updater\"}";

        /// <summary>[v0.1.138] 区块内容指纹（FNV-1a 64 over all cell values）。</summary>
        public static string ChunkContentHash(int cx, int cz) => SkylineChunkDeterminism.ChunkHash(cx, cz);

        /// <summary>[v0.1.139] 区块**高度图**指纹（只哈希 Top/Bottom/SunlightHeight，不受邻居光照传播影响）。</summary>
        public static string ChunkHeightHash(int cx, int cz) => SkylineChunkDeterminism.ChunkHeightHash(cx, cz);

        /// <summary>[v0.1.153] 区块**地形形状**指纹（只哈希 Top/Bottom，**不含随日照变化的 SunlightHeight**）。</summary>
        public static string ChunkShapeHash(int cx, int cz) => SkylineChunkDeterminism.ChunkShapeHash(cx, cz);

        /// <summary>[v0.1.140 · 学习待办 #6] **曲线采样探针**（只读）：与 `SweepBezier` 共用同一套
        /// 解析/建段/弧长等距采样，返回稠密折线 + 站点 + 步距统计，供几何误差核对。</summary>
        public static string BezierSample(string spec) => SkylineBuilder.CurveSample(spec);

        /// <summary>[v0.1.141 · 审计第 1 项 / B-03] **LOD 距离契约探针**（只读）：同一串距离下
        /// 并排给出"单元中心"与"到块 AABB 最短距离"两种口径的档号，以及边界/开闭/竖直规则。</summary>
        public static string LodDistanceContract(float d0, float d1, int steps) =>
            SkylineLod.LodDistanceContract(d0, d1, steps);

        /// <summary>[v0.1.142 · 5.2 第二段] **生成器构造成本**探针（只 new 对象，不生成区块）：
        /// 用来判定"每 worker 一份生成器"是否廉价（`notes/247 §6` 的未实测项）。</summary>
        public static string GeneratorCtorProbe(int count) =>
            SkylineChunkDeterminism.GeneratorCtorProbe(count);

        /// <summary>[v0.1.150 · CC P3] **日照 pass 的同轮次幂等性探针**（受控：会重跑该 pass 并把状态
        /// 退回 `InvalidLight` 让引擎重走完整光照）。见 `notes/261 §5`。</summary>
        public static string SunLightReplay(int cx, int cz, bool restore = true) =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater
                ?.SunLightReplay(cx, cz, restore)
            ?? "{\"ok\":false,\"err\":\"no updater\"}";

        /// <summary>[v0.1.146 · 审计验收用] **区块读盘失败注入**的账本（默认开关 -1 = 关）。</summary>
        public static string ChunkReadFailureInjection() => new JsonObject {
            ["injectAt"] = TerrainSerializer23.InjectChunkReadFailure,
            ["injectedFailures"] = TerrainSerializer23.InjectedChunkReadFailures,
            ["note"] = "只用于验收：非 IOException 的注入不会走『弹窗 + DisposeProject』那条分支"
        }.ToJsonString();

        /// <summary>[v0.1.146 · 审计验收用] 转发到 `TerrainSerializer23.InjectChunkReadFailure`
        /// （桥的 `setprop` 目标是 `skyline`，所以要在这里开一个转发口）。**默认 -1 = 关**。</summary>
        public static int ChunkReadFailureInjectionAt {
            get => TerrainSerializer23.InjectChunkReadFailure;
            set => TerrainSerializer23.InjectChunkReadFailure = value;
        }

        /// <summary>[v0.1.147 · notes/257 方案 C] **坏档账本**（只读）：区块读盘失败的计数与最近几条记录。
        /// `acceptedAfterFailure>0` = "坏档被当成已加载"真实发生过。</summary>
        public static string ChunkLoadFailures() => TerrainSerializer23.DescribeChunkLoadFailures();

        /// <summary>
        /// **[v0.1.156 · CC `074658Z` Q2 裁定] 只读**：把"逐 pass 之外"的两大块成本量出来 ——
        /// **区块分配/释放**（`Terrain.AllocateChunk/FreeChunk`）与**存盘**（`TerrainSerializer23.SaveChunkData`）。
        ///
        /// 为什么要它：lockstep 把可并行的 `lightMs` 腰斩（1067→561 ms）而**端到端墙钟不动**
        /// （`notes/278 §3`）⇒ 说明大头在"逐 pass 之外"。审计要求把**未归因时间压到 10% 以内**
        /// 再引用占比数字。本探针只报累计计数与累计毫秒，**不重置、不改行为**。
        /// </summary>
        public static string TerrainCostStats() {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            TerrainSerializer23 ser = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)
                ?.TerrainSerializer as TerrainSerializer23;
            return new JsonObject {
                ["allocCount"] = terrain?.AllocCount ?? -1,
                ["allocMs"] = Math.Round(terrain?.AllocMs ?? -1, 1),
                ["freeCount"] = terrain?.FreeCount ?? -1,
                ["freeMs"] = Math.Round(terrain?.FreeMs ?? -1, 1),
                ["saveChunkCount"] = ser?.SaveChunkCount ?? -1,
                ["saveChunkMs"] = Math.Round(ser?.SaveChunkMs ?? -1, 1),
                // [v0.1.156 · CC 075119Z P3] `SetCellValueFast` 的**跨块写**与**静默丢弃**计数。
                // 跨轮逐位比对的前置条件就是"那一刻邻居在场"；这两条计数把它变成可观测事实。
                ["boundaryWrites"] = terrain?.BoundaryWrites ?? -1,
                ["droppedNeighborWrites"] = terrain?.DroppedNeighborWrites ?? -1,
                ["note"] = "累计只读账本：分配/释放来自 Terrain，存盘来自 TerrainSerializer23；"
                           + "boundaryWrites/droppedNeighborWrites 是 SetCellValueFast 的跨块写与静默丢弃；"
                           + "配合 TerrainUpdateStats 的逐 pass 分账，用来把未归因时间压到 10% 以内"
            }.ToJsonString();
        }

        /// <summary>[v0.1.151 · 验收用] 该区块本轮是否**从存档读出**过（只读）。</summary>
        public static string ChunkLoadedFromDisk(int cx, int cz) => new JsonObject {
            ["chunk"] = new JsonArray(cx, cz),
            ["loadedFromDisk"] = TerrainSerializer23.WasLoadedFromDisk(cx, cz),
        }.ToJsonString();

        /// <summary>[v0.1.154 · 验收用] 清零"从盘读回"溯源 ⇒ 之后的 `ChunkLoadedFromDisk` 只反映**本轮**。</summary>
        public static string ResetDiskProvenance() {
            TerrainSerializer23.ResetDiskProvenance();
            return "{\"ok\":true}";
        }

        /// <summary>[v0.1.152 · CC P3] **最近的有壳立方体**（只读）：给门禁的
        /// `shell-mesh-tiers` / `shell-column` 当**固定锚点**用，避免它们随玩家机位 SKIP。</summary>
        public static string NearestShellCube(int worldX, int worldY, int worldZ) =>
            SkylineCubeShellStore.NearestShellCube(worldX, worldY, worldZ);

        /// <summary>[v0.1.152] 最近 N 个有壳立方体（给 `shell-column` 依次试候选用）。</summary>
        public static string NearestShellCubes(int worldX, int worldY, int worldZ, int count) =>
            SkylineCubeShellStore.NearestShellCubes(worldX, worldY, worldZ, count);

        /// <summary>
        /// [v0.1.149 · **CC 裁定后的默认值**：**默认 true = 开（修法 A 生效）**]。
        ///
        /// 打开后：`TerrainSerializer23.LoadChunkData` 遇到**非 IOException** 的坏档异常时**额外返回 false**
        /// ⇒ 更新器把该区块当作"存档里没有"⇒ **按同一坐标重新生成**（而不是带着未反序列化的空内容
        /// 进入后续 pass；那种情况下玩家一编辑就会把空内容固化进存档，`notes/257 §6` 已实测）。
        ///
        /// **为什么默认关**：它改变"坏档时的世界演化"，属引擎语义变更 ⇒ 按协作约定交审计/用户裁定。
        /// 验收 `heightlab/skyline-v0148-corrupt-regenerate.py` 用"注入 → 编辑 → 保存 → 重开"对照：
        /// 关=原地形丢失（278,356 → 28 格）；开=区块被重新生成、内容自洽且不塌成空。
        ///
        /// **【v0.1.149】审计已裁定（`to-cc/…081600Z-0001` 与 `…082034Z-0001`）：缺陷成立、定级 P1、修法选 A**，
        /// 因此**默认值翻成 true**（保持开关只为"A/B 对照与一键回滚"）。裁定同时要求四条验收（见 `notes/261`）。
        /// </summary>
        public static bool CorruptChunkRegenerate {
            get => TerrainSerializer23.RegenerateCorruptChunk;
            set => TerrainSerializer23.RegenerateCorruptChunk = value;
        }

        /// <summary>[v0.1.149 · 验收用] 转发"注入目标区块"（默认 int.MinValue = 任意区块）。</summary>
        public static int ChunkReadFailureTargetCx {
            get => TerrainSerializer23.InjectChunkReadFailureCx;
            set => TerrainSerializer23.InjectChunkReadFailureCx = value;
        }

        /// <summary>[v0.1.149 · 验收用] 转发"注入目标区块"（z 分量）。</summary>
        public static int ChunkReadFailureTargetCz {
            get => TerrainSerializer23.InjectChunkReadFailureCz;
            set => TerrainSerializer23.InjectChunkReadFailureCz = value;
        }

        /// <summary>[v0.1.138] 仅对未改动区块强制重生成（确定性测试用）。</summary>
        public static string ChunkForceRegenerate(int cx, int cz) =>
            SkylineChunkDeterminism.ChunkForceRegenerate(cx, cz);

        /// <summary>[v0.1.136] 体素壳刷新票的账本（编辑失效化后恢复体素壳用）。</summary>
        public static string VoxelRefreshTickets() => new JsonObject {
            ["pending"] = SkylineCubeShellStore.VoxelRefreshTicketsPending,
            ["issued"] = SkylineCubeShellStore.VoxelRefreshTicketsIssued,
            ["used"] = SkylineCubeShellStore.VoxelRefreshTicketsUsed,
            ["cap"] = SkylineCubeShellStore.VoxelRefreshTicketCap,
            ["voxelShellCount"] = SkylineCubeShellStore.VoxelShellCubes,
            ["voxelCap"] = SkylineCubeShellStore.SurfaceVoxelMaxCubes,
            ["note"] = "编辑失效化前有体素壳的立方体会拿到票；重采时凭票绕过 512 额度一次"
        }.ToJsonString();

        /// <summary>
        /// [v0.1.99] 里程碑 2.3：**LOD 是否参与云层阴影**（默认开）。
        /// 云影与体积云 shader **同源**（同一哈希/值噪声/密度场 + 同样的光学厚度口径），
        /// 沿太阳方向在云带里积一次、烘进 LOD 顶点色 ⇒ 与"看得见的云"对齐，
        /// 且 CPU 烘焙 / GPU 体积着色两条路径自动一致（与自阴影同一做法）。
        /// 关掉 = 逐位回到 v0.1.98（A/B 用）。
        /// </summary>
        public static bool LodCloudShadow {
            get => SkylineLodCloudShadow.Enabled;
            set {
                if (SkylineLodCloudShadow.Enabled != value) {
                    SkylineLodCloudShadow.Enabled = value;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>[v0.1.99] 云影**最多压暗多少**（0..1，默认 0.55）。改动会立刻重建 LOD。</summary>
        public static float LodCloudShadowDepth {
            get => SkylineLodCloudShadow.Depth;
            set {
                float v = Math.Clamp(value, 0f, 1f);
                if (MathF.Abs(SkylineLodCloudShadow.Depth - v) > 1e-4f) {
                    SkylineLodCloudShadow.Depth = v;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>[v0.1.99] 云影确定性自检：沿风向移动 64 个采样点，因子必须**有变化**且在 (0,1] 内。</summary>
        public static string LodCloudShadowSelfCheck() => SkylineLodCloudShadow.SelfCheck();

        /// <summary>[v0.1.158 · 用户口径 4.4-1] **云影单色调试开关**（默认关）：
        /// 打开后被云影压暗的 LOD 顶点色不再是"变暗"，而是乘上 `LodCloudShadowTint` 的**高饱和平色**
        /// （默认黄绿）——用来一眼看出云影落在哪，也避免与"云本身的白"混淆。</summary>
        public static bool LodCloudShadowTintEnabled {
            get => SkylineLodCloudShadow.TintEnabled;
            set {
                if (SkylineLodCloudShadow.TintEnabled != value) {
                    SkylineLodCloudShadow.TintEnabled = value;
                    SkylineLod.RequestRebuild();   // 顶点色是烘出来的 ⇒ 改了要重建
                }
            }
        }

        /// <summary>[v0.1.158] 云影单色（默认黄绿 0.72/1.00/0.24，高饱和）。</summary>
        public static Vector3 LodCloudShadowTint {
            get => SkylineLodCloudShadow.Tint;
            set {
                SkylineLodCloudShadow.Tint = value;
                SkylineLod.RequestRebuild();
            }
        }

        /// <summary>
        /// [v0.1.100] 里程碑 2.3 第三项：**固定光源（火把/灯）造成的亮度斑块**（默认开）。
        /// 开 = LOD 顶面基色取 `max(实心方块自身 light, **上方空气格 light**)`；
        /// 关 = 逐位回到 v0.1.99（只读实心方块自身 light ⇒ 看不到火光亮斑）。改动会立刻重建 LOD。
        /// </summary>
        public static bool LodAirLightPatch {
            get => SkylineLod.LodAirLightPatch;
            set {
                if (SkylineLod.LodAirLightPatch != value) {
                    SkylineLod.LodAirLightPatch = value;
                    SkylineLod.RequestRebuild();
                }
            }
        }

        /// <summary>[v0.1.100] 只读探针：回读某世界坐标所在 LOD 单元的 `light / lightAir / effective`。</summary>
        public static string LodAirLightProbe(int worldX, int worldZ) => SkylineLod.AirLightProbe(worldX, worldZ);

        /// <summary>v0.1.1：LOD 开启时把视图雾的跨度拉远到 LOD 半径的 90%，
        /// 让"视距边缘的真实地形"与 LOD 层共用同一条雾曲线（消除交接跳变）。</summary>
        public static bool LodFogExtend {
            get => SkylineAtmosphere.FogExtendEnabled;
            set => SkylineAtmosphere.FogExtendEnabled = value;
        }

        /// <summary>v0.1.2：视觉球"三档"开关——短球内强制全精度 / 两球之间按 d_box / 全球外占位。</summary>
        public static bool VisualSphereEnabled {
            get => SkylineRender.VisualSphereEnabled;
            set => SkylineRender.VisualSphereEnabled = value;
        }

        /// <summary>短球半径系数（× 视距）。</summary>
        public static float VisualSphereShortFactor {
            get => SkylineRender.ShortSphereFactor;
            set => SkylineRender.ShortSphereFactor = value;
        }

        /// <summary>v0.1.5：光影包接管 LOD 绘制（Dawnlight/Iris 式）。</summary>
        public static bool LodExternalShaderHooked {
            get => SkylineLod.ExternalShaderHooked;
            set => SkylineLod.ExternalShaderHooked = value;
        }

        // ---------------------------------------------------------------- v0.1.14：球形加载窗

        /// <summary>
        /// 球形加载窗总开关（**默认关闭** = 与 2D 加载逐位一致）。打开后相机的 update location 走
        /// 椭球判据 `dx² + (dy/m)² + dz² ≤ content²`（m = `SubsystemSky.VisibilityRangeYMultiplier`，
        /// 竖直用"该列内容带 bottom..top"）——相机远高于地形时，正下方的列不再被强制常驻
        /// （远景交给超视距 LOD）；已卸载列的内容带会被记住，避免"卸载→未知→又装回"的抖动。
        /// </summary>
        public static bool SphereLoadingEnabled { get; set; }

        /// <summary>
        /// [v0.1.28] 球形加载窗的**立方体粒度**判据（默认开；仅 `SphereLoadingEnabled` 打开时生效）：
        /// 竖直方向不再只用"5 点采样内容带"，而是把区块的 256 列 top/bottom 聚合成 **32³ 分带内容掩码**，
        /// 逐分带与椭球的竖直覆盖求交 —— 这是里程碑 3 的 P2 剩余（`notes/81`），为 P3 的 32³ 存储铺路。
        /// 关掉它可退回 v0.1.14 的行为做 A/B（`skyline.CubeBands`）。
        /// </summary>
        public static bool SphereLoadingCubeBands { get; set; } = true;

        /// <summary>
        /// [v0.1.22] **帧内地形更新预算**（毫秒；默认 10 = 引擎原本的硬编码值，范围 2~50）。
        /// 每帧的地形状态机最多跑这么久。大规模建造时"几何追平"是瓶颈（`notes/91`：写 4096 格 100 ms
        /// 但几何要 4~8 s），把预算调大能缩短等待、代价是这段时间帧时间上升。
        /// </summary>
        public static int TerrainUpdateBudgetMs {
            get => m_terrainUpdateBudgetMs;
            set => m_terrainUpdateBudgetMs = Math.Clamp(value, 2, 50);
        }

        static int m_terrainUpdateBudgetMs = 10;

        /// <summary>
        /// [v0.1.139 · 里程碑 5.2 第二段] **有界并行**的日照/高度 pass 并发度：**0/1 = 串行（默认）**，
        /// 2/4 = 每次最多让这么多区块的 `GenerateChunkSunLightAndHeight` 同时跑。
        ///
        /// 为什么只并行这一段（源码级核实，见 `notes/248`）：
        ///   * `GenerateChunkSunLightAndHeight` 只**读写本区块**（Get/SetCellValueFast、
        ///     SetTop/Bottom/SunlightHeightFast）+ 只读 `BlocksManager.Blocks` ⇒ 无邻居读写、
        ///     无共享可变状态、不碰 Storage、无 mod hook；
        ///   * 同一个 light 阶段的 `lightSources` / `propagate` **必须串行**：`TerrainUpdater.m_lightSources`
        ///     是共享实例字段（`TerrainUpdater.cs:183/738/1135`），且 `PropagateLightSources()` 会
        ///     **写邻居区块**（`:1078-1108` → `:1125`），并发会破坏块边光照一致性。
        /// </summary>
        public static int ParallelSunLightWorkers {
            get => m_parallelSunLightWorkers;
            set => m_parallelSunLightWorkers = Math.Clamp(value, 0, 8);
        }

        static int m_parallelSunLightWorkers;

        /// <summary>
        /// [v0.1.144 · 审计验收用] **异常注入**：`>= 0` 时，并行批内**第 N 个** worker 会抛一个
        /// `InvalidOperationException`（默认 **-1 = 关**）。用途是**可判定地**验收审计的两条停止条件：
        ///   * "取消/异常后**未提交结果不污染世界**"——异常在 `Parallel.For` 里抛出 ⇒ 状态推进那一段
        ///     不会被执行 ⇒ 本批**整体不提交**；
        ///   * "**异常可定位**"——异常沿 `SynchronousUpdateFunction` 抛到 `ThreadUpdateFunction` 的
        ///     `catch (Exception e) { Log.Error(...) }`（`TerrainUpdater.cs:956-958`）⇒ 游戏日志里能看到。
        /// 只有在 `ParallelSunLightWorkers >= 2` 时才有意义。**一次性**：抛出后自动复位成 -1
        /// （否则每个批都失败 ⇒ 该状态永远推进不了 ⇒ 预加载永远走不完）。
        /// </summary>
        public static int ParallelSunLightInjectFailure {
            get => m_parallelSunLightInjectFailure;
            set => m_parallelSunLightInjectFailure = Math.Clamp(value, -1, 64);
        }

        static int m_parallelSunLightInjectFailure = -1;

        /// <summary>[v0.1.139] 并行日照 pass 的账本（batches/chunks/回退），用于验收与回归。</summary>
        public static string ParallelSunLightStats() =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater?.DescribeParallelSunLight()
            ?? "{\"err\":\"no updater\"}";

        /// <summary>
        /// **[v0.1.156 · 里程碑 5.2 第二段] lockstep 批推进的并发度**（默认 **0 = 关**）。
        ///
        /// 它把候选窗口从"只有 `InvalidLight`"扩到 **`InvalidContents1..4` + `InvalidLight`**：
        /// 一次凑一批**互不邻近**（Chebyshev ≥ 3）的区块并行做纯计算，`join` 之后由地形工作线程
        /// **串行**推进状态机。设计与四条不变量见 `notes/254`；CC 在 `080120Z` 裁定：
        /// **I2 保留**（本实现按 ≥3，比草案的 ≥2 更严，理由见 `TerrainUpdater.TryParallelLockstepStep` 的注释：
        /// 生成器**确有跨块写**，写邻域是 3×3 区块）、**vertices 暂缓**、**I5 先做**；
        /// **默认翻开需三条判据同时满足**：①六条不变量含 I5 注入全 PASS ②同 seed 交叉轮次哈希逐位相同
        /// ③同负载墙钟中位数改善 ≥ 15%（r=6、169 区块、弃首轮、≥5 遍取中位）。达不到就保持默认关并如实报告。
        ///
        /// 取 0/1 时完全回退现有串行路径（逐位不变）；≥2 才起并行。
        /// </summary>
        public static int ParallelLockstepWorkers {
            get => m_parallelLockstepWorkers;
            set => m_parallelLockstepWorkers = Math.Clamp(value, 0, 8);
        }

        static int m_parallelLockstepWorkers;

        /// <summary>
        /// [v0.1.156 · 审计验收用] **lockstep 批的异常注入**：`>= 0` 时第 N 个 worker 抛异常
        /// （默认 **-1 = 关**）。语义与 `ParallelSunLightInjectFailure` 完全一致：抛在 `Parallel.For` 内
        /// ⇒ **状态推进段不执行 ⇒ 整批不提交**（I5）；异常沿 `SynchronousUpdateFunction` 抛到
        /// `ThreadUpdateFunction` 的 `catch` ⇒ 游戏日志可定位。**一次性**：抛出后自动复位成 -1。
        /// </summary>
        public static int ParallelLockstepInjectFailure {
            get => m_parallelLockstepInjectFailure;
            set => m_parallelLockstepInjectFailure = value;
        }

        static int m_parallelLockstepInjectFailure = -1;

        /// <summary>**[v0.1.156] lockstep 的候选窗口系数**（默认 12）：
        /// `scanCap = max(workers × 系数, 48)`。调大能看见更多候选（可能凑出更大的批），
        /// 代价是每次调用多扫一点 —— `notes/278 §8.2` 指出的"候选可用性"杠杆的旋钮。</summary>
        public static int ParallelLockstepScanFactor {
            get => TerrainUpdater.ParallelLockstepScanFactor;
            set => TerrainUpdater.ParallelLockstepScanFactor = Math.Clamp(value, 2, 128);
        }

        /// <summary>lockstep 批推进的账本（只读探针）：批数/区块数/最大批/并发度上限/I2 拒绝数/各段计数/注入次数。</summary>
        public static string ParallelLockstepStats() =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater?.DescribeParallelLockstep()
            ?? "{\"err\":\"no updater\"}";

        /// <summary>
        /// [v0.1.157 · CC `121304Z` 裁定④] **单分量的批内上限 M**（默认 **4**，范围 1~64）：
        /// 一个连通分量一次最多做 M 块，多出来的留到下一批。它是**单分量尾长**旋钮，
        /// 与 `ParallelLockstepWorkers`（并行宽度）**正交**；用 **P95 分量大小 ÷ M** 判断是否要加大。
        /// </summary>
        public static int ParallelLockstepComponentMax {
            get => TerrainUpdater.ParallelLockstepComponentMax;
            set => TerrainUpdater.ParallelLockstepComponentMax = Math.Clamp(value, 1, 64);
        }

        /// <summary>
        /// [v0.1.157] **分量组批（I2'）开关**（默认 **true**）：`false` 退回旧的 I2 贪心挑选。
        /// 留这个开关是为了**同一份二进制**里跑"只换调度"的 A/B 对照（CC 裁定③：先跑改前基线）。
        /// </summary>
        public static bool ParallelLockstepComponentBatching {
            get => TerrainUpdater.ParallelLockstepComponentBatching;
            set => TerrainUpdater.ParallelLockstepComponentBatching = value;
        }

        /// <summary>
        /// [v0.1.157] **批调度算法**（0/1/2，默认 1）：`0` 旧 I2 贪心、`1` I2' 连通分量（CC `121304Z` 批准）、
        /// `2` **按 worker 装箱**（跨 worker 两两 ≥ gap、列内按全局距离序串行）——**待 CC 审**。
        /// </summary>
        public static int ParallelLockstepScheduling {
            get => TerrainUpdater.ParallelLockstepScheduling;
            set => TerrainUpdater.ParallelLockstepScheduling = Math.Clamp(value, 0, 2);
        }

        /// <summary>
        /// [v0.1.157 · CC 条件 5/6 的可调证据] **只读分量探针**：报出当前候选的连通分量、
        /// "相邻块是否全部同分量"、"分量间写邻域是否两两不相交"。不推进状态、不写账本。
        /// </summary>
        public static string ParallelLockstepComponents() =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater?.DescribeLockstepComponents()
            ?? "{\"err\":\"no updater\"}";

        /// <summary>
        /// [v0.1.16] 球形加载窗的**内容距离**（米）：0 = 沿用调用方的默认（64，即 content = max(64, visibility)）。
        /// 调大（例如 256）会让超视距 LOD 的**细环**也能被采到——LOD 只能采样"已加载"的区块，
        /// 而引擎默认的 content=64/视距=128 意味着 136~256 m 的细环平时根本没加载过，
        /// 于是视距边缘的 LOD 是"稀疏补丁"（实测细层仅 530 个单元）。代价是常驻列数与内存上升，
        /// 所以做成开关 + 可测项（见 `notes/86`）。
        /// </summary>
        public static int SphereLoadingContentRadius { get; set; }

        /// <summary>当前已分配的区块列数（验证球形加载窗用）。</summary>
        public static int AllocatedChunkCount =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain?.AllocatedChunks?.Length ?? -1;

        /// <summary>球形加载窗诊断：开关 / 已分配列数 / 内容带缓存条数 / 竖直系数。</summary>
        public static string SphereLoadingDescribe() {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            float ym = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRangeYMultiplier ?? 1f;
            return $"sphereLoading enabled={SphereLoadingEnabled} allocatedChunks={AllocatedChunkCount} "
                + $"cubeBands={SphereLoadingCubeBands} bandChecks={updater?.SphereBandChecks ?? -1} "
                + $"bandDrops={updater?.SphereBandDrops ?? -1} "
                + $"bandCache={updater?.ColumnBandCacheCount ?? -1} yMultiplier={ym:0.##}";
        }

        /// <summary>[v0.1.22] "编辑 → 几何追平"的直接量测诊断（最近一次 / 平均值 / 样本数）。</summary>
        public static string EditSettleDescribe() {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return "editSettle: no updater";
            }
            return $"editSettle budgetMs={TerrainUpdateBudgetMs} last={updater.LastEditSettleMs:0.0}ms "
                + $"mean={updater.MeanEditSettleMs:0.0}ms samples={updater.EditSettleSamples}";
        }

        /// <summary>[v0.1.28] 球窗判定诊断：`skyline.ChunkWindowDecision(cx,cz)`（见 notes/98）。</summary>
        public static string ChunkWindowDecision(int cx, int cz) {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return "chunkWindow: no updater";
            }
            return updater.DescribeChunkWindowDecision(cx, cz);
        }

        /// <summary>LOD 网格元数据（层/单元尺寸/半径/索引数），供光影包读取。</summary>
        public static string LodMeshMetadata() => SkylineLod.MeshMetadata();

        /// <summary>[v0.1.73] LOD 单元区域仓（落盘 + 按需回读）的账本。</summary>
        public static string LodRegionStore() => SkylineLod.RegionStoreDescribe();

        /// <summary>[v0.1.74] 地形区块表的一致性诊断（开地址表被置空打断探测链 → 僵尸区块）。</summary>
        public static string TerrainStorage() {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            if (terrain == null) {
                return "{\"ok\":false,\"err\":\"no terrain\"}";
            }
            return terrain.m_allChunks.Diagnose(terrain.m_allocatedChunks.Count);
        }

        /// <summary>[v0.1.157b] **开地址区块表的删除策略**（默认 **true** = 回移删除）：
        /// 关闭后退回旧语义（直接置空 ⇒ 打断探测链 ⇒ 重复分配 + 僵尸区块）。只为对照/回滚而留。</summary>
        public static bool ChunkTableBackwardShiftRemove {
            get => Terrain.ChunkTableBackwardShiftRemove;
            set => Terrain.ChunkTableBackwardShiftRemove = value;
        }

        /// <summary>
        /// [v0.1.157b · **确定性差分验收**] 区块表的"删除断链"探针（只读世界，用**临时表**做实验）。
        ///
        /// 做法：在一个**不属于世界**的 `Terrain.ChunksStorage` 里插入 3 个**同 home 槽**的区块
        /// （home = `(x + (y&lt;&lt;8)) &amp; 0xFFFF`；取 `(0,0) / (65536,0) / (131072,0)` ⇒ 都落在槽 0），
        /// 它们按线性探测依次占 0/1/2 号槽。然后删掉**第一个**，再看另外两个还能不能 `Get` 到：
        ///   * **旧语义**（直接置空）：探测到槽 0 就是空 ⇒ `Get` 返回 null ⇒ **链被切断**（这就是
        ///     `notes/263` 那个现象的**最小复现**，也解释了"重复分配 + 僵尸区块"）；
        ///   * **新语义**（回移删除）：簇被压实到 0/1 号槽 ⇒ 两个都还能找到。
        ///
        /// 返回两条分支的读数与结论，供门禁/审计直接判定"修复是否在效"。
        /// **不动世界**：临时表、临时区块，实验结束逐个 `DisposeForProbe()` 归还缓存。
        /// </summary>
        public static string ChunkTableRemoveProbe() {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            if (terrain == null) {
                return "{\"ok\":false,\"err\":\"no terrain\"}";
            }
            // 同 home 槽的三个坐标（65536 的倍数 ⇒ `& 65535` 后都为 0）
            int[][] coords = [[0, 0], [65536, 0], [131072, 0]];
            (JsonObject r, int rAfter) = RunChunkTableRemoveBranch(terrain, coords, legacy: true);
            (JsonObject n, int nAfter) = RunChunkTableRemoveBranch(terrain, coords, legacy: false);
            bool fixes = nAfter == 2 && rAfter <= 1;
            // 定向压力：一个 **64 条目的长探测簇**（真实表装载率极低、这种形状很少见，
            // 但它正是"洞一出现，后面整段都失联"的最坏形状）——删中间一条，数其余还能找到几条。
            const int stressEntries = 64;
            int[][] stressCoords = new int[stressEntries][];
            for (int k = 0; k < stressEntries; k++) {
                stressCoords[k] = [k * 65536, 0];       // 全是 home = 0 ⇒ 一个长簇
            }
            (JsonObject sr, int srAfter) = RunChunkTableRemoveBranch(
                terrain, stressCoords, legacy: true, removeIndex: stressEntries / 2);
            (JsonObject sn, int snAfter) = RunChunkTableRemoveBranch(
                terrain, stressCoords, legacy: false, removeIndex: stressEntries / 2);
            // **回卷簇**（覆盖 Knuth 判据的 `i > j` 分支）：home 槽 = 65535 的三个坐标
            // （`65536k − 1`）⇒ 依次占 65535、回卷到 0、再到 1。删第一个时 `i=65535 > j`，
            // 走的是另一个分支 —— 只测 `i ≤ j` 会漏掉整个回卷情形（本轮审计点）。
            int[][] wrapCoords = [[65535, 0], [131071, 0], [196607, 0]];
            (JsonObject wr, int wrAfter) = RunChunkTableRemoveBranch(terrain, wrapCoords, legacy: true);
            (JsonObject wn, int wnAfter) = RunChunkTableRemoveBranch(terrain, wrapCoords, legacy: false);
            return new JsonObject {
                ["ok"] = true,
                ["probe"] = "chunk-table-remove-chain",
                ["collisionHome"] = 0,
                ["coords"] = new JsonArray(coords[0][0], coords[1][0], coords[2][0]),
                ["legacy"] = r,
                ["backwardShift"] = n,
                ["fixEffective"] = fixes,
                ["stress"] = new JsonObject {
                    ["entries"] = stressEntries,
                    ["removedIndex"] = stressEntries / 2,
                    ["legacy"] = sr,
                    ["backwardShift"] = sn,
                    ["legacyLost"] = srAfter,
                    ["backwardShiftLost"] = (stressEntries - 1) - snAfter,
                    ["fixEffective"] = snAfter == stressEntries - 1 && srAfter < stressEntries - 1,
                },
                ["wraparound"] = new JsonObject {
                    ["coords"] = new JsonArray(65535, 131071, 196607),
                    ["legacy"] = wr,
                    ["backwardShift"] = wn,
                    ["fixEffective"] = wnAfter == 2 && wrAfter <= 1,
                },
                ["backwardShiftEnabled"] = Terrain.ChunkTableBackwardShiftRemove,
                ["allBranchesEffective"] = fixes
                    && snAfter == stressEntries - 1 && wnAfter == 2,
                ["note"] = "旧语义删掉簇首后，同簇其余 key 的探测链被空槽截断（reachableAfterRemove 应为 0~1）；"
                           + "回移删除后应为 2（两个都还能找到）。"
            }.ToJsonString();
        }

        /// <summary>[v0.1.157b] 探针的分支实现：建临时表 → 插 3 个同簇区块 → 删第一个 → 数还能找到几个。</summary>
        static (JsonObject Json, int ReachableAfter) RunChunkTableRemoveBranch(
            Terrain terrain, int[][] coords, bool legacy, int removeIndex = 0) {
            Terrain.ChunksStorage table = new();
            var chunks = new System.Collections.Generic.List<TerrainChunk>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (int[] c in coords) {
                TerrainChunk chunk = new(terrain, c[0], c[1]);
                chunks.Add(chunk);
                table.Add(c[0], c[1], chunk);
            }
            double insertMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            int before = 0;
            for (int i = 0; i < coords.Length; i++) {
                if (table.Get(coords[i][0], coords[i][1]) == chunks[i]) {
                    before++;
                }
            }
            if (legacy) {
                table.RemoveLegacyNullOnly(coords[removeIndex][0], coords[removeIndex][1]);
            }
            else {
                table.Remove(coords[removeIndex][0], coords[removeIndex][1]);
            }
            double removeMs = sw.Elapsed.TotalMilliseconds;
            bool removedGone = table.Get(coords[removeIndex][0], coords[removeIndex][1]) == null;
            int after = 0;
            for (int i = 0; i < coords.Length; i++) {
                if (i == removeIndex) {
                    continue;
                }
                if (table.Get(coords[i][0], coords[i][1]) == chunks[i]) {
                    after++;
                }
            }
            foreach (TerrainChunk chunk in chunks) {
                chunk.DisposeForProbe();
            }
            JsonObject json = new JsonObject {
                ["legacyNullOnly"] = legacy,
                ["inserted"] = coords.Length,
                ["reachableBeforeRemove"] = before,
                ["reachableAfterRemove"] = after,
                ["removedEntryGone"] = removedGone,
                ["shiftMoves"] = table.BackwardShiftMoves,
                ["insertMs"] = Math.Round(insertMs, 3),
                ["removeMs"] = Math.Round(removeMs, 3),
            };
            return (json, after);
        }

        /// <summary>[v0.1.15] LOD 坡向明暗强度（0 = 关闭；默认 0.45）。改动后需重建网格才生效：
        /// `skyline.LodReset()` 或等下一次 `MeshRebuildSeconds`。</summary>
        public static float LodSlopeShadingStrength {
            get => SkylineLod.SlopeShadingStrength;
            set => SkylineLod.SlopeShadingStrength = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>[v0.1.15] 坡向明暗增益诊断（min/mean/max，取最后一次网格重建里抽样的一列）。</summary>
        public static string LodSlopeShadingStats() => SkylineLod.SlopeShadingStats();

        /// <summary>[v0.1.15] 坡向明暗确定性自检（合成地形：平地 / 东坡 / 西坡）。</summary>
        public static string LodSlopeShadingSelfCheck() => SkylineLod.SlopeShadingSelfCheck();

        /// <summary>[v0.1.19] LOD 自阴影强度（0 = 关闭，默认 0.35）。改后等下一次网格重建生效。</summary>
        public static float LodSelfShadowStrength {
            get => SkylineLod.SelfShadowStrength;
            set => SkylineLod.SelfShadowStrength = Math.Clamp(value, 0f, 0.9f);
        }

        /// <summary>[v0.1.19] 自阴影统计（阴影中单元数/采样数）。</summary>
        public static string LodSelfShadowStats() => SkylineLod.SelfShadowStats();

        /// <summary>[v0.1.19] 自阴影确定性自检（高墙背光侧应判阴影）。</summary>
        public static string LodSelfShadowSelfCheck() => SkylineLod.SelfShadowSelfCheck();

        /// <summary>
        /// [v0.1.17] 区块达到 Valid 时是否立刻通知超视距 LOD 补采该单元（默认 **true**）。
        /// 关闭后回到"只靠轮转游标 + 采样戳"的旧节奏，可用于 A/B（见 `notes/87`）。
        /// </summary>
        public static bool BackfillOnValid { get; set; } = true;

        /// <summary>[v0.1.17] 诊断：因"区块刚 Valid"而标脏的次数。</summary>
        public static long LodBackfilledOnValid => SkylineLod.BackfilledOnValid;

        public static string LodDescribe() => SkylineLod.Describe();

        public static string LodSurvey() => SkylineLod.Survey();

        public static void LodReset() => SkylineLod.Reset();

        public static void LodSaveNow() => SkylineLod.Save();

        // ==========================================================================================
        // v0.0.5：区块列驻留（Chunk Residency）
        //
        // 解决的问题："远处建造静默失效"——玩家周围只有约 3 个区块（~48 格）的列常驻内存，
        //   更远的列会被 TerrainUpdater 释放（先存档再 FreeChunk），此时写入不报错但读回是 0、
        //   传送也过不去。大型建筑（特别是"站在中心建一整片"）必须有办法把这批列钉在内存里。
        //
        // 设计口径：
        //   * **独立开关** <see cref="ChunkResidencyMode"/>：与将来"游览模式的球形视距"互不干扰——
        //     本机制只往 TerrainUpdater 的 update locations 里追加"覆盖圆"，不改相机的可见距离，
        //     也不改 <see cref="SubsystemSky.VisibilityRange"/>；关掉开关就完全恢复原版行为。
        //   * 区域用**世界坐标矩形**描述，内部用一串半径 <see cref="ResidencyCircleRadiusBlocks"/>
        //     的圆去覆盖（步长 = 半径，网格排布可无缝覆盖），这样能直接复用引擎既有的
        //     allocate/load/upgrade 流程，不另起一套加载器。
        //   * <see cref="ResidencyFullDetail"/> = true：区域内列提升到 Valid（含几何/光照，可直接取景）；
        //     false：只提升到 InvalidVertices1（内容已加载、可读写，但不生成几何，省 CPU/显存）。
        //   * 内存代价必须如实告知：每个区块 16×16×2048×4B ≈ **2 MB**，<see cref="ResidencyStatus"/>
        //     会给出估算值；超过 <see cref="ResidencyMaxChunks"/> 的请求会被拒绝（默认 192 ≈ 384 MB）。
        // ==========================================================================================

        /// <summary>临时驻留区（<see cref="EnsureRegionLoaded"/> 用）固定使用的名字。</summary>
        public const string EnsureRegionName = "__ensure";

        /// <summary>驻留区域（世界坐标，闭区间）。</summary>
        public sealed class ResidencyRegion {
            public string Name;
            public int MinX;
            public int MinZ;
            public int MaxX;
            public int MaxZ;

            /// <summary>是否为临时区域（<see cref="EnsureRegionLoaded"/> 注册，会按 TTL 自动过期）。</summary>
            public bool Temporary;

            /// <summary>过期时刻（<see cref="Engine.Time.RealTime"/> 口径），仅 Temporary 有效。</summary>
            public double ExpiresAt;

            public override string ToString() => $"{Name} [{MinX},{MinZ}]..[{MaxX},{MaxZ}]";
        }

        /// <summary>用圆覆盖驻留区域的中间表示（TerrainUpdater 直接消费）。</summary>
        public struct ResidencyCircle {
            public Vector2 Center;
            public float RadiusBlocks;
        }

        public static readonly List<ResidencyRegion> Regions = [];

        static int m_residencyVersion = 1;
        static bool m_chunkResidencyMode;
        static bool m_residencyFullDetail = true;
        static int m_residencyCircleRadiusBlocks = 64;
        static int m_residencyMaxChunks = 192;
        static double m_ensureRegionTtlSeconds = 300.0;
        static string m_residencyLastError = "";

        /// <summary>
        /// 区块列驻留总开关（**独立模式开关**，默认关闭）。
        /// 打开后 <see cref="Regions"/> 里的矩形区域会被钉在内存里，远处写入不再静默失效。
        /// </summary>
        public static bool ChunkResidencyMode {
            get => m_chunkResidencyMode;
            set {
                if (m_chunkResidencyMode != value) {
                    m_chunkResidencyMode = value;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>true：驻留列提升到 Valid（含几何，可直接看/取景）；false：只加载内容（省 CPU/显存）。</summary>
        public static bool ResidencyFullDetail {
            get => m_residencyFullDetail;
            set {
                if (m_residencyFullDetail != value) {
                    m_residencyFullDetail = value;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>覆盖圆半径（格），步长 = 半径 → 网格排布可无缝覆盖（默认 64 格 = 4 区块）。</summary>
        public static int ResidencyCircleRadiusBlocks {
            get => m_residencyCircleRadiusBlocks;
            set {
                int clamped = Math.Clamp(value, 16, 1024);
                if (m_residencyCircleRadiusBlocks != clamped) {
                    m_residencyCircleRadiusBlocks = clamped;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>驻留区块数上限（内存保护；超过就拒绝新增区域）。</summary>
        public static int ResidencyMaxChunks {
            get => m_residencyMaxChunks;
            set => m_residencyMaxChunks = Math.Clamp(value, 1, 65536);
        }

        /// <summary>临时驻留区的存活时间（秒），到期由 <see cref="Tick"/> 自动回收。</summary>
        public static double EnsureRegionTtlSeconds {
            get => m_ensureRegionTtlSeconds;
            set => m_ensureRegionTtlSeconds = Math.Clamp(value, 5.0, 86400.0);
        }

        /// <summary>配置版本号：TerrainUpdater 只在版本变化时重建驻留地点。</summary>
        public static int ResidencyVersion => m_residencyVersion;

        /// <summary>最近一次驻留操作的错误（空串 = 无错）。</summary>
        public static string ResidencyLastError => m_residencyLastError;

        /// <summary>加一个驻留区域（世界坐标闭区间，会自动归一化；同名覆盖）。</summary>
        public static bool AddResidencyRegion(string name, int x1, int z1, int x2, int z2) =>
            AddResidencyRegionInternal(name, x1, z1, x2, z2, false, 0.0);

        /// <summary>移除一个驻留区域。</summary>
        public static bool RemoveResidencyRegion(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                m_residencyLastError = "region name is empty";
                return false;
            }
            int removed = Regions.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) {
                m_residencyVersion++;
                m_residencyLastError = "";
                return true;
            }
            m_residencyLastError = $"region '{name}' not found";
            return false;
        }

        /// <summary>清空全部驻留区域。</summary>
        public static void ClearResidencyRegions() {
            if (Regions.Count > 0) {
                Regions.Clear();
                m_residencyVersion++;
            }
            m_residencyLastError = "";
        }

        /// <summary>
        /// 请求把某个矩形区域加载到"可读写"状态（**非阻塞**）：注册临时区域后立刻返回，
        /// 之后用 <see cref="ResidencyStatus"/> 轮询直到该区域的 <c>contentsReady == chunks</c>。
        /// 需要先打开 <see cref="ChunkResidencyMode"/>（独立开关），否则会明确报错而不是静默不做事。
        /// </summary>
        public static string EnsureRegionLoaded(int x1, int z1, int x2, int z2) {
            if (!ChunkResidencyMode) {
                return new JsonObject {
                    ["ok"] = false,
                    ["error"] = "ChunkResidencyMode_disabled",
                    ["hint"] = "先设置 SkylineRuntime.ChunkResidencyMode=true（独立开关，与游览模式的球形视距互不冲突）"
                }.ToJsonString();
            }
            bool ok = AddResidencyRegionInternal(EnsureRegionName, x1, z1, x2, z2, true, Time.RealTime + EnsureRegionTtlSeconds);
            JsonObject result = ResidencyStatusObject();
            result["ok"] = ok;
            if (!ok) {
                result["error"] = m_residencyLastError;
            }
            return result.ToJsonString();
        }

        /// <summary>临时驻留区是否已过期（由 TerrainUpdater 每帧调用，开销极小）。</summary>
        public static void Tick() {
            // [v0.1.0] 家具 LOD 的动态档位检查（内部自带限频，见 SkylineRender.Tick）
            SkylineRender.Tick();
            // [v0.1.0] 超视距 LOD：采集粗网格 + 定时重建网格/落盘（见 SkylineLod.Tick）
            SkylineLod.Tick();
            // [v0.1.13] 32³ 三维窗口账本（默认关；见 SkylineCubeWindow.cs）
            CubeWindowTick();
            // [v0.1.24] 大批写入期的光照去抖：写入停下后统一降级一次（见 SkylineBulkEdit.cs）
            TickDeferredLight();
            // [v0.1.31] 地形顶点阴影的"随太阳重烘焙"（默认随 TerrainShadowEnabled 生效；见 SkylineTerrainShadow.cs）
            TerrainShadowTick();
            // [v0.1.34] GPU 阴影采样：启用但还没有深度图时自动补一次捕获（见 SkylineGpuShadowSample.cs）
            GpuShadowTick();
            // [v0.1.106] 固定光源（点光源）：每帧收集相机附近最近的 K 个发光方块，绑给不透明变体
            {
                SubsystemTerrain st = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(true);
                ComponentPlayer cp = players != null && players.ComponentPlayers.Count > 0
                    ? players.ComponentPlayers[0] : null;
                Camera cam = cp?.GameWidget?.ActiveCamera;
                if (st?.Terrain != null && cam != null) {
                    SkylinePointLights.Tick(st.Terrain, cam.ViewPosition);
                }
            }
            if (Regions.Count == 0) {
                return;
            }
            double now = Time.RealTime;
            int removed = Regions.RemoveAll(r => r.Temporary && now >= r.ExpiresAt);
            if (removed > 0) {
                m_residencyVersion++;
            }
        }

        /// <summary>驻留状态（JSON 字符串）：区域列表 + 每个区域的区块统计 + 内存估算。</summary>
        public static string ResidencyStatus() => ResidencyStatusObject().ToJsonString();

        /// <summary>某个区域的内容是否已经全部可读写。</summary>
        public static bool IsRegionContentsReady(string name) {
            ResidencyRegion region = FindRegion(name);
            if (region == null) {
                return false;
            }
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (terrain == null) {
                return false;
            }
            foreach (Point2 coords in EnumerateRegionChunks(region)) {
                TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(coords);
                if (chunk == null || chunk.ThreadState < TerrainChunkState.InvalidLight) {
                    return false;
                }
            }
            return true;
        }

        public static ResidencyRegion FindRegion(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                return null;
            }
            foreach (ResidencyRegion region in Regions) {
                if (string.Equals(region.Name, name, StringComparison.OrdinalIgnoreCase)) {
                    return region;
                }
            }
            return null;
        }

        /// <summary>把驻留区域铺成一串"覆盖圆"（TerrainUpdater 用这些圆心+半径当 update locations）。</summary>
        public static List<ResidencyCircle> BuildResidencyCircles() {
            List<ResidencyCircle> result = [];
            if (!ChunkResidencyMode || Regions.Count == 0) {
                return result;
            }
            int radius = ResidencyCircleRadiusBlocks;
            int margin = TerrainChunk.Size * 2;     // 外扩 2 个区块，避免"区块跨边界但圆心不在圆内"
            foreach (ResidencyRegion region in Regions) {
                int minX = region.MinX - margin;
                int maxX = region.MaxX + margin;
                int minZ = region.MinZ - margin;
                int maxZ = region.MaxZ + margin;
                for (int centerX = minX; centerX <= maxX + radius; centerX += radius) {
                    for (int centerZ = minZ; centerZ <= maxZ + radius; centerZ += radius) {
                        result.Add(new ResidencyCircle {
                            Center = new Vector2(centerX, centerZ),
                            RadiusBlocks = radius
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>估算一个区域会占多少区块（用于内存保护）。</summary>
        public static int CountRegionChunks(int x1, int z1, int x2, int z2) {
            Normalize(ref x1, ref x2);
            Normalize(ref z1, ref z2);
            int chunksX = (x2 >> TerrainChunk.SizeBits) - (x1 >> TerrainChunk.SizeBits) + 1;
            int chunksZ = (z2 >> TerrainChunk.SizeBits) - (z1 >> TerrainChunk.SizeBits) + 1;
            return chunksX * chunksZ;
        }

        static bool AddResidencyRegionInternal(string name, int x1, int z1, int x2, int z2, bool temporary, double expiresAt) {
            if (string.IsNullOrWhiteSpace(name)) {
                m_residencyLastError = "region name is empty";
                return false;
            }
            Normalize(ref x1, ref x2);
            Normalize(ref z1, ref z2);
            int chunks = CountRegionChunks(x1, z1, x2, z2);
            int existing = FindRegion(name) is ResidencyRegion previous ? CountRegionChunks(previous.MinX, previous.MinZ, previous.MaxX, previous.MaxZ) : 0;
            if (chunks - existing + TotalRegionChunks() > ResidencyMaxChunks) {
                m_residencyLastError =
                    $"residency chunk budget exceeded: wanted {chunks} (existing same-name {existing}), "
                    + $"total would be {chunks - existing + TotalRegionChunks()} > ResidencyMaxChunks {ResidencyMaxChunks} "
                    + $"(each chunk ≈ {TerrainChunk.Height * TerrainChunk.Size * TerrainChunk.Size * 4 / 1048576.0:0.0} MB)";
                return false;
            }
            Regions.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            Regions.Add(new ResidencyRegion {
                Name = name,
                MinX = x1,
                MinZ = z1,
                MaxX = x2,
                MaxZ = z2,
                Temporary = temporary,
                ExpiresAt = expiresAt
            });
            m_residencyVersion++;
            m_residencyLastError = "";
            return true;
        }

        static JsonObject ResidencyStatusObject() {
            JsonObject root = new() {
                ["mode"] = ChunkResidencyMode,
                ["fullDetail"] = ResidencyFullDetail,
                ["circleRadiusBlocks"] = ResidencyCircleRadiusBlocks,
                ["maxChunks"] = ResidencyMaxChunks,
                ["version"] = m_residencyVersion,
                ["lastError"] = m_residencyLastError
            };
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (terrain == null) {
                root["ok"] = false;
                root["error"] = "no world loaded";
                return root;
            }
            double chunkMegabytes = TerrainChunk.Height * TerrainChunk.Size * TerrainChunk.Size * 4 / 1048576.0;
            JsonArray regions = [];
            int totalChunks = 0;
            int totalAllocated = 0;
            int totalContents = 0;
            int totalGeometry = 0;
            foreach (ResidencyRegion region in Regions) {
                int chunks = 0;
                int allocated = 0;
                int contentsReady = 0;
                int geometryReady = 0;
                foreach (Point2 coords in EnumerateRegionChunks(region)) {
                    chunks++;
                    TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(coords);
                    if (chunk == null) {
                        continue;
                    }
                    allocated++;
                    if (chunk.ThreadState >= TerrainChunkState.InvalidLight) {
                        contentsReady++;
                    }
                    if (chunk.State >= TerrainChunkState.Valid) {
                        geometryReady++;
                    }
                }
                totalChunks += chunks;
                totalAllocated += allocated;
                totalContents += contentsReady;
                totalGeometry += geometryReady;
                regions.Add(new JsonObject {
                    ["name"] = region.Name,
                    ["minX"] = region.MinX,
                    ["minZ"] = region.MinZ,
                    ["maxX"] = region.MaxX,
                    ["maxZ"] = region.MaxZ,
                    ["temporary"] = region.Temporary,
                    ["expiresInSeconds"] = region.Temporary ? Math.Max(0.0, region.ExpiresAt - Time.RealTime) : (double?)null,
                    ["chunks"] = chunks,
                    ["allocated"] = allocated,
                    ["contentsReady"] = contentsReady,
                    ["geometryReady"] = geometryReady,
                    ["estimatedMB"] = Math.Round(chunks * chunkMegabytes, 1)
                });
            }
            root["ok"] = true;
            root["regions"] = regions;
            root["totals"] = new JsonObject {
                ["chunks"] = totalChunks,
                ["allocated"] = totalAllocated,
                ["contentsReady"] = totalContents,
                ["geometryReady"] = totalGeometry,
                ["estimatedMB"] = Math.Round(totalChunks * chunkMegabytes, 1),
                ["chunkMB"] = Math.Round(chunkMegabytes, 2)
            };
            return root;
        }

        static IEnumerable<Point2> EnumerateRegionChunks(ResidencyRegion region) {
            int minChunkX = region.MinX >> TerrainChunk.SizeBits;
            int maxChunkX = region.MaxX >> TerrainChunk.SizeBits;
            int minChunkZ = region.MinZ >> TerrainChunk.SizeBits;
            int maxChunkZ = region.MaxZ >> TerrainChunk.SizeBits;
            for (int x = minChunkX; x <= maxChunkX; x++) {
                for (int z = minChunkZ; z <= maxChunkZ; z++) {
                    yield return new Point2(x, z);
                }
            }
        }

        static int TotalRegionChunks() {
            int total = 0;
            foreach (ResidencyRegion region in Regions) {
                total += CountRegionChunks(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            }
            return total;
        }

        static void Normalize(ref int a, ref int b) {
            if (a > b) {
                (a, b) = (b, a);
            }
        }
    }
}
