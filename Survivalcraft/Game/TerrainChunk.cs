using Engine;

namespace Game {
    public class TerrainChunk : IDisposable {
        public struct BrushPaint {
            public Point3 Position;

            public TerrainBrush Brush;
        }

        public const int SizeBits = 4;

        public const int Size = 16;

        public const int HeightBits = 11;   // [负高度实验] 索引改成算术偏移后这里只作参考

        /// <summary>[负高度实验] 世界最低层（含）。0 以上仍保留到 <see cref="HeightMinusOne"/>。</summary>
        public const int MinHeight = -1024;   // [v0.0.3] 负高度对齐到 -1024

        /// <summary>[负高度实验] 每区块的竖直层数 = MaxHeight - MinHeight + 1。</summary>
        public const int Height = 2048;     // -1024..1023

        public const int SizeMinusOne = 15;

        public const int HeightMinusOne = 1023;   // = 最大 y（MaxHeight）

        public const int SliceHeight = 16;

        public const int SlicesCount = 128;  // [v0.0.3] 2048/16 = 128

        public Terrain Terrain;

        public Point2 Coords;

        public Point2 Origin;

        public BoundingBox BoundingBox;

        public Vector2 Center;

        public TerrainChunkState State;

        public TerrainChunkState ThreadState;

        public bool WasDowngraded;

        public TerrainChunkState? DowngradedState;

        public bool WasUpgraded;

        public TerrainChunkState? UpgradedState;

        public int ModificationCounter;

        public float[] HazeEnds = new float[4];

        public bool AreBehaviorsNotified;

        public bool IsLoaded;

        public volatile bool NewGeometryData;

        public TerrainChunkGeometry Geometry = new();

        // ===== v0.1.4：**竖直分节**（32³ 路线第 1 步）=====
        // 把"整列 Size×Size×Height 一块"拆成 ColumnSlicesCount 个 256 层的子列，**按需租借**：
        // 高空玩家只需顶部子列，竖直方向的裁剪才真正省内存（notes/67 §3）。读取未租借的段返回 0（空气），
        // 写入会自动租借该段。对外访问全部走 Get/SetCellValueFast，行为与原整块一致。
        public const int ColumnSliceHeight = 256;
        public const int ColumnSlicesCount = Height / ColumnSliceHeight;
        public int[][] Cells;

        // ===== v0.1.28：**32³ 分带内容掩码**（球形加载窗的"立方体粒度"判据，见 notes/98）=====
        // bit i ↔ y ∈ [MinHeight + 32i, MinHeight + 32i + 31]：该区块任意一列的内容带（bottom..top）
        // 与这个分带相交即置位。由 TerrainUpdater 惰性计算并缓存；stamp 在编辑（ModificationCounter）
        // 或状态推进（State，含光照阶段写高度）后失效。
        public ulong ContentBandMask32;
        public int ContentBandMask32Stamp = int.MinValue;

        public long[] Shafts;                                  // [高度实验] int[] -> long[]（高度字段要 10 位）

        public static ArrayCache<int> m_cellsCache = new([Size * Size * ColumnSliceHeight], 0.66f, 60f, 0.33f, 5f);

        public static ArrayCache<long> m_shaftsCache = new([Size * Size], 0.66f, 60f, 0.33f, 5f);

        public DynamicArray<BrushPaint> m_brushPaints = [];

        public TerrainGeometry[] ChunkSliceGeometries = new TerrainGeometry[SlicesCount];

        public DynamicArray<TerrainChunkGeometry.Buffer> Buffers = [];

        public int[] SliceContentsHashes = new int[SlicesCount];

        public int[] GeneratedSliceContentsHashes = new int[SlicesCount];

        public TerrainChunk(Terrain terrain, int x, int z) {
            Terrain = terrain;
            Coords = new Point2(x, z);
            Origin = new Point2(x * Size, z * Size);
            // [负高度实验] 包围盒跟着世界竖直范围走（z 裁剪要用它，不能还是 0..Height）
            BoundingBox = new BoundingBox(
                new Vector3(Origin.X, MinHeight, Origin.Y),
                new Vector3(Origin.X + Size, HeightMinusOne + 1, Origin.Y + Size));
            Center = new Vector2((float)Origin.X + Size / 2, (float)Origin.Y + Size / 2);
            Cells = new int[ColumnSlicesCount][];
            Shafts = m_shaftsCache.Rent(Size * Size, true);
        }

        /// <summary>取某一段（未租借则为 null；读路径当成全 0/空气）。</summary>
        public int[] GetColumnSlice(int seg) =>
            seg >= 0 && seg < ColumnSlicesCount ? Cells[seg] : null;

