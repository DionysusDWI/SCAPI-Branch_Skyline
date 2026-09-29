using System.Runtime.CompilerServices;
using Engine;

namespace Game {
    public class Terrain : IDisposable {
        public class ChunksStorage {
            public const int Shift = 8;

            public const int Capacity = 65536;

            public const int CapacityMinusOne = 65535;

            /// <summary>
            /// ⚠️ **[v0.1.158 · CC `130316Z` P3#3] 遍历期间禁止删除**：本表从 v0.1.158 起用
            /// **回移删除**（`RemoveWithBackwardShift`），它会**移动遍历位置前后的条目**
            /// ⇒ 任何"边遍历 `m_array` 边 `Remove`"的写法都会漏读/重复读。
            /// 现状没有这种用法（释放循环走的是 `m_allocatedChunks` 的**快照**
            /// `m_allocatedChunksArray`，且变更单线程于主更新流；外部只持对象引用、不持槽位）——
            /// 但**新代码不许破坏它**。要删就先收集 key，遍历结束后再删；
            /// 一致性检查（`Diagnose`）只读，可以安全遍历。
            /// </summary>
            public TerrainChunk[] m_array = new TerrainChunk[Capacity];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public virtual TerrainChunk Get(int x, int y) {
                int num = (x + (y << Shift)) & CapacityMinusOne;
                TerrainChunk terrainChunk;
                while (true) {
                    terrainChunk = m_array[num];
                    if (terrainChunk == null) {
                        return null;
                    }
                    if (terrainChunk.Coords.X == x
                        && terrainChunk.Coords.Y == y) {
                        break;
                    }
                    num = (num + 1) & CapacityMinusOne;
                }
                return terrainChunk;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public virtual TerrainChunk Get(Point2 p) => Get(p.X, p.Y);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public virtual TerrainChunk Get(Point3 p) => Get(p.X, p.Z);

            public virtual void Add(int x, int y, TerrainChunk chunk) {
                int num = (x + (y << Shift)) & CapacityMinusOne;
                while (m_array[num] != null) {
                    num = (num + 1) & CapacityMinusOne;
                }
                m_array[num] = chunk;
            }

        /// <summary>
        /// [v0.1.157b · 修根因] **删除时回移同簇元素（Knuth TAOCP 3 §6.4 Algorithm R）**。
        ///
        /// 为什么必须这么删（`notes/263` 抓到的现象、`notes/153` 的僵尸区块）：
        /// 这是**线性探测**的开地址表，槽位被直接置空会在**簇中间留一个洞** ——
        /// 洞里原本经过的那些 key 从此 `Get` 返回 null（探测到洞就停），于是：
        ///   ① 引擎以为该坐标没加载 ⇒ `AllocateChunk` 的防重检查失效 ⇒ **同一坐标被分配两次**；
        ///   ② `FreeChunk` 在位序上先碰到哪个就删哪个 ⇒ 表里会留下**已 Dispose 或不在册**的条目
        ///      （门禁 `terrain-storage` 的 `arrayNonEmpty > allocated` 就是这个），
        ///      而那些区块带着 128 个 slice 几何与 VB/IB ⇒ **只增不减**（这正是用户红线的风险源）。
        ///
        /// 回移把"洞"顺着簇往后推，直到簇尾（空槽）为止 ⇒ 删除后**簇内仍然没有洞**，
        /// 其余 key 的探测链完好。表的装载率极低（455 / 65536），簇长只有个位数，代价可忽略；
        /// 可用 `Terrain.ChunkTableBackwardShiftRemove=false` 退回旧的"直接置空"（只为对照/回滚）。
        /// </summary>
        public virtual void Remove(int x, int y) {
            RemoveCalls++;
            if (!Terrain.ChunkTableBackwardShiftRemove) {
                RemoveLegacyNullOnly(x, y);
                return;
            }
            int num = (x + (y << Shift)) & CapacityMinusOne;
            while (true) {
                TerrainChunk terrainChunk = m_array[num];
                if (terrainChunk == null) {
                    return;
                }
                if (terrainChunk.Coords.X == x
                    && terrainChunk.Coords.Y == y) {
                    break;
                }
                num = (num + 1) & CapacityMinusOne;
            }
            RemoveWithBackwardShift(num);
        }

        /// <summary>**[对照用]** 旧语义：直接把槽位置空（会打断其他 key 的探测链，见上面的注释）。</summary>
        public virtual void RemoveLegacyNullOnly(int x, int y) {
            RemoveCalls++;
            int num = (x + (y << Shift)) & CapacityMinusOne;
            while (true) {
                TerrainChunk terrainChunk = m_array[num];
                if (terrainChunk == null) {
                    return;
                }
                if (terrainChunk.Coords.X == x
                    && terrainChunk.Coords.Y == y) {
                    break;
                }
                num = (num + 1) & CapacityMinusOne;
            }
            if (m_array[(num + 1) & CapacityMinusOne] != null) {
                ChainBreakingRemoves++;      // 旧语义下这里就是"确定性制造一个缺陷"的那一步
            }
            m_array[num] = null;
            LegacyNullRemoves++;
        }

        /// <summary>[v0.1.157b] 回移删除的核心：把 `hole` 处的洞顺着探测簇往后推。</summary>
        void RemoveWithBackwardShift(int hole) {
            // [v0.1.158 · CC P3#2] "簇中删除"计数：下一个槽非空 = 这次删除**在簇中间留洞**，
            // 在旧语义下必然切断别人的探测链（新语义把它填回来，但这个计数仍然是"缺陷生成率"的度量）
            if (m_array[(hole + 1) & CapacityMinusOne] != null) {
                ChainBreakingRemoves++;
            }
            m_array[hole] = null;
            int i = hole;
            int j = i;
            while (true) {
                j = (j + 1) & CapacityMinusOne;
                TerrainChunk c = m_array[j];
                if (c == null) {
                    return;                     // 簇结束：洞停在 i，链上没有断口
                }
                int home = (c.Coords.X + (c.Coords.Y << Shift)) & CapacityMinusOne;
                // Knuth Algorithm R 的移动判据（`i <= j` 分支专门处理"探测跨过表尾回卷"的簇）
                bool move = i <= j
                    ? (home <= i || home > j)
                    : (home <= i && home > j);
                if (move) {
                    m_array[i] = c;
                    m_array[j] = null;
                    i = j;
                    BackwardShiftMoves++;
                }
            }
        }

        // [v0.1.157b] 删除路径的只读账本（门禁/探针读）：
        //   `LegacyNullRemoves` 只在退档开关打开时增长；`BackwardShiftMoves` 是回移次数。
        public long BackwardShiftMoves;
        public long LegacyNullRemoves;
        /// <summary>删除调用的总次数（两条策略都计）——**用它才能区分"没有删除"与"删除但没发生回移"**。</summary>
        public long RemoveCalls;

        /// <summary>
        /// [v0.1.158 · CC `130316Z` P3#2] **"簇中删除"计数**：删除那一刻，被删槽的**下一个槽非空**
        /// ⇒ 这次删除在**簇中间**留了洞 ⇒ 在旧语义（直接置空）下**必然**切断某个 key 的探测链。
        /// 为什么必须单独计它：等"症状"（`arrayNonEmpty > allocated` 或重复分配）是**概率事件**，
        /// 在低装载率下可能要跑几小时才出现（本轮 3,100 次删除、`shiftMoves=0` 就说明
        /// **删除大多落在簇尾** ⇒ 症状型测量天然测不到）。这个计数器把"缺陷生成"变成**确定性可数**的：
        /// 浸泡时直接读"每千次删除的簇中删除数"。
        /// </summary>
        public long ChainBreakingRemoves;

        /// <summary>
        /// [v0.1.74] **诊断**：开地址表的一致性。
        ///
        /// 背景（这是"内存只增不减"的根因）：`Remove` 直接把槽位置空，而这是**线性探测**表 ——
        /// 置空会**打断其他 key 的探测链**：原本探测经过这个槽的 key 从此 `Get` 返回 null，
        /// 于是同一坐标会被**重复 `AllocateChunk`**（`Get != null` 的防重检查也因此失效），
        /// 旧的 TerrainChunk 既不在 `m_allocatedChunks` 里（**永远不会被 `FreeChunk`**）、
        /// 又占着 128 个 slice 几何与一堆 VB/IB ⇒ **僵尸区块只增不减**。
        /// 实测：堆里 1,092 个 `TerrainChunk`，而在册只有 ~208（`notes/153`）。
        /// </summary>
        public virtual string Diagnose(int allocatedCount) {
            int nonEmpty = 0;
            var seen = new Dictionary<long, int>();
            int duplicates = 0;
            for (int i = 0; i < Capacity; i++) {
                TerrainChunk chunk = m_array[i];
                if (chunk == null) {
                    continue;
                }
                nonEmpty++;
                long key = ((long)chunk.Coords.X << 32) ^ (uint)chunk.Coords.Y;
                if (seen.TryGetValue(key, out int n)) {
                    seen[key] = n + 1;
                    duplicates++;
                }
                else {
                    seen[key] = 1;
                }
            }
            return $"{{\"allocated\":{allocatedCount},\"arrayNonEmpty\":{nonEmpty},"
                + $"\"distinctCoords\":{seen.Count},\"duplicates\":{duplicates},\"capacity\":{Capacity},"
                // ⚠️ 布尔必须输出 JSON 的小写 true/false —— C# 的 `$"{bool}"` 给的是 `True`，
                //    那会让严格解析（python `json.loads`）直接失败（本轮踩到）。
                + $"\"backwardShiftRemove\":{(Terrain.ChunkTableBackwardShiftRemove ? "true" : "false")},"
                + $"\"removeCalls\":{RemoveCalls},\"shiftMoves\":{BackwardShiftMoves},"
                + $"\"legacyNullRemoves\":{LegacyNullRemoves},"
                + $"\"chainBreakingRemoves\":{ChainBreakingRemoves}}}";
        }
        }

