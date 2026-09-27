using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.51：**32³ 表面壳的生产路径**（新目标 4.3 的第三步）。
    ///
    /// v0.1.49 造出壳（16 KiB vs 128 KiB），v0.1.50 把壳网格化（剔面 + 贪心合并）并证明能画；
    /// 但那时还是"手动采集 + 实验层"。本版接上生产语义：
    ///
    ///   1. **卸载即采集**：区块离开加载范围的那一刻（`TerrainUpdater.AllocateAndFreeChunks`
    ///      的 `NotifyChunkUnloading` 钩子，**主线程**、数据还在），把该区块所属的 32³ 立方体
    ///      —— 只要它的 2×2 个区块都还在、都已 Valid（内容与光照都算好）—— 采成一张壳存下来。
    ///      这就是 Distant Horizons 的"边加载边建 LOD 数据库"在本作的对应机制
    ///      （v0.1.17 进入即采 + v0.1.47 卸载前采，本版把**壳**挂到后一个钩子上）。
    ///   2. **离开加载范围之后才渲染**：真实地形还在时（区块已分配）**一律不画壳**，
    ///      否则两层会互相穿插；只有区块真被释放了，壳才顶上（这就是"距离分层"的第一刀）。
    ///   3. **交接带内替换现有 LOD**：壳只画在 [视距-8, 视距+96] 这一圈（与 v0.1.45 的 4 m 近环同一带），
    ///      并整体抬高 `BandLift`（默认 0.35 m）—— 壳是真高、LOD 是 16 m 平滑面，抬高一点让真高在带内稳定胜出。
    ///
    /// 代价（如实记）：壳 16 KiB/立方体，网格约 48 KiB/立方体（合并口径），两者都按下限设阈值；
    /// 采集与建模都有预算（每 Tick 最多几个立方体 / 几毫秒），避免卸载瞬间卡帧。
    /// </summary>
    public static class SkylineCubeShellStore {
        public const int CubeSize = CubeSurface32.Size;
        public const int ShellBytesPerCube = 16384;

        /// <summary>总开关：关掉即完全不采集、不渲染（默认开）。</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>渲染开关（采集照常，只是不画；A/B 用）。</summary>
        public static bool RenderEnabled { get; set; } = true;
        /// <summary>壳数量上限（4096 × 16 KiB = 64 MiB）；超出按"最久没被用到"淘汰。</summary>
        public static int MaxCubes { get; set; } = 4096;
        /// <summary>
        /// 一次"离开加载范围"事件里最多同步采集几个立方体（防大卸载卡帧；超出的计入 `skippedOverBudget`）。
        /// 实测单个立方体采集 **0.79 ms**（AgentLab）：64 个 ≈ 50 ms，而"改视距"这种一次性大卸载本来就在
        /// 暂停菜单里发生；正常走路/跨区每次只掉几个区块，代价在毫秒级。
        /// </summary>
        public static int InlineHarvestPerCall { get; set; } = 64;
        /// <summary>每 Tick 建网格的时间预算（毫秒）。</summary>
        public static float MeshBudgetMs { get; set; } = 4f;
        /// <summary>
        /// [v0.1.52] **分距离精度阶梯**（毫秒级的"最小体素 / 最小材质分片"边长）。
        /// 语义：`rel = 距离 − 视距`；`rel ≤ TierMetres[0]` → 1 m（完整 32³），
        /// 再往下依次 2 m（16³）、4 m（8³）、8 m（4³）、16 m（2³）、最后 32 m（**整块**）。
        /// 壳本身**始终按 1 m 采集**（16 KiB 不变），降精度在**建网格时聚合**出来
        /// （高度取主流材质列的中位、材质取众数 —— 与现有 LOD 同口径），所以同一张壳可以按距离换档。
        /// </summary>
        public static float[] TierMetres { get; set; } = [48f, 96f, 192f, 384f, 768f];

        /// <summary>
        /// 交接带外边界（米，相对视距）。**与 `TierMetres` 解耦**：档位表只决定"哪一档"，
        /// 带宽独立（否则把档位表调小会把绘制范围一起缩没 —— v0.1.52 实测踩过）。
        /// </summary>
        public static float BandMetres { get; set; } = 768f;
        public static float BandInset { get; set; } = 8f;
        /// <summary>带内抬高（米）：让真高在带内压过 16 m 平滑面（0 = 不抬，用于 A/B）。</summary>
        public static float BandLift { get; set; } = 0.35f;
        /// <summary>带内每帧最多画多少个壳（防止一次画几百个 draw call）。</summary>
        public static int MaxDrawPerFrame { get; set; } = 192;
        /// <summary>网格口径：true = 贪心合并（四边形最少）、false = 逐格（纹理精确）。</summary>
        public static bool UseMergedMesh { get; set; } = true;

        /// <summary>[v0.1.52] 网格滑动窗口：距离超过 `BandMetres × MeshReleaseFactor` 或地形已加载时**释放网格**（壳留着）。</summary>
        public static float MeshReleaseFactor { get; set; } = 1f;
        /// <summary>[v0.1.52] 网格滑动窗口开关（关掉 = 网格一直常驻，相当于 v0.1.51 的行为）。</summary>
        public static bool MeshSlidingWindow { get; set; } = true;

        // ---------------- 表面体素壳进生产路径（v0.1.60，目标 1.6 → 1.3「体积感」） ----------------
        /// <summary>
        /// [v0.1.60] 是否在**最近档**额外采"表面体素壳"（`SurfaceVoxelShell32`）并用六向贪心网格画它。
        /// 关掉 = 逐位回到 v0.1.59 的列顶高度场行为。
        /// </summary>
        public static bool SurfaceVoxelEnabled { get; set; } = true;
        /// <summary>
        /// [v0.1.60] 只有档位 ≤ 这个值的立方体才用体素网格（默认 **1 = 只最近一档**）。
        /// 理由（实测）：体素网格 ≈ 336 KiB/立方体（是列顶高度场的 ~2.4×），
        /// 全档位放开会把常驻显存/内存拉高一个数量级；而"体积感"最需要表达的正是**眼前这一圈**。
        /// </summary>
        public static int SurfaceVoxelMaxStep { get; set; } = 1;
        /// <summary>[v0.1.60] 持有体素壳的立方体上限（每份 16 KiB；512 份 = 8 MiB）。超出的记 `voxelSkippedByCap`。</summary>
        public static int SurfaceVoxelMaxCubes { get; set; } = 512;
        /// <summary>[v0.1.60] 体素壳的采集半径（米，相对视距）：`rel ≤ 此值` 才采（默认 48 = `TierMetres[0]`）。</summary>
        public static float SurfaceVoxelRelMetres { get; set; } = 48f;

        // ---------------- 带内主动补采（v0.1.61，里程碑 1.2「无缝转换」的数据侧） ----------------
        /// <summary>
        /// [v0.1.61] **在地形还在的时候就把它所属的 32³ 壳采好**，这样地形释放的那一刻壳已经就位。
        ///
        /// 为什么必须主动采：壳只在"离开加载范围"这一个时刻采（那时要 2×2 区块都还 Valid），
        /// 一旦某个兄弟区块当时没就绪，这个立方体就**永远不会有壳**（`skippedNotReady`），
        /// 除非玩家再走回来一次。于是"已加载区块 / 壳"的边界上就留下洞 —— 这就是用户说的
        /// "区块加载边界与 LOD 显示边界存在区隔"里，**数据侧**那一半。
        /// </summary>
        public static bool BackfillEnabled { get; set; } = true;
        /// <summary>[v0.1.61] 从"视距 − `BandInset`"往外扫多宽（米）。默认 192 m ≈ 6 个立方体环，覆盖交界最显眼的一段。</summary>
        public static float BackfillRelMetres { get; set; } = 192f;
        /// <summary>
        /// [v0.1.61] **从加载半径内侧多少米就开始扫**（默认 64 m）。
        /// 必须往内扫的原因（实测踩到）：游戏的**区块加载半径比视距小**（视距 128 m 时，
        /// 壳体带 [120, 896] m 里只有个位数立方体是"地形还在"），所以"只扫带内"会**什么都扫不到**
        /// （第一次实测 `backfillMissingCubes=0`、`lastBackfillMs=0.01 ms`）。
        /// 而真正需要预先采的，正是"现在还在、马上要被释放"的那一圈。
        /// </summary>
        public static float BackfillInsideMetres { get; set; } = 64f;
        /// <summary>[v0.1.61] 每 Tick 最多补采几个立方体（冷启动/走动时的一次性成本，补完就停）。</summary>
        public static int BackfillPerTick { get; set; } = 2;
        /// <summary>[v0.1.61] 每 Tick 补采的时间预算（毫秒）。</summary>
        public static float BackfillBudgetMs { get; set; } = 3f;
        public static long BackfilledTotal { get; private set; }
        public static int BackfillScanned { get; private set; }
        public static int BackfillMissingCubes { get; private set; }
        public static int BackfillReadyCubes { get; private set; }
        public static double LastBackfillMs { get; private set; }

        // ---------------- 存档 P4（v0.1.53） ----------------
        /// <summary>存档开关。</summary>
        public static bool PersistenceEnabled { get; set; } = true;
        /// <summary>每个 Tick 最多写几条（1 条 = 16 KiB）；默认 64 条 ≈ 1 MiB/Tick，避免一次性写 64 MiB 卡帧。</summary>
        public static int SaveRecordsPerTick { get; set; } = 64;
        /// <summary>自动存档间隔（秒，仅在"脏"时触发）。</summary>
        public static double SaveIntervalSeconds { get; set; } = 60.0;

        const int SaveMagic = 0x53434B53;        // "SCKS"
        const int SaveVersion = 1;
        /// <summary>一条记录的字节数：3×int 坐标（12 B）+ 壳（16,384 B）。</summary>
        public const int RecordBytes = 12 + CubeSurface32.SerializedBytes;
        /// <summary>[v0.1.57] 删除标记（墓碑）用的全零壳。</summary>
        static readonly CubeSurface32 m_emptyShell = new(0, 0, 0);
        static readonly HashSet<(int Cx, int Cy, int Cz)> m_dirtyCubes = [];
        static readonly HashSet<(int Cx, int Cy, int Cz)> m_tombstones = [];
        static bool m_appendMode;
        static bool m_fileHeaderValid;
        static int m_fileRecords;
        static long m_appendedRecords;
        static long m_appendedBytes;
        static long m_tombstonesWritten;
        static string m_worldDir, m_shellPath, m_saveTempPath;
        static Stream m_saveStream;
        static BinaryWriter m_saveWriter;
        static List<(int Cx, int Cy, int Cz)> m_saveQueue;
        static int m_saveTotal, m_saveWritten;
        static double m_nextSaveTime;
        static bool m_dirty;

        public static bool SaveInProgress => m_saveStream != null;
        public static bool Dirty => m_dirty;
        public static long SavedRecordsTotal { get; private set; }
        public static long LoadedRecordsTotal { get; private set; }
        public static long AppendedRecordsTotal => m_appendedRecords;
        public static long AppendedBytes => m_appendedBytes;
        public static long CompactRewrites => m_compactRewrites;
        public static long TombstonesWritten => m_tombstonesWritten;
        public static long TombstonesLoaded { get; private set; }
        public static int FileRecords => m_fileRecords;
        static long m_compactRewrites;
        public static double LastSaveMs { get; private set; }
        public static double LastLoadMs { get; private set; }
        public static long FileBytes { get; private set; }
        public static string PersistenceError { get; private set; } = "";
        public static string PersistencePath => m_shellPath ?? "";
        /// <summary>壳接管后让现有 LOD 层让位（默认开；false = 两层叠着画，用于 A/B 看穿插）。</summary>
        public static bool RestrictLod { get; set; } = true;
        /// <summary>
        /// **只画"四邻都在"的壳**（默认**关**）。壳是"走过才有的"，覆盖天然是补丁状；
        /// 只画孤立的一张 = 与旁边 LOD 层拼出锯齿缝（v0.1.51 实测画面发花的主因）。
        /// 要求上下左右四个邻居立方体也有壳，画出来的就是**连片**的一整块，与 LOD 的边界是干净的直边。
        /// **但默认不启用**：刚离开加载范围的那一圈壳是**薄环**（径向 2 个立方体厚），
        /// 环上的立方体必然有一侧邻居还是"真实地形"（没有壳）→ 实测 40 个壳里只有 **2** 个满足四邻齐全，
        /// 等于把功能关掉了。它适合"玩家已经走过一大片"（DH 式长期游玩）之后再打开。
        /// </summary>
        public static bool RequireNeighbors { get; set; } = false;

        /// <summary>
        /// [v0.1.55] **覆盖够了就自动打开四邻规则**：`BandCoverage ≥ MinBandCoverage` 时启用
        /// （覆盖不足时强行启用会把功能关掉 —— v0.1.51 实测薄环上 40 个壳只有 2 个合格）。
        /// </summary>
        public static bool RequireNeighborsWhenCovered { get; set; } = true;
        public static float MinBandCoverage { get; set; } = 0.6f;
        public static float BandCoverage { get; private set; }
        public static bool NeighborRuleActive { get; private set; }

        // ---------------- [v0.1.55] 更宽的采集口径：区块 Valid 就采（不等卸载） ----------------
        /// <summary>开关：区块刚 Valid 时把它所属的 32³ 立方体排进采集队列。</summary>
        public static bool CaptureOnValid { get; set; } = true;
        /// <summary>待采队列上限（超出就丢弃新的，记 `pendingDropped`）。</summary>
        public static int MaxPending { get; set; } = 4096;
        /// <summary>每 Tick 最多处理几个待采立方体。</summary>
        public static int PendingPerTick { get; set; } = 8;
        /// <summary>每 Tick 采壳的时间预算（毫秒）。</summary>
        public static float HarvestBudgetMs { get; set; } = 4f;
        /// <summary>兄弟区块还没就绪时的重试次数上限（之后就放弃这个立方体）。</summary>
        public static int MaxPendingRetries { get; set; } = 8;

        // 更新线程只允许碰这两个并发容器（`m_entries` 只在主线程动）
        static readonly ConcurrentQueue<(int Cx, int Cy, int Cz)> m_pendingQueue = new();
        static readonly ConcurrentDictionary<(int Cx, int Cy, int Cz), int> m_pendingSet = new();
        public static int PendingCount => m_pendingQueue.Count;
        public static long HarvestedOnValid { get; private set; }
        public static long PendingDropped { get; private set; }
        public static long PendingSkipped { get; private set; }

        sealed class Entry {
            public CubeSurface32 Shell;
            public CubeSurfaceMesh32 Mesh;
            public int MeshStep;                 // [v0.1.52] 当前网格是按哪一档建的
            // [v0.1.60] 表面体素壳（只在最近档采；有它就优先画它 —— 网格互斥，见 DisposeMeshes）
            public SurfaceVoxelShell32 VoxelShell;
            public SurfaceVoxelMesh VoxelMesh;
            public bool MeshIsVoxel;             // 当前常驻的是哪一类网格
            public double LastUsed;
        }

        // 用元组当键：立方体坐标（含负坐标）直接可用，不做位打包（省掉一整类符号 bug）
        static readonly Dictionary<(int Cx, int Cy, int Cz), Entry> m_entries = [];
        static readonly Queue<(int Cx, int Cy, int Cz)> m_meshQueue = [];
        static readonly HashSet<(int Cx, int Cy, int Cz)> m_queued = [];

        // ---- 统计（判据都从这里出） ----
        public static int CubeCount => m_entries.Count;
        public static long ShellBytes => (long)m_entries.Count * ShellBytesPerCube;
        public static long MeshVertexBytes { get; private set; }
        public static long HarvestedTotal { get; private set; }
        public static long SkippedNotReady { get; private set; }
        public static long SkippedDuplicate { get; private set; }
        public static long EvictedTotal { get; private set; }
        public static long MeshedTotal { get; private set; }
        public static long SkippedOverBudget { get; private set; }
        public static long MeshRebuiltTotal { get; private set; }
        public static long MeshReleasedTotal { get; private set; }
        public static int MeshResident { get; private set; }
        public static string StepHistogram { get; private set; } = "";
        public static long DrawnLastFrame { get; private set; }
        /// <summary>[v0.1.60] 其中有多少个是**表面体素网格**（其余是列顶高度场网格）。</summary>
        public static long DrawnVoxelLastFrame { get; private set; }
        public static long SkippedBecauseLoaded { get; private set; }
        public static long SkippedIsolated { get; private set; }
        public static double LastHarvestMs { get; private set; }
        public static double LastMeshMs { get; private set; }
        public static double LastPendingMs { get; private set; }
        public static string LastError { get; private set; } = "";

        // [v0.1.60] 表面体素壳的账（判据都从这里出）
        /// <summary>持有体素壳的立方体数（每 Tick 在 `BuildMeshes` 里实数一遍，避免计数器漂移）。</summary>
        public static int VoxelShellCubes { get; private set; }
        public static long VoxelShellBytes => (long)VoxelShellCubes * SurfaceVoxelShell32.ShellBytes;
        public static int VoxelMeshResident { get; private set; }
        public static long VoxelMeshBytes { get; private set; }
        public static long VoxelHarvestedTotal { get; private set; }
        public static long VoxelSkippedByCap { get; private set; }
        public static long VoxelDegradedCubes { get; private set; }
        public static long VoxelDroppedVoxels { get; private set; }
        public static long VoxelMeshedTotal { get; private set; }
        public static double LastVoxelHarvestMs { get; private set; }
        /// <summary>[v0.1.60] 每 Tick 最多**预热/刷新**几个立方体的体素壳（超出的留到下一 Tick）。</summary>
        public static int VoxelPrewarmPerTick { get; set; } = 2;
        /// <summary>[v0.1.60] 预热用的每 Tick 时间预算（毫秒）。</summary>
        public static float VoxelPrewarmBudgetMs { get; set; } = 3f;
        /// <summary>[v0.1.60] 刷新（覆盖已有体素壳）的次数 —— 与 `VoxelHarvestedTotal`（新建）分开记。</summary>
        public static long VoxelRefreshedTotal { get; private set; }
        /// <summary>[v0.1.60] 预热时因为"地形不在/未 Valid"跳过的次数（如实记，不当成错误）。</summary>
        public static long VoxelPrewarmSkippedNoTerrain { get; private set; }
        public static double LastPrewarmMs { get; private set; }
        static int m_voxelShellCount;

        static Terrain Terrain => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
        static float ViewRangeMetres => GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
            ?? SettingsManager.VisibilityRange;

        /// <summary>[v0.1.60] 释放一个条目的**两类网格**（体素网格与高度场网格互斥，释放时都清掉最省心）。</summary>
        static void DisposeMeshes(Entry entry) {
            entry.Mesh?.Dispose();
            entry.Mesh = null;
            entry.VoxelMesh?.Dispose();
            entry.VoxelMesh = null;
            entry.MeshIsVoxel = false;
        }

        /// <summary>
        /// [v0.1.60] 丢弃一个条目（淘汰 / 清空 / 忘记都走这里）——
        /// 保证"持有体素壳的立方体数"不会因为某个删除路径忘了减而漂移。
        /// </summary>
        static void ReleaseEntry(Entry entry) {
            DisposeMeshes(entry);
            if (entry.VoxelShell != null) {
                entry.VoxelShell = null;
                if (m_voxelShellCount > 0) {
                    m_voxelShellCount--;
                }
            }
        }

        /// <summary>
        /// [v0.1.60] 给一个**刚采到的**立方体补采"表面体素壳"（目标 1.6：采所有裸露在外的方块 → 1.3 的体积感）。
        ///
        /// 两道门控（都是为了内存与帧时间）：
        ///   1. **只采最近档**：`rel = 距离 − 视距`，`rel > SurfaceVoxelRelMetres` 就不采
        ///      —— 远的立方体用不到体素网格（档位 > `SurfaceVoxelMaxStep`）；
        ///   2. **总份数上限** `SurfaceVoxelMaxCubes`（每份 16 KiB），超出的记 `voxelSkippedByCap` 如实报。
        ///
        /// **必须在地形还在的时候采**（与壳采集同一个理由：卸载后读回来是"假空"）。
        /// </summary>
        static void TryHarvestVoxelShell(Entry entry, Terrain terrain, int cx, int cy, int cz) {
            if (!SurfaceVoxelEnabled || entry == null || terrain == null) {
                return;
            }
            if (m_voxelShellCount >= SurfaceVoxelMaxCubes) {
                VoxelSkippedByCap++;
                return;
            }
            float rel = CubeDistance(cx, cz, SkylineLod.CameraViewPosition()) - ViewRangeMetres;
            if (rel > SurfaceVoxelRelMetres) {
                return;
            }
            HarvestVoxelShell(entry, terrain, cx, cy, cz);
        }

        /// <summary>
        /// [v0.1.60] **真正抓一次**表面体素壳并挂到条目上（新建或**刷新**）。
        /// 返回 true 表示这次抓到并写入了（`VoxelCount == 0` 的空立方体不算）。
        /// </summary>
        static bool HarvestVoxelShell(Entry entry, Terrain terrain, int cx, int cy, int cz) {
            if (!SurfaceVoxelEnabled || entry == null || terrain == null) {
                return false;
            }
            if (entry.VoxelShell == null && m_voxelShellCount >= SurfaceVoxelMaxCubes) {
                VoxelSkippedByCap++;
                return false;
            }
            Stopwatch watch = Stopwatch.StartNew();
            SurfaceVoxelShell32 voxels = SurfaceVoxelShell32.ExtractFrom(terrain, cx, cy, cz);
            watch.Stop();
            LastVoxelHarvestMs = watch.Elapsed.TotalMilliseconds;
            if (voxels.VoxelCount == 0) {
                return false;                                 // 全空立方体：不占额度
            }
            bool had = entry.VoxelShell != null;
            entry.VoxelShell = voxels;
            if (had) {
                VoxelRefreshedTotal++;
            }
            else {
                m_voxelShellCount++;
                VoxelHarvestedTotal++;
            }
            return true;
        }

        /// <summary>一个立方体覆盖的 2×2 区块是否**都已分配且已 Valid**（内容与光照都算好）。</summary>
        static bool ChunksAllValid(Terrain terrain, int cx, int cz) {
            if (terrain == null) {
                return false;
            }
            for (int dx = 0; dx < 2; dx++) {
                for (int dz = 0; dz < 2; dz++) {
                    TerrainChunk chunk = terrain.GetChunkAtCoords(cx * 2 + dx, cz * 2 + dz);
                    if (chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// [v0.1.61] **带内主动补采**（里程碑 1.2 无缝转换的数据侧）：扫"视距 − `BandInset`"外
        /// `BackfillRelMetres` 米的一圈 32 m 网格，凡是**还没有壳**、且它的 2×2 区块**都已 Valid** 的立方体，
        /// 就地采一张壳（顺带按 `SurfaceVoxelRelMetres` 采体素壳）。
        ///
        /// 语义上与"卸载即采"完全一致（同一条 `CubeSurface32.Extract` + 同一套材质替换规则），区别只是
        /// **时机提前到"地形还在"的时候** —— 于是地形释放的那一刻壳已经在仓里，
        /// `OnChunksLeavingRange` 会把它当重复条目跳过（不会覆盖成旧的），边界上不再出现"该有壳却没有"的洞。
        ///
        /// **它治的是"将来"的洞，不是"过去"的洞**：地形已经释放、当时又没采到的立方体，
        /// 这里 `ChunksAllValid` 为假、只能等玩家再走近一次 —— 这一点如实记在 `backfillMissingCubes` 里。
        /// </summary>
        static void BackfillBandShells(Terrain terrain) {
            if (!BackfillEnabled || terrain == null || BackfillPerTick <= 0) {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            Vector3 camera = SkylineLod.CameraViewPosition();
            float viewRange = ViewRangeMetres;
            // 从"带内侧"起扫（默认 视距−8−64 m），一直到 视距+BackfillRelMetres：
            // 内侧那一段正是"地形还在、马上要被释放"的立方体 —— 预先采好它们，边界就不会有洞。
            float near = MathF.Max(MathF.Max(viewRange - BandInset, 0f) - BackfillInsideMetres, 0f);
            float far = MathF.Min(viewRange + BandMetres, viewRange + BackfillRelMetres);
            float nearSq = near * near, farSq = far * far;
            int cx0 = (int)MathF.Floor((camera.X - far) / CubeSize);
            int cx1 = (int)MathF.Floor((camera.X + far) / CubeSize);
            int cz0 = (int)MathF.Floor((camera.Z - far) / CubeSize);
            int cz1 = (int)MathF.Floor((camera.Z + far) / CubeSize);
            int budget = BackfillPerTick;
            int scanned = 0, missing = 0, added = 0;
            for (int cx = cx0; cx <= cx1; cx++) {
                for (int cz = cz0; cz <= cz1; cz++) {
                    float wx = cx * CubeSize + CubeSize * 0.5f;
                    float wz = cz * CubeSize + CubeSize * 0.5f;
                    float dx = wx - camera.X, dz = wz - camera.Z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < nearSq || d2 > farSq) {
                        continue;
                    }
                    scanned++;
                    // 2×2 区块的顶面范围（与"卸载前预扫"同一口径）
                    int minTop = int.MaxValue, maxTop = int.MinValue;
                    bool ok = true;
                    for (int sx = 0; sx < 2 && ok; sx++) {
                        for (int sz = 0; sz < 2 && ok; sz++) {
                            TerrainChunk chunk = terrain.GetChunkAtCoords(cx * 2 + sx, cz * 2 + sz);
                            if (chunk == null || chunk.ThreadState < TerrainChunkState.Valid) {
                                ok = false;
                                break;
                            }
                            for (int x = 0; x < TerrainChunk.Size; x++) {
                                for (int z = 0; z < TerrainChunk.Size; z++) {
                                    int top = chunk.GetTopHeightFast(x, z);
                                    if (top > maxTop) {
                                        maxTop = top;
                                    }
                                    if (top < minTop) {
                                        minTop = top;
                                    }
                                }
                            }
                        }
                    }
                    if (!ok || maxTop < TerrainChunk.MinHeight) {
                        continue;
                    }
                    for (int cy = Math.Max(minTop, TerrainChunk.MinHeight) >> 5; cy <= maxTop >> 5; cy++) {
                        (int Cx, int Cy, int Cz) key = (cx, cy, cz);
                        if (m_entries.ContainsKey(key)) {
                            continue;
                        }
                        missing++;
                        if (budget <= 0 || watch.Elapsed.TotalMilliseconds > BackfillBudgetMs
                            || m_entries.Count >= MaxCubes) {
                            continue;                        // 预算用完：只如实计数，下一 Tick 再来
                        }
                        CubeSurface32 shell = CubeSurface32.Extract(terrain, cx, cy, cz);
                        if (shell.QuadCount == 0) {
                            continue;
                        }
                        Entry entry = new() { Shell = shell, LastUsed = Time.RealTime };
                        m_entries[key] = entry;
                        TryHarvestVoxelShell(entry, terrain, cx, cy, cz);
                        added++;
                        budget--;
                        BackfilledTotal++;
                        MarkCubeDirty(key);
                        if (m_queued.Add(key)) {
                            m_meshQueue.Enqueue(key);
                        }
                    }
                }
            }
            BackfillScanned = scanned;
            BackfillMissingCubes = missing;
            BackfillReadyCubes = added;
            LastBackfillMs = watch.Elapsed.TotalMilliseconds;
            if (added > 0) {
                SkylineLod.RequestRebuild();      // 壳接管的那片变了 → 让 LOD 立刻重排（让位）
            }
        }

        /// <summary>
        /// [v0.1.60] **体素壳预热 / 刷新**（每 Tick 受 `VoxelPrewarmPerTick` + `VoxelPrewarmBudgetMs` 限制）。
        ///
        /// 为什么必须有这一步（不是重复劳动）：
        ///   1. **升级迁移**：v0.1.59 之前落盘的壳只有列顶高度场、没有体素数据；而壳是"**写一次**"的
        ///      （`OnChunksLeavingRange` 遇到已有条目直接 `skippedDuplicate`），**不预热就永远不会升级**；
        ///   2. **地形改动**：玩家在原地建/挖之后，壳要等地形**再次卸载**才会重采 —— 预热让"卸载那一刻"数据已经是对的；
        ///   3. **只对最近档**（`rel ≤ SurfaceVoxelRelMetres`）且**地形已 Valid** 的立方体做 —— 远处用不到体素网格。
        /// </summary>
        static void PrewarmVoxelShells(Terrain terrain) {
            PrewarmPass(terrain, VoxelPrewarmPerTick, VoxelPrewarmBudgetMs, refresh: false);
        }

        /// <summary>
        /// [v0.1.60] **同步强制预热**（取证/手动生成用）：最多给 `maxCubes` 个"最近档 + 地形已 Valid"的条目
        /// 补采/刷新体素壳；不受每 Tick 预算限制，但受 `maxCubes` 限制（避免一次卡很久）。返回本次成功几个。
        /// </summary>
        public static int PrewarmNow(int maxCubes, bool refresh = false) {
            return PrewarmPass(Terrain, maxCubes, double.MaxValue, refresh);
        }

        /// <summary>
        /// [v0.1.60] 预热/刷新的**唯一实现**。
        /// `refresh: false`（每 Tick 的自动预热）= **只补没有体素壳的**条目 —— 一次性，不会反复重采同一个立方体
        /// （v1 的第一版就踩过：同一批 12 个立方体被反复重采 1.4 万次，白烧 1~3 ms/帧）。
        /// `refresh: true`（显式调用 / 手动生成）= 覆盖已有体素壳，用于"玩家建/挖之后刷新壳"。
        /// 返回真正写入的个数。
        /// </summary>
        static int PrewarmPass(Terrain terrain, int maxCubes, double budgetMs, bool refresh) {
            if (!SurfaceVoxelEnabled || terrain == null || maxCubes <= 0 || m_entries.Count == 0) {
                return 0;
            }
            int done = 0;
            Stopwatch watch = Stopwatch.StartNew();
            Vector3 camera = SkylineLod.CameraViewPosition();
            float viewRange = ViewRangeMetres;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                if (done >= maxCubes || watch.Elapsed.TotalMilliseconds > budgetMs) {
                    break;
                }
                if (m_voxelShellCount >= SurfaceVoxelMaxCubes && kv.Value.VoxelShell == null) {
                    VoxelSkippedByCap++;
                    break;
                }
                if (!refresh && kv.Value.VoxelShell != null) {
                    continue;                                 // 一次性补采：已有的不重采
                }
                if (CubeDistance(kv.Key.Cx, kv.Key.Cz, camera) - viewRange > SurfaceVoxelRelMetres) {
                    continue;
                }
                if (!ChunksAllValid(terrain, kv.Key.Cx, kv.Key.Cz)) {
                    VoxelPrewarmSkippedNoTerrain++;
                    continue;
                }
                if (HarvestVoxelShell(kv.Value, terrain, kv.Key.Cx, kv.Key.Cy, kv.Key.Cz)) {
                    done++;
                }
            }
            LastPrewarmMs = watch.Elapsed.TotalMilliseconds;
            return done;
        }

        /// <summary>"四邻都在"检查（`RequireNeighbors` 的判定体）。</summary>
        static bool NeighborhoodComplete(int cx, int cy, int cz) {
            if (!NeighborRuleActive) {
                return true;
            }
            return m_entries.ContainsKey((cx - 1, cy, cz)) && m_entries.ContainsKey((cx + 1, cy, cz))
                && m_entries.ContainsKey((cx, cy, cz - 1)) && m_entries.ContainsKey((cx, cy, cz + 1));
        }

        /// <summary>
        /// [v0.1.55] **区块刚 Valid**（进入加载范围）时由 `SkylineLod.NotifyChunkValid` 转发。
        /// **这个钩子在更新线程上**（`TerrainUpdater.ThreadUpdateFunction` → `UpdateChunkSingleStep`），
        /// 所以这里**只允许入队**：不读 `m_entries`、不碰地形；真正的采集在主线程 `Tick()` 里做。
        /// （与 v0.1.36 修掉的那次"更新线程碰非并发集合"是同一类问题，这里是主动规避。）
        /// </summary>
        public static void OnChunkValid(TerrainChunk chunk) {
            if (!Enabled || !CaptureOnValid || chunk == null) {
                return;
            }
            try {
                if (m_pendingSet.Count >= MaxPending) {
                    PendingDropped++;
                    return;
                }
                int minTop = int.MaxValue, maxTop = int.MinValue;
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        int top = chunk.GetTopHeightFast(x, z);
                        if (top > maxTop) {
                            maxTop = top;
                        }
                        if (top < minTop) {
                            minTop = top;
                        }
                    }
                }
                if (maxTop < TerrainChunk.MinHeight) {
                    return;                                  // 整块全空（例如高空立方体）
                }
                int cxc = chunk.Coords.X >> 1, czc = chunk.Coords.Y >> 1;
                for (int cy = Math.Max(minTop, TerrainChunk.MinHeight) >> 5; cy <= maxTop >> 5; cy++) {
                    (int Cx, int Cy, int Cz) key = (cxc, cy, czc);
                    if (m_pendingSet.TryAdd(key, 0)) {
                        m_pendingQueue.Enqueue(key);
                    }
                }
            }
            catch (Exception e) {
                LastError = e.Message;                       // 只记字符串，不在这里抛
            }
        }

        /// <summary>
        /// [v0.1.60] **主线程**手动入队：把某一列（区块坐标）所属的 32³ 立方体排进待采队列。
        /// 供"手动生成 LOD"（`SkylineLodManualBuild`）复用同一条采集通道；与 `OnChunkValid` 用同一套分带计算。
        /// </summary>
        public static int QueueCubeForColumn(int chunkX, int chunkZ) {
            if (!Enabled) {
                return 0;
            }
            try {
                Terrain terrain = Terrain;
                TerrainChunk chunk = terrain?.GetChunkAtCoords(chunkX, chunkZ);
                if (chunk == null) {
                    return 0;
                }
                int minTop = int.MaxValue, maxTop = int.MinValue;
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        int top = chunk.GetTopHeightFast(x, z);
                        if (top > maxTop) {
                            maxTop = top;
                        }
                        if (top < minTop) {
                            minTop = top;
                        }
                    }
                }
                if (maxTop < TerrainChunk.MinHeight) {
                    return 0;
                }
                int cxc = chunkX >> 1, czc = chunkZ >> 1;
                int queued = 0;
                for (int cy = Math.Max(minTop, TerrainChunk.MinHeight) >> 5; cy <= maxTop >> 5; cy++) {
                    (int Cx, int Cy, int Cz) key = (cxc, cy, czc);
                    if (m_entries.ContainsKey(key)) {
                        continue;                                // 已经有壳：不重复采
                    }
                    if (m_pendingSet.TryAdd(key, 0)) {
                        m_pendingQueue.Enqueue(key);
                        queued++;
                    }
                }
                return queued;
            }
            catch (Exception e) {
                LastError = e.Message;
                return 0;
            }
        }

        /// <summary>[v0.1.55] 主线程：按预算处理待采队列（兄弟区块没就绪就稍后重试，最多 `MaxPendingRetries` 次）。</summary>
        static void HarvestPending() {
            if (m_pendingQueue.IsEmpty) {
                return;
            }
            Terrain terrain = Terrain;
            if (terrain == null) {
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            int budget = Math.Max(0, PendingPerTick);
            int attempts = m_pendingQueue.Count;                 // 本轮最多看这么多（避免死循环）
            while (budget > 0 && attempts-- > 0 && m_pendingQueue.TryDequeue(out var key)) {
                if (watch.Elapsed.TotalMilliseconds > HarvestBudgetMs) {
                    m_pendingQueue.Enqueue(key);                 // 预算用完：放回去，下帧再说
                    break;
                }
                if (m_entries.ContainsKey(key)) {
                    m_pendingSet.TryRemove(key, out _);
                    PendingSkipped++;
                    continue;
                }
                bool ready = true;
                for (int dx = 0; dx < 2 && ready; dx++) {
                    for (int dz = 0; dz < 2 && ready; dz++) {
                        TerrainChunk sibling = terrain.GetChunkAtCoords(key.Cx * 2 + dx, key.Cz * 2 + dz);
                        if (sibling == null || sibling.ThreadState < TerrainChunkState.Valid) {
                            ready = false;
                        }
                    }
                }
                if (!ready) {
                    int retries = m_pendingSet.TryGetValue(key, out int n) ? n + 1 : 1;
                    if (retries > MaxPendingRetries) {
                        m_pendingSet.TryRemove(key, out _);
                        SkippedNotReady++;
                        continue;                                // 放弃（下次区块再 Valid 时会重新入队）
                    }
                    m_pendingSet[key] = retries;
                    m_pendingQueue.Enqueue(key);                 // 稍后重试（区块还在，数据不会丢）
                    continue;
                }
                Stopwatch one = Stopwatch.StartNew();
                CubeSurface32 shell = CubeSurface32.Extract(terrain, key.Cx, key.Cy, key.Cz);
                one.Stop();
                LastHarvestMs = one.Elapsed.TotalMilliseconds;
                m_pendingSet.TryRemove(key, out _);
                if (shell.QuadCount == 0) {
                    continue;
                }
                Entry entry = new() { Shell = shell, LastUsed = Time.RealTime };
                m_entries[key] = entry;
                TryHarvestVoxelShell(entry, terrain, key.Cx, key.Cy, key.Cz);   // [v0.1.60] 最近档补采体素壳
                HarvestedTotal++;
                HarvestedOnValid++;
                MarkCubeDirty(key);                  // [v0.1.57] 只把这一条标脏（增量存档）
                budget--;
            }
            LastPendingMs = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>[v0.1.52] 距离（米）→ 本档的**最小体素边长**（1/2/4/8/16/32 m）。</summary>
        public static int StepForDistance(float distance, float viewRange) {
            float[] tiers = TierMetres;
            float rel = distance - viewRange;
            int step = 1;
            for (int i = 0; i < tiers.Length; i++) {
                if (rel <= tiers[i]) {
                    return step;
                }
                step *= 2;
            }
            return Math.Min(step, CubeSize);
        }

        static float CubeDistance(int cx, int cz, in Vector3 camera) {
            float wx = cx * CubeSize + CubeSize * 0.5f;
            float wz = cz * CubeSize + CubeSize * 0.5f;
            float dx = wx - camera.X, dz = wz - camera.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// 这个世界坐标处"有没有一张**带内的**壳"。给现有 LOD 层判断"该不该让位"用。
        /// 带规则与 `Draw` 完全一致、且同样用**立方体中心**测距 —— 两边同口径才不会出现
        /// "LOD 让位了、壳却没画"的洞。
        /// </summary>
        public static bool HasShellInBand(int worldX, int height, int worldZ, float cameraX, float cameraZ) {
            if (!Enabled || !RenderEnabled || m_entries.Count == 0) {
                return false;
            }
            (int Cx, int Cy, int Cz) key = (worldX >> 5, height >> 5, worldZ >> 5);
            if (!m_entries.ContainsKey(key)) {
                return false;
            }
            if (!NeighborhoodComplete(key.Cx, key.Cy, key.Cz)) {
                return false;
            }
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float near = MathF.Max(viewRange - BandInset, 0f);
            float far = viewRange + BandMetres;
            float wx = key.Cx * CubeSize + CubeSize * 0.5f;
            float wz = key.Cz * CubeSize + CubeSize * 0.5f;
            float dx = wx - cameraX, dz = wz - cameraZ;
            float distSq = dx * dx + dz * dz;
            return distSq >= near * near && distSq <= far * far;
        }

        /// <summary>
        /// **预扫**：一次"离开加载范围"事件开始前，把即将释放的区块列表交进来（此时它们**全都还在**），
        /// 于是可以按 32³ 立方体（2×2 区块）**完整**采集。
        ///
        /// 为什么必须预扫而不是逐个区块释放时采：卸载是**同一个循环里逐个 Free** 的，
        /// 轮到某个区块时它的兄弟区块往往已经被释放（v0.1.51 实测：逐个采时 197 次全部 `skippedNotReady`）。
        ///
        /// 采集在这里**同步**做完（不能推迟到 Tick —— 那时区块已经没了，读回来是"假空"）；
        /// 单次上限 `InlineHarvestPerCall` 个立方体，避免一次大卸载卡帧；建模仍然推迟到 `Tick`。
        /// </summary>
        public static void OnChunksLeavingRange(List<TerrainChunk> leaving) {
            if (!Enabled || leaving == null || leaving.Count == 0) {
                return;
            }
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    return;
                }
                HashSet<(int Cx, int Cz)> cubes = [];
                foreach (TerrainChunk chunk in leaving) {
                    if (chunk == null) {
                        continue;
                    }
                    cubes.Add((chunk.Coords.X >> 1, chunk.Coords.Y >> 1));
                }
                int budget = Math.Max(0, InlineHarvestPerCall);
                foreach ((int Cx, int Cz) cube in cubes) {
                    if (budget <= 0) {
                        SkippedOverBudget++;
                        continue;
                    }
                    // 2×2 区块必须都还在、都已 Valid（内容 + 光照都算好）
                    int minTop = int.MaxValue, maxTop = int.MinValue;
                    bool ready = true;
                    for (int dx = 0; dx < 2 && ready; dx++) {
                        for (int dz = 0; dz < 2 && ready; dz++) {
                            TerrainChunk sibling = terrain.GetChunkAtCoords(cube.Cx * 2 + dx, cube.Cz * 2 + dz);
                            if (sibling == null || sibling.ThreadState < TerrainChunkState.Valid) {
                                ready = false;
                                break;
                            }
                            for (int x = 0; x < TerrainChunk.Size; x++) {
                                for (int z = 0; z < TerrainChunk.Size; z++) {
                                    int top = sibling.GetTopHeightFast(x, z);
                                    if (top > maxTop) {
                                        maxTop = top;
                                    }
                                    if (top < minTop) {
                                        minTop = top;
                                    }
                                }
                            }
                        }
                    }
                    if (!ready) {
                        SkippedNotReady++;
                        continue;
                    }
                    if (maxTop < TerrainChunk.MinHeight) {
                        continue;                            // 全空列（比如高空立方体）：没有表面可存
                    }
                    int cyLo = Math.Max(minTop, TerrainChunk.MinHeight) >> 5;
                    int cyHi = maxTop >> 5;
                    for (int cy = cyLo; cy <= cyHi; cy++) {
                        if (budget <= 0) {
                            SkippedOverBudget++;
                            continue;
                        }
                        (int Cx, int Cy, int Cz) key = (cube.Cx, cy, cube.Cz);
                        if (m_entries.ContainsKey(key)) {
                            SkippedDuplicate++;
                            continue;
                        }
                        Stopwatch watch = Stopwatch.StartNew();
                        CubeSurface32 shell = CubeSurface32.Extract(terrain, cube.Cx, cy, cube.Cz);
                        watch.Stop();
                        LastHarvestMs = watch.Elapsed.TotalMilliseconds;
                        if (shell.QuadCount == 0) {
                            continue;
                        }
                        Entry entry = new() { Shell = shell, LastUsed = Time.RealTime };
                        m_entries[key] = entry;
                        TryHarvestVoxelShell(entry, terrain, key.Cx, key.Cy, key.Cz);   // [v0.1.60] 最近档补采体素壳
                        HarvestedTotal++;
                        MarkCubeDirty(key);              // [v0.1.57] 增量存档只写这一条
                        budget--;
                        if (m_queued.Add(key)) {
                            m_meshQueue.Enqueue(key);
                        }
                    }
                }
                EvictIfNeeded();
                if (HarvestedTotal > 0) {
                    SkylineLod.RequestRebuild();          // 壳接管的那片变了 → 让 LOD 立刻重排（让位）
                    MarkDirty();                          // [v0.1.53] 新采到壳 → 存档标脏
                }
            }
            catch (Exception e) {
                LastError = e.Message;
            }
        }

        /// <summary>每帧调用（由 `SkylineLod.Tick` 转发）：按预算给新采到的壳建网格（采集在卸载预扫里同步完成）。</summary>
        public static void Tick() {
            if (!Enabled) {
                return;
            }
            try {
                // [v0.1.53] 存档 P4：换世界自动 Load；脏了按间隔开一次**分帧增量写**
                if (PersistenceEnabled) {
                    EnsureWorld();
                    double now = Time.RealTime;
                    if (m_saveStream == null && m_dirty && now >= m_nextSaveTime) {
                        StartSave();
                        m_nextSaveTime = now + Math.Max(5.0, SaveIntervalSeconds);
                    }
                    SaveTick();
                }
                HarvestPending();      // [v0.1.55] 更宽口径：区块 Valid 就采（更新线程只入队）
                BackfillBandShells(Terrain);   // [v0.1.61] 带内主动补采：地形还在就先采好（无缝转换的数据侧）
                PrewarmVoxelShells(Terrain);   // [v0.1.60] 最近档补采/刷新表面体素壳（升级迁移 + 地形改动）
                EvictIfNeeded();
                BuildMeshes();
                UpdateBandCoverage();
            }
            catch (Exception e) {
                LastError = e.Message;
                Log.Warning($"SkylineCubeShellStore.Tick: {e.Message}");
            }
        }

        public static int BandShellCubes { get; private set; }
        public static int BandExpectedCubes { get; private set; }
        public static int BandDrawableCubes { get; private set; }
        public static int BandCompleteCubes { get; private set; }

        /// <summary>
        /// [v0.1.55] 统计交接带内的壳覆盖度 → 决定"四邻规则"要不要生效。
        /// **只统计近带（第一档，默认视距−8…视距+48 m）**：整条 768 m 带面积太大
        /// （`π(896²−120²)/32² ≈ 2,400` 个立方体），用它会永远达不到阈值、门控永远关着；
        /// 而"整圈有壳、边界干净"要解决的正是**最近这一圈**。
        /// 期望立方体数用环带面积估算：`π(far² − near²) / 32²`（一个立方体 32×32 m）。
        /// </summary>
        static void UpdateBandCoverage() {
            Vector3 camera = SkylineLod.CameraViewPosition();
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float near = MathF.Max(viewRange - BandInset, 0f);
            float far = viewRange + BandMetres;
            float nearSq = near * near, farSq = far * far;
            int inside = 0, drawable = 0, complete = 0;
            Terrain terrain = Terrain;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                float wx = kv.Key.Cx * CubeSize + CubeSize * 0.5f;
                float wz = kv.Key.Cz * CubeSize + CubeSize * 0.5f;
                float dx = wx - camera.X, dz = wz - camera.Z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= nearSq && d2 <= farSq) {
                    inside++;
                }
                // [v0.1.55] 覆盖度只统计**真正会画的那批**：带内 + 地形已释放（与 Draw 同一口径）
                if (d2 < nearSq || d2 > farSq || terrain == null) {
                    continue;
                }
                if (terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2) != null
                    || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2) != null
                    || terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2 + 1) != null
                    || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2 + 1) != null) {
                    continue;
                }
                drawable++;
                if (m_entries.ContainsKey((kv.Key.Cx - 1, kv.Key.Cy, kv.Key.Cz))
                    && m_entries.ContainsKey((kv.Key.Cx + 1, kv.Key.Cy, kv.Key.Cz))
                    && m_entries.ContainsKey((kv.Key.Cx, kv.Key.Cy, kv.Key.Cz - 1))
                    && m_entries.ContainsKey((kv.Key.Cx, kv.Key.Cy, kv.Key.Cz + 1))) {
                    complete++;
                }
            }
            float expected = MathF.PI * (far * far - near * near) / (CubeSize * CubeSize);
            BandShellCubes = inside;
            BandExpectedCubes = (int)MathF.Round(expected);
            BandDrawableCubes = drawable;
            BandCompleteCubes = complete;
            // 覆盖度 = "会画的壳里四邻齐全的比例"：四邻规则只会隐藏不齐的那些，所以这就是它的可用度
            BandCoverage = drawable >= 4 ? complete / (float)drawable : 0f;
            NeighborRuleActive = RequireNeighbors
                || (RequireNeighborsWhenCovered && BandCoverage >= MinBandCoverage);
        }

        /// <summary>[v0.1.57] 立刻按当前上限淘汰（取证用 `skyline.CubeShellEvictTo`）。</summary>
        public static void EvictNow() {
            EvictIfNeeded();
        }

        /// <summary>
        /// [v0.1.60] 某一列（区块坐标）所属的立方体**已经有壳了吗**（手动生成 LOD 用来算真实完成度）。
        /// 该列涉及的分带按"顶面高度"估：`cy = maxTop &gt;&gt; 5`。
        /// </summary>
        public static bool HasCubeForColumn(int chunkX, int chunkZ) {
            try {
                Terrain terrain = Terrain;
                TerrainChunk chunk = terrain?.GetChunkAtCoords(chunkX, chunkZ);
                if (chunk == null) {
                    return true;                             // 列都没了：没什么可等的
                }
                int maxTop = int.MinValue;
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        int top = chunk.GetTopHeightFast(x, z);
                        if (top > maxTop) {
                            maxTop = top;
                        }
                    }
                }
                if (maxTop < TerrainChunk.MinHeight) {
                    return true;                             // 空列
                }
                return m_entries.ContainsKey((chunkX >> 1, maxTop >> 5, chunkZ >> 1));
            }
            catch {
                return true;
            }
        }

        static void EvictIfNeeded() {
            if (m_entries.Count <= MaxCubes) {
                return;
            }
            // 简单口径：淘汰"最久没被用到"的（LastUsed 最小）。数量不大时一次性排序足够。
            List<KeyValuePair<(int Cx, int Cy, int Cz), Entry>> list = [.. m_entries];
            list.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            int drop = m_entries.Count - MaxCubes;
            for (int i = 0; i < drop && i < list.Count; i++) {
                ReleaseEntry(list[i].Value);
                m_entries.Remove(list[i].Key);
                MarkCubeRemoved(list[i].Key);        // [v0.1.57] 淘汰 → 存档写墓碑
                EvictedTotal++;
            }
            MarkDirty();
        }

        /// <summary>
        /// [v0.1.52] **按距离分档建网格 + 滑动窗口**（每 Tick 受 `MeshBudgetMs` 限制）：
        ///   * 每立方体算出它该用的档（`StepForDistance`），与当前网格档不同就**重建**；
        ///   * 地形已加载、或距离超过 `BandMetres × MeshReleaseFactor` 的立方体**释放网格**（壳留着）；
        ///   * 于是内存占用随"玩家附近需要画多少"滑动，而不是一直常驻 4096 份网格。
        /// </summary>
        static void BuildMeshes() {
            Stopwatch watch = Stopwatch.StartNew();
            Vector3 camera = SkylineLod.CameraViewPosition();
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float releaseRange = viewRange + BandMetres * MathF.Max(MeshReleaseFactor, 1f);
            Terrain terrain = Terrain;
            MeshVertexBytes = 0;
            MeshResident = 0;
            VoxelMeshBytes = 0;                  // [v0.1.60]
            VoxelMeshResident = 0;
            VoxelShellCubes = 0;
            VoxelDegradedCubes = 0;
            VoxelDroppedVoxels = 0;
            int s1 = 0, s2 = 0, s4 = 0, s8 = 0, s16 = 0, s32 = 0;
            bool budgetHit = false;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                Entry entry = kv.Value;
                if (entry.VoxelShell != null) {
                    VoxelShellCubes++;                       // [v0.1.60] 实数一遍，避免计数器漂移
                    if (entry.VoxelShell.Degraded) {         // 降级/丢体素的**当前**普查（不是累计值）
                        VoxelDegradedCubes++;
                        VoxelDroppedVoxels += entry.VoxelShell.DroppedVoxels;
                    }
                }
                float dist = CubeDistance(kv.Key.Cx, kv.Key.Cz, camera);
                if (MeshSlidingWindow) {
                    bool terrainLoaded = terrain != null
                        && (terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2 + 1) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2 + 1) != null);
                    if (dist > releaseRange || terrainLoaded) {
                        if (entry.Mesh != null || entry.VoxelMesh != null) {
                            DisposeMeshes(entry);
                            MeshReleasedTotal++;
                        }
                        continue;
                    }
                }
                int step = StepForDistance(dist, viewRange);
                // [v0.1.60] 最近档用**表面体素网格**（体积感），其余档位仍用列顶高度场（省内存）
                bool wantVoxel = SurfaceVoxelEnabled && entry.VoxelShell != null && step <= SurfaceVoxelMaxStep;
                bool hasMesh = entry.MeshIsVoxel ? entry.VoxelMesh != null : entry.Mesh != null;
                if (!budgetHit && watch.Elapsed.TotalMilliseconds > MeshBudgetMs) {
                    budgetHit = true;                    // 本 Tick 预算用完：已有的继续统计，新的下帧再说
                }
                if (!budgetHit && (!hasMesh || entry.MeshStep != step || entry.MeshIsVoxel != wantVoxel)) {
                    DisposeMeshes(entry);
                    if (wantVoxel) {
                        entry.VoxelMesh = SurfaceVoxelMesh.Build(entry.VoxelShell, true, true);
                        entry.MeshIsVoxel = true;
                        VoxelMeshedTotal++;
                    }
                    else {
                        CubeSurface32[] one = [entry.Shell];
                        entry.Mesh = CubeSurfaceMesh32.Build(one, 1, 1, UseMergedMesh, true, step);
                        entry.MeshIsVoxel = false;
                    }
                    entry.MeshStep = step;
                    MeshRebuiltTotal++;
                    MeshedTotal++;
                }
                bool resident = entry.MeshIsVoxel ? entry.VoxelMesh != null : entry.Mesh != null;
                if (resident) {
                    long bytes = entry.MeshIsVoxel ? entry.VoxelMesh.VertexBytes : entry.Mesh.VertexBytes;
                    MeshVertexBytes += bytes;
                    MeshResident++;
                    if (entry.MeshIsVoxel) {
                        VoxelMeshBytes += bytes;
                        VoxelMeshResident++;
                    }
                    switch (entry.MeshStep) {
                        case 1: s1++; break;
                        case 2: s2++; break;
                        case 4: s4++; break;
                        case 8: s8++; break;
                        case 16: s16++; break;
                        default: s32++; break;
                    }
                }
            }
            StepHistogram = $"step1={s1} step2={s2} step4={s4} step8={s8} step16={s16} step32={s32}";
            LastMeshMs = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>由 `SubsystemTerrain.Draw` 调用（紧跟现有 LOD 层之后）。</summary>
        public static void Draw(Camera camera) {
            DrawnLastFrame = 0;
            DrawnVoxelLastFrame = 0;
            if (!Enabled || !RenderEnabled || m_entries.Count == 0 || camera == null) {
                return;
            }
            try {
                Terrain terrain = Terrain;
                if (terrain == null) {
                    return;
                }
                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                    ?? SettingsManager.VisibilityRange;
                float near = MathF.Max(viewRange - BandInset, 0f);
                float far = viewRange + BandMetres;
                float nearSq = near * near, farSq = far * far;
                Shader shader = SkylineCubeSurfaceDemo.PrepareTerrainShader(camera, BandLift);
                if (shader == null) {
                    return;
                }
                int drawn = 0;
                foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                    if (drawn >= MaxDrawPerFrame) {
                        break;
                    }
                    int cx = kv.Key.Cx, cz = kv.Key.Cz;
                    float wx = cx * CubeSize + CubeSize * 0.5f;
                    float wz = cz * CubeSize + CubeSize * 0.5f;
                    float dx = wx - viewPosition.X, dz = wz - viewPosition.Z;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < nearSq || distSq > farSq) {
                        continue;
                    }
                    // 真实地形还在（区块已分配）→ 一律不画壳（否则两层互相穿插）
                    if (terrain.GetChunkAtCoords(cx * 2, cz * 2) != null
                        || terrain.GetChunkAtCoords(cx * 2 + 1, cz * 2) != null
                        || terrain.GetChunkAtCoords(cx * 2, cz * 2 + 1) != null
                        || terrain.GetChunkAtCoords(cx * 2 + 1, cz * 2 + 1) != null) {
                        SkippedBecauseLoaded++;
                        continue;
                    }
                    Entry entry = kv.Value;
                    // [v0.1.60] 两类网格互斥：最近档是体素网格（体积感），其余是列顶高度场网格
                    var vertexBuffer = entry.MeshIsVoxel ? entry.VoxelMesh?.VertexBuffer : entry.Mesh?.VertexBuffer;
                    var indexBuffer = entry.MeshIsVoxel ? entry.VoxelMesh?.IndexBuffer : entry.Mesh?.IndexBuffer;
                    int indexCount = entry.MeshIsVoxel
                        ? (entry.VoxelMesh?.IndexCount ?? 0)
                        : (entry.Mesh?.IndexCount ?? 0);
                    if (vertexBuffer == null || indexBuffer == null || indexCount == 0) {
                        continue;
                    }
                    if (!NeighborhoodComplete(cx, kv.Key.Cy, cz)) {
                        SkippedIsolated++;
                        continue;
                    }
                    entry.LastUsed = Time.RealTime;
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                        vertexBuffer, indexBuffer, 0, indexCount);
                    if (entry.MeshIsVoxel) {
                        DrawnVoxelLastFrame++;
                    }
                    drawn++;
                }
                DrawnLastFrame = drawn;
            }
            catch (Exception e) {
                LastError = e.Message;
                Log.Warning($"SkylineCubeShellStore.Draw: {e.Message}");
            }
        }

        /// <summary>清空（换世界/测试收尾用）。</summary>
        public static void Clear() {
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                ReleaseEntry(kv.Value);
            }
            // [v0.1.57] 清空要留下"墓碑"，否则旧档里的记录会在下次读档时又冒出来
            foreach ((int Cx, int Cy, int Cz) key in m_entries.Keys) {
                m_tombstones.Add(key);
            }
            m_entries.Clear();
            m_meshQueue.Clear();
            m_queued.Clear();
            m_dirtyCubes.Clear();
            MeshVertexBytes = 0;
            MarkDirty();
        }

        /// <summary>[v0.1.57] 去掉一个立方体（LRU 淘汰 / 取证用 `skyline.CubeShellForget`）。</summary>
        public static bool Forget((int Cx, int Cy, int Cz) key) {
            if (!m_entries.TryGetValue(key, out Entry entry)) {
                return false;
            }
            ReleaseEntry(entry);
            m_entries.Remove(key);
            MarkCubeRemoved(key);
            return true;
        }

        // ---------------- 存档 P4：懒切世界 + 分帧增量写（v0.1.53） ----------------

        /// <summary>确认当前世界的存档路径；**换世界时自动 Load**（与 `SkylineLod` 同一套懒加载口径）。</summary>
        static string EnsureWorld() {
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (info == null || string.IsNullOrEmpty(info.DirectoryName)) {
                return null;
            }
            if (m_worldDir != info.DirectoryName) {
                m_worldDir = info.DirectoryName;
                m_shellPath = Storage.CombinePaths(info.DirectoryName, "SkylineShell.bin");
                Load();
            }
            return m_shellPath;
        }

        /// <summary>壳仓发生变化（新采到 / 淘汰 / 清空）→ 标脏，等下一次自动存档。</summary>
        public static void MarkDirty() {
            m_dirty = true;
        }

        /// <summary>[v0.1.57] 记下"这个立方体变了"（主线程；增量存档只写它）。</summary>
        static void MarkCubeDirty((int Cx, int Cy, int Cz) key) {
            m_dirtyCubes.Add(key);
            m_tombstones.Remove(key);
            m_dirty = true;
        }

        /// <summary>[v0.1.57] 记下"这个立方体没了"（淘汰/忘记）——存档写一条**全零壳**当作删除标记。</summary>
        static void MarkCubeRemoved((int Cx, int Cy, int Cz) key) {
            m_dirtyCubes.Remove(key);
            m_tombstones.Add(key);
            m_dirty = true;
        }

        /// <summary>
        /// [v0.1.53 全量重写 → **v0.1.57 增量的追加写**]
        /// 只有存过档、且"要改的记录数"不到文件里一半时，才走**追加**：
        /// 打开正式文件 seek 到末尾，只写脏立方体（+ 被淘汰的写成"全零壳"墓碑），最后把头上的记录数改掉；
        /// 否则（首次 / 改动过半）走**全量重写**：写临时文件再安全换名。
        /// 读档侧天然支持追加：按顺序读，**后面的记录覆盖前面的**，全零壳 = 删除。
        /// </summary>
        static void StartSave() {
            string path = EnsureWorld();
            if (path == null) {
                return;
            }
            int pending = m_dirtyCubes.Count + m_tombstones.Count;
            if (pending == 0) {
                m_dirty = false;                       // 没有要写的：别开档
                return;
            }
            bool append = m_fileHeaderValid && m_fileRecords > 0
                && pending * 2 < Math.Max(64, m_fileRecords);
            m_appendMode = append;
            m_saveQueue = [];
            if (append) {
                m_saveQueue.AddRange(m_tombstones);    // 墓碑先写（保险：删除先落地）
                m_saveQueue.AddRange(m_dirtyCubes);
            }
            else {
                m_saveQueue.AddRange(m_entries.Keys);
            }
            m_saveTotal = m_saveQueue.Count;
            m_saveWritten = 0;
            try {
                if (append) {
                    m_saveStream = Storage.OpenFile(path, OpenFileMode.ReadWrite);
                    m_saveStream.Seek(0, SeekOrigin.End);
                }
                else {
                    m_saveTempPath = path + ".tmp";
                    m_saveStream = Storage.OpenFile(m_saveTempPath, OpenFileMode.Create);
                }
                m_saveWriter = new BinaryWriter(m_saveStream);
                if (!append) {
                    m_saveWriter.Write(SaveMagic);
                    m_saveWriter.Write(SaveVersion);
                    m_saveWriter.Write(m_saveTotal);
                    m_compactRewrites++;
                }
            }
            catch (Exception e) {
                PersistenceError = e.Message;
                AbortSave();
            }
        }

        /// <summary>[v0.1.53] 立刻完整存一次（把队列一次抽干，取证/退出时用）。</summary>
        public static void SaveNow() {
            if (m_saveStream == null) {
                StartSave();
            }
            Stopwatch watch = Stopwatch.StartNew();
            while (m_saveStream != null && watch.Elapsed.TotalSeconds < 30.0) {
                SaveRecordsPerTick = Math.Max(SaveRecordsPerTick, 4096);   // 一次抽干
                SaveTick();
            }
            SaveRecordsPerTick = 64;
        }

        /// <summary>按 `SaveRecordsPerTick` 写一批；写完就落盘（临时文件 → 正式文件）。</summary>
        static void SaveTick() {
            if (m_saveStream == null) {
                return;
            }
            try {
                int budget = Math.Max(1, SaveRecordsPerTick);
                while (budget-- > 0 && m_saveQueue.Count > 0) {
                    (int Cx, int Cy, int Cz) key = m_saveQueue[^1];
                    m_saveQueue.RemoveAt(m_saveQueue.Count - 1);
                    m_saveWriter.Write(key.Cx);
                    m_saveWriter.Write(key.Cy);
                    m_saveWriter.Write(key.Cz);
                    if (m_entries.TryGetValue(key, out Entry entry)) {
                        entry.Shell.WriteTo(m_saveWriter);
                    }
                    else {
                        // [v0.1.57] 墓碑：这个立方体没了（淘汰/忘记）→ 写一条**全零壳**当删除标记，
                        // 读档侧按"后面的记录覆盖前面的、全零壳即删除"处理。
                        m_emptyShell.WriteTo(m_saveWriter);
                        m_tombstonesWritten++;
                    }
                    m_saveWritten++;
                }
                if (m_saveQueue.Count == 0) {
                    FinishSave();
                }
            }
            catch (Exception e) {
                PersistenceError = e.Message;
                AbortSave();
            }
        }

        static void FinishSave() {
            Stopwatch watch = Stopwatch.StartNew();
            try {
                m_saveWriter.Flush();
                if (m_appendMode) {
                    // 追加：把头上的"记录总数"改掉（magic 4 + version 4 → 偏移 8）
                    int total = m_fileRecords + m_saveWritten;
                    m_saveStream.Seek(8, SeekOrigin.Begin);
                    m_saveWriter.Write(total);
                    m_saveWriter.Flush();
                    m_fileRecords = total;
                    m_appendedRecords += m_saveWritten;
                    m_appendedBytes += (long)m_saveWritten * RecordBytes;
                }
                m_saveWriter.Dispose();
                m_saveStream.Dispose();
                m_saveStream = null;
                m_saveWriter = null;
                if (!m_appendMode) {
                    Storage.MoveFileSafely(m_saveTempPath, m_shellPath);   // 覆盖旧档（先写临时文件再换名）
                    m_fileRecords = m_saveWritten;
                }
                m_fileHeaderValid = true;
                FileBytes = 12L + (long)m_fileRecords * RecordBytes;
                m_dirty = false;
                m_dirtyCubes.Clear();
                m_tombstones.Clear();
                SavedRecordsTotal += m_saveWritten;
            }
            catch (Exception e) {
                PersistenceError = e.Message;
                AbortSave();
            }
            finally {
                LastSaveMs = watch.Elapsed.TotalMilliseconds;
                m_saveQueue = null;
            }
        }

        static void AbortSave() {
            try {
                m_saveWriter?.Dispose();
                m_saveStream?.Dispose();
            }
            catch {
                // ignored
            }
            m_saveWriter = null;
            m_saveStream = null;
            m_saveQueue = null;
        }

        /// <summary>读档（换世界时自动调用；也可手动 `CubeShellLoad()`）。</summary>
        public static void Load() {
            Clear();                                  // 无条件先清（避免跨世界污染 —— 与 SkylineLod v0.1.0 的修复同因）
            LoadedRecordsTotal = 0;
            string path = m_shellPath;
            if (path == null || !Storage.FileExists(path)) {
                m_dirty = true;
                return;
            }
            Stopwatch watch = Stopwatch.StartNew();
            try {
                using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read)) {
                    BinaryReader reader = new(stream);
                    int magic = reader.ReadInt32();
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    if (magic != SaveMagic || version != SaveVersion) {
                        PersistenceError = $"bad header magic={magic:x8} version={version}";
                        m_fileHeaderValid = false;
                        return;
                    }
                    m_fileHeaderValid = true;
                    TombstonesLoaded = 0;
                    for (int i = 0; i < count; i++) {
                        int cx, cy, cz;
                        try {
                            cx = reader.ReadInt32();
                            cy = reader.ReadInt32();
                            cz = reader.ReadInt32();
                        }
                        catch (EndOfStreamException) {
                            break;                    // 上次写一半就被打断：读到哪算哪
                        }
                        CubeSurface32 shell = CubeSurface32.ReadFrom(reader);
                        // [v0.1.57] 追加式存档：**后面的记录覆盖前面的**；全零壳 = 删除标记
                        if (shell.IsEmpty) {
                            m_entries.Remove((cx, cy, cz));
                            TombstonesLoaded++;
                            continue;
                        }
                        m_entries[(cx, cy, cz)] = new Entry { Shell = shell, LastUsed = 0 };
                        LoadedRecordsTotal++;
                    }
                    m_fileRecords = count;
                }
                // 注意：`path` 是引擎虚拟路径（`app:/doc/...`），不能用 `FileInfo` 取长度
                // （实测报"文件名、目录名或卷标语法不正确"）→ 优先用 `Storage.GetFileSize`，退化为按格式自算。
                FileBytes = Storage.GetFileSize(path);
                if (FileBytes <= 0) {
                    FileBytes = 12L + m_fileRecords * RecordBytes;
                }
                m_dirty = false;
                m_dirtyCubes.Clear();
                m_tombstones.Clear();
            }
            catch (Exception e) {
                PersistenceError = e.Message;
            }
            LastLoadMs = watch.Elapsed.TotalMilliseconds;
            EvictIfNeeded();
        }

        /// <summary>判据：整仓的确定性摘要（FNV-1a over 排序后的记录）——用来验"重启前后逐字节一致"。</summary>
        public static string Hash() {
            List<(int Cx, int Cy, int Cz)> keys = [.. m_entries.Keys];
            keys.Sort();
            ulong h = 14695981039346656037UL;
            foreach ((int Cx, int Cy, int Cz) key in keys) {
                Mix(ref h, (uint)key.Cx);
                Mix(ref h, (uint)key.Cy);
                Mix(ref h, (uint)key.Cz);
                CubeSurface32 shell = m_entries[key].Shell;
                for (int i = 0; i < CubeSurface32.GridCells; i++) {
                    Mix(ref h, (uint)(ushort)shell.TopHeight[i]);
                    Mix(ref h, shell.TopContents[i]);
                }
                for (int f = 0; f < 4; f++) {
                    ushort[] side = shell.SideContents[f];
                    for (int i = 0; i < CubeSurface32.GridCells; i++) {
                        Mix(ref h, side[i]);
                    }
                }
                for (int i = 0; i < CubeSurface32.GridCells; i++) {
                    Mix(ref h, (uint)(ushort)shell.BottomHeight[i]);
                    Mix(ref h, shell.BottomContents[i]);
                }
            }
            return h.ToString("x16");
        }

        static void Mix(ref ulong h, uint value) {
            for (int i = 0; i < 4; i++) {
                h ^= (byte)(value >> (i * 8));
                h *= 1099511628211UL;
            }
        }

        public static string Persistence() {
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = PersistenceEnabled,
                ["path"] = PersistencePath,
                ["fileBytes"] = FileBytes,
                ["fileMiB"] = Math.Round(FileBytes / 1048576.0, 3),
                ["cubes"] = m_entries.Count,
                ["hash"] = Hash(),
                ["dirty"] = m_dirty,
                ["saveInProgress"] = SaveInProgress,
                ["saveQueueLeft"] = m_saveQueue?.Count ?? 0,
                ["saveTotal"] = m_saveTotal,
                ["savedRecordsTotal"] = SavedRecordsTotal,
                ["loadedRecordsTotal"] = LoadedRecordsTotal,
                ["fileRecords"] = m_fileRecords,
                ["appendedRecordsTotal"] = m_appendedRecords,
                ["appendedBytes"] = m_appendedBytes,
                ["appendedMiB"] = Math.Round(m_appendedBytes / 1048576.0, 3),
                ["compactRewrites"] = m_compactRewrites,
                ["tombstonesWritten"] = m_tombstonesWritten,
                ["tombstonesLoaded"] = TombstonesLoaded,
                ["dirtyCubes"] = m_dirtyCubes.Count,
                ["pendingTombstones"] = m_tombstones.Count,
                ["appendMode"] = m_appendMode,
                ["fileHeaderValid"] = m_fileHeaderValid,
                ["lastSaveMs"] = Math.Round(LastSaveMs, 2),
                ["lastLoadMs"] = Math.Round(LastLoadMs, 2),
                ["recordsPerTick"] = SaveRecordsPerTick,
                ["recordBytes"] = CubeSurface32.SerializedBytes,
                ["recordWithKeyBytes"] = RecordBytes,
                ["intervalSeconds"] = SaveIntervalSeconds,
                ["lastError"] = PersistenceError
            }.ToJsonString();
        }

        /// <summary>[v0.1.52] 逐条清单（最多 `limit` 条）：立方体坐标、距相机、本档体素边长、是否有网格、四边形数。</summary>
        public static string List(int limit) {
            Vector3 camera = SkylineLod.CameraViewPosition();
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            JsonArray items = [];
            int n = 0;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                if (n >= Math.Max(1, limit)) {
                    break;
                }
                float dist = CubeDistance(kv.Key.Cx, kv.Key.Cz, camera);
                items.Add(new JsonObject {
                    ["cube"] = new JsonArray(kv.Key.Cx, kv.Key.Cy, kv.Key.Cz),
                    ["distanceMetres"] = Math.Round(dist, 1),
                    ["stepMetres"] = StepForDistance(dist, viewRange),
                    ["meshStep"] = kv.Value.MeshStep,
                    ["hasMesh"] = kv.Value.Mesh != null,
                    ["quads"] = kv.Value.Mesh?.Quads ?? 0
                });
                n++;
            }
            return new JsonObject {
                ["ok"] = true,
                ["viewRange"] = viewRange,
                ["total"] = m_entries.Count,
                ["listed"] = n,
                ["items"] = items
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.52] **同一张壳在各档下的网格代价表**（判据）：取一个立方体，按 step=1/2/4/8/16/32 各建一次网格，
        /// 报四边形/顶点/字节/耗时。这是"精度 ↔ 代价"最直接的证据（不需要跑很远）。
        /// </summary>
        public static string MeshTiers(int cx, int cy, int cz) {
            JsonObject root = new();
            try {
                Terrain terrain = Terrain;
                if (terrain == null) {
                    root["ok"] = false;
                    root["err"] = "no terrain";
                    return root.ToJsonString();
                }
                CubeSurface32 shell = CubeSurface32.Extract(terrain, cx, cy, cz);
                root["ok"] = true;
                root["cube"] = new JsonArray(cx, cy, cz);
                root["shellBytes"] = ShellBytesPerCube;
                root["shellQuadCountNaive"] = shell.QuadCount;
                JsonArray rows = [];
                foreach (int step in new[] { 1, 2, 4, 8, 16, 32 }) {
                    Stopwatch watch = Stopwatch.StartNew();
                    CubeSurfaceMesh32 mesh = CubeSurfaceMesh32.Build([shell], 1, 1, true, false, step);
                    watch.Stop();
                    rows.Add(new JsonObject {
                        ["stepMetres"] = step,
                        ["voxelCubeSide"] = CubeSize / step,
                        ["gridSide"] = mesh.GridSide,
                        ["quads"] = mesh.Quads,
                        ["topQuads"] = mesh.TopQuads,
                        ["wallQuads"] = mesh.WallQuads,
                        ["coveredCells"] = mesh.CoveredCells,
                        ["nonEmptyCells"] = mesh.NonEmptyCells,
                        ["mergeViolations"] = mesh.MergeViolations,
                        ["buildMs"] = Math.Round(mesh.BuildMs, 3),
                        ["wallMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 3)
                    });
                }
                root["tiers"] = rows;
                root["note"] = "四边形/耗时随 step 增大按 ~1/step² 下降（聚合发生在建网格时，壳本身不变）";
            }
            catch (Exception e) {
                root["ok"] = false;
                root["err"] = e.Message;
            }
            return root.ToJsonString();
        }

        public static string Survey() {
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["renderEnabled"] = RenderEnabled,
                ["cubes"] = m_entries.Count,
                ["maxCubes"] = MaxCubes,
                ["queued"] = m_meshQueue.Count,
                ["shellBytes"] = ShellBytes,
                ["shellMiB"] = Math.Round(ShellBytes / 1048576.0, 2),
                ["meshVertexBytes"] = MeshVertexBytes,
                ["meshMiB"] = Math.Round(MeshVertexBytes / 1048576.0, 2),
                ["meshResident"] = MeshResident,
                ["meshRebuiltTotal"] = MeshRebuiltTotal,
                ["meshReleasedTotal"] = MeshReleasedTotal,
                ["stepHistogram"] = StepHistogram,
                // [v0.1.60] 表面体素壳（目标 1.6 → 1.3「体积感」）
                ["surfaceVoxelEnabled"] = SurfaceVoxelEnabled,
                ["surfaceVoxelMaxStep"] = SurfaceVoxelMaxStep,
                ["surfaceVoxelMaxCubes"] = SurfaceVoxelMaxCubes,
                ["surfaceVoxelRelMetres"] = SurfaceVoxelRelMetres,
                ["voxelShellCubes"] = VoxelShellCubes,
                ["voxelShellBytes"] = VoxelShellBytes,
                ["voxelShellMiB"] = Math.Round(VoxelShellBytes / 1048576.0, 3),
                ["voxelMeshResident"] = VoxelMeshResident,
                ["voxelMeshBytes"] = VoxelMeshBytes,
                ["voxelMeshMiB"] = Math.Round(VoxelMeshBytes / 1048576.0, 3),
                ["voxelHarvestedTotal"] = VoxelHarvestedTotal,
                ["voxelRefreshedTotal"] = VoxelRefreshedTotal,
                ["voxelMeshedTotal"] = VoxelMeshedTotal,
                ["voxelSkippedByCap"] = VoxelSkippedByCap,
                ["voxelPrewarmSkippedNoTerrain"] = VoxelPrewarmSkippedNoTerrain,
                ["voxelDegradedCubes"] = VoxelDegradedCubes,
                ["voxelDroppedVoxels"] = VoxelDroppedVoxels,
                ["drawnVoxelLastFrame"] = DrawnVoxelLastFrame,
                ["lastVoxelHarvestMs"] = Math.Round(LastVoxelHarvestMs, 3),
                ["lastPrewarmMs"] = Math.Round(LastPrewarmMs, 3),
                ["voxelPrewarmPerTick"] = VoxelPrewarmPerTick,
                // [v0.1.61] 带内主动补采（里程碑 1.2 无缝转换的数据侧）
                ["backfillEnabled"] = BackfillEnabled,
                ["backfillRelMetres"] = BackfillRelMetres,
                ["backfillInsideMetres"] = BackfillInsideMetres,
                ["backfillPerTick"] = BackfillPerTick,
                ["backfilledTotal"] = BackfilledTotal,
                ["backfillScanned"] = BackfillScanned,
                ["backfillMissingCubes"] = BackfillMissingCubes,
                ["backfillReadyCubes"] = BackfillReadyCubes,
                ["lastBackfillMs"] = Math.Round(LastBackfillMs, 3),
                ["meshedCubes"] = MeshedTotal,
                ["harvestedTotal"] = HarvestedTotal,
                ["evictedTotal"] = EvictedTotal,
                ["drawnLastFrame"] = DrawnLastFrame,
                ["skippedNotReady"] = SkippedNotReady,
                ["skippedDuplicate"] = SkippedDuplicate,
                ["skippedOverBudget"] = SkippedOverBudget,
                ["skippedBecauseLoaded"] = SkippedBecauseLoaded,
                ["skippedIsolated"] = SkippedIsolated,
                ["harvestedOnValid"] = HarvestedOnValid,
                ["pending"] = PendingCount,
                ["pendingDropped"] = PendingDropped,
                ["pendingSkipped"] = PendingSkipped,
                ["captureOnValid"] = CaptureOnValid,
                ["bandShellCubes"] = BandShellCubes,
                ["bandExpectedCubes"] = BandExpectedCubes,
                ["bandDrawableCubes"] = BandDrawableCubes,
                ["bandCompleteCubes"] = BandCompleteCubes,
                ["bandCoverage"] = Math.Round(BandCoverage, 3),
                ["bandCoverageMetres"] = new JsonArray(MathF.Max(0f, BandInset), TierMetres.Length > 0 ? TierMetres[0] : 48f),
                ["neighborRuleActive"] = NeighborRuleActive,
                ["requireNeighborsWhenCovered"] = RequireNeighborsWhenCovered,
                ["minBandCoverage"] = MinBandCoverage,
                ["lastHarvestMs"] = Math.Round(LastHarvestMs, 3),
                ["lastMeshMs"] = Math.Round(LastMeshMs, 3),
                ["bandMetres"] = BandMetres,
                ["tierMetres"] = new JsonArray([.. TierMetres]),
                ["slidingWindow"] = MeshSlidingWindow,
                ["meshReleaseFactor"] = MeshReleaseFactor,
                ["bandInset"] = BandInset,
                ["bandLift"] = BandLift,
                ["greedy"] = UseMergedMesh,
                ["restrictLod"] = RestrictLod,
                ["requireNeighbors"] = RequireNeighbors,
                ["budget"] = new JsonObject {
                    ["inlineHarvestPerCall"] = InlineHarvestPerCall,
                    ["meshBudgetMs"] = MeshBudgetMs,
                    ["maxDrawPerFrame"] = MaxDrawPerFrame
                },
                ["lastError"] = LastError
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.CubeShell*`。</summary>
    public static partial class SkylineRuntime {
        public static string CubeShellSurvey() => SkylineCubeShellStore.Survey();
        public static string CubeShellClear() {
            SkylineCubeShellStore.Clear();
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellEnabled(bool enabled) {
            SkylineCubeShellStore.Enabled = enabled;
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellRender(bool enabled) {
            SkylineCubeShellStore.RenderEnabled = enabled;
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellGreedy(bool greedy) {
            SkylineCubeShellStore.UseMergedMesh = greedy;
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>
        /// [v0.1.60] **表面体素壳开关**（目标 1.6 → 1.3）：开 = 最近档用"采所有裸露方块"的体素网格，
        /// 关 = 逐位回到 v0.1.59 的列顶高度场。
        /// </summary>
        public static string CubeShellSurfaceVoxel(bool enabled) {
            SkylineCubeShellStore.SurfaceVoxelEnabled = enabled;
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.60] 允许多大档位用体素网格（1 = 只最近档，内存最省；2 = 最近两档 …）。</summary>
        public static string CubeShellSurfaceVoxelStep(int maxStep) {
            SkylineCubeShellStore.SurfaceVoxelMaxStep = Math.Clamp(maxStep, 1, SkylineCubeShellStore.CubeSize);
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.60] 体素壳的**份数上限**与**采集半径**（内存/耗时两道门控）；`relMetres &lt; 0` = 不改半径。</summary>
        public static string CubeShellSurfaceVoxelCap(int maxCubes, float relMetres = -1f) {
            SkylineCubeShellStore.SurfaceVoxelMaxCubes = Math.Max(0, maxCubes);
            if (relMetres >= 0f) {
                SkylineCubeShellStore.SurfaceVoxelRelMetres = relMetres;
            }
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>
        /// [v0.1.60] **同步强制预热**一批体素壳（取证 / 手动刷新用），上限 128 个/次（避免一次卡太久）。
        /// `refresh=true` = **覆盖已有体素壳**（玩家建/挖之后刷新），默认 false = 只补还没有的。
        /// 返回体素壳统计 + `prewarmedNow`（本次成功几个）。
        /// </summary>
        public static string CubeShellSurfaceVoxelPrewarm(int maxCubes, bool refresh = false) {
            int done = SkylineCubeShellStore.PrewarmNow(Math.Clamp(maxCubes, 0, 128), refresh);
            JsonObject root = JsonNode.Parse(SkylineCubeShellStore.Survey()) as JsonObject ?? new JsonObject();
            root["prewarmedNow"] = done;
            return root.ToJsonString();
        }

        /// <summary>[v0.1.60] 预热节流参数：每 Tick 几个、每 Tick 几毫秒。</summary>
        public static string CubeShellSurfaceVoxelPrewarmRate(int perTick, float budgetMs = -1f) {
            SkylineCubeShellStore.VoxelPrewarmPerTick = Math.Max(0, perTick);
            if (budgetMs >= 0f) {
                SkylineCubeShellStore.VoxelPrewarmBudgetMs = budgetMs;
            }
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>
        /// [v0.1.61] **带内主动补采开关**（里程碑 1.2 无缝转换的数据侧）：开 = 地形还在时就把 32³ 壳采好，
        /// 于是地形释放的那一刻壳已经就位、边界不留洞；关 = 逐位回到"只在卸载那一刻采"（v0.1.60 行为）。
        /// </summary>
        public static string CubeShellBackfill(bool enabled) {
            SkylineCubeShellStore.BackfillEnabled = enabled;
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.61] 补采节流参数：每 Tick 几个、从"视距−带内缩"往外扫多宽（米）。`relMetres &lt; 0` = 不改宽度。</summary>
        public static string CubeShellBackfillRate(int perTick, float relMetres = -1f) {
            SkylineCubeShellStore.BackfillPerTick = Math.Max(0, perTick);
            if (relMetres >= 0f) {
                SkylineCubeShellStore.BackfillRelMetres = relMetres;
            }
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>壳接管后让现有 LOD 层让位（默认 true；false = 两层叠着画，用于 A/B 看穿插）。</summary>
        public static string CubeShellRestrictLod(bool restrict) {
            SkylineCubeShellStore.RestrictLod = restrict;
            SkylineLod.RequestRebuild();
            return SkylineCubeShellStore.Survey();
        }
        /// <summary>只画"四邻都在"的壳（默认 false；玩家走过一大片之后再打开，边界会干净很多）。</summary>
        public static string CubeShellRequireNeighbors(bool require) {
            SkylineCubeShellStore.RequireNeighbors = require;
            SkylineLod.RequestRebuild();
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.52] 判据：给一串"相对视距的距离（米）"，报各自落在**哪一档**（最小体素边长）。</summary>
        public static string CubeShellTierProbe(string relList) {
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            JsonArray items = [];
            foreach (string part in (relList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                if (float.TryParse(part.Trim(), out float rel)) {
                    float distance = viewRange + rel;
                    items.Add(new JsonObject {
                        ["relMetres"] = rel,
                        ["distanceMetres"] = distance,
                        ["stepMetres"] = SkylineCubeShellStore.StepForDistance(distance, viewRange),
                        ["voxelCube"] = new JsonObject {
                            ["side"] = CubeSurface32.Size / SkylineCubeShellStore.StepForDistance(distance, viewRange)
                        }
                    });
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["viewRange"] = viewRange,
                ["tiersRelToView"] = new JsonArray([.. SkylineCubeShellStore.TierMetres]),
                ["probes"] = items,
                ["note"] = "stepMetres = 最小体素 / 最小材质分片的边长；1=完整 32³、2=16³、4=8³、8=4³、16=2³、32=整块"
            }.ToJsonString();
        }

        /// <summary>[v0.1.52] 桥：逐条清单（见 `SkylineCubeShellStore.List`）。</summary>
        public static string CubeShellList(int limit) => SkylineCubeShellStore.List(limit);

        /// <summary>[v0.1.52] 桥：同一张壳在各档下的网格代价表。</summary>
        public static string CubeShellMeshTiers(int cx, int cy, int cz) =>
            SkylineCubeShellStore.MeshTiers(cx, cy, cz);

        /// <summary>[v0.1.60] 切换演示层"用表面体素壳"（有体积；代价更大）。切完需重新 `CubeSurfaceHarvest`。</summary>
        public static string CubeSurfaceVoxel(bool enabled) => SkylineCubeSurfaceDemo.SetVoxelMode(enabled);

        /// <summary>
        /// [v0.1.54] **按列探测**：这一列（立方体坐标 + 列内 lx/lz）在壳网格里被画成了什么？
        /// 报：壳里的值 / 方块 / 是否完整方块（`CubeBlock`）/ 列顶高度，以及
        /// **覆盖这一列的顶面四边形**与**贴着这一列的 4 个侧壁**（跨度 + 材质 + 贴图槽）。
        /// 用途：判断"门/栅栏/台阶这类非完整方块"是不是被画成了**材质占位方盒**（顶面 + 该材质侧壁）。
        /// </summary>
        public static string CubeShellColumn(int cx, int cy, int cz, int lx, int lz) {
            JsonObject root = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    root["ok"] = false;
                    root["err"] = "no terrain";
                    return root.ToJsonString();
                }
                CubeSurface32 shell = CubeSurface32.Extract(terrain, cx, cy, cz);
                int idx = (lx & 31) + (lz & 31) * CubeSurface32.Size;
                int shellValue = shell.TopContents[idx];
                int contents = shellValue & 0x3FF;
                Block block = BlocksManager.Blocks[contents];
                root["ok"] = true;
                root["cube"] = new JsonArray(cx, cy, cz);
                root["column"] = new JsonArray(lx, lz);
                root["contents"] = contents;
                root["block"] = block?.GetType().Name ?? "";
                root["isFullCube"] = block is CubeBlock;
                root["shellTopHeight"] = shell.TopHeight[idx];
                root["shellSurfaceY"] = shell.TopHeight[idx] + 1;
                root["shellLight"] = Terrain.ExtractLight(shellValue);
                // [v0.1.56] 家具塌缩：地形里那格真实方块 vs 壳里存了什么
                int terrainValue = terrain.GetCellValue(cx * CubeSurface32.Size + (lx & 31),
                                                        shell.TopHeight[idx],
                                                        cz * CubeSurface32.Size + (lz & 31));
                root["terrainContents"] = Terrain.ExtractContents(terrainValue);
                root["terrainBlock"] = BlocksManager.Blocks[Terrain.ExtractContents(terrainValue)]?.GetType().Name ?? "";
                root["terrainData"] = Terrain.ExtractData(terrainValue);
                root["furnitureCollapsed"] = Terrain.ExtractContents(terrainValue) == FurnitureBlock.Index
                    && contents != FurnitureBlock.Index;
                if (Terrain.ExtractContents(terrainValue) == FurnitureBlock.Index) {
                    int material = CubeSurface32.FurnitureMaterial(terrainValue);
                    root["furnitureMaterialContents"] = Terrain.ExtractContents(material);
                    root["furnitureMaterialBlock"] = material > 0
                        ? BlocksManager.Blocks[Terrain.ExtractContents(material)]?.GetType().Name ?? "" : "";
                }
                root["textureSlot"] = block == null ? -1
                    : (block is CubeBlock ? block.GetFaceTextureSlot(4, shellValue)
                                          : block.GetFaceTextureSlot(0, shellValue));
                root["materialSlot"] = block == null ? -1 : SkylineLod.MaterialTextureSlot(block, shellValue);

                CubeSurfaceMesh32 built = CubeSurfaceMesh32.Build([shell], 1, 1, false, false, 1, keepQuads: true);
                root["topQuads"] = built.TopQuadsJson(lx, lz);
                root["walls"] = built.WallsJson(lx, lz);
                root["boxShape"] = built.ColumnBoxShape(lx, lz);
                root["note"] = "boxShape = 该列在网格里的实际形状（顶面 y + 贴边侧壁跨度）；"
                    + "顶面 + 该列材质侧壁 = 材质占位方盒";
            }
            catch (Exception e) {
                root["ok"] = false;
                root["err"] = e.Message;
            }
            return root.ToJsonString();
        }

        /// <summary>[v0.1.53] 立刻把壳仓存下来（取证/收尾用；正常是"脏了 + 每 60 s"自动写）。</summary>
        public static string CubeShellSaveNow() {
            SkylineCubeShellStore.MarkDirty();
            SkylineCubeShellStore.SaveNow();
            return SkylineCubeShellStore.Persistence();
        }

        /// <summary>[v0.1.53] 手动读档（换世界会自动读；这里给取证用）。</summary>
        public static string CubeShellLoad() {
            SkylineCubeShellStore.Load();
            return SkylineCubeShellStore.Persistence();
        }

        /// <summary>[v0.1.53] 存档状态：路径 / 文件字节 / 立方体数 / **整仓摘要** / 上次存取耗时。</summary>
        public static string CubeShellPersistence() => SkylineCubeShellStore.Persistence();

        /// <summary>
        /// [v0.1.57] 取证用：从壳仓里**去掉**一个立方体（会写一条"墓碑"记录，读档时等于删除）。
        /// 正常路径上只有 LRU 淘汰会走到这里。
        /// </summary>
        public static string CubeShellForget(int cx, int cy, int cz) {
            bool removed = SkylineCubeShellStore.Forget((cx, cy, cz));
            return new System.Text.Json.Nodes.JsonObject {
                ["ok"] = removed,
                ["cube"] = new System.Text.Json.Nodes.JsonArray(cx, cy, cz),
                ["cubes"] = SkylineCubeShellStore.CubeCount
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.57] 取证用：把壳仓**淘汰到最多 N 个**（走 LRU 淘汰，每个淘汰都会写墓碑）。
        /// 用来验证"改动过半 → 压缩重写"这条分支。`n &lt; 0` 表示恢复默认上限。
        /// </summary>
        public static string CubeShellEvictTo(int n) {
            if (n >= 0) {
                SkylineCubeShellStore.MaxCubes = n;
                SkylineCubeShellStore.EvictNow();
            }
            else {
                SkylineCubeShellStore.MaxCubes = 4096;
            }
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.53] 整仓摘要（重启前后比对用）。</summary>
        public static string CubeShellHash() => SkylineCubeShellStore.Hash();

        /// <summary>[v0.1.55] 开关"区块 Valid 就采"（更宽的采集口径）。</summary>
        public static string CubeShellCaptureOnValid(bool enabled) {
            SkylineCubeShellStore.CaptureOnValid = enabled;
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>[v0.1.55] 手动把"覆盖度门控的四邻规则"参数调一下（取证/A-B 用）。</summary>
        public static string CubeShellNeighborRule(bool requireNeighbors, float minCoverage) {
            SkylineCubeShellStore.RequireNeighbors = requireNeighbors;
            SkylineCubeShellStore.MinBandCoverage = minCoverage;
            SkylineLod.RequestRebuild();
            return SkylineCubeShellStore.Survey();
        }

        /// <summary>
        /// [v0.1.56] 家具塌缩开关（默认**开**）：采集壳时把家具（`contents=227`，设计索引在 data 里）
        /// 塌缩成**设计的主材质**（`SkylineFurniture.DominantMaterial`），于是壳里存的是那个材质、
        /// LOD 里自然画成"该材质的占位方盒"；关掉则保留 227（用于 A/B）。
        /// **注意**：切换后需要重新采集才会生效（壳里已经存下的值不会变）。
        /// </summary>
        public static string CubeShellFurnitureCollapse(bool enabled) {
            SkylineRuntime.ShellFurnitureCollapse = enabled;
            return new System.Text.Json.Nodes.JsonObject {
                ["ok"] = true,
                ["furnitureCollapse"] = enabled,
                ["note"] = "改这个开关只影响**之后的采集**；已存下的壳要重新采才会变"
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.52] 设档位表（相对视距的米数，最多 5 段，逗号分隔）。
        /// 默认 `48,96,192,384,768` → step 1/2/4/8/16/32。传 `0,0,0,0,0` 可强制全部"整块"（取证用）。
        /// </summary>
        public static string CubeShellTiers(string list) {
            List<float> tiers = [];
            foreach (string part in (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) {
                if (float.TryParse(part.Trim(), out float v) && v >= 0f) {
                    tiers.Add(v);
                }
            }
            if (tiers.Count == 0) {
                tiers = [48f, 96f, 192f, 384f, 768f];
            }
            SkylineCubeShellStore.TierMetres = [.. tiers];
            return SkylineCubeShellStore.Survey();
        }
    }
}
