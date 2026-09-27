using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**手动生成 LOD（+ HUD 进度条）**（用户新目标 1.7）。
    ///
    /// 用户口径："推进实现手动生成 LOD 的功能，从而可以使我们可以手动对已生成的家具等非完整方块
    /// 以及区块更新 LOD 块，从而优化测试时的视觉效果。手动生成 LOD 的功能应当加个 HUD 进度条
    /// （与 Distant Horizon mod 类似）。"
    ///
    /// 做法（**分帧推进**，绝不卡帧 —— 与 DH 的"后台逐区块重建"同思路）：
    ///   1. `Start(radiusMetres)`：把相机周围 `radius` 内的**已加载区块列**排成一张任务表（去重 + 排序：近的先做）；
    ///   2. `Tick()`：每帧在 `BudgetMs`（默认 4 ms）内处理若干列 ——
    ///      对每列：**强制 LOD 重采**（`SkylineLod` 的既有通道）+ **把该列的 32³ 立方体放进壳仓的待采队列**
    ///      （复用 v0.1.55 的 `OnChunkValid` 队列：只入队、主线程按预算采）；
    ///   3. 进度：`Done / Total / Eta`，桥 `skyline.LodManualStart(radius)` / `LodManualStatus()` / `LodManualCancel()`；
    ///      HUD 由 `SkylineHud` 已有的绘制挂点画（进度条 + 百分比 + 剩余秒数）。
    ///
    /// 与"自动"的关系：手动生成**不会**关掉自动采集/刷新，只是在自动之外**再推一把**（尤其对
    /// 家具/非完整方块这类"自动采样不容易覆盖到"的东西，手动重算一遍能立刻改善观感）。
    /// </summary>
    public static class SkylineLodManualBuild {
        public static bool Running { get; private set; }
        /// <summary>HUD 显示开关（默认开；跑完后再显示 3 s 的"完成后"条）。</summary>
        public static bool ShowHud { get; set; } = true;
        public static int Total { get; private set; }
        public static int Done { get; private set; }
        public static int ColumnsQueued { get; private set; }
        public static int CubesQueued { get; private set; }
        public static float BudgetMs { get; set; } = 4f;
        public static float RadiusMetres { get; private set; }
        public static double StartedAt { get; private set; }
        public static double FinishedAt { get; private set; }
        public static string LastError { get; private set; } = "";
        /// <summary>等待超时被跳过的列数（壳始终没采到：兄弟区块没就绪等）—— 如实报，不假装完成。</summary>
        public static int SkippedStuck { get; private set; }
        /// <summary>单列等待上限（秒）；超过就记 `SkippedStuck` 并放行，保证进度条一定能走完。</summary>
        public static double StuckSeconds { get; set; } = 20.0;

        static readonly System.Collections.Generic.List<(int X, int Z)> m_jobs = [];
        static readonly System.Collections.Generic.List<(int X, int Z, bool CubePending, double StartTime)> m_inFlight = [];
        static readonly System.Collections.Generic.List<(int X, int Z, bool CubePending, double StartTime)> m_check = [];
        static readonly System.Collections.Generic.HashSet<long> m_seen = [];
        static int m_cursor;

        public static float Progress => Total > 0 ? Math.Clamp(Done / (float)Total, 0f, 1f) : 0f;
        public static double ElapsedSeconds => StartedAt <= 0 ? 0 : (FinishedAt > 0 ? FinishedAt : Time.RealTime) - StartedAt;
        public static double EtaSeconds {
            get {
                if (!Running || Done <= 0) {
                    return 0;
                }
                double per = ElapsedSeconds / Done;
                return Math.Max(0, per * (Total - Done));
            }
        }

        /// <summary>开始一次手动生成：把相机周围 `radius` 内的**已加载列**排进任务表。</summary>
        public static string Start(float radiusMetres) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                Vector3 camera = SkylineLod.CameraViewPosition();
                RadiusMetres = Math.Clamp(radiusMetres, 16f, 1024f);
                m_jobs.Clear();
                m_inFlight.Clear();
                m_check.Clear();
                m_seen.Clear();
                m_cursor = 0;
                int cx0 = (int)MathF.Floor((camera.X - RadiusMetres) / TerrainChunk.Size);
                int cx1 = (int)MathF.Floor((camera.X + RadiusMetres) / TerrainChunk.Size);
                int cz0 = (int)MathF.Floor((camera.Z - RadiusMetres) / TerrainChunk.Size);
                int cz1 = (int)MathF.Floor((camera.Z + RadiusMetres) / TerrainChunk.Size);
                float r2 = RadiusMetres * RadiusMetres;
                for (int cz = cz0; cz <= cz1; cz++) {
                    for (int cx = cx0; cx <= cx1; cx++) {
                        float wx = cx * TerrainChunk.Size + TerrainChunk.Size * 0.5f;
                        float wz = cz * TerrainChunk.Size + TerrainChunk.Size * 0.5f;
                        float dx = wx - camera.X, dz = wz - camera.Z;
                        if (dx * dx + dz * dz > r2) {
                            continue;                        // 圆内（不是方框）
                        }
                        if (terrain.GetChunkAtCoords(cx, cz) == null) {
                            continue;                        // 未加载的列没有数据可采
                        }
                        long key = ((long)cx << 32) ^ (uint)cz;
                        if (!m_seen.Add(key)) {
                            continue;
                        }
                        m_jobs.Add((cx, cz));
                    }
                }
                // 近的先做（DH 也是这个顺序：越近越先可见）
                m_jobs.Sort((a, b) => Dist2(a.X, a.Z, camera).CompareTo(Dist2(b.X, b.Z, camera)));
                Total = m_jobs.Count;
                Done = 0;
                ColumnsQueued = 0;
                CubesQueued = 0;
                SkippedStuck = 0;
                Running = Total > 0;
                StartedAt = Time.RealTime;
                FinishedAt = 0;
                LastError = "";
                result["ok"] = true;
                result["total"] = Total;
                result["radiusMetres"] = RadiusMetres;
                result["note"] = "分帧推进：每帧最多 " + BudgetMs + " ms；HUD 会显示进度条";
            }
            catch (Exception e) {
                LastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        static float Dist2(int cx, int cz, in Vector3 camera) {
            float wx = cx * TerrainChunk.Size + TerrainChunk.Size * 0.5f;
            float wz = cz * TerrainChunk.Size + TerrainChunk.Size * 0.5f;
            float dx = wx - camera.X, dz = wz - camera.Z;
            return dx * dx + dz * dz;
        }

        public static void Cancel() {
            Running = false;
            m_jobs.Clear();
            m_seen.Clear();
        }

        /// <summary>每帧调用（由 `SkylineLod.Tick` 转发）：在预算内推进任务表。</summary>
        public static void Tick() {
            if (!Running) {
                return;
            }
            try {
                // ① 先结算"在飞"的列：LOD 单元不再脏 且 壳已入仓（或本来就有）→ 记完成
                m_check.Clear();
                m_check.AddRange(m_inFlight);
                m_inFlight.Clear();
                int budget = 256;
                double now = Time.RealTime;
                foreach ((int X, int Z, bool CubePending, double StartTime) item in m_check) {
                    if (--budget < 0) {
                        m_inFlight.Add(item);            // 下帧继续结算
                        continue;
                    }
                    if (!SkylineLod.IsCellDirty(item.X, item.Z)
                        && SkylineCubeShellStore.HasCubeForColumn(item.X, item.Z)) {
                        Done++;
                    }
                    else if (now - item.StartTime > StuckSeconds) {
                        Done++;
                        SkippedStuck++;                  // 等超时：如实记，不让进度条卡死
                    }
                    else {
                        m_inFlight.Add(item);
                    }
                }
                // ② 再按时间预算"开工"新列（只入队：真正的采样/采集由各自的预算去跑）
                Stopwatch watch = Stopwatch.StartNew();
                while (m_cursor < m_jobs.Count && m_inFlight.Count < 512) {
                    if (watch.Elapsed.TotalMilliseconds > BudgetMs) {
                        break;
                    }
                    (int X, int Z) job = m_jobs[m_cursor++];
                    TerrainChunk chunk = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)
                        ?.Terrain?.GetChunkAtCoords(job.X, job.Z);
                    if (chunk == null) {
                        Done++;
                        continue;
                    }
                    // ① 强制 LOD 重采这一列（走既有通道：标脏 + 立刻采）
                    SkylineLod.ManualResampleColumn(job.X, job.Z);
                    // ② 把该列所属的 32³ 立方体放进壳仓待采队列（复用 v0.1.55 的"只入队"接口）
                    bool cubePending = SkylineCubeShellStore.QueueCubeForColumn(job.X, job.Z) > 0;
                    ColumnsQueued++;
                    m_inFlight.Add((job.X, job.Z, cubePending, Time.RealTime));
                }
                // ③ 收尾：任务发完且没有在飞的
                if (m_cursor >= m_jobs.Count && m_inFlight.Count == 0) {
                    Running = false;
                    FinishedAt = Time.RealTime;
                }
                PushHudText();
            }
            catch (Exception e) {
                LastError = e.Message;
                Running = false;
                Log.Warning($"SkylineLodManualBuild.Tick: {e.Message}");
            }
        }

        /// <summary>把一行 ASCII 状态推给游戏自带的性能 HUD（`PerformanceManager` 每帧收集、帧末画）。</summary>
        static void PushHudText() {
            if (!ShowHud) {
                return;
            }
            bool recently = FinishedAt > 0 && Time.RealTime - FinishedAt < 3.0;
            if (!Running && !recently) {
                return;
            }
            string line = $"LOD build {Progress * 100f:0}% ({Done}/{Total}), eta {EtaSeconds:0}s";
            if (!Running) {
                line = $"LOD build done ({Done}/{Total}) in {ElapsedSeconds:0.0}s";
            }
            PerformanceManager.AddExtraStat(line);
        }

        /// <summary>
        /// HUD 里的**进度条**（与 DH 类似）：左上角、性能信息下方，背景 + 填充 + 边框。
        /// 由 `PerformanceManager.Draw()` 在 `m_primitivesRenderer.Flush()` 之前调用。
        /// </summary>
        public static void DrawHud(PrimitivesRenderer2D renderer, Viewport viewport, Vector2 scale) {
            if (!ShowHud || renderer == null) {
                return;
            }
            bool recently = FinishedAt > 0 && Time.RealTime - FinishedAt < 3.0;
            if (!Running && !recently) {
                return;
            }
            float x0 = scale.X * 4f;
            float y0 = scale.Y * 42f;
            float width = scale.X * 150f;
            float height = Math.Max(scale.Y * 5f, 4f);
            float filled = width * Math.Clamp(Progress, 0f, 1f);
            FlatBatch2D batch = renderer.FlatBatch();
            batch.QueueQuad(new Vector2(x0, y0), new Vector2(x0 + width, y0 + height), 0f, new Color(0, 0, 0, 140));
            if (filled > 0f) {
                Color fill = Running ? new Color(80, 200, 90, 230) : new Color(90, 140, 230, 230);
                batch.QueueQuad(new Vector2(x0, y0), new Vector2(x0 + filled, y0 + height), 0f, fill);
            }
            batch.QueueLine(new Vector2(x0, y0), new Vector2(x0 + width, y0), 0f, new Color(255, 255, 255, 180));
            batch.QueueLine(new Vector2(x0, y0 + height), new Vector2(x0 + width, y0 + height), 0f,
                new Color(255, 255, 255, 180));
            batch.QueueLine(new Vector2(x0, y0), new Vector2(x0, y0 + height), 0f, new Color(255, 255, 255, 180));
            batch.QueueLine(new Vector2(x0 + width, y0), new Vector2(x0 + width, y0 + height), 0f,
                new Color(255, 255, 255, 180));
        }

        public static string Status() {
            return new JsonObject {
                ["ok"] = true,
                ["running"] = Running,
                ["total"] = Total,
                ["done"] = Done,
                ["progress"] = Math.Round(Progress, 3),
                ["elapsedSeconds"] = Math.Round(ElapsedSeconds, 2),
                ["etaSeconds"] = Math.Round(EtaSeconds, 1),
                ["radiusMetres"] = RadiusMetres,
                ["columnsQueued"] = ColumnsQueued,
                ["cubesQueued"] = CubesQueued,
                ["skippedStuck"] = SkippedStuck,
                ["stuckSeconds"] = StuckSeconds,
                ["budgetMs"] = BudgetMs,
                ["lastError"] = LastError
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.LodManual*`。</summary>
    public static partial class SkylineRuntime {
        public static string LodManualStart(float radiusMetres) => SkylineLodManualBuild.Start(radiusMetres);
        public static string LodManualStatus() => SkylineLodManualBuild.Status();
        public static string LodManualCancel() {
            SkylineLodManualBuild.Cancel();
            return SkylineLodManualBuild.Status();
        }
        public static string LodManualBudget(float ms) {
            SkylineLodManualBuild.BudgetMs = Math.Clamp(ms, 0.5f, 33f);
            return SkylineLodManualBuild.Status();
        }
    }
}