        /// <summary>确保某一段已租借（写路径用）。</summary>
        public int[] EnsureColumnSlice(int seg) {
            if (Cells[seg] == null) {
                Cells[seg] = m_cellsCache.Rent(Size * Size * ColumnSliceHeight, true);
            }
            return Cells[seg];
        }

        /// <summary>已租借的段数（0..ColumnSlicesCount）——竖直分节的省内存指标。</summary>
        public int AllocatedColumnSlices {
            get {
                int n = 0;
                for (int i = 0; i < ColumnSlicesCount; i++) {
                    if (Cells[i] != null) {
                        n++;
                    }
                }
                return n;
            }
        }

        public virtual void DisposeVertexIndexBuffers() {
            foreach (TerrainChunkGeometry.Buffer b in Buffers) {
                b.Dispose();
            }
            Buffers.Clear();
        }

        public virtual void InvalidateSliceContentsHashes() {
            for (int i = 0; i < GeneratedSliceContentsHashes.Length; i++) {
                GeneratedSliceContentsHashes[i] = 0;
            }
        }

        public virtual void CopySliceContentsHashes() {
            for (int i = 0; i < GeneratedSliceContentsHashes.Length; i++) {
                GeneratedSliceContentsHashes[i] = SliceContentsHashes[i];
            }
        }

        public virtual void Dispose() {
            DisposeVertexIndexBuffers();
            if (Geometry == null) {
                throw new InvalidOperationException();
            }
            Geometry = null;
            for (int i = 0; i < ColumnSlicesCount; i++) {
                if (Cells[i] != null) {
                    m_cellsCache.Return(Cells[i]);
                    Cells[i] = null;
                }
            }
            m_shaftsCache.Return(Shafts);
        }

        public static bool IsCellValid(int x, int y, int z) {
            if (x >= 0
                && x < Size
                && y >= MinHeight
                && y <= HeightMinusOne
                && z >= 0) {
                return z < Size;
            }
            return false;
        }

        public static bool IsShaftValid(int x, int z) {
            if (x >= 0
                && x < Size
                && z >= 0) {
                return z < Size;
            }
            return false;
        }

        public static int CalculateCellIndex(int x, int y, int z) {
            // [负高度实验] 位打包改成算术偏移：索引 = (y - MinHeight) + x*Height + z*Height*Size。
            // 这样 y 可以是负数，也不再要求 Height 是 2 的幂。
            // [v0.0.3] 越界时**夹紧**而不是抛异常：几何生成会读 y±1 的邻居格，
            // 抛异常会让整片 slice 的网格生成中断（表现为"整块地形消失"）。
            if (y < MinHeight) { y = MinHeight; }
            else if (y > HeightMinusOne) { y = HeightMinusOne; }
            if (x < 0) { x = 0; } else if (x >= Size) { x = Size - 1; }
            if (z < 0) { z = 0; } else if (z >= Size) { z = Size - 1; }
            return y - MinHeight + x * Height + z * Height * Size;
        }

        public virtual int CalculateTopmostCellHeight(int x, int z) {
            int num = CalculateCellIndex(x, HeightMinusOne, z);
            int num2 = HeightMinusOne;
            while (num2 >= MinHeight) {   // [负高度实验] 原来到 0 就停
                if (Terrain.ExtractContents(GetCellValueFast(num)) != 0) {
                    return num2;
                }
                num2--;
                num--;
            }
            return 0;
        }

        // v0.1.4：分段寻址。**不变量**：索引访问器与 (x,y,z) 访问器必须落在同一格。
        //   * `CalculateCellIndex` 给的是"y 连续"的扁平索引：index = rel + x*Height + z*Height*Size
        //     （rel = y - MinHeight ∈ [0,Height)）。光照、顶面高度、切片内容哈希、方块扫描器
        //     以及地形生成器都按这个索引前后走动（index±1 就是 y±1）。
        //   * 存储按 256 层分段：段 seg = rel>>8，段内偏移 = (rel&255) + x*ColumnSliceHeight
        //     + z*ColumnSliceHeight*Size（这样一个竖直带只租 1 段，才有 -87.5% 的省内存）。
        //
        // [v0.1.8 修复] 旧实现把 index 直接当"段号 = index>>16、偏移 = index&0xFFFF"用 —— 那是
        // **另一套布局**（段内 x 步长 256、z 步长 4096），与 (x,y,z) 访问器（x 步长 2048、z 步长 32768）
        // 不一致。后果：光照/顶面/切片哈希读写的格子与真实方块数据不是同一批 →
        //   * `Terrain.GetTopHeight` 恒为 0（→ 超视距 LOD 采到"基岩高度"、地表寻路失效、
        //     切片哈希区间只覆盖 y≈0 附近的段，地面几何永不重建 → "有碰撞、无渲染"）；
        //   * 光照值写不进真实格子（→ 方块/手持物全黑）；
        //   * 地形生成器（走索引通道）生成的新区块在 (x,y,z) 视角下"地面消失"（只剩水与基岩）。
        // 现在显式换算一次：索引 → (rel, x, z) → (seg, offset)，两套通道从此一致。
        static void SplitColumnIndex(int index, out int seg, out int offset) {
            int rel = index & (Height - 1);
            int column = index >> HeightBits;                 // = x + z * Size
            seg = rel >> 8;                                   // / ColumnSliceHeight
            offset = (rel & (ColumnSliceHeight - 1)) + (column & SizeMinusOne) * ColumnSliceHeight
                + (column >> SizeBits) * (ColumnSliceHeight * Size);
        }