        public const int ContentsMask = 1023;

        public const int LightMask = 15360;

        public const int LightShift = 10;

        public const int DataMask = -16384;

        public const int DataShift = 14;

        // [负高度实验] shaft（每列元数据）在 long 里重新排：三个高度字段各 11 位（存 height - MinHeight，
        // 于是 -128 也能表示），温度/湿度各 4 位 → 11+4+4+11+11 = 41 位，仍放得进 64 位。
        public const long TopHeightMask = 0x7FFL;             // bits 0..10

        public const int TopHeightShift = 0;

        public const long TemperatureMask = 0xFL << 11;       // bits 11..14

        public const int TemperatureShift = 11;

        public const long HumidityMask = 0xFL << 15;          // bits 15..18

        public const int HumidityShift = 15;

        public const long BottomHeightMask = 0x7FFL << 19;    // bits 19..29

        public const int BottomHeightShift = 19;

        public const long SunlightHeightMask = 0x7FFL << 30;  // bits 30..40

        public const int SunlightHeightShift = 30;

        public ChunksStorage m_allChunks;

        public HashSet<TerrainChunk> m_allocatedChunks;

        public TerrainChunk[] m_allocatedChunksArray;

        public int SeasonTemperature;

        public int SeasonHumidity;

        public virtual TerrainChunk[] AllocatedChunks {
            get {
                if (m_allocatedChunksArray == null) {
                    m_allocatedChunksArray = m_allocatedChunks.ToArray();
                }
                return m_allocatedChunksArray;
            }
        }

