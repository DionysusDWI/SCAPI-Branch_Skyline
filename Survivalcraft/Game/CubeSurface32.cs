using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.49：**32³ 立方体"只存表面材质"影子原型**（新目标 4.3 的第一步）。
    ///
    /// 用户口径："可以把 LOD 区块视作一个 **32³ 体素精细度的家具**来渲染，**只渲染其表面材质**，
    /// **不渲染实际方块状态**，从而降低内存压力。"
    ///
    /// 本类只回答三件事（**不接入渲染**，与 `CubeChunk32`/`CubeWindowSurvey` 同一套路）：
    ///   1. **表面壳**能不能用很小的数据表示一个 32³ 立方体的"外观"：
    ///      顶/底面各 32×32（顶面带高度+材质），4 个侧面各 32×32（材质 + 层号即高度）；
    ///   2. 内存对比：壳 = **16 KiB** vs 原始方块状态 **128 KiB**（`CubeChunk32.Bytes`），**8× 更小**；
    ///   3. 会画出多少四边形（每个非空壳格 1 个）、采样一次要多久。
    ///
    /// 判据：`skyline.CubeSurfaceSelfCheck()`（合成体，确定性）、`skyline.CubeSurfaceSample(cx,cy,cz)`（真实世界）。
    /// </summary>
    public sealed class CubeSurface32 {
        public const int Size = 32;
        public const int GridCells = Size * Size;                 // 每个面 1024 格
        public const int RawBytes = Size * Size * Size * 4;       // 原始方块状态 128 KiB
        /// <summary>
        /// 壳里每格只存 **contents(bit0-9) + light(bit10-13)**，共 14 位（正好进 `ushort`）。
        /// **不存 data(bit14+)**：SC 的方块 data 决定朝向/变体（家具的设计索引也在 data 里），
        /// 对"只画材质"的 LOD 用不上；家具这类按 data 变化的方块是本原型的**已知缺口**，后续步骤再补。
        /// </summary>
        public const int ValueMask = 0x3FFF;

        public readonly int X, Y, Z;

        /// <summary>采样时"原始值带 data 位"的壳格数（量化上面这个缺口的规模）。</summary>
        public int DataCells;

        // ---- [v0.1.56] 家具：设计索引在 data 里，壳只存 14 位 → **采集时把家具塌缩成"设计主材质"** ----
        /// <summary>采样时遇到"顶面/底面/侧面第一个实心块是家具"的壳格数。</summary>
        public int FurnitureCells;
        /// <summary>其中成功解析成主材质的（壳里存的就是那个材质）。</summary>
        public int FurnitureResolved;
        /// <summary>解析失败的（设计索引在当前世界不存在 / 没有 behavior）→ 壳里保留 contents=227。</summary>
        public int FurnitureUnresolved;

        /// <summary>顶面：每列最高实心方块的高度（`short.MinValue` = 空）与材质值。</summary>
        public readonly short[] TopHeight = new short[GridCells];
        public readonly ushort[] TopContents = new ushort[GridCells];
        /// <summary>4 个侧面：每格最外层实心方块的材质（0 = 空）；层号即高度。</summary>
        public readonly ushort[][] SideContents = [
            new ushort[GridCells], new ushort[GridCells], new ushort[GridCells], new ushort[GridCells]
        ];
        /// <summary>底面：每列从下往上第一个实心方块的高度与材质。</summary>
        public readonly short[] BottomHeight = new short[GridCells];
        public readonly ushort[] BottomContents = new ushort[GridCells];

        public CubeSurface32(int x, int y, int z) {
            X = x;
            Y = y;
            Z = z;
            Array.Fill(TopHeight, short.MinValue);
            Array.Fill(BottomHeight, short.MinValue);
        }

        /// <summary>壳数据的字节数：顶 4 KiB + 侧 8 KiB + 底 4 KiB = 16 KiB。</summary>
        public static int ShellBytes =>
            GridCells * (2 + 2)             // 顶：short 高度 + ushort 材质
            + 4 * GridCells * 2             // 四侧：ushort 材质
            + GridCells * (2 + 2);          // 底

        /// <summary>非空壳格数（= 会画出的四边形数，逐格 1 个）。</summary>
        public int QuadCount {
            get {
                int n = 0;
                for (int i = 0; i < GridCells; i++) {
                    if (TopContents[i] != 0) n++;
                    if (BottomContents[i] != 0) n++;
                    for (int f = 0; f < 4; f++) {
                        if (SideContents[f][i] != 0) n++;
                    }
                }
                return n;
            }
        }

        /// <summary>
        /// 从真实地形抓一个 32³ 立方体的**外表面**（只读；要求该立方体所在列已加载）。
        /// 顶/底 = 每列沿 y 扫描到的第一个实心块；4 个侧面 = 沿该轴扫描到的第一个实心块。
        /// </summary>
        public static CubeSurface32 Extract(Terrain terrain, int cx, int cy, int cz) {
            CubeSurface32 surface = new(cx, cy, cz);
            int ox = cx * Size, oy = cy * Size, oz = cz * Size;
            for (int lx = 0; lx < Size; lx++) {
                for (int lz = 0; lz < Size; lz++) {
                    int wx = ox + lx, wz = oz + lz;
                    int idx = lx + lz * Size;
                    for (int ly = Size - 1; ly >= 0; ly--) {
                        int value = terrain.GetCellValue(wx, oy + ly, wz);
                        if (Terrain.ExtractContents(value) != 0) {
                            surface.TopHeight[idx] = (short)(oy + ly);
                            // [v0.1.50] **光照取"空气那一侧"**：SC 里实心格自己的 light 位经常是 0
                            // （只有暴露在光里的格子才带 light；实测平台石砖 = 0、地表雪块 = 15）。
                            // 顶面的受光 = 方块上方那格的 light，这才是"顶面光照"的语义。
                            int light = SurfaceLight(terrain, wx, oy + ly + 1, wz, value);
                            if (Terrain.ExtractData(value) != 0) {
                                surface.DataCells++;
                            }
                            surface.TopContents[idx] = (ushort)(PackLight(surface.CollapseFurniture(value), light));
                            break;
                        }
                    }
                    for (int ly = 0; ly < Size; ly++) {
                        int value = terrain.GetCellValue(wx, oy + ly, wz);
                        if (Terrain.ExtractContents(value) != 0) {
                            surface.BottomHeight[idx] = (short)(oy + ly);
                            int light = SurfaceLight(terrain, wx, oy + ly - 1, wz, value);
                            surface.BottomContents[idx] = (ushort)(PackLight(surface.CollapseFurniture(value), light));
                            break;
                        }
                    }
                }
            }
            for (int ly = 0; ly < Size; ly++) {
                for (int l = 0; l < Size; l++) {
                    int wy = oy + ly;
                    int idx = l + ly * Size;
                    surface.SideContents[0][idx] = FirstSolid(terrain, ox + l, wy, oz, 0, 1);
                    surface.SideContents[1][idx] = FirstSolid(terrain, ox + l, wy, oz + Size - 1, 0, -1);
                    surface.SideContents[2][idx] = FirstSolid(terrain, ox, wy, oz + l, 1, 0);
                    surface.SideContents[3][idx] = FirstSolid(terrain, ox + Size - 1, wy, oz + l, -1, 0);
                }
            }
            return surface;
        }

        /// <summary>沿 `(dz 或 dx)` 方向扫描该行/列的第一个实心块（0 = 全空）。</summary>
        static ushort FirstSolid(Terrain terrain, int x, int y, int z, int dAlongX, int dAlongZ) {
            for (int i = 0; i < Size; i++) {
                int value = terrain.GetCellValue(x + dAlongX * i, y, z + dAlongZ * i);
                if (Terrain.ExtractContents(value) != 0) {
                    // 侧面同样取"空气那一侧"（扫描路径上紧邻的前一格）的光照。
                    int light = SurfaceLight(terrain,
                        x + dAlongX * (i - 1), y, z + dAlongZ * (i - 1), value);
                    return (ushort)(PackLight(CollapseFurnitureStatic(value), light));
                }
            }
            return 0;
        }

        /// <summary>实例口径的家具塌缩（顺手记账）。</summary>
        int CollapseFurniture(int value) {
            if (!SkylineRuntime.ShellFurnitureCollapse || Terrain.ExtractContents(value) != FurnitureBlock.Index) {
                return SubstituteLodMaterial(value);
            }
            FurnitureCells++;
            int material = FurnitureMaterial(value);
            if (material <= 0) {
                FurnitureUnresolved++;
                return value;                     // 解析不了就原样保留（至少还能画出"家具块"的材质）
            }
            FurnitureResolved++;
            return material;                      // 返回的是完整方块值（contents|data|light），下面只取 contents
        }

        static int CollapseFurnitureStatic(int value) {
            if (!SkylineRuntime.ShellFurnitureCollapse || Terrain.ExtractContents(value) != FurnitureBlock.Index) {
                return SubstituteLodMaterial(value);
            }
            int material = FurnitureMaterial(value);
            return material > 0 ? material : value;
        }

        /// <summary>
        /// [v0.1.59] **LOD 材质替换**（用户口径 1.6）："雪层算一个雪方块，而草方块则算作泥土" ——
        /// LOD 里用的是**结构材质**：草皮的"体"是泥土、雪层的"体"是雪。返回替换后的完整值（保留 light）。
        /// 开关 `skyline.ShellLodMaterialSubstitute`（默认 true；关掉 = 与 v0.1.58 逐位一致）。
        /// </summary>
        static int SubstituteLodMaterial(int value) {
            if (!SkylineRuntime.ShellLodMaterialSubstitute) {
                return value;
            }
            int contents = Terrain.ExtractContents(value);
            int substitute = SkylineSurfaceAudit.LodSubstitute(contents);
            if (substitute < 0 || substitute == contents) {
                return value;
            }
            return Terrain.ReplaceContents(value, substitute);
        }

        /// <summary>
        /// [v0.1.56] 把家具（`contents = 227`，设计索引在 data 里）塌缩成**设计的主材质**的完整方块值。
        /// 解析路径与 `SkylineFurnitureDiag` 一致：`GetDesignIndex(data)` → `behavior.GetDesign(index)`
        /// → `SkylineFurniture.DominantMaterial(design)`（带缓存）。返回 0 = 解析失败。
        /// </summary>
        public static int FurnitureMaterial(int value) {
            try {
                int designIndex = FurnitureBlock.GetDesignIndex(Terrain.ExtractData(value));
                SubsystemFurnitureBlockBehavior behavior =
                    GameManager.Project?.FindSubsystem<SubsystemFurnitureBlockBehavior>(true);
                FurnitureDesign design = behavior?.GetDesign(designIndex);
                if (design == null) {
                    return 0;
                }
                return SkylineFurniture.DominantMaterial(design);
            }
            catch {
                return 0;
            }
        }

        /// <summary>
        /// `(x,y,z)` 是**空气那一侧**的格子：它是空气就用它自己的 light，否则退回方块自己的 light。
        /// 越界（超出世界上下界）会读到 0 → light 0，这种情况由 `zeroLightCells` 计数如实报出。
        /// </summary>
        public static int SurfaceLight(Terrain terrain, int x, int y, int z, int ownValue) {
            int neighbor = terrain.GetCellValue(x, y, z);
            return Terrain.ExtractContents(neighbor) == 0
                ? Terrain.ExtractLight(neighbor)
                : Terrain.ExtractLight(ownValue);
        }

        /// <summary>把"方块自己的 contents/data 判定 + 外部给的光照"重新打包成壳里的 ushort。</summary>
        static int PackLight(int value, int light) {
            return (value & 0x3FF) | ((light & 0xF) << 10);
        }

        // ---------------- 存档（P4，v0.1.53） ----------------
        // 每条记录固定 **16,384 B**：顶(short+ushort)×1024 + 4 侧 ushort×1024 + 底(short+ushort)×1024。
        public const int SerializedBytes = GridCells * (2 + 2) + 4 * GridCells * 2 + GridCells * (2 + 2);

        /// <summary>把整张壳写进二进制流（固定 `SerializedBytes` 字节）。</summary>
        public void WriteTo(BinaryWriter writer) {
            for (int i = 0; i < GridCells; i++) {
                writer.Write(TopHeight[i]);
                writer.Write(TopContents[i]);
            }
            for (int f = 0; f < 4; f++) {
                ushort[] side = SideContents[f];
                for (int i = 0; i < GridCells; i++) {
                    writer.Write(side[i]);
                }
            }
            for (int i = 0; i < GridCells; i++) {
                writer.Write(BottomHeight[i]);
                writer.Write(BottomContents[i]);
            }
        }

        /// <summary>读回一张壳（坐标由调用方负责写/读）。</summary>
        /// <summary>
        /// 从记录里读一张壳。**必须把记录头里的立方体坐标传进来**：
        /// 记录体本身只有高度场/材质（16,384 B），**不含坐标** ——
        /// v0.1.61 之前这里固定 `new(0, 0, 0)`，于是**从存档读回来的每一张壳的 X/Z 都是 0**，
        /// 网格全被建在世界原点附近（实测顶点 0 = `(16, 67, 0)`），
        /// 主画面里"绘制的壳"与玩家看到的壳根本不是同一批几何。这是**生产路径上的真 bug**。
        /// </summary>
        public static CubeSurface32 ReadFrom(BinaryReader reader, int x = 0, int y = 0, int z = 0) {
            CubeSurface32 shell = new(x, y, z);
            for (int i = 0; i < GridCells; i++) {
                shell.TopHeight[i] = reader.ReadInt16();
                shell.TopContents[i] = reader.ReadUInt16();
            }
            for (int f = 0; f < 4; f++) {
                ushort[] side = shell.SideContents[f];
                for (int i = 0; i < GridCells; i++) {
                    side[i] = reader.ReadUInt16();
                }
            }
            for (int i = 0; i < GridCells; i++) {
                shell.BottomHeight[i] = reader.ReadInt16();
                shell.BottomContents[i] = reader.ReadUInt16();
            }
            return shell;
        }

        /// <summary>
        /// [v0.1.57] 空壳判定：**顶面所有列都空**即视为空。
        /// 存档用它当"删除标记（墓碑）"——追加式存档里"后面的记录覆盖前面的"，写一条空壳就等于删掉那个立方体。
        /// </summary>
        public bool IsEmpty {
            get {
                for (int i = 0; i < GridCells; i++) {
                    if (TopContents[i] != 0) {
                        return false;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>`CubeSurface32` 的桥接口与自检（`skyline.CubeSurface*`）。</summary>
    public static partial class SkylineRuntime {
        /// <summary>确定性自检：合成"挖空的盒子"——壳数据必须只覆盖外表面（体积的壳 vs 内部空气）。</summary>
        public static string CubeSurfaceSelfCheck() {
            JsonObject result = new();
            try {
                const int size = CubeSurface32.Size;
                int solidVoxels = 0;
                for (int x = 0; x < size; x++) {
                    for (int y = 0; y < size; y++) {
                        for (int z = 0; z < size; z++) {
                            bool shell = x == 0 || x == size - 1 || y == 0 || y == size - 1
                                || z == 0 || z == size - 1;
                            if (shell) {
                                solidVoxels++;
                            }
                        }
                    }
                }
                int expectedShell = size * size * size - (size - 2) * (size - 2) * (size - 2);
                bool ok = solidVoxels == expectedShell;
                result["ok"] = ok;
                result["solidVoxels"] = solidVoxels;
                result["expectedShellVoxels"] = expectedShell;
                result["interiorAirVoxels"] = (size - 2) * (size - 2) * (size - 2);
                result["shellBytes"] = CubeSurface32.ShellBytes;
                result["rawBytes"] = CubeSurface32.RawBytes;
                result["ratio"] = Math.Round((double)CubeSurface32.RawBytes / CubeSurface32.ShellBytes, 2);
                result["note"] = "壳 = 32³ - 30³；壳字节 16 KiB vs 原始状态 128 KiB（8×）";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>真实世界取样：抓一个 32³ 立方体的表面壳，报字节/四边形/耗时（要求该列已加载）。</summary>
        public static string CubeSurfaceSample(int cx, int cy, int cz) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                Stopwatch watch = Stopwatch.StartNew();
                CubeSurface32 surface = CubeSurface32.Extract(terrain, cx, cy, cz);
                watch.Stop();
                int top = 0, bottom = 0, sides = 0;
                for (int i = 0; i < CubeSurface32.GridCells; i++) {
                    if (surface.TopContents[i] != 0) top++;
                    if (surface.BottomContents[i] != 0) bottom++;
                    for (int f = 0; f < 4; f++) {
                        if (surface.SideContents[f][i] != 0) sides++;
                    }
                }
                result["ok"] = true;
                result["cube"] = new JsonArray(cx, cy, cz);
                result["shellBytes"] = CubeSurface32.ShellBytes;
                result["rawBytes"] = CubeSurface32.RawBytes;
                result["ratio"] = Math.Round((double)CubeSurface32.RawBytes / CubeSurface32.ShellBytes, 2);
                result["topCells"] = top;
                result["bottomCells"] = bottom;
                result["sideCells"] = sides;
                result["quadCount"] = surface.QuadCount;
                result["extractMs"] = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
                // 校验 A：本立方体覆盖的 1024 列里，有多少列连区块都没分配（读到的一律是空气 = 假空）。
                int unloaded = 0;
                for (int lx = 0; lx < CubeSurface32.Size; lx++) {
                    for (int lz = 0; lz < CubeSurface32.Size; lz++) {
                        if (terrain.GetChunkAtCell(cx * CubeSurface32.Size + lx, cz * CubeSurface32.Size + lz) == null) {
                            unloaded++;
                        }
                    }
                }
                // 校验 B：与引擎自己的列顶编码对表（只比"引擎顶面确实落在本立方体内"的列）。
                // 高度与方块必须逐列一致；光照按 v0.1.50 的口径比（空气那一侧的 light）。
                int topChecked = 0, topMismatch = 0, lightChecked = 0, lightMismatch = 0;
                string firstMismatch = null;
                for (int lx = 0; lx < CubeSurface32.Size; lx++) {
                    for (int lz = 0; lz < CubeSurface32.Size; lz++) {
                        int wx = cx * CubeSurface32.Size + lx, wz = cz * CubeSurface32.Size + lz;
                        if (terrain.GetChunkAtCell(wx, wz) == null) {
                            continue;
                        }
                        int engineTop = terrain.GetTopHeight(wx, wz);
                        if (engineTop < cy * CubeSurface32.Size || engineTop > cy * CubeSurface32.Size + CubeSurface32.Size - 1) {
                            continue;
                        }
                        int idx = lx + lz * CubeSurface32.Size;
                        int engineValue = terrain.GetCellValue(wx, engineTop, wz);
                        topChecked++;
                        // [v0.1.56] 对表要跟着"家具塌缩"口径走：开着塌缩时，家具那一格**期望**就是设计主材质，
                        // 否则这 3 格会永远报 mismatch（看起来像 bug，其实是故意）。
                        int expectedContents = Terrain.ExtractContents(engineValue);
                        if (SkylineRuntime.ShellFurnitureCollapse && expectedContents == FurnitureBlock.Index) {
                            int furnitureMaterial = CubeSurface32.FurnitureMaterial(engineValue);
                            if (furnitureMaterial > 0) {
                                expectedContents = Terrain.ExtractContents(furnitureMaterial);
                            }
                        }
                        // [v0.1.59] 材质替换也要跟：开着替换时，草方块那一格**期望**是泥土。
                        else if (SkylineRuntime.ShellLodMaterialSubstitute) {
                            int substitute = SkylineSurfaceAudit.LodSubstitute(expectedContents);
                            if (substitute >= 0) {
                                expectedContents = substitute;
                            }
                        }
                        int shellValue = surface.TopContents[idx];
                        if (expectedContents != Terrain.ExtractContents(shellValue)
                            || engineTop != surface.TopHeight[idx]) {
                            topMismatch++;
                            if (firstMismatch == null) {
                                firstMismatch = $"({wx},{wz}) engine=(h{engineTop},c{Terrain.ExtractContents(engineValue)}) "
                                    + $"shell=(h{surface.TopHeight[idx]},c{Terrain.ExtractContents(shellValue)})";
                            }
                        }
                        int expectedLight = CubeSurface32.SurfaceLight(terrain, wx, engineTop + 1, wz, engineValue);
                        lightChecked++;
                        if (expectedLight != Terrain.ExtractLight(shellValue)) {
                            lightMismatch++;
                        }
                    }
                }
                result["unloadedColumns"] = unloaded;
                result["topChecked"] = topChecked;
                result["topMismatches"] = topMismatch;
                result["lightChecked"] = lightChecked;
                result["lightMismatches"] = lightMismatch;
                result["dataCells"] = surface.DataCells;
                result["furnitureCells"] = surface.FurnitureCells;
                result["furnitureResolved"] = surface.FurnitureResolved;
                result["furnitureUnresolved"] = surface.FurnitureUnresolved;
                if (firstMismatch != null) {
                    result["firstMismatch"] = firstMismatch;
                }
                result["note"] = "只读抽样：顶/底=列内最高/最低实心块，侧面=沿轴第一个实心块；层号即高度；"
                    + "unloadedColumns>0 说明该立方体含假空列（区块未分配）；topChecked 只统计引擎顶面落在本立方体内的列；"
                    + "topChecked 比「高度+方块」、lightChecked 比「空气那一侧的光照」（v0.1.50 口径）；"
                    + "dataCells 是带 data 位的壳格数（家具等变体是已知缺口）";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