        public virtual int GetCellValueFast(int index) {
            SplitColumnIndex(index, out int seg, out int offset);
            int[] slice = Cells[seg];
            if (slice == null) {
                return UntouchedSliceValue(index);
            }
            return slice[offset];
        }

        /// <summary>
        /// v0.1.9：**未租借的段**（= 该段从没写过任何东西）里，"顶面以上"的空气语义上是被天空照亮的空气
        /// （light=15）。早先直接返回 0（无光空气）会把这一层语义丢掉：
        ///   * 第一人称手持方块/手的取光 `Terrain.GetCellLightFast(eye)` 在高空/地表之上读到 0 → **手里方块全黑**
        ///     （用户 2026-09-27 反馈的"高度超过 0-255 限制的光照问题"）；
        ///   * 实体/环境的平滑取光同理偏暗。
        /// 注意：只对"索引落在未租借段"的读取生效（热路径上只多一个 null 判断 + 一次 y 与顶面对比），
        /// 写入侧"向未租借段写空气不租内存"的省内存策略不变（见 SetCellValueFast）。
        /// </summary>
        const int SkyLitAirValue = 15 << 10;                 // air + light 15（与光照系统写的值一致）

        int UntouchedSliceValue(int index) {
            int rel = index & (Height - 1);
            int column = index >> HeightBits;                // = x + z * Size
            int x = column & SizeMinusOne;
            int z = column >> SizeBits;
            return rel + MinHeight > GetTopHeightFast(x, z) ? SkyLitAirValue : 0;
        }

        public virtual int GetCellValueFast(int x, int y, int z) {
            int rel = y - MinHeight;
            int[] slice = Cells[rel >> 8];
            if (slice == null) {
                return y > GetTopHeightFast(x, z) ? SkyLitAirValue : 0;
            }
            return slice[(rel & 255) + x * ColumnSliceHeight + z * ColumnSliceHeight * Size];
        }

        public virtual int GetCellValueFast(Point3 p) => GetCellValueFast(p.X, p.Y, p.Z);

        public virtual void SetCellValueFast(int x, int y, int z, int value) {
            int rel = y - MinHeight;
            int seg = rel >> 8;
            if (Cells[seg] == null) {
                // v0.1.4：向未分配段写"空气"= 无操作（竖直分节的省内存来源）。
                // 注意判据用 `contents == 0` 而不是 `value == 0`——光照系统会把空气写成
                // "contents=0 + light=15"（15360）这类值，若按 value==0 判，任何一次光照重算
                // 都会把 8 段全部租出来（实测 AllocatedColumnSlices=8）。
                if ((value & 0x3FF) == 0) {
                    return;
                }
                EnsureColumnSlice(seg);
            }
            Cells[seg][(rel & 255) + x * ColumnSliceHeight + z * ColumnSliceHeight * Size] = value;
        }

        public virtual void SetCellValueFast(Point3 p, int value) {
            SetCellValueFast(p.X, p.Y, p.Z, value);
        }

        public virtual void SetCellValueFast(int index, int value) {
            SplitColumnIndex(index, out int seg, out int offset);
            if (Cells[seg] == null) {
                if ((value & 0x3FF) == 0) {
                    return;
                }
                EnsureColumnSlice(seg);
            }
            Cells[seg][offset] = value;
        }

        public virtual int GetCellContentsFast(int x, int y, int z) => Terrain.ExtractContents(GetCellValueFast(x, y, z));

        public virtual int GetCellContentsFast(Point3 p) => Terrain.ExtractContents(GetCellValueFast(p));