        public Terrain() {
            m_allChunks = new ChunksStorage();
            m_allocatedChunks = [];
        }

        public virtual void Dispose() {
            foreach (TerrainChunk allocatedChunk in m_allocatedChunks) {
                allocatedChunk.Dispose();
            }
        }

        public virtual TerrainChunk LoopChunks(int startChunkX, int startChunkZ, bool skipStartChunk, out bool hasLooped) {
            hasLooped = false;
            TerrainChunk terrainChunk = null;
            if (!skipStartChunk) {
                terrainChunk = GetChunkAtCoords(startChunkX, startChunkZ);
                if (terrainChunk != null) {
                    return terrainChunk;
                }
            }
            TerrainChunk[] allocatedChunks = AllocatedChunks;
            for (int i = 0; i < allocatedChunks.Length; i++) {
                if (ComparePoints(allocatedChunks[i].Coords, new Point2(startChunkX, startChunkZ)) > 0
                    && (terrainChunk == null || ComparePoints(allocatedChunks[i].Coords, terrainChunk.Coords) < 0)) {
                    terrainChunk = allocatedChunks[i];
                }
            }
            if (terrainChunk == null) {
                for (int j = 0; j < allocatedChunks.Length; j++) {
                    if (terrainChunk == null
                        || ComparePoints(allocatedChunks[j].Coords, terrainChunk.Coords) < 0) {
                        terrainChunk = allocatedChunks[j];
                        hasLooped = true;
                    }
                }
            }
            return terrainChunk;
        }

