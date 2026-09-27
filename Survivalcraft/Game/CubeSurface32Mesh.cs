using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.50：**32³ 表面壳的网格化 + 真绘制**（新目标 4.3 的第二步）。
    ///
    /// v0.1.49 做出了"只存表面材质"的壳（16 KiB vs 128 KiB），但如实记了一条硬伤：
    /// **朴素口径每非空壳格 1 个四边形，平均 4,470 个/立方体** —— 省了内存、赔了绘制。
    /// 本版把这条补上，并第一次把壳**画进画面**：
    ///
    ///   1. **顶面高度场**：壳的顶面给每列一个高度（`TopHeight`）+ 材质（`TopContents`），
    ///      本身就是一张 32×32 的高度场 → 直接按"相邻列高的做顶面、落差处拉裙边"建网格；
    ///   2. **面剔除**：相邻列等高 → 那一对内部面**直接不生成**（不像朴素壳那样把整格外表面全画）；
    ///   3. **贪心合并**：（等高 + 同材质 + 同光照）的矩形并成一个四边形（2D 贪心，先沿 X 再沿 Z）；
    ///      裙边同样按"连续等高邻居"合并成条带；
    ///   4. **真绘制**：把网格挂进地形不透明 pass（`SkylineCubeSurfaceDemo`），可与现有 LOD 层 A/B 对照。
    ///
    /// 如实记的口径：
    ///   * 合并后的四边形**贴图会被拉伸**（一个 tile 铺满整块矩形），
    ///     因为地形图集采样器是 Clamp，重复平铺会串到邻居 tile 上 —— 所以"合并"与"纹理密度"是取舍关系，
    ///     本版把两种网格（`culled` 逐格 / `merged` 贪心）**都建出来**，用同一个开关 A/B 看画面对比；
    ///   * 组**外边界**（没有邻居立方体的那一圈）按"闭盒"处理：裙边一直拉到本组最低顶面，
    ///     避免从侧面看穿（现有 LOD 层在这里是"缺邻居就不画"，两种口径都记在 notes 里）。
    /// </summary>
    public sealed class CubeSurfaceMesh32 {
        public const int Pad = CubeSurface32.Size;

        public int Cx0, Cy, Cz0;            // 组内第一个立方体的立方体坐标
        public int NX, NZ;                  // 立方体个数
        /// <summary>
        /// [v0.1.52] **最小体素 / 最小材质分片**的边长（米）。1 = 逐格（完整 32³ 精度）、
        /// 2 = 16³、4 = 8³、8 = 4³、16 = 2³、32 = 整块。
        /// 壳始终按 1 m 采集（16 KiB），**降精度是建网格时聚合出来的**，所以同一张壳可以按距离选不同档。
        /// </summary>
        public int Step = 1;
        public int Width => NX * Pad;
        public int Depth => NZ * Pad;
        /// <summary>本档的网格边长（= 32/Step 格/立方体）。</summary>
        public int GridSide => Pad / Step;

        // ---- 统计（判据都从这里出） ----
        public int NaiveQuads;              // 朴素壳口径（= Σ 壳格数），只为对照
        public int NonEmptyCells;           // 顶面非空列数（= 应该被覆盖的面积）
        public int TopQuads, WallQuads;
        public int Quads => TopQuads + WallQuads;
        public int CoveredCells;            // 所有顶面四边形覆盖的列数（应 == NonEmptyCells）
        public int MergeViolations;         // 贪心合并后"矩形内不一致"的列数（必须为 0）
        public int Vertices;
        public int IndexCount;
        public long VertexBytes => (long)Vertices * 20;
        public int MinSurfaceY, MaxSurfaceY;
        public double BuildMs;

        // 用字段（不是属性）：`Utilities.Dispose` 收的是 `ref`，属性不能当 ref 参数。
        public VertexBuffer VertexBuffer;
        public IndexBuffer IndexBuffer;

        // 定长数组 + 计数：SetupVertex 收的是 `ref`，用 List 时扩容会让已取的 ref 失效（野指针级风险）。
        TerrainVertex[] m_vertexData = new TerrainVertex[1024];
        int[] m_indexData = new int[1536];
        int m_vertexCount, m_indexCount;

        struct TopQuad {
            public int X, Z, W, D, Y, Value;
        }

        struct WallQuad {
            public int X, Z, Len, LowY, HighY, Value;
            public int Side;                // 0=+Z 1=-Z 2=+X 3=-X
        }

        // ---- [v0.1.54] 取证用：把生成的四边形按列暴露出来（默认关，只在按列探测时保留） ----
        public bool KeepQuads;
        public readonly List<(int X, int Z, int W, int D, int Y, int Value)> DebugTops = [];
        public readonly List<(int X, int Z, int Len, int LowY, int HighY, int Value, int Side)> DebugWalls = [];

        /// <summary>
        /// 由一组壳（`shells[ix + iz*NX]`）建一层网格。
        /// `greedy` = 是否做 2D 贪心合并；`withBuffers` = 是否创建 GPU 缓冲（只算数时省内存）。
        /// </summary>
        public static CubeSurfaceMesh32 Build(CubeSurface32[] shells, int nx, int nz, bool greedy, bool withBuffers,
                                              int step = 1, bool keepQuads = false) {
            Stopwatch watch = Stopwatch.StartNew();
            step = step switch { <= 1 => 1, 2 => 2, 4 => 4, 8 => 8, 16 => 16, _ => Pad };
            CubeSurfaceMesh32 mesh = new() {
                NX = nx,
                NZ = nz,
                Step = step,
                KeepQuads = keepQuads,
                MinSurfaceY = int.MaxValue,
                MaxSurfaceY = int.MinValue
            };
            bool originSet = false;
            for (int iz = 0; iz < nz; iz++) {
                for (int ix = 0; ix < nx; ix++) {
                    CubeSurface32 shell = shells[ix + iz * nx];
                    if (shell == null) {
                        continue;
                    }
                    if (!originSet) {
                        mesh.Cx0 = shell.X;
                        mesh.Cy = shell.Y;
                        mesh.Cz0 = shell.Z;
                        originSet = true;
                    }
                    mesh.NaiveQuads += shell.QuadCount;
                }
            }

            int gside = Pad / step;                  // 每个立方体在本档下的网格边长
            int w = nx * gside, d = nz * gside;
            int[] surfaceY = new int[w * d];        // 方块顶面 y（= 高度 + 1）；-1 = 空列
            int[] surfaceValue = new int[w * d];
            Array.Fill(surfaceY, -1);
            for (int gz = 0; gz < d; gz++) {
                for (int gx = 0; gx < w; gx++) {
                    int gi = gx + gz * w;
                    if (!Aggregate(shells, nx, step, gx, gz, out int y, out int value)) {
                        continue;
                    }
                    surfaceY[gi] = y;
                    surfaceValue[gi] = value;
                    mesh.NonEmptyCells++;
                    mesh.MinSurfaceY = Math.Min(mesh.MinSurfaceY, y);
                    mesh.MaxSurfaceY = Math.Max(mesh.MaxSurfaceY, y);
                }
            }
            if (mesh.NonEmptyCells == 0) {
                mesh.MinSurfaceY = 0;
                mesh.MaxSurfaceY = 0;
                mesh.BuildMs = watch.Elapsed.TotalMilliseconds;
                return mesh;
            }

            // ---------------- 1) 顶面：逐格 or 2D 贪心 ----------------
            List<TopQuad> tops = [];
            if (greedy) {
                bool[] used = new bool[w * d];
                for (int z = 0; z < d; z++) {
                    for (int x = 0; x < w; x++) {
                        int i = x + z * w;
                        if (used[i] || surfaceY[i] < 0) {
                            continue;
                        }
                        int y = surfaceY[i], value = surfaceValue[i];
                        int qw = 1;
                        while (x + qw < w && !used[x + qw + z * w]
                            && surfaceY[x + qw + z * w] == y && surfaceValue[x + qw + z * w] == value) {
                            qw++;
                        }
                        int qd = 1;
                        while (z + qd < d) {
                            bool rowOk = true;
                            for (int k = 0; k < qw; k++) {
                                int j = (x + k) + (z + qd) * w;
                                if (used[j] || surfaceY[j] != y || surfaceValue[j] != value) {
                                    rowOk = false;
                                    break;
                                }
                            }
                            if (!rowOk) {
                                break;
                            }
                            qd++;
                        }
                        for (int zz = z; zz < z + qd; zz++) {
                            for (int xx = x; xx < x + qw; xx++) {
                                used[xx + zz * w] = true;
                            }
                        }
                        tops.Add(new TopQuad { X = x, Z = z, W = qw, D = qd, Y = y, Value = value });
                    }
                }
                // 判据：合并出来的矩形里必须处处等高同材质（贪心写错的话这里会非 0）
                foreach (TopQuad q in tops) {
                    for (int zz = q.Z; zz < q.Z + q.D; zz++) {
                        for (int xx = q.X; xx < q.X + q.W; xx++) {
                            int i = xx + zz * w;
                            if (surfaceY[i] != q.Y || surfaceValue[i] != q.Value) {
                                mesh.MergeViolations++;
                            }
                        }
                    }
                }
            }
            else {
                for (int z = 0; z < d; z++) {
                    for (int x = 0; x < w; x++) {
                        int i = x + z * w;
                        if (surfaceY[i] < 0) {
                            continue;
                        }
                        tops.Add(new TopQuad { X = x, Z = z, W = 1, D = 1, Y = surfaceY[i], Value = surfaceValue[i] });
                    }
                }
            }
            foreach (TopQuad q in tops) {
                mesh.CoveredCells += q.W * q.D;
                mesh.TopQuads++;
                if (mesh.KeepQuads) {
                    mesh.DebugTops.Add((q.X, q.Z, q.W, q.D, q.Y, q.Value));
                }
                mesh.EmitTopQuad(q);
            }

            // ---------------- 2) 裙边：顶面落差处；组外边界拉到本组最低顶面（闭盒） ----------------
            int floorY = mesh.MinSurfaceY;
            List<WallQuad> walls = [];
            foreach (TopQuad q in tops) {
                // +Z 边（邻居行 z = q.Z + q.D），沿 X 走
                WallRun(walls, q, 0, q.X, q.W, q.Z + q.D, w, d, surfaceY, floorY);
                // -Z 边
                WallRun(walls, q, 1, q.X, q.W, q.Z - 1, w, d, surfaceY, floorY);
                // +X 边（邻居列 x = q.X + q.W），沿 Z 走
                WallRun(walls, q, 2, q.Z, q.D, q.X + q.W, w, d, surfaceY, floorY);
                // -X 边
                WallRun(walls, q, 3, q.Z, q.D, q.X - 1, w, d, surfaceY, floorY);
            }
            foreach (WallQuad wall in walls) {
                mesh.WallQuads++;
                if (mesh.KeepQuads) {
                    mesh.DebugWalls.Add((wall.X, wall.Z, wall.Len, wall.LowY, wall.HighY, wall.Value, wall.Side));
                }
                mesh.EmitWallQuad(wall);
            }

            if (withBuffers && mesh.m_vertexCount > 0) {
                mesh.CreateBuffers();
            }
            mesh.BuildMs = watch.Elapsed.TotalMilliseconds;
            return mesh;
        }

        // 聚合用的复用容器（content ≤ 1023 → 直接计数数组，免分配）
        static readonly int[] m_contentCount = new int[1024];
        static readonly List<int> m_contentTouched = new(64);
        static readonly List<int> m_heightScratch = new(1024);

        /// <summary>
        /// 把一个 `step×step` 的方块块聚合成**一格**（这就是"降精度"的实现）：
        ///   高度 = 该块内**主流材质**那些列的高度**中位**（与现有 LOD 的"材质取众数、高度取中位"同口径）；
        ///   材质 = 该块内**出现次数最多**的壳格（含它的 light）。
        /// 全空 → false。
        /// </summary>
        static bool Aggregate(CubeSurface32[] shells, int nx, int step, int gx, int gz,
                              out int surfaceY, out int surfaceValue) {
            int x0 = gx * step, z0 = gz * step;
            m_contentTouched.Clear();
            int bestContents = -1, bestCount = 0;
            for (int dz = 0; dz < step; dz++) {
                int zz = z0 + dz;
                int iz = zz / Pad;
                int lz = zz % Pad;
                int ixBase = 0;
                for (int dx = 0; dx < step; dx++) {
                    int xx = x0 + dx;
                    int ix = xx / Pad;
                    if (ix != ixBase) {
                        ixBase = ix;
                    }
                    CubeSurface32 shell = shells[ix + iz * nx];
                    if (shell == null) {
                        continue;
                    }
                    int value = shell.TopContents[xx % Pad + lz * Pad];
                    if (value == 0) {
                        continue;
                    }
                    int contents = value & 0x3FF;
                    if (m_contentCount[contents] == 0) {
                        m_contentTouched.Add(contents);
                    }
                    m_contentCount[contents]++;
                    if (m_contentCount[contents] > bestCount) {
                        bestCount = m_contentCount[contents];
                        bestContents = contents;
                    }
                }
            }
            foreach (int c in m_contentTouched) {
                m_contentCount[c] = 0;                       // 复位（只清动过的）
            }
            if (bestContents < 0) {
                surfaceY = -1;
                surfaceValue = 0;
                return false;
            }
            // 取"主流材质"那些列的高度中位
            m_heightScratch.Clear();
            for (int dz = 0; dz < step; dz++) {
                int zz = z0 + dz;
                int iz = zz / Pad, lz = zz % Pad;
                for (int dx = 0; dx < step; dx++) {
                    int xx = x0 + dx;
                    CubeSurface32 shell = shells[xx / Pad + iz * nx];
                    if (shell == null) {
                        continue;
                    }
                    int value = shell.TopContents[xx % Pad + lz * Pad];
                    if (value != 0 && (value & 0x3FF) == bestContents) {
                        m_heightScratch.Add(shell.TopHeight[xx % Pad + lz * Pad] + 1);
                    }
                }
            }
            if (m_heightScratch.Count == 0) {
                surfaceY = -1;
                surfaceValue = 0;
                return false;
            }
            m_heightScratch.Sort();
            surfaceY = m_heightScratch[m_heightScratch.Count / 2];
            // 材质取该高度上的一格（contents + 它自己的 light）
            for (int dz = 0; dz < step; dz++) {
                int zz = z0 + dz;
                int iz = zz / Pad, lz = zz % Pad;
                for (int dx = 0; dx < step; dx++) {
                    int xx = x0 + dx;
                    CubeSurface32 shell = shells[xx / Pad + iz * nx];
                    if (shell == null) {
                        continue;
                    }
                    int si = xx % Pad + lz * Pad;
                    if (shell.TopContents[si] != 0 && (shell.TopContents[si] & 0x3FF) == bestContents) {
                        surfaceValue = shell.TopContents[si];
                        return true;
                    }
                }
            }
            surfaceValue = bestContents;
            return true;
        }

        /// <summary>
        /// 沿一条边找"连续等高邻居"，与当前顶面高度有落差的段落生成裙边。
        /// `along` = 沿边走的起点（X 或 Z），`len` = 边长，`neighbor` = 邻居行/列坐标（越界 = 组外）。
        /// 生成的墙用**平面坐标**记（X = 侧面的 x 平面 / Z = 侧面的 z 平面），另一个轴配 `Len`。
        /// </summary>
        static void WallRun(List<WallQuad> walls, TopQuad q, int side,
            int along, int len, int neighbor, int w, int d, int[] surfaceY, int floorY) {
            int runStart = -1;
            int runHeight = int.MinValue;
            for (int k = 0; k <= len; k++) {
                int cur = int.MinValue;
                if (k < len) {
                    int a = along + k;
                    int n;
                    if (side <= 1) {
                        n = (neighbor >= 0 && neighbor < d) ? surfaceY[a + neighbor * w] : -1;
                    }
                    else {
                        n = (neighbor >= 0 && neighbor < w) ? surfaceY[neighbor + a * w] : -1;
                    }
                    cur = n < 0 ? floorY : n;                    // 组外/空列 → 按闭盒处理
                }
                if (cur == runHeight && k < len) {
                    continue;
                }
                if (runStart >= 0 && runHeight < q.Y) {
                    walls.Add(new WallQuad {
                        X = side <= 1 ? along + runStart : (side == 2 ? q.X + q.W : q.X),
                        Z = side <= 1 ? (side == 0 ? q.Z + q.D : q.Z) : along + runStart,
                        Len = k - runStart,
                        LowY = runHeight,
                        HighY = q.Y,
                        Value = q.Value,
                        Side = side
                    });
                }
                if (k >= len) {
                    break;
                }
                runStart = k;
                runHeight = cur;
            }
        }

        void EmitTopQuad(TopQuad q) {
            Color color = BaseColor(q.Value);
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(q.Value)];
            int slotCount = Math.Max(block.GetTextureSlotCount(q.Value), 1);
            int slot = SkylineRuntime.LodMaterialAware
                ? SkylineLod.MaterialTextureSlot(block, q.Value)
                : block.GetFaceTextureSlot(4, q.Value);
            float u0 = (slot % slotCount) / (float)slotCount;
            float v0 = (slot / slotCount) / (float)slotCount;
            float du = 1f / slotCount;
            float x0 = Cx0 * Pad + q.X * Step, z0 = Cz0 * Pad + q.Z * Step;
            float x1 = x0 + q.W * Step, z1 = z0 + q.D * Step;
            float y = q.Y;
            int v = Reserve(4);
            BlockGeometryGenerator.SetupVertex(x0, y, z0, color, u0, v0, ref m_vertexData[v]);
            BlockGeometryGenerator.SetupVertex(x1, y, z0, color, u0 + du, v0, ref m_vertexData[v + 1]);
            BlockGeometryGenerator.SetupVertex(x1, y, z1, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
            BlockGeometryGenerator.SetupVertex(x0, y, z1, color, u0, v0 + du, ref m_vertexData[v + 3]);
            AddQuadIndices(v);
        }

        void EmitWallQuad(WallQuad wall) {
            Color color = BaseColor(wall.Value);
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(wall.Value)];
            int slotCount = Math.Max(block.GetTextureSlotCount(wall.Value), 1);
            int slot = block.GetFaceTextureSlot(1, wall.Value);          // 侧面
            float u0 = (slot % slotCount) / (float)slotCount;
            float v0 = (slot / slotCount) / (float)slotCount;
            float du = 1f / slotCount;
            float yHigh = wall.HighY, yLow = wall.LowY;
            int v = Reserve(4);
            switch (wall.Side) {
                case 0: {                                                            // +Z 面
                    float x0 = Cx0 * Pad + wall.X * Step, z1 = Cz0 * Pad + wall.Z * Step;
                    float x1 = x0 + wall.Len * Step;
                    BlockGeometryGenerator.SetupVertex(x0, yHigh, z1, color, u0, v0, ref m_vertexData[v]);
                    BlockGeometryGenerator.SetupVertex(x1, yHigh, z1, color, u0 + du, v0, ref m_vertexData[v + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, yLow, z1, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
                    BlockGeometryGenerator.SetupVertex(x0, yLow, z1, color, u0, v0 + du, ref m_vertexData[v + 3]);
                    break;
                }
                case 1: {                                                            // -Z 面
                    float x0 = Cx0 * Pad + wall.X * Step, z0 = Cz0 * Pad + wall.Z * Step;
                    float x1 = x0 + wall.Len * Step;
                    BlockGeometryGenerator.SetupVertex(x1, yHigh, z0, color, u0, v0, ref m_vertexData[v]);
                    BlockGeometryGenerator.SetupVertex(x0, yHigh, z0, color, u0 + du, v0, ref m_vertexData[v + 1]);
                    BlockGeometryGenerator.SetupVertex(x0, yLow, z0, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
                    BlockGeometryGenerator.SetupVertex(x1, yLow, z0, color, u0, v0 + du, ref m_vertexData[v + 3]);
                    break;
                }
                case 2: {                                                            // +X 面
                    float x1 = Cx0 * Pad + wall.X * Step, z0 = Cz0 * Pad + wall.Z * Step;
                    float z1 = z0 + wall.Len * Step;
                    BlockGeometryGenerator.SetupVertex(x1, yHigh, z0, color, u0, v0, ref m_vertexData[v]);
                    BlockGeometryGenerator.SetupVertex(x1, yHigh, z1, color, u0 + du, v0, ref m_vertexData[v + 1]);
                    BlockGeometryGenerator.SetupVertex(x1, yLow, z1, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
                    BlockGeometryGenerator.SetupVertex(x1, yLow, z0, color, u0, v0 + du, ref m_vertexData[v + 3]);
                    break;
                }
                default: {                                                           // -X 面
                    float x0 = Cx0 * Pad + wall.X * Step, z0 = Cz0 * Pad + wall.Z * Step;
                    float z1 = z0 + wall.Len * Step;
                    BlockGeometryGenerator.SetupVertex(x0, yHigh, z1, color, u0, v0, ref m_vertexData[v]);
                    BlockGeometryGenerator.SetupVertex(x0, yHigh, z0, color, u0 + du, v0, ref m_vertexData[v + 1]);
                    BlockGeometryGenerator.SetupVertex(x0, yLow, z0, color, u0 + du, v0 + du, ref m_vertexData[v + 2]);
                    BlockGeometryGenerator.SetupVertex(x0, yLow, z1, color, u0, v0 + du, ref m_vertexData[v + 3]);
                    break;
                }
            }
            // 裙边双面（与现有 LOD 层同一口径：从任意一侧看都不能穿）
            AddQuadIndices(v, doubleSided: true);
        }

        static Color BaseColor(int value) {
            int light = SkylineRuntime.LodLightFromSamples ? Terrain.ExtractLight(value) : 13;
            byte b = (byte)(light * 17);
            return new Color(b, b, b);
        }

        /// <summary>预留 n 个顶点槽，返回起始下标（保证之后取的 ref 在本次写入期间不失效）。</summary>
        int Reserve(int n) {
            if (m_vertexCount + n > m_vertexData.Length) {
                Array.Resize(ref m_vertexData, Math.Max(m_vertexData.Length * 2, m_vertexCount + n));
            }
            int start = m_vertexCount;
            m_vertexCount += n;
            return start;
        }

        void PushIndex(int value) {
            if (m_indexCount >= m_indexData.Length) {
                Array.Resize(ref m_indexData, m_indexData.Length * 2);
            }
            m_indexData[m_indexCount++] = value;
        }

        void AddQuadIndices(int baseIndex, bool doubleSided = false) {
            PushIndex(baseIndex);
            PushIndex(baseIndex + 1);
            PushIndex(baseIndex + 2);
            PushIndex(baseIndex);
            PushIndex(baseIndex + 2);
            PushIndex(baseIndex + 3);
            if (doubleSided) {
                PushIndex(baseIndex + 2);
                PushIndex(baseIndex + 1);
                PushIndex(baseIndex);
                PushIndex(baseIndex + 3);
                PushIndex(baseIndex + 2);
                PushIndex(baseIndex);
            }
        }

        void CreateBuffers() {
            Vertices = m_vertexCount;
            IndexCount = m_indexCount;
            VertexBuffer = new VertexBuffer(TerrainVertex.VertexDeclaration, Vertices);
            VertexBuffer.SetData(m_vertexData, 0, Vertices);
            bool big = Vertices > 65535;
            IndexBuffer = new IndexBuffer(big ? IndexFormat.ThirtyTwoBits : IndexFormat.SixteenBits, IndexCount);
            if (big) {
                IndexBuffer.SetData(m_indexData, 0, IndexCount);
            }
            else {
                short[] shorts = new short[IndexCount];
                for (int i = 0; i < IndexCount; i++) {
                    shorts[i] = (short)m_indexData[i];
                }
                IndexBuffer.SetData(shorts, 0, IndexCount);
            }
        }

        public void Dispose() {
            Utilities.Dispose(ref VertexBuffer);
            Utilities.Dispose(ref IndexBuffer);
        }

        /// <summary>统计对象（可直接嵌进上层 JSON；不要复用同一个实例，JsonNode 只能有一个父节点）。</summary>
        public JsonObject DescribeObject() {
            return new JsonObject {
                ["cubes"] = new JsonArray(NX, NZ),
                ["cubeOrigin"] = new JsonArray(Cx0, Cy, Cz0),
                ["spanMetres"] = new JsonArray(Width, Depth),
                ["step"] = Step,                               // 1=32³ 完整精度 … 32=整块
                ["voxelMetres"] = Step,
                ["gridSide"] = GridSide,
                ["nonEmptyCells"] = NonEmptyCells,
                ["naiveQuads"] = NaiveQuads,
                ["topQuads"] = TopQuads,
                ["wallQuads"] = WallQuads,
                ["quads"] = Quads,
                ["coveredCells"] = CoveredCells,
                ["coveredAreaM2"] = CoveredCells * Step * Step,
                ["mergeViolations"] = MergeViolations,
                ["vertices"] = Vertices,
                ["indices"] = IndexCount,
                ["vertexBytes"] = VertexBytes,
                ["minSurfaceY"] = MinSurfaceY,
                ["maxSurfaceY"] = MaxSurfaceY,
                ["buildMs"] = Math.Round(BuildMs, 2)
            };
        }

        public string Describe() => DescribeObject().ToJsonString();

        // ---- [v0.1.54] 按列取证：这一列被画成了什么形状 ----

        /// <summary>覆盖该列的顶面四边形（合并后的矩形会包含该列）。</summary>
        public JsonArray TopQuadsJson(int lx, int lz) {
            JsonArray a = [];
            foreach ((int X, int Z, int W, int D, int Y, int Value) t in DebugTops) {
                if (lx >= t.X && lx < t.X + t.W && lz >= t.Z && lz < t.Z + t.D) {
                    a.Add(new JsonObject {
                        ["rect"] = new JsonArray(t.X, t.Z, t.W, t.D),
                        ["y"] = t.Y,
                        ["contents"] = t.Value & 0x3FF,
                        ["materialSlot"] = TextureSlotOf(t.Value)
                    });
                }
            }
            return a;
        }

        /// <summary>贴着该列的 4 个侧壁（裙边/方盒的侧面）。</summary>
        public JsonArray WallsJson(int lx, int lz) {
            JsonArray a = [];
            foreach ((int X, int Z, int Len, int LowY, int HighY, int Value, int Side) w in DebugWalls) {
                bool touches = w.Side switch {
                    0 => w.Z == lz + 1 && lx >= w.X && lx < w.X + w.Len,
                    1 => w.Z == lz && lx >= w.X && lx < w.X + w.Len,
                    2 => w.X == lx + 1 && lz >= w.Z && lz < w.Z + w.Len,
                    _ => w.X == lx && lz >= w.Z && lz < w.Z + w.Len
                };
                if (!touches) {
                    continue;
                }
                a.Add(new JsonObject {
                    ["side"] = w.Side switch { 0 => "+Z", 1 => "-Z", 2 => "+X", _ => "-X" },
                    ["planeCoord"] = w.Side <= 1 ? w.Z : w.X,
                    ["run"] = new JsonArray(w.Side <= 1 ? w.X : w.Z, w.Len),
                    ["lowY"] = w.LowY,
                    ["highY"] = w.HighY,
                    ["span"] = w.HighY - w.LowY,
                    ["contents"] = w.Value & 0x3FF,
                    ["materialSlot"] = TextureSlotOf(w.Value)
                });
            }
            return a;
        }

        /// <summary>该列的"实际形状"摘要：顶面高度 + 侧壁跨度 → 判断是不是 1 m 的材质方盒。</summary>
        public JsonObject ColumnBoxShape(int lx, int lz) {
            int topY = -1;
            foreach ((int X, int Z, int W, int D, int Y, int Value) t in DebugTops) {
                if (lx >= t.X && lx < t.X + t.W && lz >= t.Z && lz < t.Z + t.D) {
                    topY = t.Y;
                    break;
                }
            }
            int walls = 0, minLow = int.MaxValue, maxHigh = int.MinValue;
            foreach ((int X, int Z, int Len, int LowY, int HighY, int Value, int Side) w in DebugWalls) {
                bool touches = w.Side switch {
                    0 => w.Z == lz + 1 && lx >= w.X && lx < w.X + w.Len,
                    1 => w.Z == lz && lx >= w.X && lx < w.X + w.Len,
                    2 => w.X == lx + 1 && lz >= w.Z && lz < w.Z + w.Len,
                    _ => w.X == lx && lz >= w.Z && lz < w.Z + w.Len
                };
                if (!touches) {
                    continue;
                }
                walls++;
                minLow = Math.Min(minLow, w.LowY);
                maxHigh = Math.Max(maxHigh, w.HighY);
            }
            bool isOneMetreBox = walls > 0 && minLow == topY - 1 && maxHigh == topY;
            return new JsonObject {
                ["topY"] = topY,
                ["walls"] = walls,
                ["wallLowY"] = walls > 0 ? minLow : null,
                ["wallHighY"] = walls > 0 ? maxHigh : null,
                ["oneMetreBox"] = isOneMetreBox,
                ["note"] = isOneMetreBox ? "顶面 + 1 m 侧壁 = 材质占位方盒"
                    : (walls == 0 ? "只有顶面（四周邻居同高 → 侧面被剔除，视觉上仍与邻居连成一片）"
                                  : "侧壁跨度不是 1 m（可能是落差裙边或组外边界的闭盒）")
            };
        }

        static int TextureSlotOf(int value) {
            try {
                Block block = BlocksManager.Blocks[value & 0x3FF];
                return block is CubeBlock ? block.GetFaceTextureSlot(4, value) : block.GetFaceTextureSlot(0, value);
            }
            catch {
                return -1;
            }
        }
    }

    /// <summary>
    /// 32³ 表面壳的**实验渲染层**（v0.1.50）：把一组立方体的壳网格挂进地形不透明 pass，
    /// 用**与真实地形/LOD 同一套 shader + 图集 + 雾参数**画出来 —— 这就是"把 LOD 区块当作
    /// 32³ 家具来渲染"的第一张真实画面。默认关（`skyline.CubeSurfaceDraw(true)` 打开）。
    ///
    /// 生产语义（本版只做实验层，接口按生产语义留）：壳应当在**区块加载/卸载时采集**（v0.1.17 进入即采、
    /// v0.1.47 卸载前最后一采），之后即使真实区块已被释放，也能用壳继续画 1 m 精度的外观。
    /// 本版先用"采集时列必须已加载"的直读口径做验证。
    /// </summary>
    public static class SkylineCubeSurfaceDemo {
        static CubeSurface32[] m_shells;
        static int m_nx, m_nz;
        static CubeSurfaceMesh32 m_culled;      // 逐格（纹理密度精确）
        static CubeSurfaceMesh32 m_merged;      // 贪心合并（四边形最少，纹理被拉伸）
        static bool m_draw;
        static bool m_useMerged;
        static float m_drawYOffset;
        static string m_lastError = "";

        public static bool DrawEnabled => m_draw;
        public static bool UseMerged => m_useMerged;

        public static string Harvest(int cx, int cy, int cz, int nx, int nz) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                nx = Math.Clamp(nx, 1, 5);
                nz = Math.Clamp(nz, 1, 5);
                int x0 = cx - (nx / 2), z0 = cz - (nz / 2);
                CubeSurface32[] shells = new CubeSurface32[nx * nz];
                int unloadedColumns = 0, emptyCubes = 0, nonEmptyCubes = 0;
                int zeroLightCells = 0, zeroLightCubes = 0;
                Stopwatch watch = Stopwatch.StartNew();
                for (int iz = 0; iz < nz; iz++) {
                    for (int ix = 0; ix < nx; ix++) {
                        CubeSurface32 shell = CubeSurface32.Extract(terrain, x0 + ix, cy, z0 + iz);
                        shells[ix + iz * nx] = shell;
                        for (int lx = 0; lx < CubeSurface32.Size; lx++) {
                            for (int lz = 0; lz < CubeSurface32.Size; lz++) {
                                if (terrain.GetChunkAtCell((x0 + ix) * CubeSurface32.Size + lx,
                                        (z0 + iz) * CubeSurface32.Size + lz) == null) {
                                    unloadedColumns++;
                                }
                            }
                        }
                        if (shell.QuadCount == 0) {
                            emptyCubes++;
                        }
                        else {
                            nonEmptyCubes++;
                        }
                        int dark = 0;
                        for (int i = 0; i < CubeSurface32.GridCells; i++) {
                            if (shell.TopContents[i] != 0 && Terrain.ExtractLight(shell.TopContents[i]) == 0) {
                                dark++;
                            }
                        }
                        zeroLightCells += dark;
                        if (dark > 0) {
                            zeroLightCubes++;
                        }
                    }
                }
                watch.Stop();
                double harvestMs = watch.Elapsed.TotalMilliseconds;

                CubeSurfaceMesh32 culled = CubeSurfaceMesh32.Build(shells, nx, nz, false, true);
                CubeSurfaceMesh32 merged = CubeSurfaceMesh32.Build(shells, nx, nz, true, true);
                m_culled?.Dispose();
                m_merged?.Dispose();
                m_shells = shells;
                m_nx = nx;
                m_nz = nz;
                m_culled = culled;
                m_merged = merged;

                result["ok"] = true;
                result["group"] = new JsonArray(x0, cy, z0);
                result["cubeCount"] = nx * nz;
                result["spanMetres"] = new JsonArray(nx * CubeSurface32.Size, nz * CubeSurface32.Size);
                result["unloadedColumns"] = unloadedColumns;
                result["emptyCubes"] = emptyCubes;
                result["nonEmptyCubes"] = nonEmptyCubes;
                result["zeroLightCells"] = zeroLightCells;
                result["zeroLightCubes"] = zeroLightCubes;
                result["harvestMs"] = Math.Round(harvestMs, 2);
                result["naiveQuads"] = culled.NaiveQuads;
                result["culled"] = culled.DescribeObject();
                result["merged"] = merged.DescribeObject();
                result["drawing"] = m_draw;
                result["note"] = "culled=逐格(纹理精确) merged=贪心合并(纹理拉伸)；两者都可 skyline.CubeSurfaceDraw(true) 后画出来 A/B；"
                    + "zeroLightCells>0 = 这些列采集时**光照还没算**（区块只到 InvalidLight），壳里 light 位是 0 → 画出来是黑的："
                    + "要在区块已算光照（ThreadState ≥ InvalidPropagate）时采集";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        public static string SetDraw(bool enabled) {
            m_draw = enabled;
            return new JsonObject {
                ["ok"] = true,
                ["draw"] = m_draw,
                ["greedy"] = m_useMerged,
                ["culledQuads"] = m_culled?.Quads ?? 0,
                ["mergedQuads"] = m_merged?.Quads ?? 0,
                ["note"] = m_shells == null ? "还没有采集（先调 skyline.CubeSurfaceHarvest）" : "已就绪"
            }.ToJsonString();
        }

        public static string SetGreedy(bool greedy) {
            m_useMerged = greedy;
            return new JsonObject {
                ["ok"] = true,
                ["greedy"] = m_useMerged,
                ["draw"] = m_draw,
                ["quads"] = (m_useMerged ? m_merged : m_culled)?.Quads ?? 0
            }.ToJsonString();
        }

        /// <summary>
        /// 取证用：把整组壳**整体抬高 N 米**再画（`skyline.CubeSurfaceDrawOffset(y)`，默认 0）。
        /// 为什么要这个：壳顶面是**逐列真高**、现有 LOD 是 16 m 单元的**中位高**，两层叠着画会互相穿插；
        /// 抬起来之后壳网格是画面里唯一的新增内容，A/B 的像素差就是"这一层画了什么"。
        /// </summary>
        public static string SetDrawOffset(float y) {
            m_drawYOffset = y;
            return new JsonObject {
                ["ok"] = true,
                ["yOffset"] = m_drawYOffset
            }.ToJsonString();
        }

        public static string Info() {
            return new JsonObject {
                ["ok"] = true,
                ["harvested"] = m_shells != null,
                ["cubes"] = new JsonArray(m_nx, m_nz),
                ["draw"] = m_draw,
                ["greedy"] = m_useMerged,
                ["culled"] = m_culled?.DescribeObject(),
                ["merged"] = m_merged?.DescribeObject(),
                ["lastError"] = m_lastError
            }.ToJsonString();
        }

        /// <summary>由 `SubsystemTerrain.Draw` 调用（紧跟在现有 LOD 层之后）。</summary>
        public static void DrawIfEnabled(Camera camera) {
            if (!m_draw) {
                return;
            }
            CubeSurfaceMesh32 mesh = m_useMerged ? m_merged : m_culled;
            if (mesh == null || mesh.VertexBuffer == null || mesh.IndexCount == 0) {
                return;
            }
            DrawMesh(camera, mesh, m_drawYOffset);
        }

        /// <summary>
        /// 把地形不透明 pass 需要的 shader 参数与绘制状态都设好（与 `SkylineLod.Draw` 完全同口径）。
        /// 返回 null = 现在不能画（没世界/没 shader/没相机）。
        /// </summary>
        public static Shader PrepareTerrainShader(Camera camera, float yOffset) {
            try {
                SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
                if (subsystemTerrain == null || sky == null || TerrainRenderer.m_opaqueShader == null || camera == null) {
                    return null;
                }
                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                Vector3 v = new(MathF.Floor(viewPosition.X), 0f, MathF.Floor(viewPosition.Z));
                Matrix matrix = Matrix.CreateTranslation(0f, yOffset, 0f)
                    * Matrix.CreateTranslation(v - viewPosition)
                    * camera.ViewMatrix.OrientationMatrix * camera.ProjectionMatrix;
                Shader shader = TerrainRenderer.m_opaqueShader;
                shader.GetParameter("u_origin", true).SetValue(new Vector2(v.X, v.Z));
                shader.GetParameter("u_viewProjectionMatrix", true).SetValue(matrix);
                shader.GetParameter("u_viewPosition", true).SetValue(viewPosition);
                shader.GetParameter("u_samplerState", true).SetValue(
                    SettingsManager.TerrainMipmapsEnabled
                        ? new SamplerState {
                            AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                            FilterMode = TextureFilterMode.PointMipLinear, MaxLod = 4f
                        }
                        : new SamplerState {
                            AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                            FilterMode = TextureFilterMode.Point, MaxLod = 0f
                        });
                shader.GetParameter("u_fogYMultiplier", true).SetValue(sky.VisibilityRangeYMultiplier);
                shader.GetParameter("u_fogColor", true).SetValue(new Vector3(sky.ViewFogColor));
                shader.GetParameter("u_fogBottomTopDensity", true)
                    .SetValue(SkylineRuntime.FogBand(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity)));
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(SkylineRuntime.HazeStartDensity(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity)));
                shader.GetParameter("u_texture", true).SetValue(
                    subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                return shader;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineCubeSurfaceDemo.PrepareTerrainShader: {e.Message}");
                return null;
            }
        }

        /// <summary>画一张壳网格（调用前先 `PrepareTerrainShader`）。</summary>
        public static void DrawMesh(Camera camera, CubeSurfaceMesh32 mesh, float yOffset) {
            if (mesh == null || mesh.VertexBuffer == null || mesh.IndexCount == 0) {
                return;
            }
            Shader shader = PrepareTerrainShader(camera, yOffset);
            if (shader == null) {
                return;
            }
            try {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, mesh.VertexBuffer, mesh.IndexBuffer, 0, mesh.IndexCount);
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineCubeSurfaceDemo.DrawMesh: {e.Message}");
            }
        }
    }

    /// <summary>桥：`skyline.CubeSurface*`（与 `CubeSurfaceSelfCheck`/`CubeSurfaceSample` 同一组）。</summary>
    public static partial class SkylineRuntime {
        /// <summary>采集一组立方体的壳并建两种网格（逐格 / 贪心），返回统计与判据。</summary>
        public static string CubeSurfaceHarvest(int cx, int cy, int cz, int nx, int nz) =>
            SkylineCubeSurfaceDemo.Harvest(cx, cy, cz, nx, nz);

        /// <summary>开关"画这组壳网格"（默认关）。</summary>
        public static string CubeSurfaceDraw(bool enabled) => SkylineCubeSurfaceDemo.SetDraw(enabled);

        /// <summary>切换画哪张网格：false = 逐格（纹理精确）、true = 贪心合并（四边形最少）。</summary>
        public static string CubeSurfaceGreedy(bool greedy) => SkylineCubeSurfaceDemo.SetGreedy(greedy);

        /// <summary>取证用：把壳整体抬高 N 米再画（默认 0），避免与 16 m LOD 层互相穿插。</summary>
        public static string CubeSurfaceDrawOffset(float y) => SkylineCubeSurfaceDemo.SetDrawOffset(y);

        /// <summary>当前状态（是否已采集 / 是否在画 / 两种网格的统计）。</summary>
        public static string CubeSurfaceMeshInfo() => SkylineCubeSurfaceDemo.Info();
    }
}