        public virtual int GetCellLightFast(int x, int y, int z) => Terrain.ExtractLight(GetCellValueFast(x, y, z));

        public virtual int GetCellLightFast(Point3 p) => Terrain.ExtractLight(GetCellValueFast(p));

        public virtual long GetShaftValueFast(int x, int z) => Shafts[x + z * Size];

        public virtual long GetShaftValueFast(Point2 p) => Shafts[p.X + p.Y * Size];

        public virtual void SetShaftValueFast(int x, int z, long value) => Shafts[x + z * Size] = value;

        public virtual void SetShaftValueFast(Point2 p, long value) => Shafts[p.X + p.Y * Size] = value;

        public virtual int GetTemperatureFast(int x, int z) => Terrain.ExtractTemperature(GetShaftValueFast(x, z));

        public virtual int GetTemperatureFast(Point2 p) => Terrain.ExtractTemperature(GetShaftValueFast(p));

        public virtual void SetTemperatureFast(int x, int z, int temperature) => SetShaftValueFast(x, z, Terrain.ReplaceTemperature(GetShaftValueFast(x, z), temperature));

        public virtual void SetTemperatureFast(Point2 p, int temperature) => SetShaftValueFast(p, Terrain.ReplaceTemperature(GetShaftValueFast(p), temperature));

        public virtual int GetHumidityFast(int x, int z) => Terrain.ExtractHumidity(GetShaftValueFast(x, z));

        public virtual int GetHumidityFast(Point2 p) => Terrain.ExtractHumidity(GetShaftValueFast(p));

        public virtual void SetHumidityFast(int x, int z, int humidity) => SetShaftValueFast(x, z, Terrain.ReplaceHumidity(GetShaftValueFast(x, z), humidity));

        public virtual void SetHumidityFast(Point2 p, int humidity) => SetShaftValueFast(p, Terrain.ReplaceHumidity(GetShaftValueFast(p), humidity));

        public virtual int GetTopHeightFast(int x, int z) => Terrain.ExtractTopHeight(GetShaftValueFast(x, z));

        public virtual int GetTopHeightFast(Point2 p) => Terrain.ExtractTopHeight(GetShaftValueFast(p));

        public virtual void SetTopHeightFast(int x, int z, int topHeight) => SetShaftValueFast(x, z, Terrain.ReplaceTopHeight(GetShaftValueFast(x, z), topHeight));

        public virtual void SetTopHeightFast(Point2 p, int topHeight) => SetShaftValueFast(p, Terrain.ReplaceTopHeight(GetShaftValueFast(p), topHeight));

        public virtual int GetBottomHeightFast(int x, int z) => Terrain.ExtractBottomHeight(GetShaftValueFast(x, z));

        public virtual int GetBottomHeightFast(Point2 p) => Terrain.ExtractBottomHeight(GetShaftValueFast(p));

        public virtual void SetBottomHeightFast(int x, int z, int bottomHeight) => SetShaftValueFast(x, z, Terrain.ReplaceBottomHeight(GetShaftValueFast(x, z), bottomHeight));

        public virtual void SetBottomHeightFast(Point2 p, int bottomHeight) => SetShaftValueFast(p, Terrain.ReplaceBottomHeight(GetShaftValueFast(p), bottomHeight));

        public virtual int GetSunlightHeightFast(int x, int z) => Terrain.ExtractSunlightHeight(GetShaftValueFast(x, z));

        public virtual int GetSunlightHeightFast(Point2 p) => Terrain.ExtractSunlightHeight(GetShaftValueFast(p));

        public virtual void SetSunlightHeightFast(int x, int z, int sunlightHeight) => SetShaftValueFast(x, z, Terrain.ReplaceSunlightHeight(GetShaftValueFast(x, z), sunlightHeight));

        public virtual void SetSunlightHeightFast(Point2 p, int sunlightHeight) => SetShaftValueFast(p, Terrain.ReplaceSunlightHeight(GetShaftValueFast(p), sunlightHeight));

        public virtual void AddBrushPaint(int x, int y, int z, TerrainBrush brush) => m_brushPaints.Add(new BrushPaint { Position = new Point3(x, y, z), Brush = brush });

        public virtual void AddBrushPaint(Point3 p, TerrainBrush brush) => m_brushPaints.Add(new BrushPaint { Position = p, Brush = brush });

        public virtual void ApplyBrushPaints(TerrainChunk chunk) {
            foreach (BrushPaint brushPaint in m_brushPaints) {
                brushPaint.Brush.PaintFast(chunk, brushPaint.Position.X, brushPaint.Position.Y, brushPaint.Position.Z);
            }
        }
    }
}
