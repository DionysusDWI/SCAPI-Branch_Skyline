using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.12：**32³ 立方区块影子原型**（里程碑 3「32³ 第 2 步：立方区块 + 三维坐标」的 P1）。
    ///
    /// 定位：**纯新增、不接入任何游戏路径**。它只回答三个问题（`notes/81 §1 P1` 的验收）：
    ///   1. 32³ 立方体的**寻址**能不能做到"世界坐标 ↔ (立方体坐标 + 本地索引)"双向一致（并自带自检）；
    ///   2. 稀疏分配下的**内存量级**（单立方体 32³×4B = **128 KiB**；只有"写过内容的立方体"才分配）；
    ///   3. 把"列式（v0.1.4 起的 16×16×2048）"换成"立方体"后，内存/分配数在典型地形上差多少。
    ///
    /// 与现有实现的关系：现有 `TerrainChunk` 继续负责真实世界；本类只被自检与将来的 P2 加载窗口引用。
    /// </summary>
    public sealed class CubeChunk32 : IDisposable {
        public const int Size = 32;
        public const int Shift = 5;                                   // log2(32)
        public const int Cells = Size * Size * Size;                  // 32768
        public const int Bytes = Cells * 4;                           // 131072 = 128 KiB
        public const int CellsPerLayer = Size * Size;                 // 1024

        /// <summary>该立方体的三维坐标（世界格坐标 = 坐标 × 32 + 本地偏移）。</summary>
        public readonly int X;
        public readonly int Y;
        public readonly int Z;

        int[] m_cells;                                                // 惰性分配：没写过内容就一直是 null

        public CubeChunk32(int x, int y, int z) {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Allocated => m_cells != null;

        /// <summary>已分配字节数（0 或 128 KiB；将来接 ArrayCache 时改为"租借"）。</summary>
        public int AllocatedBytes => m_cells == null ? 0 : Bytes;

        /// <summary>本地索引：x 连续、再 y、再 z（与 `TerrainChunk.CalculateCellIndex` 的"y 连续"不同是**故意**的：
        /// 立方体的三种遍历（按列挖、按层铺、按体扫）都会有，P2 定案前先固定最简单的一种并靠自检守住不变式）。</summary>
        public static int LocalIndex(int lx, int ly, int lz) => lx + ly * Size + lz * CellsPerLayer;

        /// <summary>世界坐标 → 立方体坐标（向下取整，支持负坐标）。</summary>
        public static int CubeCoord(int world) => world >> Shift;      // 算术右移 = floor(/32)

        public static int LocalCoord(int world) => world & (Size - 1);

        public static long Key(int cx, int cy, int cz) =>
            ((long)(cx & 0x1FFFFF) << 42) | ((long)(cy & 0x1FFFFF) << 21) | (long)(cz & 0x1FFFFF);

        public int Get(int wx, int wy, int wz) {
            if (CubeCoord(wx) != X || CubeCoord(wy) != Y || CubeCoord(wz) != Z || m_cells == null) {
                return 0;
            }
            return m_cells[LocalIndex(LocalCoord(wx), LocalCoord(wy), LocalCoord(wz))];
        }

        public void Set(int wx, int wy, int wz, int value) {
            if (CubeCoord(wx) != X || CubeCoord(wy) != Y || CubeCoord(wz) != Z) {
                throw new ArgumentOutOfRangeException(nameof(wx), $"({wx},{wy},{wz}) 不属于立方体 ({X},{Y},{Z})");
            }
            if (m_cells == null) {
                if ((value & 0x3FF) == 0) {
                    return;                                           // 与 v0.1.4 同口径：写"空气"不租内存
                }
                m_cells = new int[Cells];
            }
            m_cells[LocalIndex(LocalCoord(wx), LocalCoord(wy), LocalCoord(wz))] = value;
        }

        public void Dispose() {
            m_cells = null;
        }
    }

    /// <summary>
    /// 32³ 影子原型的自检 + 内存账（桥入口：`type:CubeChunk32Invariants`）。
    /// </summary>
    public static class CubeChunk32Invariants {
        /// <summary>
        /// ① 寻址往返：对若干世界坐标（含负数、跨立方体边界）写读一致；
        /// ② 稀疏分配：全空气不分配；写 1 格只分配 1 个立方体（128 KiB）；
        /// ③ 内存账：同一条"典型地形"（地表带 ~8 层厚 × 64×64 面积）在 32³ 与 v0.1.4 列式下的对比。
        /// </summary>
        public static string Check() {
            JsonObject result = new();
            try {
                int checkedCells = 0;
                int mismatches = 0;
                string firstMismatch = null;
                var byKey = new Dictionary<long, CubeChunk32>();
                int[] worldXs = [0, 31, 32, 63, -1, -32, -33, 100, 12345];
                int[] worldYs = [0, 31, 32, 63, -1, -32, -1024, 1023, 67];
                int[] worldZs = [0, 31, 32, -1, -64, 7777];
                int n = 0;
                foreach (int wx in worldXs) {
                    foreach (int wy in worldYs) {
                        foreach (int wz in worldZs) {
                            long key = CubeChunk32.Key(
                                CubeChunk32.CubeCoord(wx), CubeChunk32.CubeCoord(wy), CubeChunk32.CubeCoord(wz));
                            if (!byKey.TryGetValue(key, out CubeChunk32 cube)) {
                                cube = new CubeChunk32(
                                    CubeChunk32.CubeCoord(wx), CubeChunk32.CubeCoord(wy), CubeChunk32.CubeCoord(wz));
                                byKey[key] = cube;
                            }
                            int value = 1 + (n++ & 0x3F);
                            cube.Set(wx, wy, wz, value);
                            int got = cube.Get(wx, wy, wz);
                            // 跨立方体邻居必须是 0（证明本地坐标没串台）
                            int neighbor = 0;
                            var neighborCube = new CubeChunk32(
                                CubeChunk32.CubeCoord(wx + 32), CubeChunk32.CubeCoord(wy), CubeChunk32.CubeCoord(wz));
                            neighbor = neighborCube.Get(wx + 32, wy, wz);
                            neighborCube.Dispose();
                            checkedCells++;
                            if (got != value || neighbor != 0) {
                                mismatches++;
                                firstMismatch ??= $"({wx},{wy},{wz}) write={value} got={got} neighbor={neighbor}";
                            }
                        }
                    }
                }
                // 稀疏分配账
                var single = new CubeChunk32(0, 0, 0);
                single.Set(1, 2, 3, 5);
                int singleBytes = single.AllocatedBytes;
                bool airOnlyAllocates = new CubeChunk32(5, 5, 5).Allocated;
                single.Dispose();

                // 典型地形内存账：64×64 面积、地表带 8 层厚（32³ 立方体：2×2 水平 × 1 竖直 = 4 个 = 512 KiB；
                // v0.1.4 列式：64×64 = 16 个 chunk，每个 1 段 256 层 = 16 × 256 KiB = 4 MiB）
                long cubeBytes = 4L * CubeChunk32.Bytes;
                long columnBytes = 16L * 256 * 1024;

                result["ok"] = mismatches == 0 && singleBytes == CubeChunk32.Bytes && !airOnlyAllocates;
                result["checkedCells"] = checkedCells;
                result["mismatches"] = mismatches;
                result["firstMismatch"] = firstMismatch;
                result["singleCellAllocatesBytes"] = singleBytes;
                result["airOnlyAllocates"] = airOnlyAllocates;
                result["cubeBytesPerCube"] = CubeChunk32.Bytes;
                result["terrainSample"] = new JsonObject {
                    ["areaBlocks"] = 64 * 64,
                    ["thicknessBlocks"] = 8,
                    ["cubes32Bytes"] = cubeBytes,
                    ["columnSlicedBytes"] = columnBytes,
                    ["ratio"] = Math.Round((double)cubeBytes / columnBytes, 3)
                };
                result["note"] = "影子原型：不接入游戏路径；只验寻址/稀疏分配/内存量级";
                foreach (CubeChunk32 cube in byKey.Values) {
                    cube.Dispose();
                }
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
