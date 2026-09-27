using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.24：**大批写入期的光照去抖**（`notes/93 §4` 的第 ② 条候选优化）。
    ///
    /// 问题（`notes/93` 实测）：受控 4096 格批量改造里，**光照链路 0.557 s** 是最大单项
    /// （Light 0.317 / LightSources 0.198 / Propagate 0.042）。原因是**每次 `ChangeCell` 都会
    /// 把邻域降级到 InvalidLight**，于是同一批里的 4096 次写入会让同一批区块被反复降级/重算 ——
    /// 绝大部分是重复劳动。
    ///
    /// 做法：只有在"**明显是批量操作**"时才去抖（阈值默认 64 次 / 50 ms 窗口）——
    ///   * 普通玩家放/挖一两格（低于阈值）→ 行为与原来完全一致（立即降级）；
    ///   * 批量写入期间 → 把受影响的区块收进集合，**等写入停下来 150 ms** 再统一降级一次。
    /// 代价是批量建造期间光照/几何短暂滞后（几十毫秒级），换取光照重算次数成倍下降。
    ///
    /// 开关：`skyline.DeferLightOnBulkEdits`（默认 **true**）、`BulkEditThreshold`、`BulkEditWindowMs`、
    /// `BulkEditSettleMs`；诊断：`skyline.BulkEditDescribe()`。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>批量写入期是否把光照降级去抖（默认开；低于阈值时行为与原版一致）。</summary>
        public static bool DeferLightOnBulkEdits { get; set; } = true;

        /// <summary>判定"这是批量写入"的阈值：窗口期内达到这么多次写入才开始去抖。</summary>
        public static int BulkEditThreshold { get; set; } = 64;

        /// <summary>判定窗口（毫秒）。</summary>
        public static int BulkEditWindowMs { get; set; } = 50;

        /// <summary>写入停止多久之后统一降级（毫秒）。</summary>
        public static int BulkEditSettleMs { get; set; } = 150;

        static double m_windowStart;
        static int m_windowCount;
        static double m_lastEditTime;
        static bool m_deferring;
        static readonly HashSet<long> m_pendingChunks = [];
        static long m_deferredEdits;
        static long m_deferredChunks;
        static long m_appliedBatches;

        /// <summary>诊断计数。</summary>
        public static string BulkEditDescribe() =>
            $"bulkEdit defer={DeferLightOnBulkEdits} threshold={BulkEditThreshold}/{BulkEditWindowMs}ms "
            + $"settle={BulkEditSettleMs}ms deferring={m_deferring} pending={m_pendingChunks.Count} "
            + $"deferredEdits={m_deferredEdits} deferredChunks={m_deferredChunks} appliedBatches={m_appliedBatches}";

        /// <summary>
        /// 写入侧调用：返回 true 表示"这次光照降级被推迟了"（调用方应跳过 `DowngradeChunkNeighborhoodState`）。
        /// </summary>
        public static bool ShouldDeferLight(Point2 coords) {
            if (!DeferLightOnBulkEdits) {
                return false;
            }
            double now = Time.RealTime;
            double window = Math.Max(BulkEditWindowMs, 1) / 1000.0;
            if (now - m_windowStart > window) {
                m_windowStart = now;
                m_windowCount = 0;
            }
            m_windowCount++;
            m_lastEditTime = now;
            if (!m_deferring && m_windowCount < Math.Max(BulkEditThreshold, 1)) {
                return false;                                  // 普通编辑：立即降级，行为不变
            }
            m_deferring = true;
            m_deferredEdits++;
            long key = ((long)coords.X << 32) | (uint)coords.Y;
            if (m_pendingChunks.Add(key)) {
                m_deferredChunks++;
            }
            return true;
        }

        /// <summary>每帧由 `SkylineRuntime.Tick()` 调用：写入停下来之后统一降级一次。</summary>
        static void TickDeferredLight() {
            if (!m_deferring || m_pendingChunks.Count == 0) {
                return;
            }
            double settle = Math.Max(BulkEditSettleMs, 10) / 1000.0;
            if (Time.RealTime - m_lastEditTime < settle) {
                return;                                        // 还在写，继续攒
            }
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return;
            }
            foreach (long key in m_pendingChunks) {
                updater.DowngradeChunkNeighborhoodState(
                    new Point2((int)(key >> 32), (int)(key & 0xFFFFFFFF)),
                    1, TerrainChunkState.InvalidLight, false);
            }
            m_appliedBatches++;
            m_pendingChunks.Clear();
            m_deferring = false;
            m_windowCount = 0;
        }
    }
}
