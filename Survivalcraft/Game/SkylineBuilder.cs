using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— **曲线建筑生成（贝塞尔曲线铺设）**（v0.0.6，游戏本体内置，非 mod）。
    ///
    /// 用户口径（逐字）：
    ///   * "建筑辅助 API（曲线生成/区域复制/镜像/蓝图导入导出）……类似于 Minecraft 的 Axiom 模组所提供的、
    ///     基于贝塞尔曲线的建筑生成方式"；
    ///   * "**注意这不是某个圆或者椭圆的一部分，而是通过贝塞尔曲线生成的**"——参考图是一条多车道弯道公路，
    ///     横截面（车道/护栏/路肩）沿着任意贝塞尔曲线扫出去。
    ///
    /// 实现要点（和"圆弧/椭圆"的本质区别）：
    ///   1. 曲线 = **三次贝塞尔**（4 控制点一段）；控制点多于 4 个时用 Catmull-Rom 转贝塞尔
    ///      （曲线**过**每个控制点，且 C1 连续）；
    ///   2. 采样 = **按弧长等距**（先建累计弧长表再等距取站），所以弯道处不会"站变密/变疏"；
    ///   3. 坐标系 = **平行传输（rotation-minimizing frame）**：把上一站的法向绕"切线夹角轴"旋转过去，
    ///      避免简单叉乘导致的扭转（剖面歪斜）；
    ///   4. 剖面 = 从世界里**切一段横截面**（建议沿路径方向厚 1 格），局部轴 =（横 R / 竖 U / 沿 A）；
    ///   5. 写世界只走 `SubsystemTerrain.ChangeCell`（会刷光照/几何/通知方块行为），并做 **Undo 记录**；
    ///   6. `groundFollow`：按曲线 XZ 的地表高度把每一站"贴地"（可加 `groundOffset`），做出贴着地形走的道路；
    ///   7. 远处列没加载时**如实统计** `skippedNotLoaded`，并可选自动向 `SkylineRuntime` 申请区块列驻留。
    ///
    /// 桥调用（AgentBridge 根对象 `skylinebuilder` / `builder`）：
    ///   {"op":"invoke","target":"skylinebuilder","member":"Preview","action":"call","args":["&lt;spec&gt;"]}
    ///   {"op":"invoke","target":"skylinebuilder","member":"SweepBezier","action":"call","args":["&lt;spec&gt;"]}
    ///   {"op":"invoke","target":"skylinebuilder","member":"Undo","action":"call"}
    ///
    /// spec 文法（空格分隔的 key:value，`points` 与 `profile` 用 `;` 分隔坐标）：
    ///   points:2560,90,6740;2600,120,6780;2660,80,6820;2720,100,6870   ← 4 个点 = 一段三次贝塞尔；≥5 个点 = Catmull-Rom
    ///   profile:2558,86,6738;2566,92,6740                              ← 横截面盒子（含端点）
    ///   step:1 onlyAir:1 groundFollow:0 groundOffset:0 vy:0 lat:0 mirror:0
    ///   dry:0 max:200000 ensureLoaded:1
    /// </summary>
    public static class SkylineBuilder {
        const string FormatVersion = "1";

        // ============================================================================================
        // 结果 / 参数
        // ============================================================================================

        public sealed class Result {
            public string Op = "none";
            public int Stations;
            public int ProfileVoxels;
            public int Voxels;
            public int Written;
            public int SkippedAirOnly;
            public int SkippedNotLoaded;
            public int SkippedOutside;
            public int Failed;
            public int RecalcChunks;
            public bool DryRun;
            public float Ms;
            public string Bounds = "";
            public string Warning = "";
            public string Error = "";

            public string Describe() {
                if (!string.IsNullOrEmpty(Error)) {
                    return $"{Op} ERROR {Error}";
                }
                return $"{Op} stations={Stations} profile={ProfileVoxels} voxels={Voxels} written={Written} "
                    + $"skipAir={SkippedAirOnly} skipNotLoaded={SkippedNotLoaded} skipOutside={SkippedOutside} failed={Failed} "
                    + $"recalcChunks={RecalcChunks} dry={DryRun} ms={Ms:0.0} bounds={Bounds}"
                    + (string.IsNullOrEmpty(Warning) ? "" : $" warning={Warning}");
            }

            public JsonObject ToJson() => new() {
                ["op"] = Op,
                ["stations"] = Stations,
                ["profileVoxels"] = ProfileVoxels,
                ["voxels"] = Voxels,
                ["written"] = Written,
                ["skipAir"] = SkippedAirOnly,
                ["skipNotLoaded"] = SkippedNotLoaded,
                ["skipOutside"] = SkippedOutside,
                ["failed"] = Failed,
                ["recalcChunks"] = RecalcChunks,
                ["dryRun"] = DryRun,
                ["ms"] = Ms,
                ["bounds"] = Bounds,
                ["warning"] = Warning,
                ["error"] = Error
            };
        }

        public static Result LastResult { get; private set; } = new() { Op = "none" };

        sealed class Options {
            public float Step = 1f;
            public bool OnlyAir = true;
            public bool GroundFollow;
            public float GroundOffset;
            public float VerticalOffset;
            public float LateralOffset;
            public bool Mirror;
            public bool DryRun;
            public int MaxBlocks = 200000;
            public bool EnsureLoaded = true;
        }

        struct Cubic {
            public Vector3 P0, P1, P2, P3;
        }

        struct ProfileVoxel {
            public int R, U, A;
            public int Value;
        }

        sealed class Profile {
            public readonly List<ProfileVoxel> Voxels = [];
            public int MinR = int.MaxValue, MaxR = int.MinValue;
            public int MinU = int.MaxValue, MaxU = int.MinValue;
            public Vector3 Along, Up, Right;
        }

        // Undo：记录被覆盖的旧格（上限 200 万，保护内存）
        const int UndoCapacity = 2000000;
        static readonly List<(int X, int Y, int Z, int Value)> m_undo = [];

        // ============================================================================================
        // 对外入口
        // ============================================================================================

        public static string Describe() =>
            $"SkylineBuilder v{FormatVersion} undoDepth={m_undo.Count} last=[{LastResult.Describe()}]";

        public static string LastResultJson() => LastResult.ToJson().ToJsonString();

        /// <summary>只算不写：返回采样站数、包围盒、将要写入的格数。</summary>
        public static string Preview(string spec) {
            Result result = Run(spec, true);
            LastResult = result;
            return LastResult.Describe();
        }

        /// <summary>沿贝塞尔曲线把横截面扫出去（真正写世界）。</summary>
        public static string SweepBezier(string spec) {
            Result result = Run(spec, false);
            LastResult = result;
            return LastResult.Describe();
        }

        /// <summary>撤销最近一次扫掠（按记录的旧值逐格还原）。</summary>
        public static string Undo() {
            var result = new Result { Op = "Undo" };
            try {
                SubsystemTerrain subsystemTerrain = GetTerrainSubsystem();
                if (subsystemTerrain == null || m_undo.Count == 0) {
                    result.Warning = "nothing to undo";
                    LastResult = result;
                    return LastResult.Describe();
                }
                var columns = new HashSet<long>();
                for (int i = m_undo.Count - 1; i >= 0; i--) {
                    (int x, int y, int z, int value) = m_undo[i];
                    subsystemTerrain.ChangeCell(x, y, z, value);
                    columns.Add(ChunkKey(x, z));
                    result.Written++;
                }
                result.RecalcChunks = Recalc(subsystemTerrain, columns);
                result.Stations = m_undo.Count;
                m_undo.Clear();
            }
            catch (Exception ex) {
                result.Error = ex.Message;
                Log.Error($"SkylineBuilder: undo failed. {ex}");
            }
            LastResult = result;
            return LastResult.Describe();
        }

        // ============================================================================================
        // 主流程
        // ============================================================================================

        static SubsystemTerrain GetTerrainSubsystem() => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);

        static Result Run(string spec, bool forceDryRun) {
            var result = new Result { Op = forceDryRun ? "Preview" : "SweepBezier" };
            long startTicks = DateTime.UtcNow.Ticks;
            try {
                SubsystemTerrain subsystemTerrain = GetTerrainSubsystem();
                if (subsystemTerrain == null) {
                    result.Error = "no terrain";
                    return result;
                }
                Terrain terrain = subsystemTerrain.Terrain;
                if (!TryParseSpec(spec, out List<Vector3> points, out Vector3 profileMin, out Vector3 profileMax,
                        out Options options, out string parseError)) {
                    result.Error = parseError;
                    return result;
                }
                if (forceDryRun) {
                    options.DryRun = true;
                }
                List<Cubic> cubics = BuildCubics(points);
                if (cubics.Count == 0) {
                    result.Error = "need at least 4 control points (or 3+ for Catmull-Rom)";
                    return result;
                }
                Vector3 tangent0 = Vector3.Normalize(Derivative(cubics[0], 0f));
                Profile profile = CaptureProfile(terrain, profileMin, profileMax, tangent0);
                result.ProfileVoxels = profile.Voxels.Count;
                if (profile.Voxels.Count == 0) {
                    result.Error = "profile is empty (nothing but air in the given box)";
                    return result;
                }
                List<(Vector3 P, Vector3 T, Vector3 R, Vector3 U)> stations = BuildStations(cubics, options.Step);
                result.Stations = stations.Count;
                if (!options.DryRun && options.EnsureLoaded) {
                    ReserveRegion(points, profile, stations, options, result);
                }
                int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
                int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
                var columns = new HashSet<long>();
                int writtenThisRun = 0;
                for (int i = 0; i < stations.Count; i++) {
                    (Vector3 p, Vector3 t, Vector3 r, Vector3 u) = stations[i];
                    Vector3 origin = p + options.VerticalOffset * Vector3.UnitY + options.LateralOffset * r;
                    if (options.GroundFollow) {
                        int cx = (int)MathF.Round(origin.X);
                        int cz = (int)MathF.Round(origin.Z);
                        int top = terrain.CalculateTopmostCellHeight(cx, cz);
                        if (top > TerrainChunk.MinHeight) {
                            float desiredBottom = top + 1 + options.GroundOffset;
                            origin.Y += desiredBottom - (origin.Y + profile.MinU);
                        }
                    }
                    for (int v = 0; v < profile.Voxels.Count; v++) {
                        ProfileVoxel voxel = profile.Voxels[v];
                        int lateral = options.Mirror ? -voxel.R : voxel.R;
                        Vector3 world = origin + lateral * r + voxel.U * u + voxel.A * t;
                        int x = (int)MathF.Round(world.X);
                        int y = (int)MathF.Round(world.Y);
                        int z = (int)MathF.Round(world.Z);
                        result.Voxels++;
                        if (y < TerrainChunk.MinHeight || y > TerrainChunk.HeightMinusOne) {
                            result.SkippedOutside++;
                            continue;
                        }
                        if (writtenThisRun >= options.MaxBlocks || result.Written >= options.MaxBlocks) {
                            result.Warning = $"maxBlocks={options.MaxBlocks} reached";
                            break;
                        }
                        int existing = terrain.GetCellValue(x, y, z);
                        if (options.OnlyAir && Terrain.ExtractContents(existing) != 0) {
                            result.SkippedAirOnly++;
                            continue;
                        }
                        if (options.DryRun) {
                            result.Written++;
                            UpdateBounds(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, x, y, z);
                            continue;
                        }
                        if (!IsColumnUsable(terrain, x, z)) {
                            result.SkippedNotLoaded++;
                            continue;
                        }
                        RecordUndo(x, y, z, existing);
                        subsystemTerrain.ChangeCell(x, y, z, voxel.Value);
                        writtenThisRun++;
                        result.Written++;
                        columns.Add(ChunkKey(x, z));
                        UpdateBounds(ref minX, ref minY, ref minZ, ref maxX, ref maxY, ref maxZ, x, y, z);
                    }
                }
                result.DryRun = options.DryRun;
                if (!options.DryRun && columns.Count > 0) {
                    result.RecalcChunks = Recalc(subsystemTerrain, columns);
                }
                result.Bounds = minX == int.MaxValue
                    ? "(empty)"
                    : $"[{minX},{minY},{minZ}]..[{maxX},{maxY},{maxZ}]";
            }
            catch (Exception ex) {
                result.Error = ex.Message;
                Log.Error($"SkylineBuilder: failed. {ex}");
            }
            result.Ms = (float)(DateTime.UtcNow.Ticks - startTicks) / 10000f;
            return result;
        }

        static void UpdateBounds(ref int minX, ref int minY, ref int minZ, ref int maxX, ref int maxY, ref int maxZ,
            int x, int y, int z) {
            if (x < minX) { minX = x; }
            if (y < minY) { minY = y; }
            if (z < minZ) { minZ = z; }
            if (x > maxX) { maxX = x; }
            if (y > maxY) { maxY = y; }
            if (z > maxZ) { maxZ = z; }
        }

        static void RecordUndo(int x, int y, int z, int oldValue) {
            if (m_undo.Count >= UndoCapacity) {
                return;   // 超上限就不再记录（宁可不能撤销，也不要吃爆内存）
            }
            m_undo.Add((x, y, z, oldValue));
        }

        /// <summary>只有"这一列已分配且内容已就绪"才写：否则引擎会静默丢弃（这正是区块驻留要解决的问题）。</summary>
        static bool IsColumnUsable(Terrain terrain, int x, int z) {
            TerrainChunk chunk = terrain.GetChunkAtCell(x, z);
            return chunk != null && chunk.ThreadState >= TerrainChunkState.InvalidLight;
        }

        static long ChunkKey(int x, int z) =>
            ((long)(x >> TerrainChunk.SizeBits) << 32) | (uint)(z >> TerrainChunk.SizeBits);

        static int Recalc(SubsystemTerrain subsystemTerrain, HashSet<long> columns) {
            if (subsystemTerrain?.TerrainUpdater == null || columns == null) {
                return 0;
            }
            int count = 0;
            foreach (long key in columns) {
                int cx = (int)(key >> 32);
                int cz = (int)(key & 0xFFFFFFFFL);
                subsystemTerrain.TerrainUpdater.DowngradeChunkNeighborhoodState(
                    new Point2(cx, cz), 1, TerrainChunkState.InvalidLight, false);
                count++;
            }
            return count;
        }

        /// <summary>可选：把整条曲线 + 剖面的包围盒交给区块驻留（独立开关），避免"远处写入静默失效"。</summary>
        static void ReserveRegion(List<Vector3> points, Profile profile, List<(Vector3 P, Vector3 T, Vector3 R, Vector3 U)> stations,
            Options options, Result result) {
            try {
                float pad = 8f + MathF.Max(MathF.Abs(profile.MinR), MathF.Abs(profile.MaxR))
                    + MathF.Max(MathF.Abs(profile.MinU), MathF.Abs(profile.MaxU));
                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach ((Vector3 p, Vector3 t, Vector3 r, Vector3 u) in stations) {
                    minX = MathF.Min(minX, p.X - pad);
                    maxX = MathF.Max(maxX, p.X + pad);
                    minZ = MathF.Min(minZ, p.Z - pad);
                    maxZ = MathF.Max(maxZ, p.Z + pad);
                }
                if (minX > maxX) {
                    return;
                }
                if (!SkylineRuntime.ChunkResidencyMode) {
                    SkylineRuntime.ChunkResidencyMode = true;
                    result.Warning = Append(result.Warning, "chunk residency auto-enabled (remember to turn it off / let the TTL expire)");
                }
                string json = SkylineRuntime.EnsureRegionLoaded(
                    (int)MathF.Floor(minX), (int)MathF.Floor(minZ),
                    (int)MathF.Ceiling(maxX), (int)MathF.Ceiling(maxZ)
                );
                Log.Information($"SkylineBuilder: residency requested for the curve bounds: {json}");
            }
            catch (Exception ex) {
                Log.Error($"SkylineBuilder: residency request failed (ignored). {ex}");
            }
        }

        static string Append(string a, string b) => string.IsNullOrEmpty(a) ? b : a + "; " + b;

        // ============================================================================================
        // 曲线：三次贝塞尔 + Catmull-Rom 拼接 + 弧长等距采样 + 平行传输坐标系
        // ============================================================================================

        static List<Cubic> BuildCubics(List<Vector3> points) {
            var cubics = new List<Cubic>();
            if (points.Count == 4) {
                cubics.Add(new Cubic { P0 = points[0], P1 = points[1], P2 = points[2], P3 = points[3] });
                return cubics;
            }
            if (points.Count < 3) {
                return cubics;
            }
            // Catmull-Rom：曲线**过**每个点，C1 连续；再转成三次贝塞尔段
            for (int i = 0; i < points.Count - 1; i++) {
                Vector3 p0 = points[Math.Max(i - 1, 0)];
                Vector3 p1 = points[i];
                Vector3 p2 = points[i + 1];
                Vector3 p3 = points[Math.Min(i + 2, points.Count - 1)];
                cubics.Add(new Cubic {
                    P0 = p1,
                    P1 = p1 + (p2 - p0) / 6f,
                    P2 = p2 - (p3 - p1) / 6f,
                    P3 = p2
                });
            }
            return cubics;
        }

        static Vector3 Evaluate(Cubic c, float t) {
            float s = 1f - t;
            return s * s * s * c.P0 + 3f * s * s * t * c.P1 + 3f * s * t * t * c.P2 + t * t * t * c.P3;
        }

        static Vector3 Derivative(Cubic c, float t) {
            float s = 1f - t;
            return 3f * s * s * (c.P1 - c.P0) + 6f * s * t * (c.P2 - c.P1) + 3f * t * t * (c.P3 - c.P2);
        }

        /// <summary>按弧长等距取站（不是按 t 等分——那样弯道处站点会变密）。</summary>
        static List<(Vector3 P, Vector3 T, Vector3 R, Vector3 U)> BuildStations(List<Cubic> cubics, float step) {
            const int Subdivisions = 64;
            var samples = new List<(float Length, Vector3 P, Vector3 T)>();
            float total = 0f;
            Vector3 previous = Evaluate(cubics[0], 0f);
            samples.Add((0f, previous, Vector3.Normalize(Derivative(cubics[0], 0f))));
            foreach (Cubic cubic in cubics) {
                for (int i = 1; i <= Subdivisions; i++) {
                    float t = i / (float)Subdivisions;
                    Vector3 p = Evaluate(cubic, t);
                    total += Vector3.Distance(p, previous);
                    Vector3 tangent = Derivative(cubic, t);
                    samples.Add((total, p, tangent.LengthSquared() > 1e-8f ? Vector3.Normalize(tangent) : samples[^1].T));
                    previous = p;
                }
            }
            step = MathUtils.Max(step, 0.1f);
            int stationCount = Math.Max(2, (int)MathF.Floor(total / step) + 1);
            var stations = new List<(Vector3, Vector3, Vector3, Vector3)>(stationCount);
            int cursor = 0;
            Vector3 up = MathF.Abs(Vector3.Dot(samples[0].T, Vector3.UnitY)) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, samples[0].T));
            up = Vector3.Normalize(Vector3.Cross(samples[0].T, right));
            Vector3 lastT = samples[0].T;
            for (int station = 0; station < stationCount; station++) {
                float target = MathUtils.Min(station * step, total);
                while (cursor < samples.Count - 2 && samples[cursor + 1].Length < target) {
                    cursor++;
                }
                float span = samples[cursor + 1].Length - samples[cursor].Length;
                float f = span > 1e-6f ? (target - samples[cursor].Length) / span : 0f;
                Vector3 p = Vector3.Lerp(samples[cursor].P, samples[cursor + 1].P, f);
                Vector3 t = Vector3.Normalize(Vector3.Lerp(samples[cursor].T, samples[cursor + 1].T, f));
                // 平行传输：把上一站的法向绕"切线夹角轴"转过来（避免扭转）
                Vector3 axis = Vector3.Cross(lastT, t);
                float axisLength = axis.Length();
                if (axisLength > 1e-6f) {
                    float angle = MathF.Atan2(axisLength, MathUtils.Clamp(Vector3.Dot(lastT, t), -1f, 1f));
                    Quaternion rotation = Quaternion.CreateFromAxisAngle(axis / axisLength, angle);
                    right = Vector3.Normalize(Vector3.Transform(right, rotation));
                    up = Vector3.Normalize(Vector3.Transform(up, rotation));
                    lastT = t;
                }
                right = Vector3.Normalize(right - Vector3.Dot(right, t) * t);
                up = Vector3.Normalize(Vector3.Cross(t, right));
                stations.Add((p, t, right, up));
            }
            return stations;
        }

        /// <summary>从世界区域切一段横截面：世界轴按"与起始切线最接近"的原则映到局部 (沿 A / 竖 U / 横 R)。</summary>
        static Profile CaptureProfile(Terrain terrain, Vector3 boxMin, Vector3 boxMax, Vector3 tangent0) {
            var profile = new Profile();
            Vector3 along = MathF.Abs(tangent0.X) >= MathF.Abs(tangent0.Y) && MathF.Abs(tangent0.X) >= MathF.Abs(tangent0.Z)
                ? new Vector3(tangent0.X >= 0 ? 1 : -1, 0, 0)
                : MathF.Abs(tangent0.Y) >= MathF.Abs(tangent0.Z)
                    ? new Vector3(0, tangent0.Y >= 0 ? 1 : -1, 0)
                    : new Vector3(0, 0, tangent0.Z >= 0 ? 1 : -1);
            Vector3 up = MathF.Abs(Vector3.Dot(along, Vector3.UnitY)) > 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, along));
            up = Vector3.Normalize(Vector3.Cross(along, right));
            profile.Along = along;
            profile.Up = up;
            profile.Right = right;
            int x1 = (int)MathF.Floor(MathF.Min(boxMin.X, boxMax.X)), x2 = (int)MathF.Floor(MathF.Max(boxMin.X, boxMax.X));
            int y1 = (int)MathF.Floor(MathF.Min(boxMin.Y, boxMax.Y)), y2 = (int)MathF.Floor(MathF.Max(boxMin.Y, boxMax.Y));
            int z1 = (int)MathF.Floor(MathF.Min(boxMin.Z, boxMax.Z)), z2 = (int)MathF.Floor(MathF.Max(boxMin.Z, boxMax.Z));
            int centreX = (x1 + x2) / 2, centreY = (y1 + y2) / 2, centreZ = (z1 + z2) / 2;
            for (int x = x1; x <= x2; x++) {
                for (int y = y1; y <= y2; y++) {
                    for (int z = z1; z <= z2; z++) {
                        int value = terrain.GetCellValue(x, y, z);
                        int contents = Terrain.ExtractContents(value);
                        if (contents == 0) {
                            continue;
                        }
                        var d = new Vector3(x - centreX, y - centreY, z - centreZ);
                        int r = (int)MathF.Round(Vector3.Dot(d, right));
                        int u = (int)MathF.Round(Vector3.Dot(d, up));
                        int a = (int)MathF.Round(Vector3.Dot(d, along));
                        profile.Voxels.Add(new ProfileVoxel {
                            R = r,
                            U = u,
                            A = a,
                            Value = Terrain.MakeBlockValue(contents, Terrain.ExtractData(value), 0)
                        });
                        profile.MinR = Math.Min(profile.MinR, r);
                        profile.MaxR = Math.Max(profile.MaxR, r);
                        profile.MinU = Math.Min(profile.MinU, u);
                        profile.MaxU = Math.Max(profile.MaxU, u);
                    }
                }
            }
            return profile;
        }

        // ============================================================================================
        // spec 解析
        // ============================================================================================

        static bool TryParseSpec(string spec, out List<Vector3> points, out Vector3 profileMin, out Vector3 profileMax,
            out Options options, out string error) {
            points = [];
            profileMin = profileMax = Vector3.Zero;
            options = new Options();
            error = null;
            if (string.IsNullOrWhiteSpace(spec)) {
                error = "empty spec";
                return false;
            }
            bool hasProfile = false;
            foreach (string rawToken in spec.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
                int colon = rawToken.IndexOf(':');
                if (colon <= 0) {
                    error = $"bad token '{rawToken}'";
                    return false;
                }
                string key = rawToken[..colon].Trim().ToLowerInvariant();
                string value = rawToken[(colon + 1)..].Trim();
                switch (key) {
                    case "points":
                        foreach (string item in value.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
                            if (!TryParseVector(item, out Vector3 p)) {
                                error = $"bad point '{item}'";
                                return false;
                            }
                            points.Add(p);
                        }
                        break;
                    case "profile": {
                        string[] ends = value.Split(';', StringSplitOptions.RemoveEmptyEntries);
                        if (ends.Length != 2
                            || !TryParseVector(ends[0], out profileMin)
                            || !TryParseVector(ends[1], out profileMax)) {
                            error = "profile must be 'x1,y1,z1;x2,y2,z2'";
                            return false;
                        }
                        hasProfile = true;
                        break;
                    }
                    case "step":
                        options.Step = ParseFloat(value, options.Step);
                        break;
                    case "onlyair":
                        options.OnlyAir = ParseBool(value, options.OnlyAir);
                        break;
                    case "groundfollow":
                        options.GroundFollow = ParseBool(value, options.GroundFollow);
                        break;
                    case "groundoffset":
                        options.GroundOffset = ParseFloat(value, options.GroundOffset);
                        break;
                    case "vy":
                        options.VerticalOffset = ParseFloat(value, options.VerticalOffset);
                        break;
                    case "lat":
                        options.LateralOffset = ParseFloat(value, options.LateralOffset);
                        break;
                    case "mirror":
                        options.Mirror = ParseBool(value, options.Mirror);
                        break;
                    case "dry":
                        options.DryRun = ParseBool(value, options.DryRun);
                        break;
                    case "max":
                        options.MaxBlocks = (int)ParseFloat(value, options.MaxBlocks);
                        break;
                    case "ensureloaded":
                        options.EnsureLoaded = ParseBool(value, options.EnsureLoaded);
                        break;
                    default:
                        error = $"unknown key '{key}'";
                        return false;
                }
            }
            if (points.Count < 4) {
                error = $"need at least 4 control points, got {points.Count}";
                return false;
            }
            if (!hasProfile) {
                error = "profile box is required (cut a cross-section from the world)";
                return false;
            }
            return true;
        }

        static bool TryParseVector(string text, out Vector3 value) {
            value = Vector3.Zero;
            string[] parts = text.Split(',');
            if (parts.Length != 3) {
                return false;
            }
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) {
                return false;
            }
            value = new Vector3(x, y, z);
            return true;
        }

        static float ParseFloat(string text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

        static bool ParseBool(string text, bool fallback) {
            if (string.IsNullOrEmpty(text)) {
                return fallback;
            }
            char c = char.ToLowerInvariant(text[0]);
            return c switch {
                '1' or 't' or 'y' or 'o' => true,      // true / yes / on
                '0' or 'f' or 'n' => false,
                _ => fallback
            };
        }
    }
}