        public virtual TerrainChunk LoopChunks(int startChunkX, int startChunkZ, bool skipStartChunk) =>
            LoopChunks(startChunkX, startChunkZ, skipStartChunk, out bool _);

        public virtual TerrainChunk GetChunkAtCoords(int chunkX, int chunkZ) => m_allChunks.Get(chunkX, chunkZ);

        public virtual TerrainChunk GetChunkAtCoords(Point2 p) => m_allChunks.Get(p.X, p.Y);

        public virtual TerrainChunk GetChunkAtCoords(int chunkX, int chunkY, int chunkZ) =>
            chunkY is >= 0 and < TerrainChunk.Height / TerrainChunk.Size ? m_allChunks.Get(chunkX, chunkZ) : null;

        public virtual TerrainChunk GetChunkAtCoords(Point3 chunkP) => chunkP.Y is >= 0 and < TerrainChunk.Height / TerrainChunk.Size ? m_allChunks.Get(chunkP.X, chunkP.Z) : null;

        public virtual TerrainChunk GetChunkAtCell(int x, int z) => GetChunkAtCoords(x >> TerrainChunk.SizeBits, z >> TerrainChunk.SizeBits);

        public virtual TerrainChunk GetChunkAtCell(Point2 p) => GetChunkAtCoords(p.X >> TerrainChunk.SizeBits, p.Y >> TerrainChunk.SizeBits);

        public virtual TerrainChunk GetChunkAtCell(int x, int y, int z) => y is >= TerrainChunk.MinHeight and <= TerrainChunk.HeightMinusOne
            ? m_allChunks.Get(x >> TerrainChunk.SizeBits, z >> TerrainChunk.SizeBits)
            : null;

        public virtual TerrainChunk GetChunkAtCell(Point3 p) => p.Y is >= TerrainChunk.MinHeight and <= TerrainChunk.HeightMinusOne
            ? m_allChunks.Get(p.X >> TerrainChunk.SizeBits, p.Z >> TerrainChunk.SizeBits)
            : null;

        public virtual TerrainChunk AllocateChunk(int chunkX, int chunkZ) {
            if (GetChunkAtCoords(chunkX, chunkZ) != null) {
                throw new InvalidOperationException("Chunk already allocated.");
            }
            // [v0.1.156 · CC `074658Z` Q2 裁定] **只读计时**：把"分配"的成本量出来，
            //   好把预加载里那 ~0.7 s **未归因**时间压到 10% 以内（`notes/248 P3` 同一条）。
            //   只累加计数与耗时，不改任何行为。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            TerrainChunk terrainChunk = new(this, chunkX, chunkZ);
            m_allocatedChunks.Add(terrainChunk);
            m_allChunks.Add(chunkX, chunkZ, terrainChunk);
            m_allocatedChunksArray = null;
            AllocCount++;
            AllocMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                       / System.Diagnostics.Stopwatch.Frequency;
            return terrainChunk;
        }

