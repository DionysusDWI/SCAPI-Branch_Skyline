using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;
using GameEntitySystem;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.13：**32³ 三维窗口账本**（里程碑 3「32³ 第 2 步」P2 的第一刀，见 `notes/81`）。
    ///
    /// 它与 P1 的影子原型（`CubeChunk32`）配合，回答"把加载窗口从**列**（16×16×2048）换成
    /// **立方体**（32³、三维坐标）以后，窗口里到底有多少块、要多少内存、每帧算一次要多久"。
    ///
    /// 设计口径（**默认零影响**）：
    ///   * `CubeWindowEnabled = false`（默认）时 `CubeWindowTick()` 立即返回，不分配、不遍历；
    ///   * 它**只做账本**：真实施工（P3/P4）之前不碰 `TerrainChunk`、不碰加载/卸载/存档；
    ///   * 椭球判据与 `notes/64` 的"视觉球"一致：`dx² + (dy/m)² + dz² ≤ R²`，
    ///     半径默认取 `SubsystemSky.VisibilityRange`（用户设置视距），`m = CubeWindowYMultiplier`；
    ///   * 立方体按"与椭球**相交**（包围盒最近点）"计数 —— 与现有列式"被球波及的列都要加载"同口径，
    ///     因此两组数字可比。
    ///
    /// [v0.1.39] 追加**立方体级判据**（不是账本，而是"该立方体要不要保留"的可查判定）：
    ///   * `skyline.CubeWindowDecision(cx,cy,cz)` —— 三维坐标单立方体判定（球窗 + **该 32 层分带的掩码位**），
    ///     并与 v0.1.28 的列级判据对照；
    ///   * `skyline.CubeBandSurveyHere(radius,yRadius)` —— 以相机所在立方体为中心的**专项窗口报告**
    ///     （含"列级判据会多保留多少"的收益口径）；
    ///   * `skyline.CubeInvariantsCheck(samples)` —— 立方体坐标 ↔ 分带掩码位 ↔ (x,y,z) 的寻址门禁。
    /// </summary>
    public static partial class SkylineRuntime {
        // ---------------- 配置（全部可在运行时改，桥可直接读写） ----------------

        /// <summary>账本开关。默认 **false**：关闭时不遍历、不分配。</summary>
        public static bool CubeWindowEnabled { get; set; }

        /// <summary>球半径（格）；0 = 用 `SubsystemSky.VisibilityRange`（游戏设置视距）。</summary>
        public static int CubeWindowRadiusBlocks { get; set; }

        /// <summary>竖直压缩系数（椭球 `dy/m`）；1 = 正球。</summary>
        public static float CubeWindowYMultiplier { get; set; } = 1f;

        /// <summary>中心移动多少格之后重算（默认 8 = 半个 16 m 列；避免每帧重算）。</summary>
        public static int CubeWindowMoveThreshold { get; set; } = 8;

        /// <summary>单次重算最多检查多少个候选立方体（防"视距调到 4096"时卡帧）。0 = 不限。</summary>
        public static int CubeWindowCandidateBudget { get; set; } = 400000;

        // ---------------- 只读统计 ----------------

        public static int CubeWindowCubes => m_cubeKeys.Count;
        public static int CubeWindowCubesWithContent => m_lastCubesWithContent;
        public static int CubeWindowColumns => m_columnKeys.Count;
        public static long CubeWindowBytes => (long)m_cubeKeys.Count * CubeChunk32.Bytes;
        public static long CubeWindowContentBytes => (long)m_lastCubesWithContent * CubeChunk32.Bytes;
        public static long CubeWindowColumnBytes => (long)m_columnKeys.Count * 256 * 1024;   // v0.1.4 列式：普通地面 1 段 = 256 KiB
        public static float CubeWindowLastMs => m_lastMs;
        public static int CubeWindowUpdates => m_updates;
        public static int CubeWindowCandidates => m_lastCandidates;
        public static bool CubeWindowBudgetHit => m_lastBudgetHit;

        // ---------------- 内部状态 ----------------

        static readonly HashSet<long> m_cubeKeys = [];
        static readonly HashSet<long> m_cubeKeysWithContent = [];      // 球内 **且有地形内容** 的立方体
        static readonly HashSet<long> m_columnKeys = [];
        static Vector3 m_lastCenter;
        static bool m_hasCenter;
        static float m_lastMs;
        static int m_updates;
        static int m_lastCandidates;
        static bool m_lastBudgetHit;
        static int m_lastCubesWithContent;

        /// <summary>每帧由 `SkylineRuntime.Tick()` 调用（内部按"中心移动阈值"限频）。</summary>
        static void CubeWindowTick() {
            if (!CubeWindowEnabled) {
                return;
            }
            try {
                ComponentPlayer player = LocalPlayer();
                ComponentBody body = player?.ComponentBody;
                if (body == null) {
                    return;
                }
                Vector3 center = body.Position;
                if (m_hasCenter && Vector3.DistanceSquared(center, m_lastCenter)
                    < (float)CubeWindowMoveThreshold * CubeWindowMoveThreshold) {
                    return;
                }
                Recompute(center);
                m_lastCenter = center;
                m_hasCenter = true;
            }
            catch (Exception e) {
                Log.Warning($"SkylineRuntime.CubeWindowTick: {e.Message}");
            }
        }

        static void Recompute(Vector3 center) {
            Stopwatch watch = Stopwatch.StartNew();
            int radius = CubeWindowRadiusBlocks > 0
                ? CubeWindowRadiusBlocks
                : (int)(GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                        ?? SettingsManager.VisibilityRange);
            float m = MathF.Max(CubeWindowYMultiplier, 0.05f);
            float r2 = (float)radius * radius;
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            m_cubeKeys.Clear();
            m_cubeKeysWithContent.Clear();
            m_columnKeys.Clear();
            m_lastCandidates = 0;
            m_lastBudgetHit = false;

            const int cubeSize = CubeChunk32.Size;                   // 32
            int cx0 = FloorDiv((int)center.X - radius, cubeSize);
            int cx1 = FloorDiv((int)center.X + radius, cubeSize);
            int cy0 = FloorDiv((int)center.Y - (int)(radius * m), cubeSize);
            int cy1 = FloorDiv((int)center.Y + (int)(radius * m), cubeSize);
            int cz0 = FloorDiv((int)center.Z - radius, cubeSize);
            int cz1 = FloorDiv((int)center.Z + radius, cubeSize);
            for (int cx = cx0; cx <= cx1; cx++) {
                for (int cz = cz0; cz <= cz1; cz++) {
                    for (int cy = cy0; cy <= cy1; cy++) {
                        if (CubeWindowCandidateBudget > 0 && ++m_lastCandidates > CubeWindowCandidateBudget) {
                            m_lastBudgetHit = true;
                            goto done;
                        }
                        float x0 = cx * cubeSize, x1 = x0 + cubeSize;
                        float y0 = cy * cubeSize, y1 = y0 + cubeSize;
                        float z0 = cz * cubeSize, z1 = z0 + cubeSize;
                        if (!IntersectsEllipsoid(center, radius, m, x0, x1, y0, y1, z0, z1, r2)) {
                            continue;
                        }
                        m_cubeKeys.Add(CubeChunk32.Key(cx, cy, cz));
                        // 内容感知：只算"该立方体的高度区间与所在地形的内容带相交"的那些
                        // （球内的高空空气不该算进 3D 窗口的收益对比里）。
                        int sampleX = cx * cubeSize + cubeSize / 2;
                        int sampleZ = cz * cubeSize + cubeSize / 2;
                        int topHere = terrain?.GetTopHeight(sampleX, sampleZ) ?? 0;
                        int bottomHere = terrain?.GetBottomHeight(sampleX, sampleZ) ?? 0;
                        if ((int)y1 > bottomHere - 1 && (int)y0 <= topHere + 1) {
                            m_cubeKeysWithContent.Add(CubeChunk32.Key(cx, cy, cz));
                        }
                        // 列（16×16，全高）口径：同一椭球波及的列
                        int colX0 = FloorDiv((int)x0, TerrainChunk.Size);
                        int colX1 = FloorDiv((int)x1 - 1, TerrainChunk.Size);
                        int colZ0 = FloorDiv((int)z0, TerrainChunk.Size);
                        int colZ1 = FloorDiv((int)z1 - 1, TerrainChunk.Size);
                        for (int lx = colX0; lx <= colX1; lx++) {
                            for (int lz = colZ0; lz <= colZ1; lz++) {
                                float px0 = lx * TerrainChunk.Size, px1 = px0 + TerrainChunk.Size;
                                float pz0 = lz * TerrainChunk.Size, pz1 = pz0 + TerrainChunk.Size;
                                // 列在竖直方向是全高：只要水平距离在球内就该加载（与现有实现同口径）
                                if (IntersectsEllipsoid(center, radius, m,
                                        px0, px1, TerrainChunk.MinHeight, TerrainChunk.HeightMinusOne + 1,
                                        pz0, pz1, r2)) {
                                    m_columnKeys.Add(((long)lx << 32) | (uint)lz);
                                }
                            }
                        }
                    }
                }
            }
        done:
            watch.Stop();
            m_lastMs = (float)watch.Elapsed.TotalMilliseconds;
            m_lastCubesWithContent = m_cubeKeysWithContent.Count;
            m_updates++;
        }

        /// <summary>立方体包围盒与椭球是否相交（点-盒最近距离）。</summary>
        static bool IntersectsEllipsoid(Vector3 center, float radius, float m,
            float x0, float x1, float y0, float y1, float z0, float z1, float r2) {
            float dx = ClosestAxisDistance(center.X, x0, x1);
            float dy = ClosestAxisDistance(center.Y, y0, y1) / m;
            float dz = ClosestAxisDistance(center.Z, z0, z1);
            return dx * dx + dy * dy + dz * dz <= r2;
        }

        static float ClosestAxisDistance(float c, float lo, float hi) =>
            c < lo ? lo - c : (c > hi ? c - hi : 0f);

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -(((-a) + b - 1) / b);

        static ComponentPlayer LocalPlayer() {
            SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(true);
            return players != null && players.ComponentPlayers.Count > 0 ? players.ComponentPlayers[0] : null;
        }

        // ---------------- 诊断（桥：skyline.CubeWindowDescribe / CubeWindowSurvey） ----------------

        public static string CubeWindowDescribe() =>
            $"cubeWindow enabled={CubeWindowEnabled} radius={CubeWindowRadiusBlocks} "
            + $"cubes={m_cubeKeys.Count}(content={m_lastCubesWithContent}) columns={m_columnKeys.Count} "
            + $"bytes={CubeWindowBytes / 1024}KiB(content={CubeWindowContentBytes / 1024}KiB) "
            + $"vs columns={CubeWindowColumnBytes / 1024}KiB "
            + $"ms={m_lastMs:0.0} updates={m_updates} budgetHit={m_lastBudgetHit}";

        public static string CubeWindowSurvey() => new JsonObject {
            ["enabled"] = CubeWindowEnabled,
            ["radiusBlocks"] = CubeWindowRadiusBlocks > 0
                ? CubeWindowRadiusBlocks
                : (int)(GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                        ?? SettingsManager.VisibilityRange),
            ["yMultiplier"] = Math.Round(CubeWindowYMultiplier, 3),
            ["cubes"] = m_cubeKeys.Count,
            ["cubesWithContent"] = m_lastCubesWithContent,
            ["columns"] = m_columnKeys.Count,
            ["cubeBytes"] = CubeWindowBytes,
            ["cubeContentBytes"] = CubeWindowContentBytes,
            ["columnBytes"] = CubeWindowColumnBytes,
            ["cubeBytesPerCube"] = CubeChunk32.Bytes,
            ["lastRecomputeMs"] = Math.Round(m_lastMs, 3),
            ["updates"] = m_updates,
            ["candidates"] = m_lastCandidates,
            ["candidateBudget"] = CubeWindowCandidateBudget,
            ["budgetHit"] = m_lastBudgetHit,
            ["center"] = m_hasCenter
                ? new JsonArray(Math.Round(m_lastCenter.X, 1), Math.Round(m_lastCenter.Y, 1), Math.Round(m_lastCenter.Z, 1))
                : null,
            ["note"] = "账本模式：只统计 32³ 三维窗口的规模/内存/重算耗时，不参与加载与存档"
        }.ToJsonString();

        // ============================================================================================
        // [v0.1.39] 立方体级判据（三维坐标）：账本回答"有多少"，下面回答"这一块要不要"
        // ============================================================================================

        /// <summary>[v0.1.39] 单立方体判定（三维坐标）：球窗椭球内？该 32 层分带真有内容？
        /// 旧列级判据会不会保留整列？最终 kept？见 `TerrainUpdater.CubeWindowDecide`。</summary>
        public static string CubeWindowDecision(int cx, int cy, int cz) {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return "cubeWindowDecision: no updater";
            }
            return updater.DescribeCubeWindowDecision(cx, cy, cz);
        }

        /// <summary>[v0.1.39] **专项窗口报告**：以相机所在立方体为中心，统计立方体级判据的分布，
        /// 并与 v0.1.28 列级判据对照（`emptyButColumnRuleKeeps` = 列级会多保留的立方体数，这是 P3 收益口径）。</summary>
        public static string CubeBandSurveyHere(int radiusCubes = 4, int yRadius = 3) {
            Camera camera = GetCamera();
            if (camera == null) {
                return "cubeBandSurvey: no camera";
            }
            Vector3 position = camera.ViewPosition;
            int pcx = (int)MathF.Floor(position.X / TerrainUpdater.CubeSize);
            int pcy = (int)MathF.Floor(position.Y / TerrainUpdater.CubeSize);
            int pcz = (int)MathF.Floor(position.Z / TerrainUpdater.CubeSize);
            return CubeBandSurveyAt(pcx, pcy, pcz, radiusCubes, yRadius);
        }

        /// <summary>[v0.1.39] 指定立方体中心的专项窗口报告（脚本/取证用：不必把相机搬过去，
        /// 例如对比"地面"与"高空"同一个 x/z 下的立方体窗口差别）。</summary>
        public static string CubeBandSurveyAt(int pcx, int pcy, int pcz,
                                              int radiusCubes = 4, int yRadius = 3) {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return "cubeBandSurvey: no updater";
            }
            return updater.CubeWindowSurvey(pcx, pcy, pcz,
                Math.Clamp(radiusCubes, 1, 16), Math.Clamp(yRadius, 0, 16));
        }

        /// <summary>
        /// [v0.1.39] **立方体寻址门禁**：立方体坐标 ↔ 分带掩码位 ↔ (x,y,z) 必须自洽。
        /// 覆盖世界竖直两端、32 的倍数/±1 边界与随机抽样；任何一条不成立 → ok=false。
        /// 施工（P3）前后各跑一次。
        /// </summary>
        public static string CubeInvariantsCheck(int samples = 4096) {
            JsonObject result = new();
            try {
                const int size = 32;
                int checkedCells = 0;
                int mismatches = 0;
                string firstMismatch = null;
                int bandOffset = -TerrainChunk.MinHeight / size;      // -1024/32 = 32
                void CheckCell(int x, int y, int z) {
                    checkedCells++;
                    // 立方体坐标用**算术右移**（= floor 除法），负数才与游戏里 ToChunk 的约定一致
                    int cx = x >> 5;
                    int cy = y >> 5;
                    int cz = z >> 5;
                    int bandIndex = (y - TerrainChunk.MinHeight) >> 5;   // 掩码 64 位 ↔ 2048 m / 32
                    int y0 = cy << 5;
                    bool inside = y >= y0 && y < y0 + size;
                    int rcx = (cx << 5 | 16) >> 5;
                    int rcy = (cy << 5 | 16) >> 5;
                    int rcz = (cz << 5 | 16) >> 5;
                    bool roundTrip = rcx == cx && rcy == cy && rcz == cz;
                    if (bandIndex != cy + bandOffset || !inside || !roundTrip
                        || bandIndex < 0 || bandIndex > 63) {
                        mismatches++;
                        firstMismatch ??= $"(x={x},y={y},z={z}) cube=({cx},{cy},{cz}) bandIndex={bandIndex} "
                            + $"expected={cy + bandOffset} inside={inside} roundTrip={roundTrip}";
                    }
                }
                int minY = TerrainChunk.MinHeight;
                int maxY = TerrainChunk.HeightMinusOne;
                foreach (int y in new[] { minY, minY + 1, -993, -992, -991, -1, 0, 1, 31, 32, 33,
                                          maxY - 1, maxY }) {
                    CheckCell(0, y, 0);
                    CheckCell(31, y, -31);
                }
                Random random = new(12345);
                int n = Math.Clamp(samples, 16, 200000);
                for (int i = 0; i < n; i++) {
                    CheckCell(random.Int(-4096, 4096), random.Int(minY, maxY), random.Int(-4096, 4096));
                }
                result["ok"] = mismatches == 0;
                result["cubeSize"] = size;
                result["bandOffset"] = bandOffset;
                result["checkedCells"] = checkedCells;
                result["mismatches"] = mismatches;
                result["firstMismatch"] = firstMismatch;
                result["note"] = "bandIndex == cy + 32；立方体装得下该格；中心反查回同一立方体";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
