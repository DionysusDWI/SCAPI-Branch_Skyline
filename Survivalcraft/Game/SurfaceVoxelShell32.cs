using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**表面体素壳**（用户新目标 1.6 → 1.3 的"体积感"）。
    ///
    /// v0.1.59 量化出来的事实：一个森林地表立方体的**裸露体素 2,373 个**，其中 **56.8% 不是列顶**，
    /// 侧向+朝下的裸露面约占 38% —— 而现有壳（`CubeSurface32` = 列顶高度场）**结构上**只能表达 1,024 个列顶。
    ///
    /// 本类换掉这个结构：**逐体素记"裸露"**。
    ///
    ///   1. **占用位图** `ulong[512]`（32³ 位 = **4 KiB**）：哪个体素是实心的；
    ///   2. **稀疏体素表** `uint[MaxVoxels]`：每个**裸露**体素一条，`bit0-14 = 体素索引`（x | z&lt;&lt;5 | y&lt;&lt;10）、
    ///      `bit15-28 = 材质值`（contents + light，与 v0.1.59 的 LOD 结构材质规则一致）；
    ///   3. 预算：`MaxVoxels = 2560` → `2560×4 + 4096 = **14,336 B = 14 KiB**`（仍 ≤ 16 KiB 的老预算）；
    ///      超出的记 `Degraded`（v1 先"记不下就丢"，并在统计里如实报，后续再做优先级/降级策略）。
    ///
    /// **暴露判据**：实心且六邻居里有空气。立方体**外面那圈按真实地形读**；
    /// 读到未加载（值 0）时按"**未知=按实心**"处理（保守：宁可不画，也不要在区块边界凭空长出墙面）。
    /// 材质按 v0.1.59 的规则替换（草→泥土、雪层/雪→雪）。
    /// </summary>
    public sealed class SurfaceVoxelShell32 {
        public const int Size = 32;
        public const int CellCount = Size * Size * Size;          // 32,768
        public const int MaskWords = CellCount / 64;              // 512
        /// <summary>
        /// 体素上限：`3072×4 + 4096 = 16,384 B` = **正好 16 KiB**（与列顶高度场壳同预算）。
        /// 实测：森林地表 2,373 条（够）、密树冠 2,861 条（够）；再密的立方体记 `Degraded` 并如实报 `dropped`。
        /// </summary>
        public const int MaxVoxels = 3072;

        public readonly int X, Y, Z;
        public readonly ulong[] Mask = new ulong[MaskWords];
        public readonly uint[] Voxels = new uint[MaxVoxels];
        public int VoxelCount;
        /// <summary>true = 裸露体素超过 `MaxVoxels`（v1 只记前 MaxVoxels 条，如实标记）。</summary>
        public bool Degraded;
        /// <summary>被丢掉的裸露体素数（`Degraded` 时 &gt; 0）。</summary>
        public int DroppedVoxels;
        /// <summary>六邻居里读到"未加载（未知）"的次数（保守按实心算的次数）。</summary>
        public int UnknownNeighbors;

        public SurfaceVoxelShell32(int x, int y, int z) {
            X = x;
            Y = y;
            Z = z;
        }

        public static int PackIndex(int lx, int ly, int lz) => lx | (lz << 5) | (ly << 10);
        public static int IndexX(int i) => i & 31;
        public static int IndexZ(int i) => (i >> 5) & 31;
        public static int IndexY(int i) => (i >> 10) & 31;

        public bool IsSolid(int lx, int ly, int lz) {
            int index = PackIndex(lx, ly, lz);
            return (Mask[index >> 6] & (1UL << (index & 63))) != 0;
        }

        void SetSolid(int lx, int ly, int lz) {
            int index = PackIndex(lx, ly, lz);
            Mask[index >> 6] |= 1UL << (index & 63);
        }

        public int MaterialAt(int slot) => (int)((Voxels[slot] >> 15) & 0x3FFF);
        public int IndexAt(int slot) => (int)(Voxels[slot] & 0x7FFF);

        /// <summary>字节数（位图 + 稀疏表；按固定容量算，便于与 16 KiB 预算对照）。</summary>
        public static int ShellBytes => MaskWords * 8 + MaxVoxels * 4;   // 4096 + 10240 = 14336

        /// <summary>
        /// 从一个 32³ 立方体抓**表面体素**（只读；要求所在列已加载）。
        /// `unknownCountsAsSolid` = 读到未加载格时按实心算（默认 true，保守）。
        /// </summary>
        public static SurfaceVoxelShell32 ExtractFrom(Terrain terrain, int cx, int cy, int cz,
                                                      bool substituteMaterial = true, bool unknownCountsAsSolid = true) {
            SurfaceVoxelShell32 shell = new(cx, cy, cz);
            if (terrain == null) {
                return shell;
            }
            int ox = cx * Size, oy = cy * Size, oz = cz * Size;
            // 读 34³（含外面一圈）；用一个 byte 标记"这一格是不是未知（未加载）"
            int[] cells = new int[34 * 34 * 34];
            bool[] unknown = new bool[34 * 34 * 34];
            for (int y = -1; y <= Size; y++) {
                for (int z = -1; z <= Size; z++) {
                    for (int x = -1; x <= Size; x++) {
                        int wx = ox + x, wy = oy + y, wz = oz + z;
                        TerrainChunk chunk = terrain.GetChunkAtCell(wx, wz);
                        if (chunk == null) {
                            unknown[Idx(x, y, z)] = true;      // 未加载：值不可信
                            cells[Idx(x, y, z)] = 0;
                        }
                        else {
                            cells[Idx(x, y, z)] = terrain.GetCellValue(wx, wy, wz);
                        }
                    }
                }
            }
            // 1) 占用位图：立方体内部的实心体素
            for (int y = 0; y < Size; y++) {
                for (int z = 0; z < Size; z++) {
                    for (int x = 0; x < Size; x++) {
                        if (Terrain.ExtractContents(cells[Idx(x, y, z)]) != 0) {
                            shell.SetSolid(x, y, z);
                        }
                    }
                }
            }
            // 2) 稀疏表：只记**裸露**（六邻居里有空气）的体素
            int[] dx = [0, 0, 1, -1, 0, 0];
            int[] dy = [1, -1, 0, 0, 0, 0];
            int[] dz = [0, 0, 0, 0, 1, -1];
            int written = 0;
            for (int y = 0; y < Size; y++) {
                for (int z = 0; z < Size; z++) {
                    for (int x = 0; x < Size; x++) {
                        int value = cells[Idx(x, y, z)];
                        if (Terrain.ExtractContents(value) == 0) {
                            continue;
                        }
                        bool exposed = false;
                        for (int f = 0; f < 6; f++) {
                            int j = Idx(x + dx[f], y + dy[f], z + dz[f]);
                            if (unknown[j]) {
                                shell.UnknownNeighbors++;
                                if (!unknownCountsAsSolid) {
                                    exposed = true;
                                    break;
                                }
                                continue;                        // 未知按实心：这一面不算暴露
                            }
                            if (Terrain.ExtractContents(cells[j]) == 0) {
                                exposed = true;
                                break;
                            }
                        }
                        if (!exposed) {
                            continue;
                        }
                        if (written >= MaxVoxels) {
                            shell.Degraded = true;
                            shell.DroppedVoxels++;
                            continue;
                        }
                        int material = substituteMaterial ? Substitute(value) : value;
                        shell.Voxels[written++] = (uint)(PackIndex(x, y, z) | ((material & 0x3FFF) << 15));
                    }
                }
            }
            shell.VoxelCount = written;
            return shell;
        }

        static int Substitute(int value) {
            int contents = Terrain.ExtractContents(value);
            int substitute = SkylineSurfaceAudit.LodSubstitute(contents);
            return substitute > 0 && substitute != contents ? Terrain.ReplaceContents(value, substitute) : value;
        }

        static int Idx(int x, int y, int z) => (x + 1) + (z + 1) * 34 + (y + 1) * 34 * 34;

        public JsonObject Describe() {
            return new JsonObject {
                ["cube"] = new JsonArray(X, Y, Z),
                ["voxels"] = VoxelCount,
                ["maxVoxels"] = MaxVoxels,
                ["degraded"] = Degraded,
                ["dropped"] = DroppedVoxels,
                ["unknownNeighbors"] = UnknownNeighbors,
                ["shellBytes"] = ShellBytes,
                ["fillRatio"] = Math.Round(VoxelCount / (double)MaxVoxels, 3)
            };
        }
    }

    /// <summary>桥：`skyline.SurfaceVoxel*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>抓一个立方体的表面体素壳并报统计（1.6 的数据结构验证）。</summary>
        public static string SurfaceVoxelSample(int cx, int cy, int cz) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                SurfaceVoxelShell32 shell = SurfaceVoxelShell32.ExtractFrom(terrain, cx, cy, cz);
                result["ok"] = true;
                result["shell"] = shell.Describe();
                result["heightFieldShellBytes"] = CubeSurface32.ShellBytes;
                result["rawBlockStateBytes"] = CubeSurface32.RawBytes;
                result["note"] = "表面体素壳 = 4 KiB 占用位图 + 稀疏体素表（索引 15 位 + 材质 14 位，每条 4 B）；"
                    + "对比：列顶高度场壳 16 KiB、满方块状态 128 KiB";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