        public virtual void FreeChunk(TerrainChunk chunk) {
            if (!m_allocatedChunks.Remove(chunk)) {
                throw new InvalidOperationException("Chunk not allocated.");
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            m_allChunks.Remove(chunk.Coords.X, chunk.Coords.Y);
            m_allocatedChunksArray = null;
            chunk.Dispose();
            FreeCount++;
            FreeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                      / System.Diagnostics.Stopwatch.Frequency;
        }

        // [v0.1.156] 分配/释放的只读账本（累计；探针读，不重置）
        public long AllocCount;
        public double AllocMs;
        public long FreeCount;
        public double FreeMs;

        /// <summary>
        /// [v0.1.157b] **开地址区块表的删除策略开关**（默认 **true** = 回移删除）。
        /// `false` 退回旧语义（直接置空，会打断其他 key 的探测链 ⇒ 重复分配 + 僵尸区块，
        /// 见 `notes/263`）。留这个开关只是为了**同一份二进制里跑对照**与紧急回滚，
        /// 不是"两个都合理"——默认必须是修好的那一支。
        /// </summary>
        public static bool ChunkTableBackwardShiftRemove = true;

        public static int ComparePoints(Point2 c1, Point2 c2) {
            if (c1.Y != c2.Y) {
                return c1.Y <= c2.Y ? -1 : 1;
            }
            if (c1.X != c2.X) {
                return c1.X <= c2.X ? -1 : 1;
            }
            return 0;
        }

        public static Point2 ToChunk(Vector2 p) => ToChunk(ToCell(p.X), ToCell(p.Y));

        public static Point2 ToChunk(int x, int z) => new(x >> TerrainChunk.SizeBits, z >> TerrainChunk.SizeBits);

        public static int ToCell(float x) => (int)MathF.Floor(x);

        public static Point2 ToCell(float x, float y) => new((int)MathF.Floor(x), (int)MathF.Floor(y));

        public static Point2 ToCell(Vector2 p) => new((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));

        public static Point3 ToCell(float x, float y, float z) => new((int)MathF.Floor(x), (int)MathF.Floor(y), (int)MathF.Floor(z));

        public static Point3 ToCell(Vector3 p) => new((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));

        public virtual bool IsCellValid(int x, int y, int z) => y is >= TerrainChunk.MinHeight and <= TerrainChunk.HeightMinusOne;

        public virtual bool IsCellValid(Point3 p) => p.Y is >= TerrainChunk.MinHeight and <= TerrainChunk.HeightMinusOne;

        public virtual int GetCellValue(int x, int y, int z) => !IsCellValid(x, y, z) ? 0 : GetCellValueFast(x, y, z);

        public virtual int GetCellValue(Point3 p) => !IsCellValid(p) ? 0 : GetCellValueFast(p);

        public virtual int GetCellContents(int x, int y, int z) => !IsCellValid(x, y, z) ? 0 : GetCellContentsFast(x, y, z);

        public virtual int GetCellContents(Point3 p) => !IsCellValid(p) ? 0 : GetCellContentsFast(p);

        public virtual int GetCellLight(int x, int y, int z) => !IsCellValid(x, y, z) ? 0 : GetCellLightFast(x, y, z);

        public virtual int GetCellLight(Point3 p) => !IsCellValid(p) ? 0 : GetCellLightFast(p);

        public virtual int GetCellValueFast(int x, int y, int z) => GetChunkAtCell(x, z)?.GetCellValueFast(x & 0xF, y, z & 0xF) ?? 0;

        public virtual int GetCellValueFast(Point3 p) => GetChunkAtCell(p)?.GetCellValueFast(p.X & 0xF, p.Y, p.Z & 0xF) ?? 0;

        public virtual int GetCellValueFastChunkExists(int x, int y, int z) => GetChunkAtCell(x, z).GetCellValueFast(x & 0xF, y, z & 0xF);

        public virtual int GetCellValueFastChunkExists(Point3 p) => GetChunkAtCell(p).GetCellValueFast(p.X & 0xF, p.Y, p.Z & 0xF);

        public virtual int GetCellContentsFast(int x, int y, int z) => ExtractContents(GetCellValueFast(x, y, z));

        public virtual int GetCellContentsFast(Point3 p) => ExtractContents(GetCellValueFast(p));

        public virtual int GetCellLightFast(int x, int y, int z) => ExtractLight(GetCellValueFast(x, y, z));

        public virtual int GetCellLightFast(Point3 p) => ExtractLight(GetCellValueFast(p));

        // [v0.1.156 · CC `075119Z` P3] **让"静默丢弃"可观测**（只加计数，不改行为）：
        //   `?.` 在邻居区块**未分配**时把这次写入**静默丢掉**，于是"跨轮逐位比对"会随
        //   "那一刻邻居在不在场"而漂移（已有确定性缺口，非并行引入）。审计要求：要么在文档里
        //   固定"邻居在场"前置，要么给丢弃加显式计数器。这里两件都做 —— 计数器 + 探针。
        public long DroppedNeighborWrites;
        public long BoundaryWrites;

        public virtual void SetCellValueFast(int x, int y, int z, int value) {
            TerrainChunk chunkAtCell = GetChunkAtCell(x, z);
            if (chunkAtCell != null) {
                BoundaryWrites++;
                chunkAtCell.SetCellValueFast(x & 0xF, y, z & 0xF, value);
            }
            else {
                DroppedNeighborWrites++;
            }
        }

        public virtual void SetCellValueFast(Point3 p, int value) {
            TerrainChunk chunkAtCell = GetChunkAtCell(p.X, p.Z);
            if (chunkAtCell != null) {
                BoundaryWrites++;
                chunkAtCell.SetCellValueFast(p.X & 0xF, p.Y, p.Z & 0xF, value);
            }
            else {
                DroppedNeighborWrites++;
            }
        }

        public virtual int CalculateTopmostCellHeight(int x, int z) => GetChunkAtCell(x, z)?.CalculateTopmostCellHeight(x & 0xF, z & 0xF) ?? 0;

        public virtual int CalculateTopmostCellHeight(Point2 p) => GetChunkAtCell(p.X, p.Y)?.CalculateTopmostCellHeight(p.X & 0xF, p.Y & 0xF) ?? 0;

        public virtual long GetShaftValue(int x, int z) => GetChunkAtCell(x, z)?.GetShaftValueFast(x & 0xF, z & 0xF) ?? 0L;

        public virtual long GetShaftValue(Point2 p) => GetChunkAtCell(p.X, p.Y)?.GetShaftValueFast(p.X & 0xF, p.Y & 0xF) ?? 0L;

        public virtual void SetShaftValue(int x, int z, long value) => GetChunkAtCell(x, z)?.SetShaftValueFast(x & 0xF, z & 0xF, value);

        public virtual void SetShaftValue(Point2 p, long value) => GetChunkAtCell(p.X, p.Y)?.SetShaftValueFast(p.X & 0xF, p.Y & 0xF, value);

        public virtual int GetTemperature(int x, int z) => ExtractTemperature(GetShaftValue(x, z));

        public virtual int GetTemperature(Point2 p) => ExtractTemperature(GetShaftValue(p));

        public virtual void SetTemperature(int x, int z, int temperature) => SetShaftValue(x, z, ReplaceTemperature(GetShaftValue(x, z), temperature));

        public virtual void SetTemperature(Point2 p, int temperature) => SetShaftValue(p, ReplaceTemperature(GetShaftValue(p), temperature));

        public virtual int GetHumidity(int x, int z) => ExtractHumidity(GetShaftValue(x, z));

        public virtual int GetHumidity(Point2 p) => ExtractHumidity(GetShaftValue(p));

        public virtual void SetHumidity(int x, int z, int humidity) => SetShaftValue(x, z, ReplaceHumidity(GetShaftValue(x, z), humidity));

        public virtual int GetTopHeight(int x, int z) => ExtractTopHeight(GetShaftValue(x, z));

        public virtual void SetTopHeight(int x, int z, int topHeight) => SetShaftValue(x, z, ReplaceTopHeight(GetShaftValue(x, z), topHeight));

        public virtual int GetBottomHeight(int x, int z) => ExtractBottomHeight(GetShaftValue(x, z));

        public virtual void SetBottomHeight(int x, int z, int bottomHeight) => SetShaftValue(x, z, ReplaceBottomHeight(GetShaftValue(x, z), bottomHeight));

        public virtual int GetSunlightHeight(int x, int z) => ExtractSunlightHeight(GetShaftValue(x, z));

        public virtual int GetSunlightHeight(Point2 p) => ExtractSunlightHeight(GetShaftValue(p));

        public virtual void SetSunlightHeight(int x, int z, int sunlightHeight) => SetShaftValue(x, z, ReplaceSunlightHeight(GetShaftValue(x, z), sunlightHeight));

        public virtual void SetSunlightHeight(Point2 p, int sunlightHeight) => SetShaftValue(p, ReplaceSunlightHeight(GetShaftValue(p), sunlightHeight));

        public static int MakeBlockValue(int contents) => contents & ContentsMask;

        public static int MakeBlockValue(int contents, int light, int data) =>
            (contents & ContentsMask) | ((light << LightShift) & LightMask) | ((data << DataShift) & DataMask);

        public static int ExtractContents(int value) => value & ContentsMask;

        public static int ExtractLight(int value) => (value & LightMask) >> LightShift;

        public static int ExtractData(int value) => (value & DataMask) >> DataShift;

        // [负高度实验] 三个高度字段都存 (height - MinHeight)，取出时加回来
        public static int ExtractTopHeight(long value) => (int)(value & TopHeightMask) + TerrainChunk.MinHeight;

        public static int ExtractBottomHeight(long value) => (int)((value & BottomHeightMask) >> BottomHeightShift) + TerrainChunk.MinHeight;

        public static int ExtractSunlightHeight(long value) => (int)(((ulong)value >> SunlightHeightShift) & 0x7FF) + TerrainChunk.MinHeight;

        public static int ExtractHumidity(long value) => (int)((value & HumidityMask) >> HumidityShift);

        public static int ExtractTemperature(long value) => (int)((value & TemperatureMask) >> TemperatureShift);

        /// <summary>
        ///     方块值的最低10位，替换为目标Content
        /// </summary>
        public static int ReplaceContents(int value, int contents) => value ^ ((value ^ contents) & ContentsMask);

        /// <summary>
        ///     方块值的最低10位，替换为目标Content(value始终为0时)
        /// </summary>
        public static int ReplaceContents(int contents) => contents & ContentsMask;

        public static int ReplaceLight(int value, int light) => value ^ ((value ^ (light << LightShift)) & LightMask);

        public static int ReplaceData(int value, int data) => value ^ ((value ^ (data << DataShift)) & DataMask);

        public static long ReplaceTopHeight(long value, int topHeight) =>
            (value & ~TopHeightMask) | ((long)(topHeight - TerrainChunk.MinHeight) & TopHeightMask);

        public static long ReplaceBottomHeight(long value, int bottomHeight) =>
            (value & ~BottomHeightMask) | (((long)(bottomHeight - TerrainChunk.MinHeight) & 0x7FF) << BottomHeightShift);

        public static long ReplaceSunlightHeight(long value, int sunlightHeight) =>
            (value & ~SunlightHeightMask) | (((long)(sunlightHeight - TerrainChunk.MinHeight) & 0x7FF) << SunlightHeightShift);

        public static long ReplaceHumidity(long value, int humidity) => value ^ ((value ^ ((long)humidity << HumidityShift)) & HumidityMask);

        public static long ReplaceTemperature(long value, int temperature) => value ^ ((value ^ ((long)temperature << TemperatureShift)) & TemperatureMask);

        public virtual int GetSeasonalTemperature(int x, int z) => Math.Max(GetTemperature(x, z) + SeasonTemperature, 0);

        public virtual int GetSeasonalTemperature(long shaftValue) => Math.Max(ExtractTemperature(shaftValue) + SeasonTemperature, 0);

        public virtual int GetSeasonalHumidity(int x, int z) => Math.Max(GetHumidity(x, z) + SeasonHumidity, 0);

        public virtual int GetSeasonalHumidity(long shaftValue) => Math.Max(ExtractHumidity(shaftValue) + SeasonHumidity, 0);
    }
}
