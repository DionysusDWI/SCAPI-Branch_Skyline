using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// [v0.1.137] 里程碑 5.2 第一段：**Chunky 式区块预加载**（先移植，后适配）。
    ///
    /// 为什么要它（目标 5.2 原文）："为了提升区块加载性能，需要学习并迁移 Chunky 和 C2ME 这两个模组的源码，
    /// 前者可以实现区块预加载，后者可以使用多线程加速加载速度，这些都是为了日后引入 terrain-diffusion 的
    /// 巨型地形块做的先行工作。"
    ///
    /// **移植思路（本项目怎么落到 SCAPI 上）**：Chunky 在 MC 里做的是"给一个区域，按螺旋顺序把区块推进加载队列，
    /// 分帧推进、可取消、给进度/ETA"。本引擎里"要不要加载某区块"完全由
    /// `TerrainUpdater.SetUpdateLocation(locationIndex, center, visibility, content)` 决定 ——
    /// 所以最小且正确的移植是**加一个"虚拟加载点"**：把目标区域当成一个临时的玩家位置交给更新器，
    /// 由引擎自己按既有线程/状态机把区块加载到 Valid；我们只负责**排队、报进度、可取消、如实分账**。
    ///
    /// 与 `SkylineLodManualBuild` 的分工：那个是"手动把 LOD 壳补满"，这个是"把**真地形**预加载进来"。
    /// </summary>
    public static class SkylineChunkPreloader {
        /// <summary>使用的虚拟更新位置编号（远离玩家索引 0..N；`RemoveUpdateLocation` 时按它摘掉）。</summary>
        public const int VirtualLocationIndex = 9001;

        public static bool Running { get; private set; }
        /// <summary>
        /// [v0.1.137 修正] **已完成但尚未释放**：全部区块到 Valid 后**不自动摘点**
        /// （Chunky 同样把预加载的区块留着），由调用方 `Release()`（或 `Cancel()`）决定何时释放。
        /// 为什么必须这样：第一版完成即 `RemoveUpdateLocation` ⇒ 引擎立刻把 1600 m 外的区块按"超距"卸掉，
        /// 于是"预加载完再检查目标列有没有地形"永远查不到（实测 `target-has-terrain` 假 FAIL）。
        /// </summary>
        public static bool Completed { get; private set; }
        public static int CenterChunkX { get; private set; }
        public static int CenterChunkZ { get; private set; }
        public static int RadiusChunks { get; private set; }
        public static int TotalChunks { get; private set; }

        static long m_startTicks;
        static long m_lastValid;
        static double m_lastProgressTime;
        static double m_lastEta;
        static double m_frames;
        static double m_maxFrameMs;
        static double m_frameMsSum;
        static string m_lastError = "";

        public static long InvalidChunkStates { get; private set; }
        public static long StartedTotal { get; private set; }
        public static long CompletedTotal { get; private set; }
        public static long CancelledTotal { get; private set; }
        public static double LastDurationSeconds { get; private set; }

        /// <summary>
        /// 开始预加载：以 (centerX,centerZ) 的世界坐标为中心、`radiusChunks` 个区块为半径。
        /// `visibilityBlocks` 交给更新器的可见距离（默认按半径算：radius×16）。
        /// </summary>
        public static string Start(float centerX, float centerZ, int radiusChunks, float visibilityBlocks = -1f) {
            if (Running) {
                return new JsonObject { ["ok"] = false, ["err"] = "preload already running", ["status"] = StatusObject() }
                    .ToJsonString();
            }
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "no TerrainUpdater" }.ToJsonString();
            }
            RadiusChunks = Math.Clamp(radiusChunks, 1, 24);          // 24 区块 = 384 m 半径；再大交给多次调用
            CenterChunkX = (int)MathF.Floor(centerX / TerrainChunk.Size);
            CenterChunkZ = (int)MathF.Floor(centerZ / TerrainChunk.Size);
            TotalChunks = (2 * RadiusChunks + 1) * (2 * RadiusChunks + 1);
            // [v0.1.137 修正] 引擎的 `IsChunkInRange` 是**圆形**判据（`dist² ≤ ContentDistance²`），
            // 而我们的目标区域是 (2r+1)² 的**方形**网格 ⇒ 若只传 `r×16`，四角（r×16×√2）会被排除，
            // 于是 Valid 数永远停在"圆内格点数"（实测 r=6 时正好 113/169 = 66.9%）。
            // 所以默认可见距离取**外接圆半径 + 一格余量**：`(r + 1) × 16 × √2`。
            float corners = (RadiusChunks + 1) * TerrainChunk.Size * MathF.Sqrt(2f);
            float visibility = visibilityBlocks > 0f ? MathF.Max(visibilityBlocks, corners) : corners;
            try {
                // [v0.1.137 修正] **用 2D 重载**：带 y 的那个重载会把 `SphereWindow` 置真（受
                // `SkylineRuntime.SphereLoadingEnabled` 影响），而虚拟预加载点的 y 没有意义 ⇒
                // 球窗的竖直判据会把大部分目标区块挡在"内容范围"外（实测只有 15/81 到 Valid）。
                // 2D 重载显式 `SphereWindow=false`，与 `PlayerData` 给待出生点下发的方式一致。
                updater.SetUpdateLocation(VirtualLocationIndex,
                    new Vector2(centerX, centerZ), visibility, visibility);
            }
            catch (Exception e) {
                m_lastError = $"SetUpdateLocation: {e.Message}";
                return new JsonObject { ["ok"] = false, ["err"] = m_lastError }.ToJsonString();
            }
            Running = true;
            Completed = false;
            StartedTotal++;
            m_startTicks = Stopwatch.GetTimestamp();
            m_lastProgressTime = Time.RealTime;
            m_lastValid = -1;
            m_lastEta = -1;
            m_frames = 0;
            m_maxFrameMs = 0;
            m_frameMsSum = 0;
            Log.Information($"SkylineChunkPreloader: start center=({CenterChunkX},{CenterChunkZ}) r={RadiusChunks} "
                + $"total={TotalChunks} visibility={visibility:0}m");
            return new JsonObject {
                ["ok"] = true, ["running"] = true, ["centerChunk"] = new JsonArray(CenterChunkX, CenterChunkZ),
                ["radiusChunks"] = RadiusChunks, ["totalChunks"] = TotalChunks,
                ["visibilityBlocks"] = Math.Round(visibility, 1),
            }.ToJsonString();
        }

        /// <summary>取消：摘掉虚拟加载点（引擎会按自己的规则把超距区块释放）。</summary>
        public static string Cancel() {
            if (!Running) {
                return new JsonObject { ["ok"] = true, ["running"] = false, ["note"] = "not running" }.ToJsonString();
            }
            Detach();
            CancelledTotal++;
            Log.Information($"SkylineChunkPreloader: cancelled after {ElapsedSeconds():0.0}s "
                + $"({ValidChunks()}/{TotalChunks} valid)");
            return StatusObject().ToJsonString();
        }

        /// <summary>[v0.1.137] 释放（预加载已完成时用）：与 `Cancel()` 同样摘点，但单独计账。</summary>
        public static string Release() {
            if (!Running) {
                return new JsonObject { ["ok"] = true, ["running"] = false, ["note"] = "not running" }.ToJsonString();
            }
            bool wasCompleted = Completed;
            Detach();
            if (wasCompleted) {
                ReleasedTotal++;
            }
            else {
                CancelledTotal++;                 // 没完成就释放 = 取消
            }
            return StatusObject().ToJsonString();
        }

        public static long ReleasedTotal { get; private set; }

        static void Detach() {
            try {
                GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater
                    ?.RemoveUpdateLocation(VirtualLocationIndex);
            }
            catch (Exception e) {
                m_lastError = $"RemoveUpdateLocation: {e.Message}";
            }
            Running = false;
            // 注意：`Completed` 保留（供调用方在 Release 后读取"这次到底完成了没有"）
        }

        public static double ElapsedSeconds() =>
            Stopwatch.GetElapsedTime(m_startTicks).TotalSeconds;

        /// <summary>目标区域里已经到 `Valid` 的区块数。</summary>
        public static int ValidChunks() {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            if (terrain == null) {
                return 0;
            }
            int valid = 0;
            for (int cx = CenterChunkX - RadiusChunks; cx <= CenterChunkX + RadiusChunks; cx++) {
                for (int cz = CenterChunkZ - RadiusChunks; cz <= CenterChunkZ + RadiusChunks; cz++) {
                    TerrainChunk chunk = terrain.GetChunkAtCoords(cx, cz);
                    if (chunk != null && chunk.ThreadState >= TerrainChunkState.Valid) {
                        valid++;
                    }
                }
            }
            return valid;
        }

        /// <summary>每帧调用（由 `SkylineLod.Tick` 转发）：只记账 + 判定完成，不做加载本身。</summary>
        public static void Tick() {
            if (!Running) {
                return;
            }
            try {
                double frameMs = Program.LastFrameTime * 1000.0;
                if (frameMs > 0 && frameMs < 1000) {
                    m_frames++;
                    m_frameMsSum += frameMs;
                    if (frameMs > m_maxFrameMs) {
                        m_maxFrameMs = frameMs;
                    }
                }
                long valid = ValidChunks();
                double now = Time.RealTime;
                if (now - m_lastProgressTime >= 0.5) {
                    if (valid != m_lastValid && valid > 0) {
                        // ETA 用"最近 0.5 s 的增量速度"估计，避免早期慢启动把 ETA 拉爆
                        double rate = valid / Math.Max(ElapsedSeconds(), 0.001);
                        m_lastEta = rate > 0 ? (TotalChunks - valid) / rate : -1;
                    }
                    m_lastValid = valid;
                    m_lastProgressTime = now;
                }
                // [v0.1.137 修正] **只在"未完成 → 完成"的跃变上计数一次**：第一版把 `CompletedTotal++`
                // 放在每帧都成立的 `valid >= TotalChunks` 分支里（因为完成不再自动摘点），
                // 结果 completedTotal 每帧 +1（实测一跑变 26）。
                if (valid >= TotalChunks && !Completed) {
                    LastDurationSeconds = ElapsedSeconds();
                    CompletedTotal++;
                    Completed = true;
                    Log.Information($"SkylineChunkPreloader: completed {TotalChunks}/{TotalChunks} "
                        + $"in {LastDurationSeconds:0.0}s（保持加载，等调用方 Release）");
                }
            }
            catch (Exception e) {
                m_lastError = "tick: " + e.Message;
            }
        }

        public static JsonObject StatusObject() => new() {
            ["running"] = Running,
            ["completed"] = Completed,
            ["centerChunk"] = new JsonArray(CenterChunkX, CenterChunkZ),
            ["radiusChunks"] = RadiusChunks,
            ["totalChunks"] = TotalChunks,
            ["validChunks"] = Running ? ValidChunks() : 0,
            ["progressPct"] = TotalChunks > 0 ? Math.Round(100.0 * ValidChunks() / TotalChunks, 1) : 0.0,
            ["elapsedSeconds"] = Math.Round(Running ? ElapsedSeconds() : LastDurationSeconds, 2),
            ["etaSeconds"] = Math.Round(m_lastEta, 1),
            ["framesObserved"] = (long)m_frames,
            ["avgFrameMs"] = m_frames > 0 ? Math.Round(m_frameMsSum / m_frames, 2) : 0.0,
            ["maxFrameMs"] = Math.Round(m_maxFrameMs, 2),
            ["startedTotal"] = StartedTotal,
            ["completedTotal"] = CompletedTotal,
            ["cancelledTotal"] = CancelledTotal,
            ["releasedTotal"] = ReleasedTotal,
            ["lastDurationSeconds"] = Math.Round(LastDurationSeconds, 2),
            ["virtualLocationIndex"] = VirtualLocationIndex,
            ["lastError"] = m_lastError,
            ["note"] = "Chunky 式预加载的**移植最小版**：给一个虚拟 UpdateLocation，让引擎按既有线程加载到 Valid；"
                       + "分帧只做记账/判定，取消即摘点（引擎按自己的规则释放）"
        };

        public static string Status() => StatusObject().ToJsonString();
    }
}
