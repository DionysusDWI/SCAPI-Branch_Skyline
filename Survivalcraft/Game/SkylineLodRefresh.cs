using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.8：**LOD 单元失效 + 按需重采**（notes/75）。
    ///
    /// 现状问题（v0.1.0 ~ v0.1.6）：`SkylineLod.Harvest()` 里只要"粗层 + 4 个细子单元都在字典里"
    /// 就 `continue`（SkylineLod.cs:173-185），于是一个单元**采过一次就永远不再采**：
    ///   ① 在已加载区块里挖/放方块 → 玩家走远后，远景 LOD 还是旧地形；
    ///   ② 改完再让区块卸载/重载 → 同样旧数据（`Terrain.FreeChunk` 会 Dispose 区块对象、
    ///      重分配时是 `new TerrainChunk`，见 Terrain.cs:178-196）。
    ///
    /// 两条互补通道（都不新增每帧分配，都复用现成预算）：
    ///   * **推（低时延，主通道）**：`SubsystemTerrain.ChangeCell` 里加一行
    ///     `SkylineLod.NotifyCellChanged(x, z)`——把落点所在的 16 m 单元丢进脏集合，
    ///     `Harvest()` 用 `DirtyChunksPerTick`（**额外**预算，不挤占 `ChunksPerTick` 的轮转采集）
    ///     优先重采，并等 `DirtySettleSeconds` 让光照阶段把"顶面高度"沉降完再采；
    ///     采完还会在 `DirtyVerifySeconds` 后复核一次。编辑一定发生在已加载区块里，
    ///     所以正常情况下**一秒级**就重采完。
    ///   * **拉（兜底，零侵入）**：轮转采集时比对"采样戳"=（区块对象引用 + `ModificationCounter`
    ///     + 采样时刻）。区块被释放重分配 → 引用不同 → 重采；计数变了 → 重采；
    ///     超过 `RefreshSeconds` → 兜底重采。这样没走 `ChangeCell` 的写入路径
    ///     （例：命令方块 `place` 用 `SetCellValueFast`）最迟一个保鲜期也会被纠回来。
    ///
    /// 存档/重载：采样戳与脏集合都是**内存态**，`Reset()`/`Load()`（含切世界）会清空。
    /// 于是"改动后没来得及重采就退出"的情况，下次加载时因为**没有采样戳**会在第一次轮到时重采，
    /// 自动纠正 —— 即"会话内首次轮到必采"这条规则同时修掉了历史遗留的脏数据。
    ///
    /// 已知边界（写进 notes/75 §6）：①区块没加载时无法重采，脏项留在队列里等玩家靠近；
    /// ②`ModificationCounter` 会被 `TerrainSerializer23.SaveChunk` 清零（TerrainSerializer23.cs:593-598），
    /// 单独用它会漏判，所以采样戳还比对象引用与时间；③真正的"在无加载区改地形"不会发生——
    /// 引擎会静默丢写（见 AgentBridge 的 chunk residency 说明）。
    /// </summary>
    public static partial class SkylineLod {
        // ---------------- 开关（v0.1.8） ----------------

        /// <summary>
        /// 每 Tick 允许额外做几个脏重采（**不挤占** <see cref="ChunksPerTick"/> 的轮转预算）。
        /// 默认 8：单次重采只做 256 列的顶面查询 + 256 次取样，开销很小；调大能让"编辑后
        /// 几帧内"就刷新远景，调 0 = 关闭推通道（只留轮转 + 保鲜期兜底）。
        /// </summary>
        public static int DirtyChunksPerTick { get; set; } = 8;

        /// <summary>
        /// [v0.1.16] 脏重采的**上限自适应**：队列越长，每 Tick 处理越多（上限 = 本值），
        /// 避免"内容大批变更（世界加载/大面积编辑）"时队列积压（实测：一次把内容距离调到 256 m
        /// 后 `dirty=510/q=510` 长期挂着，LOD 刷新时延被拖到 0.5 s 级）。
        /// 单次重采只做 256 列顶面查询 + 256 次取样，代价很小，所以上限可以给到 64。
        /// </summary>
        public static int DirtyChunksMaxPerTick { get; set; } = 64;

        /// <summary>
        /// 编辑后的**沉降期**（秒）：LOD 的采样数据源是 `TerrainChunk.GetTopHeightFast`（区块的
        /// "顶面高度"字段），它由地形更新器的光照阶段重算——而光照阶段是**排队**跑的（实测编辑后
        /// 约 0.3 s 到位）。不等沉降就重采会采到旧顶面，而且采样戳随后会被当成"刚采过"，
        /// 一直错到保鲜期（2026-09-27 12:48 实测：顶面 0.3 s 更新、LOD 9 s+ 仍是旧值）。
        /// v0.1.9 起 `SubsystemTerrain.ChangeCell` **就地维护顶面高度**（不等光照阶段），
        /// 所以这里只需一个很短的安全间隔（默认 0.15 s）。
        /// </summary>
        public static float DirtySettleSeconds { get; set; } = 0.15f;

        /// <summary>
        /// 脏重采后的**复核期**（秒）：脏重采把采样戳的"保鲜起点"往后挪，使该单元在
        /// 约这么长时间后被兜底通道再采一次 —— 万一第一次仍撞上未沉降完的光照阶段，
        /// 这次复核会把它纠正回来。设 0 = 关闭复核。
        /// </summary>
        public static float DirtyVerifySeconds { get; set; } = 2.0f;

        /// <summary>脏集合上限；超过就放弃精确集合，改走 <see cref="OverflowSweepSeconds"/> 的全量校验慢通道。</summary>
        public static int MaxDirtyCells { get; set; } = 8192;

        /// <summary>采样戳的兜底保鲜期（秒）；0 = 关闭兜底（只靠推通道 + 引用/计数比对）。</summary>
        public static float RefreshSeconds { get; set; } = 300f;

        /// <summary>脏集合溢出后"全量校验"的持续时长（秒）。</summary>
        public static float OverflowSweepSeconds { get; set; } = 90f;

        /// <summary>编辑触发的重采，多久之内允许做下一次网格重建（仍受 MeshRebuildSeconds 节流）。</summary>
        public static float DirtyRebuildSeconds { get; set; } = 0.5f;

        /// <summary>空单元（整格没有任何地形）是否删除既有采样。默认关 = 保持 v0.1.0~v0.1.6 行为。</summary>
        public static bool RemoveEmptiedCells { get; set; }

        /// <summary>这次为什么重采（诊断用；None 表示不需要重采）。</summary>
        internal enum ResampleReason {
            None = 0,
            Dirty,           // 推通道：ChangeCell 标脏
            FirstSeen,       // 本会话第一次采到这个单元
            NewChunk,        // 区块被释放过：这个对象我还没采过
            CounterChanged,  // ModificationCounter 变了
            Refresh,         // 超过 RefreshSeconds 的兜底
            Sweep,           // 脏集合溢出后的全量校验
            Unloading        // [v0.1.47] 区块即将离开加载范围（卸载前最后一采）
        }

        struct SampleStamp {
            public object Chunk;    // 采样时的 TerrainChunk 实例（重分配后必然是别的对象）
            public int Counter;     // 采样时的 ModificationCounter
            public double Time;     // 采样时刻（Time.RealTime）
        }

        static readonly Dictionary<long, SampleStamp> m_stamps = [];
        // [v0.1.36] 这三份脏集合会被**两个线程**碰：
        //   * 写入侧（`SubsystemTerrain.ChangeCell` → NotifyCellChanged）与区块转 Valid 的回调
        //     跑在 **TerrainUpdater 的更新线程**上（`TerrainUpdater.UpdateChunkSingleStep`）；
        //   * 消费侧（SkylineLod.Tick → TryTakeDirtyChunk / RecordSample）跑在主线程。
        // 2026-09-27 实测日志：`SkylineLod.MarkDirty` 抛
        //   InvalidOperationException: Operations that change non-concurrent collections must have
        //   exclusive access …（`Dictionary` 内部状态被打散，随后 SkylineLod.Tick 也连续告警）。
        // 修法：换成并发集合（`ConcurrentDictionary` 当 set 用），语义不变、无锁竞争窗口。
        static readonly ConcurrentDictionary<long, byte> m_dirtyCells = [];         // set: key → 0
        static readonly ConcurrentQueue<long> m_dirtyQueue = [];
        static readonly ConcurrentDictionary<long, double> m_dirtyTime = [];        // v0.1.8：标脏时刻（沉降期判定用）
        static double m_sweepUntil;
        static double m_lastEditTime;
        static float m_lastDirtyLatencyMs;
        static int m_resampledTotal;
        static int m_resampledDirty;
        static int m_resampledStamp;
        static int m_resampledSweep;
        // v0.1.16：脏通道诊断计数
        static long m_dirtyScans;
        static long m_dirtyTaken;
        static long m_dirtySkippedYoung;
        static long m_dirtySkippedUnloaded;

        // ---------------- 只读状态 ----------------

        public static int DirtyCells => m_dirtyCells.Count;
        public static int PendingResamples => m_dirtyQueue.Count;
        public static int ResampledTotal => m_resampledTotal;
        public static int ResampledDirty => m_resampledDirty;

        /// <summary>[v0.1.16] 本 Tick 实际允许的脏重采数（= 队列长度 / 16，夹在 [DirtyChunksPerTick, 上限]）。</summary>
        internal static int EffectiveDirtyBudget {
            get {
                int floor = Math.Max(DirtyChunksPerTick, 0);
                int max = Math.Max(DirtyChunksMaxPerTick, floor);
                int adaptive = m_dirtyQueue.Count / 16;
                return Math.Clamp(Math.Max(floor, adaptive), floor, max);
            }
        }

        /// <summary>Reset()/Load()（含切世界）时清空刷新状态：与 m_cells / m_cellsFine 同生命周期。</summary>
        static void ResetRefreshState() {
            m_stamps.Clear();
            m_dirtyCells.Clear();
            m_dirtyQueue.Clear();
            m_dirtyTime.Clear();
            m_sweepUntil = 0.0;
            m_lastEditTime = 0.0;
            m_lastDirtyLatencyMs = 0f;
        }

        // ---------------- 推通道 ----------------

        /// <summary>写入侧入口（<c>SubsystemTerrain.ChangeCell</c> 调用）：把一格折算成 16 m 单元并标脏。</summary>
        public static void NotifyCellChanged(int x, int z) {
            if (!Enabled) {
                return;
            }
            MarkDirty(Key(x >> CellShift, z >> CellShift));
        }

        /// <summary>
        /// [v0.1.17] 区块**刚达到 Valid** 时由 `TerrainUpdater` 调用：立刻把这个单元标脏，
        /// 让"刚加载出来/玩家刚走过的地形"在下一个 Tick 就进 LOD（而不是等轮转游标转过来，
        /// 800+ 列时要十几秒）。可用 `SkylineRuntime.LodBackfillOnValid` 关闭做 A/B。
        /// </summary>
        public static void NotifyChunkValid(TerrainChunk chunk) {
            if (!Enabled || chunk == null || !SkylineRuntime.BackfillOnValid) {
                return;
            }
            MarkDirty(Key(chunk.Origin.X >> CellShift, chunk.Origin.Y >> CellShift));
            m_backfilled++;
        }

        static long m_backfilled;

        /// <summary>[v0.1.17] 因"区块刚 Valid"而标脏的次数（诊断）。</summary>
        public static long BackfilledOnValid => m_backfilled;

        /// <summary>把一个 LOD 单元标脏（幂等；只用现成集合，不分配新对象）。</summary>
        public static void MarkDirty(long key) {
            m_lastEditTime = Time.RealTime;
            m_dirtyTime[key] = Time.RealTime;               // 沉降期起算点（重复标脏会刷新它）
            if (!m_dirtyCells.TryAdd(key, 0)) {
                return;                                     // 已经在待办里
            }
            if (m_dirtyCells.Count > MaxDirtyCells) {
                // 溢出保护：放弃精确集合，改走"全量校验"慢通道——期间轮到的已加载区块一律重采。
                m_dirtyCells.Clear();
                m_dirtyQueue.Clear();
                m_dirtyTime.Clear();
                m_sweepUntil = Time.RealTime + MathF.Max(OverflowSweepSeconds, 1f);
                Log.Warning($"SkylineLod: dirty cells overflow (> {MaxDirtyCells}), full sweep for {OverflowSweepSeconds:0}s");
                return;
            }
            m_dirtyQueue.Enqueue(key);
        }

        /// <summary>从脏队列里取一个"已加载且合法"的区块；没加载的重新排队（玩家靠近时自然会被采）。</summary>
        static bool TryTakeDirtyChunk(Terrain terrain, out TerrainChunk chunk, out ResampleReason reason) {
            chunk = null;
            reason = ResampleReason.Dirty;
            double now = Time.RealTime;
            int count = m_dirtyQueue.Count;
            for (int i = 0; i < count; i++) {
                if (!m_dirtyQueue.TryPeek(out long key)) {
                    break;
                }
                if (!m_dirtyCells.ContainsKey(key)) {
                    m_dirtyQueue.TryDequeue(out _);
                    m_dirtyTime.TryRemove(key, out _);
                    continue;                               // 队列里有重复项 / 已经重采过了
                }
                // v0.1.8/0.1.16：等沉降期再采。**注意**：标脏会刷新时间戳（重复编辑），所以队首可能
                // 一直是"刚标脏"的项 —— 早期实现在这里 `return false`（认为"队首没到点，整队都没到点"），
                // 结果被反复编辑的队首把整条脏队列**锁死**（实测 `dirty=510/q=510` 60 s 不降，
                // 只有 0.33 次/秒的重采）。现在改成"跳过该项、放到队尾，继续看后面的项"。
                if (DirtySettleSeconds > 0f
                    && m_dirtyTime.TryGetValue(key, out double dirtyAt)
                    && now - dirtyAt < DirtySettleSeconds) {
                    m_dirtyQueue.TryDequeue(out _);
                    m_dirtyQueue.Enqueue(key);
                    m_dirtySkippedYoung++;
                    continue;
                }
                m_dirtyScans++;
                TerrainChunk candidate = terrain.GetChunkAtCoords((int)(key >> 32), (int)(key & 0xFFFFFFFF));
                if (candidate == null || candidate.ThreadState < TerrainChunkState.Valid) {
                    m_dirtyQueue.TryDequeue(out _);
                    m_dirtyQueue.Enqueue(key);              // 还没加载 → 留到以后
                    m_dirtySkippedUnloaded++;
                    continue;
                }
                m_dirtyQueue.TryDequeue(out _);
                m_dirtyTaken++;
                chunk = candidate;
                return true;
            }
            return false;
        }

        // ---------------- 拉通道（采样戳） ----------------

        /// <summary>轮转采集这次轮到该区块时，判断要不要重采。</summary>
        internal static ResampleReason NeedsResample(TerrainChunk chunk) {
            int cx = chunk.Origin.X >> CellShift;
            int cz = chunk.Origin.Y >> CellShift;
            long key = Key(cx, cz);
            if (m_dirtyCells.ContainsKey(key)) {
                return ResampleReason.Dirty;
            }
            if (Time.RealTime < m_sweepUntil) {
                return ResampleReason.Sweep;
            }
            if (!m_stamps.TryGetValue(key, out SampleStamp stamp)) {
                return ResampleReason.FirstSeen;            // 本会话没采过（v0.1.0~v0.1.6 的历史数据也走这里纠正）
            }
            if (!ReferenceEquals(stamp.Chunk, chunk)) {
                return ResampleReason.NewChunk;             // 区块被 FreeChunk → AllocateChunk 重建过
            }
            if (stamp.Counter != chunk.ModificationCounter) {
                return ResampleReason.CounterChanged;       // 内容被改过（含存档把计数清零）
            }
            if (RefreshSeconds > 0f && Time.RealTime - stamp.Time > RefreshSeconds) {
                return ResampleReason.Refresh;              // 兜底：慢速全量保鲜
            }
            return ResampleReason.None;
        }

        /// <summary>采样完成后登记采样戳、清脏标记、记统计。</summary>
        internal static void RecordSample(TerrainChunk chunk, ResampleReason reason) {
            int cx = chunk.Origin.X >> CellShift;
            int cz = chunk.Origin.Y >> CellShift;
            long key = Key(cx, cz);
            m_stamps[key] = new SampleStamp {
                Chunk = chunk,
                Counter = chunk.ModificationCounter,
                // v0.1.8：脏重采把"保鲜起点"往后挪，使该单元在 DirtyVerifySeconds 后被兜底通道复核一次
                // （万一第一次仍撞上未沉降完的光照阶段，复核负责纠正）。
                Time = reason == ResampleReason.Dirty && DirtyVerifySeconds > 0f && DirtyVerifySeconds < RefreshSeconds
                    ? Time.RealTime - RefreshSeconds + DirtyVerifySeconds
                    : Time.RealTime
            };
            if (m_dirtyCells.TryRemove(key, out _)) {
                m_dirtyTime.TryRemove(key, out _);
                m_resampledDirty++;
                m_lastDirtyLatencyMs = (float)((Time.RealTime - m_lastEditTime) * 1000.0);
                // 编辑触发的重采：把下一次网格重建提前（DirtyRebuildSeconds 后），别等满 MeshRebuildSeconds。
                double soon = Time.RealTime + MathF.Max(DirtyRebuildSeconds, 0f);
                if (m_nextRebuild > soon) {
                    m_nextRebuild = soon;
                }
            }
            switch (reason) {
                case ResampleReason.Sweep:
                    m_resampledSweep++;
                    break;
                case ResampleReason.Dirty:
                    break;                                  // 已在上面计数
                default:
                    m_resampledStamp++;
                    break;
            }
            m_resampledTotal++;
        }

        // ---------------- 诊断 ----------------

        /// <summary>读某单元当前的 LOD 采样 + 采样戳（只读；验证脚本用，桥可直接调
        /// `{"op":"invoke","target":"type:Game.SkylineLod","member":"LodCellAt","action":"call","args":[cx,cz]}`）。</summary>
        public static string LodCellAt(int cx, int cz) {
            long key = Key(cx, cz);
            JsonObject result = new() {
                ["ok"] = true,
                ["cx"] = cx,
                ["cz"] = cz,
                ["dirty"] = m_dirtyCells.ContainsKey(key)
            };
            if (m_cells.TryGetValue(key, out Cell coarse)) {
                result["coarseHeight"] = coarse.Height;
                result["coarseValue"] = coarse.Value;
                result["coarseContents"] = Terrain.ExtractContents(coarse.Value);
            }
            JsonArray fine = new JsonArray();
            for (int k = 0; k < 4; k++) {
                long fkey = Key(cx * 2 + (k & 1), cz * 2 + (k >> 1));
                fine.Add(m_cellsFine.TryGetValue(fkey, out Cell cell) ? cell.Height : (short)-32768);
            }
            result["fineHeights"] = fine;
            if (m_stamps.TryGetValue(key, out SampleStamp stamp)) {
                result["stampCounter"] = stamp.Counter;
                result["stampAgeSeconds"] = Math.Round(Math.Max(0.0, Time.RealTime - stamp.Time), 2);
            }
            result["refresh"] = RefreshSurveyJson();
            return result.ToJsonString();
        }

        static string RefreshDescribe() =>
            $"dirty={m_dirtyCells.Count}/q={m_dirtyQueue.Count} resample={m_resampledTotal}"
            + $"(dirty={m_resampledDirty} stamp={m_resampledStamp} sweep={m_resampledSweep})"
            + $" latencyMs={m_lastDirtyLatencyMs:0} sweepLeft={Math.Max(0.0, m_sweepUntil - Time.RealTime):0}s";

        static JsonObject RefreshSurveyJson() => new() {
            ["dirtyCells"] = m_dirtyCells.Count,
            ["pendingResamples"] = m_dirtyQueue.Count,
            ["resampledTotal"] = m_resampledTotal,
            ["resampledDirty"] = m_resampledDirty,
            ["resampledStamp"] = m_resampledStamp,
            ["resampledSweep"] = m_resampledSweep,
            ["lastDirtyLatencyMs"] = Math.Round(m_lastDirtyLatencyMs, 1),
            ["stamps"] = m_stamps.Count,
            ["dirtySettleSeconds"] = Math.Round(DirtySettleSeconds, 2),
            ["dirtyVerifySeconds"] = Math.Round(DirtyVerifySeconds, 2),
            ["dirtyChunksMaxPerTick"] = DirtyChunksMaxPerTick,
            ["effectiveDirtyBudget"] = EffectiveDirtyBudget,
            ["dirtyScans"] = m_dirtyScans,
            ["dirtyTaken"] = m_dirtyTaken,
            ["dirtySkippedYoung"] = m_dirtySkippedYoung,
            ["dirtySkippedUnloaded"] = m_dirtySkippedUnloaded,
            ["backfilledOnValid"] = m_backfilled,
            ["refreshSeconds"] = RefreshSeconds,
            ["dirtyChunksPerTick"] = DirtyChunksPerTick,
            ["sweepSecondsLeft"] = Math.Round(Math.Max(0.0, m_sweepUntil - Time.RealTime), 1)
        };
    }
}
