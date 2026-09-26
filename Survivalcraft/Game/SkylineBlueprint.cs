using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— **建筑辅助：蓝图 / 区域变换**（v0.0.5，游戏本体内置，非 mod）。
    ///
    /// 目标（用户给定）：让 Agent 能"把一栋房子当成一个对象"来建造 ——
    ///   * **捕获**（Capture）：把世界里的一个长方体区域存成命名蓝图（相对坐标 + 完整 block value）；
    ///   * **放置**（Paste）：按名字贴回世界，支持**旋转 90°/180°/270°**、**X/Z 镜像**、
    ///     "只填空位"（onlyAir）等模式；
    ///   * **区域变换**（Mirror / Rotate）：不改蓝图，直接对世界里的区域做镜像/旋转（就地）；
    ///   * **导入导出**（Export / Import）：蓝图存成 JSON 文件，可以跨存档/跨机器搬运；
    ///   * **Fill / Replace**：整片填充与材质替换（别墅家具化时最常用）。
    ///
    /// 三条硬口径：
    ///   1. **写世界只走 `SubsystemTerrain.ChangeCell`**（会刷光照/几何/通知方块行为），
    ///      不用 `SetCellValueFast` —— 后者会让新放下的方块处于"没光的默认态"（见 notes/38、notes/41）；
    ///      写完**回读校验**（比较 contents + data；light 会被引擎重算，故意不参与判定），
    ///      不一致就如实计入 `failed`，不假装成功。
    ///   2. **列没加载就不写**：目标区块列不在内存里（玩家离得远 / 已被 FreeChunk / 内容还没从磁盘加载）
    ///      时，写入会"静默丢失"。本模块默认**跳过并计入 `skippedNotLoaded`**，
    ///      并在返回里提示开 `SkylineRuntime.ChunkResidencyMode`（见 notes/47 与 SkylineRuntime 的驻留区）。
    ///      传 `force=true` 可以强行写（只在明确知道后果时用）。
    ///   3. 所有成员都**不抛异常**：出错返回 `ERROR: ...` 字符串，方便桥反射调用。
    ///
    /// 桥用法（AgentBridge 的反射根 `skylineblueprint` / `blueprint` → `Game.SkylineBlueprint`）：
    ///   {"op":"invoke","target":"blueprint","member":"Describe"}
    ///   {"op":"invoke","target":"blueprint","member":"Capture","args":["villa",2418,80,6708,2481,95,6771,false]}
    ///   {"op":"invoke","target":"blueprint","member":"Paste","args":["villa",2600,80,6708,90,false,false,false,true,true,false]}
    ///   {"op":"invoke","target":"blueprint","member":"Export","args":["villa",""]}
    /// </summary>
    public static class SkylineBlueprint {
        // ============================================================================================
        // 常量与数据模型
        // ============================================================================================

        public const int FormatVersion = 1;

        /// <summary>单次捕获/放置的体素上限（防手滑把整个世界拷进内存）。</summary>
        public const int MaxVoxels = 400000;

        /// <summary>单个区域内侧扫描的格数上限（捕获时按体素算，这里只是粗筛）。</summary>
        public const long MaxScanCells = 8000000L;

        /// <summary>蓝图目录（默认；Export/Import 可以给绝对路径覆盖）。</summary>
        public static string Directory => Path.Combine(AppContext.BaseDirectory, "Blueprints");

        /// <summary>一个体素：相对坐标（0 起点）+ 完整 block value（含 contents/data/light）。</summary>
        public sealed class Voxel {
            public int X;
            public int Y;
            public int Z;
            public int Value;
        }

        /// <summary>命名蓝图。坐标一律是**相对最小角**的 0 起点。</summary>
        public sealed class Blueprint {
            public string Name = "";
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public string CapturedAt = "";
            public string Source = "";
            public List<Voxel> Voxels = [];

            public int Count => Voxels.Count;

            public string SizeText => $"{SizeX}x{SizeY}x{SizeZ}";

            public string Describe() =>
                $"{Name} size={SizeText} voxels={Count} captured={CapturedAt} source={Source}";
        }

        /// <summary>一次操作的结果（`LastResult` 会一直留着，便于事后查）。</summary>
        public sealed class Result {
            public string Op = "";
            public string Name = "";
            public string Box = "";
            public int Voxels;
            public int Written;
            public int WrittenAir;
            public int SkippedAir;
            public int SkippedOccupied;
            public int SkippedNotLoaded;
            public int SkippedOutOfWorld;
            public int Failed;
            public int RecalcChunks;
            public double ElapsedMs;
            public string Warning = "";
            public string Error = "";

            public string Describe() {
                var sb = new StringBuilder();
                sb.Append(Error.Length > 0 ? "ERROR" : "ok");
                sb.Append(" op=").Append(Op);
                if (Name.Length > 0) {
                    sb.Append(" name=").Append(Name);
                }
                if (Box.Length > 0) {
                    sb.Append(" box=").Append(Box);
                }
                sb.Append(" voxels=").Append(Voxels)
                    .Append(" written=").Append(Written)
                    .Append(" air=").Append(WrittenAir)
                    .Append(" skipAir=").Append(SkippedAir)
                    .Append(" skipOccupied=").Append(SkippedOccupied)
                    .Append(" skipNotLoaded=").Append(SkippedNotLoaded)
                    .Append(" skipOutside=").Append(SkippedOutOfWorld)
                    .Append(" failed=").Append(Failed)
                    .Append(" recalcChunks=").Append(RecalcChunks)
                    .Append(" ms=").Append(ElapsedMs.ToString("0.0"));
                if (Warning.Length > 0) {
                    sb.Append(" warning=").Append(Warning);
                }
                if (Error.Length > 0) {
                    sb.Append(" error=").Append(Error);
                }
                return sb.ToString();
            }

            public JsonObject ToJson() => new() {
                ["ok"] = Error.Length == 0,
                ["op"] = Op,
                ["name"] = Name,
                ["box"] = Box,
                ["voxels"] = Voxels,
                ["written"] = Written,
                ["writtenAir"] = WrittenAir,
                ["skippedAir"] = SkippedAir,
                ["skippedOccupied"] = SkippedOccupied,
                ["skippedNotLoaded"] = SkippedNotLoaded,
                ["skippedOutOfWorld"] = SkippedOutOfWorld,
                ["failed"] = Failed,
                ["recalcChunks"] = RecalcChunks,
                ["elapsedMs"] = Math.Round(ElapsedMs, 1),
                ["warning"] = Warning.Length > 0 ? Warning : null,
                ["error"] = Error.Length > 0 ? Error : null,
            };
        }

        static readonly Dictionary<string, Blueprint> m_blueprints = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一次操作的结果（成功/失败都在）。</summary>
        public static Result LastResult { get; private set; } = new() { Op = "none" };

        // ============================================================================================
        // 对内工具
        // ============================================================================================

        static SubsystemTerrain SubsystemTerrain() => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);

        static Terrain GetTerrain(out SubsystemTerrain subsystemTerrain, out string error) {
            error = null;
            subsystemTerrain = SubsystemTerrain();
            if (subsystemTerrain == null) {
                error = "no SubsystemTerrain (世界还没加载完？)";
                return null;
            }
            Terrain terrain = subsystemTerrain.Terrain;
            if (terrain == null) {
                error = "no Terrain";
                return null;
            }
            return terrain;
        }

        static string Error(string op, string message) {
            LastResult = new Result { Op = op, Error = message };
            return "ERROR: " + message;
        }

        static void Normalize(ref int x1, ref int y1, ref int z1, ref int x2, ref int y2, ref int z2) {
            if (x1 > x2) {
                (x1, x2) = (x2, x1);
            }
            if (y1 > y2) {
                (y1, y2) = (y2, y1);
            }
            if (z1 > z2) {
                (z1, z2) = (z2, z1);
            }
        }

        static string BoxText(int x1, int y1, int z1, int x2, int y2, int z2) =>
            $"[{x1},{y1},{z1}]..[{x2},{y2},{z2}]";

        /// <summary>该目标格现在能不能写（列是否在内存里）。返回 null = 可以写，否则是"不能写"的原因分类。</summary>
        static string WriteBlocker(Terrain terrain, int x, int y, int z) {
            if (!terrain.IsCellValid(x, y, z)) {
                return "outOfWorld";
            }
            TerrainChunk chunk = terrain.GetChunkAtCell(x, z);
            if (chunk == null) {
                return "notAllocated";
            }
            if (chunk.ThreadState < TerrainChunkState.InvalidLight) {
                return "contentsNotLoaded";
            }
            return null;
        }

        /// <summary>
        /// 回读校验口径：**contents + data** 一致就算写成功。
        /// 故意**不比较 light**：`ChangeCell` 之后引擎会按周围环境重算光照，
        /// 我们写进去的 block value 里的 light 位是旧值，逐位比较会把正常写入误判成 failed。
        /// </summary>
        static bool SameBlockIgnoringLight(int actual, int expected) =>
            Terrain.ExtractContents(actual) == Terrain.ExtractContents(expected)
            && Terrain.ExtractData(actual) == Terrain.ExtractData(expected);

        /// <summary>一片区域写完之后的必做步骤：让光照/几何失效并重算（等价于桥的 op:recalc）。</summary>
        static int Recalc(SubsystemTerrain subsystemTerrain, HashSet<long> chunkColumns) {
            if (subsystemTerrain?.TerrainUpdater == null || chunkColumns == null) {
                return 0;
            }
            int count = 0;
            foreach (long key in chunkColumns) {
                int cx = (int)(key >> 32);
                int cz = (int)(key & 0xFFFFFFFFL);
                subsystemTerrain.TerrainUpdater.DowngradeChunkNeighborhoodState(
                    new Point2(cx, cz), 1, TerrainChunkState.InvalidLight, false);
                count++;
            }
            return count;
        }

        static long ChunkKey(int x, int z) =>
            ((long)(x >> TerrainChunk.SizeBits) << 32) | (uint)(z >> TerrainChunk.SizeBits);

        // ============================================================================================
        // 状态查询
        // ============================================================================================

        /// <summary>一行状态（桥的根对象入口）。</summary>
        public static string Describe() =>
            $"SkylineBlueprint v{FormatVersion} blueprints={m_blueprints.Count} dir={Directory} last=[{LastResult.Describe()}]";

        /// <summary>最近一次操作的详细 JSON。</summary>
        public static string LastResultJson() => LastResult.ToJson().ToJsonString();

        /// <summary>所有蓝图的名字/尺寸/体素数（多行）。</summary>
        public static string List() {
            if (m_blueprints.Count == 0) {
                return "(no blueprints)";
            }
            var sb = new StringBuilder();
            foreach (Blueprint bp in m_blueprints.Values) {
                sb.AppendLine(bp.Describe());
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>某个蓝图的详情（含前 8 个体素，便于肉眼确认体素是不是空的）。</summary>
        public static string Info(string name) {
            Blueprint bp = Find(name);
            if (bp == null) {
                return $"ERROR: no blueprint named '{name}'";
            }
            var sb = new StringBuilder();
            sb.AppendLine(bp.Describe());
            sb.AppendLine($"origin(relative) = (0,0,0) .. ({bp.SizeX - 1},{bp.SizeY - 1},{bp.SizeZ - 1})");
            int shown = 0;
            foreach (Voxel v in bp.Voxels) {
                if (shown++ >= 8) {
                    sb.AppendLine($"... ({bp.Count - 8} more)");
                    break;
                }
                sb.AppendLine($"  ({v.X},{v.Y},{v.Z}) value={v.Value} contents={Terrain.ExtractContents(v.Value)} data={Terrain.ExtractData(v.Value)}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>删掉一个内存里的蓝图（不影响已经导出的文件）。</summary>
        public static string Forget(string name) =>
            m_blueprints.Remove(name ?? "") ? $"ok removed '{name}'" : $"ERROR: no blueprint named '{name}'";

        static Blueprint Find(string name) =>
            name != null && m_blueprints.TryGetValue(name, out Blueprint bp) ? bp : null;

        // ============================================================================================
        // 捕获
        // ============================================================================================

        /// <summary>
        /// 把世界里的长方体区域存成蓝图（名字重复会**覆盖**）。
        /// `includeAir=true` 时空气格也会被存下来，贴回去时可以"挖空"目标区域（默认 false：只存非空气）。
        /// </summary>
        public static string Capture(string name,
            int x1, int y1, int z1,
            int x2, int y2, int z2,
            bool includeAir = false,
            bool force = false) {
            if (string.IsNullOrWhiteSpace(name)) {
                return Error("Capture", "name is required");
            }
            Terrain terrain = GetTerrain(out _, out string error);
            if (terrain == null) {
                return Error("Capture", error);
            }
            try {
                Normalize(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                long scan = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
                if (scan > MaxScanCells) {
                    return Error("Capture", $"region too big to scan: {scan} cells (max {MaxScanCells})");
                }
                var bp = new Blueprint {
                    Name = name,
                    SizeX = x2 - x1 + 1,
                    SizeY = y2 - y1 + 1,
                    SizeZ = z2 - z1 + 1,
                    CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Source = BoxText(x1, y1, z1, x2, y2, z2),
                };
                var result = new Result { Op = "Capture", Name = name, Box = bp.Source };
                double t0 = Time.RealTime;
                for (int x = x1; x <= x2; x++) {
                    for (int y = y1; y <= y2; y++) {
                        for (int z = z1; z <= z2; z++) {
                            if (!terrain.IsCellValid(x, y, z)) {
                                result.SkippedOutOfWorld++;
                                continue;
                            }
                            string blocker = WriteBlocker(terrain, x, y, z);
                            if (blocker != null && !force) {
                                result.SkippedNotLoaded++;
                                continue;
                            }
                            int value = terrain.GetCellValue(x, y, z);
                            if (Terrain.ExtractContents(value) == 0) {
                                if (!includeAir) {
                                    result.SkippedAir++;
                                    continue;
                                }
                            }
                            bp.Voxels.Add(new Voxel { X = x - x1, Y = y - y1, Z = z - z1, Value = value });
                        }
                    }
                }
                result.Voxels = bp.Count;
                result.Written = bp.Count;
                if (bp.Count == 0) {
                    return Error("Capture", $"nothing captured from {bp.Source} (region empty / not loaded?)");
                }
                if (result.SkippedNotLoaded > 0) {
                    result.Warning = $"{result.SkippedNotLoaded} cells were in NOT-loaded columns and were skipped; "
                        + "turn on SkylineRuntime.ChunkResidencyMode (residency region) if you need them";
                }
                m_blueprints[name] = bp;
                result.ElapsedMs = (Time.RealTime - t0) * 1000.0;
                LastResult = result;
                return result.Describe();
            }
            catch (Exception e) {
                return Error("Capture", e.Message);
            }
        }

        /// <summary>按"最小角 + 尺寸"捕获（给脚本用，语义和 <see cref="Capture"/> 一样）。</summary>
        public static string CaptureAt(string name,
            int x, int y, int z,
            int sizeX, int sizeY, int sizeZ,
            bool includeAir = false,
            bool force = false) =>
            Capture(name, x, y, z, x + sizeX - 1, y + sizeY - 1, z + sizeZ - 1, includeAir, force);

        // ============================================================================================
        // 放置 / 复制
        // ============================================================================================

        /// <summary>
        /// 把蓝图贴回世界：`(x,y,z)` 是蓝图的**最小角**（相对坐标 (0,0,0)）落点。
        /// `rotation`：0/90/180/270（绕 Y 轴，顺时针；其它值会被吸附到最近的 90°）；
        /// `mirrorX` / `mirrorZ`：先镜像再旋转；`onlyAir=true` 只填空气位（"盖在房子上不破坏原物"）；
        /// `writeAir=false` 时不写蓝图里的空气格（默认写，等于"连挖空一起还原"）。
        /// </summary>
        public static string Paste(string name,
            int x, int y, int z,
            int rotation = 0,
            bool mirrorX = false,
            bool mirrorZ = false,
            bool onlyAir = false,
            bool writeAir = true,
            bool recalc = true,
            bool force = false) {
            Blueprint bp = Find(name);
            if (bp == null) {
                return Error("Paste", $"no blueprint named '{name}' (先 Capture/Import)");
            }
            return PasteInternal(bp, x, y, z, rotation, mirrorX, mirrorZ, onlyAir, writeAir, recalc, force);
        }

        /// <summary>
        /// 一步到位：把世界里的一个区域**复制**到另一个位置（不用先起名字），
        /// 支持同样的旋转/镜像/只填空位。适合"这面墙照那样再砌一遍"。
        /// </summary>
        public static string Copy(
            int x1, int y1, int z1, int x2, int y2, int z2,
            int toX, int toY, int toZ,
            int rotation = 0,
            bool mirrorX = false,
            bool mirrorZ = false,
            bool onlyAir = false,
            bool includeAir = false,
            bool force = false) {
            Terrain terrain = GetTerrain(out _, out string error);
            if (terrain == null) {
                return Error("Copy", error);
            }
            try {
                Normalize(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                long scan = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
                if (scan > MaxScanCells) {
                    return Error("Copy", $"source region too big: {scan} cells");
                }
                var bp = new Blueprint {
                    Name = "(copy)",
                    SizeX = x2 - x1 + 1,
                    SizeY = y2 - y1 + 1,
                    SizeZ = z2 - z1 + 1,
                    CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Source = BoxText(x1, y1, z1, x2, y2, z2),
                };
                for (int sx = x1; sx <= x2; sx++) {
                    for (int sy = y1; sy <= y2; sy++) {
                        for (int sz = z1; sz <= z2; sz++) {
                            if (WriteBlocker(terrain, sx, sy, sz) != null && !force) {
                                continue;
                            }
                            int value = terrain.GetCellValue(sx, sy, sz);
                            if (Terrain.ExtractContents(value) == 0 && !includeAir) {
                                continue;
                            }
                            bp.Voxels.Add(new Voxel { X = sx - x1, Y = sy - y1, Z = sz - z1, Value = value });
                        }
                    }
                }
                if (bp.Count == 0) {
                    return Error("Copy", $"source region {bp.Source} is empty (or not loaded)");
                }
                string paste = PasteInternal(bp, toX, toY, toZ, rotation, mirrorX, mirrorZ, onlyAir, true, true, force);
                if (LastResult != null) {
                    LastResult.Op = "Copy";
                    LastResult.Box = $"{bp.Source} -> [{toX},{toY},{toZ}] rot={((rotation % 360) + 360) % 360} "
                        + $"mirrorX={mirrorX} mirrorZ={mirrorZ}";
                }
                return paste;
            }
            catch (Exception e) {
                return Error("Copy", e.Message);
            }
        }

        /// <summary>就地镜像一个区域（以区域中心平面为轴）。`axis` = "x" 或 "z"。</summary>
        public static string MirrorRegion(string axis,
            int x1, int y1, int z1, int x2, int y2, int z2,
            bool includeAir = true) {
            string a = (axis ?? "x").Trim().ToLowerInvariant();
            if (a != "x" && a != "z") {
                return Error("MirrorRegion", "axis must be \"x\" or \"z\"");
            }
            Terrain terrain = GetTerrain(out _, out string error);
            if (terrain == null) {
                return Error("MirrorRegion", error);
            }
            try {
                Normalize(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                var bp = new Blueprint {
                    Name = "(mirror)",
                    SizeX = x2 - x1 + 1,
                    SizeY = y2 - y1 + 1,
                    SizeZ = z2 - z1 + 1,
                    CapturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Source = BoxText(x1, y1, z1, x2, y2, z2),
                };
                for (int x = x1; x <= x2; x++) {
                    for (int y = y1; y <= y2; y++) {
                        for (int z = z1; z <= z2; z++) {
                            if (!terrain.IsCellValid(x, y, z)) {
                                continue;
                            }
                            int value = terrain.GetCellValue(x, y, z);
                            if (Terrain.ExtractContents(value) == 0 && !includeAir) {
                                continue;
                            }
                            bp.Voxels.Add(new Voxel { X = x - x1, Y = y - y1, Z = z - z1, Value = value });
                        }
                    }
                }
                if (bp.Count == 0) {
                    return Error("MirrorRegion", $"region {bp.Source} is empty");
                }
                bool mirrorX = a == "x";
                string paste = PasteInternal(bp, x1, y1, z1, 0, mirrorX, !mirrorX, false, true, true, true);
                if (LastResult != null) {
                    LastResult.Op = "MirrorRegion";
                    LastResult.Box = $"{bp.Source} axis={a}";
                }
                return paste;
            }
            catch (Exception e) {
                return Error("MirrorRegion", e.Message);
            }
        }

        static string PasteInternal(Blueprint bp,
            int x, int y, int z,
            int rotation,
            bool mirrorX, bool mirrorZ,
            bool onlyAir, bool writeAir, bool recalc, bool force) {
            Terrain terrain = GetTerrain(out SubsystemTerrain subsystemTerrain, out string error);
            if (terrain == null) {
                return Error("Paste", error);
            }
            try {
                if (bp.Count > MaxVoxels) {
                    return Error("Paste", $"blueprint has {bp.Count} voxels (max {MaxVoxels})");
                }
                int rot = ((rotation % 360) + 360) % 360;
                rot = ((int)MathF.Round(rot / 90f) * 90) % 360;
                int sizeX = bp.SizeX;
                int sizeZ = bp.SizeZ;
                var result = new Result {
                    Op = "Paste",
                    Name = bp.Name,
                    Voxels = bp.Count,
                    Box = $"[{x},{y},{z}] rot={rot} mirrorX={mirrorX} mirrorZ={mirrorZ} onlyAir={onlyAir}",
                };
                var chunkColumns = new HashSet<long>();
                double t0 = Time.RealTime;
                foreach (Voxel v in bp.Voxels) {
                    int rx = v.X;
                    int rz = v.Z;
                    if (mirrorX) {
                        rx = sizeX - 1 - rx;
                    }
                    if (mirrorZ) {
                        rz = sizeZ - 1 - rz;
                    }
                    switch (rot) {
                        case 90: {
                            (rx, rz) = (sizeZ - 1 - rz, rx);
                            break;
                        }
                        case 180: {
                            rx = sizeX - 1 - rx;
                            rz = sizeZ - 1 - rz;
                            break;
                        }
                        case 270: {
                            (rx, rz) = (rz, sizeX - 1 - rx);
                            break;
                        }
                    }
                    int tx = x + rx;
                    int ty = y + v.Y;
                    int tz = z + rz;
                    bool isAir = Terrain.ExtractContents(v.Value) == 0;
                    if (isAir && !writeAir) {
                        result.SkippedAir++;
                        continue;
                    }
                    if (!terrain.IsCellValid(tx, ty, tz)) {
                        result.SkippedOutOfWorld++;
                        continue;
                    }
                    string blocker = WriteBlocker(terrain, tx, ty, tz);
                    if (blocker != null && !force) {
                        result.SkippedNotLoaded++;
                        continue;
                    }
                    if (onlyAir && Terrain.ExtractContents(terrain.GetCellValue(tx, ty, tz)) != 0) {
                        result.SkippedOccupied++;
                        continue;
                    }
                    subsystemTerrain.ChangeCell(tx, ty, tz, v.Value);
                    if (SameBlockIgnoringLight(terrain.GetCellValue(tx, ty, tz), v.Value)) {
                        if (isAir) {
                            result.WrittenAir++;
                        }
                        else {
                            result.Written++;
                        }
                        chunkColumns.Add(ChunkKey(tx, tz));
                    }
                    else {
                        result.Failed++;
                    }
                }
                if (recalc) {
                    result.RecalcChunks = Recalc(subsystemTerrain, chunkColumns);
                }
                if (result.SkippedNotLoaded > 0) {
                    result.Warning = $"{result.SkippedNotLoaded} target cells are in NOT-loaded chunk columns "
                        + "(writes there would be silently dropped). Turn on SkylineRuntime.ChunkResidencyMode "
                        + "+ AddResidencyRegion for the destination area, or pass force=true.";
                }
                if (result.Failed > 0) {
                    result.Warning = (result.Warning.Length > 0 ? result.Warning + " | " : "")
                        + $"{result.Failed} writes did not stick (read-back mismatch)";
                }
                result.ElapsedMs = (Time.RealTime - t0) * 1000.0;
                LastResult = result;
                return result.Describe();
            }
            catch (Exception e) {
                return Error("Paste", e.Message);
            }
        }

        // ============================================================================================
        // 填充 / 替换
        // ============================================================================================

        /// <summary>
        /// 整片填充（走 ChangeCell + 回读校验 + 光照重算）。`onlyAir=true` 时只填空气位。
        /// 这是"地板/墙/屋顶一次成型"的最常用入口。
        /// </summary>
        public static string Fill(
            int x1, int y1, int z1,
            int x2, int y2, int z2,
            int contents, int data = 0,
            bool onlyAir = false,
            bool recalc = true,
            bool force = false) {
            Terrain terrain = GetTerrain(out SubsystemTerrain subsystemTerrain, out string error);
            if (terrain == null) {
                return Error("Fill", error);
            }
            int value = Terrain.MakeBlockValue(contents, 0, data);
            return FillInternal(terrain, subsystemTerrain, value, x1, y1, z1, x2, y2, z2, onlyAir, recalc, force);
        }

        static string FillInternal(Terrain terrain, SubsystemTerrain subsystemTerrain, int value,
            int x1, int y1, int z1, int x2, int y2, int z2,
            bool onlyAir, bool recalc, bool force) {
            try {
                Normalize(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                long volume = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
                if (volume > MaxVoxels) {
                    return Error("Fill", $"volume {volume} exceeds max {MaxVoxels}");
                }
                var result = new Result {
                    Op = "Fill",
                    Voxels = (int)Math.Min(volume, int.MaxValue),
                    Box = BoxText(x1, y1, z1, x2, y2, z2),
                };
                var chunkColumns = new HashSet<long>();
                double t0 = Time.RealTime;
                for (int x = x1; x <= x2; x++) {
                    for (int y = y1; y <= y2; y++) {
                        for (int z = z1; z <= z2; z++) {
                            if (!terrain.IsCellValid(x, y, z)) {
                                result.SkippedOutOfWorld++;
                                continue;
                            }
                            if (WriteBlocker(terrain, x, y, z) != null && !force) {
                                result.SkippedNotLoaded++;
                                continue;
                            }
                            if (onlyAir && Terrain.ExtractContents(terrain.GetCellValue(x, y, z)) != 0) {
                                result.SkippedOccupied++;
                                continue;
                            }
                            subsystemTerrain.ChangeCell(x, y, z, value);
                            if (SameBlockIgnoringLight(terrain.GetCellValue(x, y, z), value)) {
                                result.Written++;
                                chunkColumns.Add(ChunkKey(x, z));
                            }
                            else {
                                result.Failed++;
                            }
                        }
                    }
                }
                if (recalc) {
                    result.RecalcChunks = Recalc(subsystemTerrain, chunkColumns);
                }
                if (result.SkippedNotLoaded > 0) {
                    result.Warning = $"{result.SkippedNotLoaded} cells are in NOT-loaded chunk columns (skipped; "
                        + "turn on SkylineRuntime.ChunkResidencyMode for the target area)";
                }
                result.ElapsedMs = (Time.RealTime - t0) * 1000.0;
                LastResult = result;
                return result.Describe();
            }
            catch (Exception e) {
                return Error("Fill", e.Message);
            }
        }

        /// <summary>
        /// 材质替换：把区域里所有 `fromContents` 的方块换成 `toContents`（可带 data）。
        /// 别墅改造（换地板/换墙面）用它比"逐格 act"快几个数量级。
        /// </summary>
        public static string Replace(
            int x1, int y1, int z1,
            int x2, int y2, int z2,
            int fromContents, int toContents, int toData = 0,
            bool recalc = true,
            bool force = false) {
            Terrain terrain = GetTerrain(out SubsystemTerrain subsystemTerrain, out string error);
            if (terrain == null) {
                return Error("Replace", error);
            }
            try {
                Normalize(ref x1, ref y1, ref z1, ref x2, ref y2, ref z2);
                long volume = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
                if (volume > MaxVoxels) {
                    return Error("Replace", $"volume {volume} exceeds max {MaxVoxels}");
                }
                int newValue = Terrain.MakeBlockValue(toContents, 0, toData);
                var result = new Result {
                    Op = "Replace",
                    Voxels = (int)Math.Min(volume, int.MaxValue),
                    Box = BoxText(x1, y1, z1, x2, y2, z2),
                };
                var chunkColumns = new HashSet<long>();
                double t0 = Time.RealTime;
                for (int x = x1; x <= x2; x++) {
                    for (int y = y1; y <= y2; y++) {
                        for (int z = z1; z <= z2; z++) {
                            if (!terrain.IsCellValid(x, y, z)) {
                                result.SkippedOutOfWorld++;
                                continue;
                            }
                            if (WriteBlocker(terrain, x, y, z) != null && !force) {
                                result.SkippedNotLoaded++;
                                continue;
                            }
                            if (Terrain.ExtractContents(terrain.GetCellValue(x, y, z)) != fromContents) {
                                result.SkippedOccupied++;
                                continue;
                            }
                            subsystemTerrain.ChangeCell(x, y, z, newValue);
                            if (SameBlockIgnoringLight(terrain.GetCellValue(x, y, z), newValue)) {
                                result.Written++;
                                chunkColumns.Add(ChunkKey(x, z));
                            }
                            else {
                                result.Failed++;
                            }
                        }
                    }
                }
                if (recalc) {
                    result.RecalcChunks = Recalc(subsystemTerrain, chunkColumns);
                }
                if (result.SkippedNotLoaded > 0) {
                    result.Warning = $"{result.SkippedNotLoaded} cells are in NOT-loaded chunk columns (skipped; "
                        + "turn on SkylineRuntime.ChunkResidencyMode for the target area)";
                }
                result.ElapsedMs = (Time.RealTime - t0) * 1000.0;
                LastResult = result;
                return result.Describe();
            }
            catch (Exception e) {
                return Error("Replace", e.Message);
            }
        }

        // ============================================================================================
        // 导入 / 导出（JSON 文件）
        // ============================================================================================

        static string ResolvePath(string name, string path) {
            if (!string.IsNullOrWhiteSpace(path)) {
                return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
            }
            return Path.Combine(Directory, name + ".json");
        }

        /// <summary>把蓝图写成 JSON 文件（默认 `&lt;游戏目录&gt;/Blueprints/&lt;name&gt;.json`）。</summary>
        public static string Export(string name, string path = "") {
            Blueprint bp = Find(name);
            if (bp == null) {
                return $"ERROR: no blueprint named '{name}'";
            }
            try {
                string file = ResolvePath(bp.Name, path);
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir)) {
                    System.IO.Directory.CreateDirectory(dir);
                }
                var voxels = new JsonArray();
                foreach (Voxel v in bp.Voxels) {
                    voxels.Add(new JsonArray(v.X, v.Y, v.Z, v.Value));
                }
                var root = new JsonObject {
                    ["version"] = FormatVersion,
                    ["name"] = bp.Name,
                    ["size"] = new JsonArray(bp.SizeX, bp.SizeY, bp.SizeZ),
                    ["capturedAt"] = bp.CapturedAt,
                    ["source"] = bp.Source,
                    ["count"] = bp.Count,
                    ["voxels"] = voxels,
                };
                File.WriteAllText(file, root.ToJsonString(), new UTF8Encoding(false));
                LastResult = new Result {
                    Op = "Export",
                    Name = bp.Name,
                    Voxels = bp.Count,
                    Written = bp.Count,
                    Box = file,
                };
                return $"ok exported '{bp.Name}' ({bp.Count} voxels, {bp.SizeText}) -> {file}";
            }
            catch (Exception e) {
                return Error("Export", e.Message);
            }
        }

        /// <summary>从 JSON 文件读蓝图进内存（`name` 留空就用文件里的名字）。</summary>
        public static string Import(string path, string name = "") {
            try {
                string file = ResolvePath(name, path);
                if (!File.Exists(file)) {
                    return Error("Import", $"file not found: {file}");
                }
                JsonNode node = JsonNode.Parse(File.ReadAllText(file));
                if (node is not JsonObject root) {
                    return Error("Import", "not a JSON object");
                }
                var size = root["size"] as JsonArray;
                if (size == null || size.Count < 3) {
                    return Error("Import", "missing size");
                }
                var bp = new Blueprint {
                    Name = !string.IsNullOrWhiteSpace(name)
                        ? name
                        : (root["name"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file)),
                    SizeX = size[0].GetValue<int>(),
                    SizeY = size[1].GetValue<int>(),
                    SizeZ = size[2].GetValue<int>(),
                    CapturedAt = root["capturedAt"]?.GetValue<string>() ?? "",
                    Source = root["source"]?.GetValue<string>() ?? file,
                };
                if (root["voxels"] is JsonArray voxels) {
                    foreach (JsonNode item in voxels) {
                        if (item is not JsonArray a || a.Count < 4) {
                            continue;
                        }
                        bp.Voxels.Add(new Voxel {
                            X = a[0].GetValue<int>(),
                            Y = a[1].GetValue<int>(),
                            Z = a[2].GetValue<int>(),
                            Value = a[3].GetValue<int>(),
                        });
                    }
                }
                if (bp.Count == 0) {
                    return Error("Import", $"'{file}' has no voxels");
                }
                m_blueprints[bp.Name] = bp;
                LastResult = new Result {
                    Op = "Import",
                    Name = bp.Name,
                    Voxels = bp.Count,
                    Written = bp.Count,
                    Box = file,
                };
                return $"ok imported '{bp.Name}' ({bp.Count} voxels, {bp.SizeText}) from {file}";
            }
            catch (Exception e) {
                return Error("Import", e.Message);
            }
        }

        /// <summary>列出蓝图目录里的 .json 文件（不含内存里的蓝图）。</summary>
        public static string ListFiles() {
            try {
                if (!System.IO.Directory.Exists(Directory)) {
                    return $"(no directory yet: {Directory})";
                }
                var sb = new StringBuilder();
                foreach (string file in System.IO.Directory.GetFiles(Directory, "*.json")) {
                    var info = new FileInfo(file);
                    sb.AppendLine($"{Path.GetFileNameWithoutExtension(file)} {info.Length} B {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                }
                string text = sb.ToString().TrimEnd();
                return text.Length > 0 ? text : "(empty)";
            }
            catch (Exception e) {
                return "ERROR: " + e.Message;
            }
        }
    }
}
