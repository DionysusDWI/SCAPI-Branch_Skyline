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
        public static long SkippedBecauseLoaded { get; private set; }
        public static long SkippedIsolated { get; private set; }
        public static double LastHarvestMs { get; private set; }
        public static double LastMeshMs { get; private set; }
        public static double LastPendingMs { get; private set; }
        public static string LastError { get; private set; } = "";

        static Terrain Terrain => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;

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
                m_entries[key] = new Entry { Shell = shell, LastUsed = Time.RealTime };
                HarvestedTotal++;
                HarvestedOnValid++;
                MarkDirty();
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
                        m_entries[key] = new Entry { Shell = shell, LastUsed = Time.RealTime };
                        HarvestedTotal++;
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

        static void EvictIfNeeded() {
            if (m_entries.Count <= MaxCubes) {
                return;
            }
            // 简单口径：淘汰"最久没被用到"的（LastUsed 最小）。数量不大时一次性排序足够。
            List<KeyValuePair<(int Cx, int Cy, int Cz), Entry>> list = [.. m_entries];
            list.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            int drop = m_entries.Count - MaxCubes;
            for (int i = 0; i < drop && i < list.Count; i++) {
                list[i].Value.Mesh?.Dispose();
                m_entries.Remove(list[i].Key);
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
            int s1 = 0, s2 = 0, s4 = 0, s8 = 0, s16 = 0, s32 = 0;
            bool budgetHit = false;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                Entry entry = kv.Value;
                float dist = CubeDistance(kv.Key.Cx, kv.Key.Cz, camera);
                if (MeshSlidingWindow) {
                    bool terrainLoaded = terrain != null
                        && (terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2, kv.Key.Cz * 2 + 1) != null
                            || terrain.GetChunkAtCoords(kv.Key.Cx * 2 + 1, kv.Key.Cz * 2 + 1) != null);
                    if (dist > releaseRange || terrainLoaded) {
                        if (entry.Mesh != null) {
                            entry.Mesh.Dispose();
                            entry.Mesh = null;
                            MeshReleasedTotal++;
                        }
                        continue;
                    }
                }
                int step = StepForDistance(dist, viewRange);
                if (!budgetHit && watch.Elapsed.TotalMilliseconds > MeshBudgetMs) {
                    budgetHit = true;                    // 本 Tick 预算用完：已有的继续统计，新的下帧再说
                }
                if (!budgetHit && (entry.Mesh == null || entry.MeshStep != step)) {
                    entry.Mesh?.Dispose();
                    CubeSurface32[] one = [entry.Shell];
                    entry.Mesh = CubeSurfaceMesh32.Build(one, 1, 1, UseMergedMesh, true, step);
                    entry.MeshStep = step;
                    MeshRebuiltTotal++;
                    MeshedTotal++;
                }
                if (entry.Mesh != null) {
                    MeshVertexBytes += entry.Mesh.VertexBytes;
                    MeshResident++;
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
                    if (entry.Mesh == null || entry.Mesh.VertexBuffer == null || entry.Mesh.IndexCount == 0) {
                        continue;
                    }
                    if (!NeighborhoodComplete(cx, kv.Key.Cy, cz)) {
                        SkippedIsolated++;
                        continue;
                    }
                    entry.LastUsed = Time.RealTime;
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                        entry.Mesh.VertexBuffer, entry.Mesh.IndexBuffer, 0, entry.Mesh.IndexCount);
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
                kv.Value.Mesh?.Dispose();
            }
            m_entries.Clear();
            m_meshQueue.Clear();
            m_queued.Clear();
            MeshVertexBytes = 0;
            MarkDirty();
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

        /// <summary>开始一次增量存档：只写头 + 排队，正文在 `SaveTick` 里按预算分批写。</summary>
        static void StartSave() {
            string path = EnsureWorld();
            if (path == null) {
                return;
            }
            m_saveQueue = [.. m_entries.Keys];
            m_saveTotal = m_saveQueue.Count;
            m_saveWritten = 0;
            m_saveTempPath = path + ".tmp";
            try {
                m_saveStream = Storage.OpenFile(m_saveTempPath, OpenFileMode.Create);
                m_saveWriter = new BinaryWriter(m_saveStream);
                m_saveWriter.Write(SaveMagic);
                m_saveWriter.Write(SaveVersion);
                m_saveWriter.Write(m_saveTotal);
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
                    if (!m_entries.TryGetValue(key, out Entry entry)) {
                        continue;                        // 期间被淘汰：不写这条（Load 侧按实际写入量容错）
                    }
                    m_saveWriter.Write(key.Cx);
                    m_saveWriter.Write(key.Cy);
                    m_saveWriter.Write(key.Cz);
                    entry.Shell.WriteTo(m_saveWriter);
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
                m_saveWriter.Dispose();
                m_saveStream.Dispose();
                m_saveStream = null;
                m_saveWriter = null;
                Storage.MoveFileSafely(m_saveTempPath, m_shellPath);   // 覆盖旧档（先写临时文件再换名）
                // 每条记录 = 12 B 坐标（3×int）+ 16,384 B 壳；整个文件 = 12 B 头 + N 条
                FileBytes = 12L + (long)m_saveWritten * RecordBytes;
                m_dirty = false;
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
                        return;
                    }
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
                        m_entries[(cx, cy, cz)] = new Entry { Shell = shell, LastUsed = 0 };
                        LoadedRecordsTotal++;
                    }
                }
                // 注意：`path` 是引擎虚拟路径（`app:/doc/...`），不能用 `FileInfo` 取长度
                // （实测报"文件名、目录名或卷标语法不正确"）→ 按本格式自己算：12 B 头 + N 条 × 16,396 B。
                FileBytes = 12L + LoadedRecordsTotal * RecordBytes;
                m_dirty = false;
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
