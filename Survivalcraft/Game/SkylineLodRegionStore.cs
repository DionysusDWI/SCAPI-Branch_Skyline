using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.73：**LOD 单元按区域落盘 + 按需回读** —— 里程碑 2.2 的收官。
    ///
    /// ## 要解决的问题（v0.1.70 实测出来的那一条）
    /// LOD 单元表**就是"走过就记住"的轨迹缓存**：视距只有 128 m，而 LOD 要画 `[136, 1024] m` 那一圈，
    /// 那些数据**只能**来自"玩家曾经走近时采下来的"。所以 v0.1.70 里"按距离把单元扔掉"的尝试
    /// **实测会把远景一起丢掉**（同位置 `cellsInMesh` 148 → 0），只能默认关。
    ///
    /// 真正的解法来自 `notes/150`（DH 深挖）：**数据在磁盘、内存只留视距内的**。
    /// 本文件就是这条：把单元表按 **512 m × 512 m 的区域**切成小文件，
    /// 相机附近的区域常驻内存，远的区域**写盘后从内存移除**，走回去时**按需回读**。
    ///
    /// ## 口径
    ///   * 区域 = `RegionCells`(=32) × `CellSize`(=16 m) = **512 m 见方**；两层（16 m 粗 / 8 m 细）共用同一张区域网格；
    ///   * 常驻半径 `RegionKeepMetres`（默认 **2048 m**）—— 比 LOD 最大绘制半径（1024 m）大一倍，
    ///     所以"绘制需要的单元一定在内存里"；淘汰半径就是它（同一个值，避免"刚丢又要读"）；
    ///   * **回读有预算**：每 Tick 最多 `RegionLoadPerTick`(默认 4) 个区域，避免一次走远就卡一下；
    ///   * 旧格式（`SkylineLod.bin` v3）**自动迁移**：读进内存 → 标脏 → 下一次保存时按区域写出，
    ///     旧文件改名为 `SkylineLod.v3.bak`（不删，可回退）；
    ///   * `RegionStoreEnabled=false` 时**完全走旧路径**（整表单文件），可做 A/B。
    ///
    /// ## 诚实边界
    ///   * 区域文件是**整区域重写**（不是增量追加）：一个区域 ~几十 KB，重写代价可接受，
    ///     而且比套用壳仓的"追加 + 墓碑 + 压实"简单得多；
    ///   * **第二层表面字段（Height2/Value2/Light2/HasSecond）不落盘** —— 与 v0.1.72 的旧格式一致
    ///     （第二层默认关，且它是 v0.1.62 的负面结果）；要落盘就得升版本号，本轮不做；
    ///   * 淘汰是**按区域**的，不是按单元 —— 区域边界上会多留最多 512 m 的一圈（是笔可算的浪费）。
    /// </summary>
    public static partial class SkylineLod {
        /// <summary>[v0.1.73] 区域仓开关。默认开。</summary>
        public static bool RegionStoreEnabled { get; set; } = true;

        /// <summary>常驻半径（米）。默认 2048 = LOD 最大绘制半径 1024 的两倍。</summary>
        public static float RegionKeepMetres { get; set; } = 2048f;

        /// <summary>每 Tick 最多回读几个区域（防"一次性走远"造成卡顿）。</summary>
        public static int RegionLoadPerTick { get; set; } = 4;

        /// <summary>区域边长（以 16 m 粗格计）。32 × 16 m = 512 m。</summary>
        public const int RegionCells = 32;

        /// <summary>区域边长（米）。</summary>
        public static float RegionMetres => RegionCells * CellSize;

        static readonly HashSet<long> m_residentRegions = [];
        static readonly HashSet<long> m_knownRegions = [];
        static readonly HashSet<long> m_dirtyRegions = [];
        static long m_regionsLoaded;
        static long m_regionsSaved;
        static long m_regionsEvicted;
        static string m_regionLastError = "";
        static double m_regionLastTick;

        public static int RegionsResident => m_residentRegions.Count;
        public static int RegionsKnown => m_knownRegions.Count;
        public static int RegionsDirty => m_dirtyRegions.Count;
        public static long RegionsLoadedTotal => m_regionsLoaded;
        public static long RegionsSavedTotal => m_regionsSaved;
        public static long RegionsEvictedTotal => m_regionsEvicted;

        static long RegionKey(int rx, int rz) => ((long)rx << 32) ^ (uint)rz;

        static void UnpackRegion(long key, out int rx, out int rz) {
            rx = (int)(key >> 32);
            rz = (int)(key & 0xFFFFFFFF);
        }

        /// <summary>世界坐标 → 区域下标（负数也正确：用 floor 而不是截断）。</summary>
        static int RegionIndexOf(float world) => (int)MathF.Floor(world / RegionMetres);

        static long RegionKeyOfCoarseCell(long cellKey) {
            int cx = (int)(cellKey >> 32), cz = (int)(cellKey & 0xFFFFFFFF);
            return RegionKey(RegionIndexOf((cx << CellShift) + CellSize * 0.5f),
                             RegionIndexOf((cz << CellShift) + CellSize * 0.5f));
        }

        static long RegionKeyOfFineCell(long cellKey) {
            int cx = (int)(cellKey >> 32), cz = (int)(cellKey & 0xFFFFFFFF);
            return RegionKey(RegionIndexOf((cx << FineShift) + FineSize * 0.5f),
                             RegionIndexOf((cz << FineShift) + FineSize * 0.5f));
        }

        static long CameraRegionKey() {
            Vector3 camera = CameraViewPosition();
            return RegionKey(RegionIndexOf(camera.X), RegionIndexOf(camera.Z));
        }

        static string RegionDir() {
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (info == null || string.IsNullOrEmpty(info.DirectoryName)) {
                return null;
            }
            return Storage.CombinePaths(info.DirectoryName, "SkylineLodRegions");
        }

        static string RegionPath(int rx, int rz) =>
            Storage.CombinePaths(RegionDir(), $"r_{rx}_{rz}.bin");

        /// <summary>采集到新单元时调用：把该单元所在区域标脏并标记为常驻。</summary>
        static void MarkCoarseRegionDirty(long coarseKey) {
            if (!RegionStoreEnabled) {
                return;
            }
            long rk = RegionKeyOfCoarseCell(coarseKey);
            m_dirtyRegions.Add(rk);
            m_residentRegions.Add(rk);
        }

        static void MarkFineRegionDirty(long fineKey) {
            if (!RegionStoreEnabled) {
                return;
            }
            long rk = RegionKeyOfFineCell(fineKey);
            m_dirtyRegions.Add(rk);
            m_residentRegions.Add(rk);
        }

        // ============================================================================================
        // 存 / 读
        // ============================================================================================

        const uint RegionMagic = 0x524C4B53;   // 'SKLR'
        const int RegionVersion = 1;

        static void WriteCell(BinaryWriter writer, Cell cell) {
            writer.Write(cell.Height);
            writer.Write(cell.Value);
            writer.Write(cell.Light);
        }

        static Cell ReadCell(BinaryReader reader) => new() {
            Height = reader.ReadInt16(),
            Value = reader.ReadUInt16(),
            Light = reader.ReadByte()
        };

        /// <summary>把一个区域**整块重写**（该区域内两层单元一起写）。</summary>
        static void SaveRegion(long regionKey) {
            int rx, rz;
            UnpackRegion(regionKey, out rx, out rz);
            string path = RegionPath(rx, rz);
            if (path == null) {
                return;
            }
            // 目录可能还不存在（淘汰路径会直接调到这里，而 `SaveDirtyRegions` 的那次建目录不一定先跑过）
            string dir = RegionDir();
            if (dir != null && !Storage.DirectoryExists(dir)) {
                Storage.CreateDirectory(dir);
            }
            List<long> coarseKeys = [];
            foreach (KeyValuePair<long, Cell> pair in m_cells) {
                if (RegionKeyOfCoarseCell(pair.Key) == regionKey) {
                    coarseKeys.Add(pair.Key);
                }
            }
            List<long> fineKeys = [];
            foreach (KeyValuePair<long, Cell> pair in m_cellsFine) {
                if (RegionKeyOfFineCell(pair.Key) == regionKey) {
                    fineKeys.Add(pair.Key);
                }
            }
            using (Stream stream = Storage.OpenFile(path, OpenFileMode.Create)) {
                var writer = new BinaryWriter(stream);
                writer.Write(RegionMagic);
                writer.Write(RegionVersion);
                writer.Write(rx);
                writer.Write(rz);
                writer.Write(coarseKeys.Count);
                foreach (long key in coarseKeys) {
                    writer.Write(key);
                    WriteCell(writer, m_cells[key]);
                }
                writer.Write(fineKeys.Count);
                foreach (long key in fineKeys) {
                    writer.Write(key);
                    WriteCell(writer, m_cellsFine[key]);
                }
            }
            m_knownRegions.Add(regionKey);
            m_regionsSaved++;
        }

        /// <summary>回读一个区域（把它的单元插回内存）。返回是否有变化。</summary>
        static bool LoadRegion(long regionKey) {
            int rx, rz;
            UnpackRegion(regionKey, out rx, out rz);
            string path = RegionPath(rx, rz);
            if (path == null || !Storage.FileExists(path)) {
                return false;
            }
            using (Stream stream = Storage.OpenFile(path, OpenFileMode.Read)) {
                var reader = new BinaryReader(stream);
                uint magic = reader.ReadUInt32();
                int version = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                if (magic != RegionMagic || version != RegionVersion) {
                    m_regionLastError = $"region {rx},{rz} bad header (magic={magic:x} v={version})";
                    return false;
                }
                int coarseCount = reader.ReadInt32();
                for (int i = 0; i < coarseCount; i++) {
                    long key = reader.ReadInt64();
                    m_cells[key] = ReadCell(reader);
                }
                int fineCount = reader.ReadInt32();
                for (int i = 0; i < fineCount; i++) {
                    long key = reader.ReadInt64();
                    m_cellsFine[key] = ReadCell(reader);
                }
            }
            m_residentRegions.Add(regionKey);
            m_knownRegions.Add(regionKey);
            m_regionsLoaded++;
            return true;
        }

        /// <summary>保存所有脏区域（按区域整块重写）。</summary>
        static void SaveDirtyRegions() {
            if (m_dirtyRegions.Count == 0) {
                return;
            }
            if (RegionDir() == null) {
                return;
            }
            if (!Storage.DirectoryExists(RegionDir())) {
                Storage.CreateDirectory(RegionDir());
            }
            long[] keys = new long[m_dirtyRegions.Count];
            m_dirtyRegions.CopyTo(keys);
            m_dirtyRegions.Clear();
            foreach (long key in keys) {
                try {
                    SaveRegion(key);
                }
                catch (Exception e) {
                    m_regionLastError = e.Message;
                    m_dirtyRegions.Add(key);      // 写失败就留着，下一轮再试
                }
            }
        }

        /// <summary>
        /// 每 Tick：①把相机附近、磁盘上有、内存里没有的区域**按需回读**；②把超出保留半径的区域
        /// **写盘后从内存移除**。两个方向都用同一个半径，避免"刚丢掉又要读回来"的抖动。
        /// </summary>
        static void RegionStoreTick() {
            if (!RegionStoreEnabled) {
                return;
            }
            string dir = RegionDir();
            if (dir == null) {
                return;
            }
            double now = Time.RealTime;
            if (now - m_regionLastTick < 0.5) {
                return;                                  // 0.5 s 一次足够，避免每帧扫区域
            }
            m_regionLastTick = now;
            Vector3 camera = CameraViewPosition();
            int centreRx = RegionIndexOf(camera.X);
            int centreRz = RegionIndexOf(camera.Z);
            int span = (int)MathF.Ceiling(RegionKeepMetres / RegionMetres);
            float keepSq = RegionKeepMetres * RegionKeepMetres;

            // ① 按需回读
            int loadedThisTick = 0;
            for (int dx = -span; dx <= span && loadedThisTick < RegionLoadPerTick; dx++) {
                for (int dz = -span; dz <= span && loadedThisTick < RegionLoadPerTick; dz++) {
                    int rx = centreRx + dx, rz = centreRz + dz;
                    long key = RegionKey(rx, rz);
                    if (m_residentRegions.Contains(key) || !m_knownRegions.Contains(key)) {
                        continue;
                    }
                    // [v0.1.73 修复] **回读判据必须与淘汰判据同口径（同一个圆）**。
                    // 第一版回读用的是方形 span、淘汰用的是圆形半径 → 方形四角的区域
                    // 会被"读进来 → 同一 tick 判定超距 → 写盘扔掉"**无限抖动**：
                    // 实测一分钟 647 次读 / 648 次淘汰（磁盘与主线程都被白烧）。
                    float dcx = (rx + 0.5f) * RegionMetres - camera.X;
                    float dcz = (rz + 0.5f) * RegionMetres - camera.Z;
                    if (dcx * dcx + dcz * dcz > keepSq) {
                        continue;
                    }
                    if (LoadRegion(key)) {
                        loadedThisTick++;
                        m_dirty = true;
                    }
                }
            }

            // ② 距离之外的区域写盘并移出内存
            List<long> evict = null;
            foreach (long key in m_residentRegions) {
                int rx, rz;
                UnpackRegion(key, out rx, out rz);
                float cx = (rx + 0.5f) * RegionMetres - camera.X;
                float cz = (rz + 0.5f) * RegionMetres - camera.Z;
                if (cx * cx + cz * cz > keepSq) {
                    (evict ??= []).Add(key);
                }
            }
            if (evict == null) {
                return;
            }
            foreach (long key in evict) {
                try {
                    SaveRegion(key);                     // 先落盘（内部会把它加进 known）
                    RemoveRegionCells(key);
                    m_residentRegions.Remove(key);
                    m_dirtyRegions.Remove(key);
                    m_regionsEvicted++;
                }
                catch (Exception e) {
                    m_regionLastError = e.Message;
                }
            }
            if (evict.Count > 0) {
                m_dirty = true;                          // 网格要按新的常驻集合重建
            }
        }

        static void RemoveRegionCells(long regionKey) {
            List<long> remove = null;
            foreach (long key in m_cells.Keys) {
                if (RegionKeyOfCoarseCell(key) == regionKey) {
                    (remove ??= []).Add(key);
                }
            }
            if (remove != null) {
                foreach (long key in remove) {
                    m_cells.Remove(key);
                }
            }
            List<long> removeFine = null;
            foreach (long key in m_cellsFine.Keys) {
                if (RegionKeyOfFineCell(key) == regionKey) {
                    (removeFine ??= []).Add(key);
                }
            }
            if (removeFine != null) {
                foreach (long key in removeFine) {
                    m_cellsFine.Remove(key);
                }
            }
        }

        /// <summary>启动时扫一遍区域目录，建立"磁盘上有哪些区域"的清单。</summary>
        static void ScanKnownRegions() {
            m_knownRegions.Clear();
            string dir = RegionDir();
            if (dir == null || !Storage.DirectoryExists(dir)) {
                return;
            }
            try {
                foreach (string name in Storage.ListFileNames(dir)) {
                    string file = Storage.GetFileName(name);
                    if (!file.StartsWith("r_") || !file.EndsWith(".bin")) {
                        continue;
                    }
                    string body = file[2..^4];
                    int split = body.IndexOf('_');
                    if (split <= 0) {
                        continue;
                    }
                    if (int.TryParse(body[..split], out int rx) && int.TryParse(body[(split + 1)..], out int rz)) {
                        m_knownRegions.Add(RegionKey(rx, rz));
                    }
                }
            }
            catch (Exception e) {
                m_regionLastError = e.Message;
            }
        }

        static void ResetRegionState() {
            m_residentRegions.Clear();
            m_knownRegions.Clear();
            m_dirtyRegions.Clear();
            m_regionLastTick = 0;
        }

        /// <summary>启动/切世界时把相机附近的区域一次性读进来（**不受每 Tick 预算限制**：
        /// 启动本来就有一段加载时间，逐步读会让"刚进世界时远景是空的"）。</summary>
        static void LoadNearbyRegions() {
            Vector3 camera = CameraViewPosition();
            int centreRx = RegionIndexOf(camera.X);
            int centreRz = RegionIndexOf(camera.Z);
            int span = (int)MathF.Ceiling(RegionKeepMetres / RegionMetres);
            for (int dx = -span; dx <= span; dx++) {
                for (int dz = -span; dz <= span; dz++) {
                    long key = RegionKey(centreRx + dx, centreRz + dz);
                    if (m_residentRegions.Contains(key) || !m_knownRegions.Contains(key)) {
                        continue;
                    }
                    try {
                        LoadRegion(key);
                    }
                    catch (Exception e) {
                        m_regionLastError = e.Message;
                    }
                }
            }
        }

        // ============================================================================================
        // 旧格式迁移（SkylineLod.bin v3 → 区域文件）
        // ============================================================================================

        static string LegacyPath() {
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (info == null || string.IsNullOrEmpty(info.DirectoryName)) {
                return null;
            }
            return Storage.CombinePaths(info.DirectoryName, "SkylineLod.bin");
        }

        /// <summary>旧整表单文件 → 区域仓。只在"区域目录里一个区域都没有、但旧文件在"时做一次。</summary>
        static void MigrateLegacyFile() {
            if (!RegionStoreEnabled || m_knownRegions.Count > 0) {
                return;
            }
            string legacy = LegacyPath();
            if (legacy == null || !Storage.FileExists(legacy)) {
                return;
            }
            try {
                using (Stream stream = Storage.OpenFile(legacy, OpenFileMode.Read)) {
                    var reader = new BinaryReader(stream);
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++) {
                        long key = reader.ReadInt64();
                        short height = reader.ReadInt16();
                        ushort value = reader.ReadUInt16();
                        byte light = version >= 3 ? reader.ReadByte() : (byte)15;
                        m_cells[key] = new Cell { Height = height, Value = value, Light = light };
                        m_dirtyRegions.Add(RegionKeyOfCoarseCell(key));
                        m_residentRegions.Add(RegionKeyOfCoarseCell(key));
                    }
                    if (version >= 2) {
                        int countFine = reader.ReadInt32();
                        for (int i = 0; i < countFine; i++) {
                            long key = reader.ReadInt64();
                            short height = reader.ReadInt16();
                            ushort value = reader.ReadUInt16();
                            byte light = version >= 3 ? reader.ReadByte() : (byte)15;
                            m_cellsFine[key] = new Cell { Height = height, Value = value, Light = light };
                            long rk = RegionKeyOfFineCell(key);
                            m_dirtyRegions.Add(rk);
                            m_residentRegions.Add(rk);
                        }
                    }
                }
                // 旧文件改名保留（不删），避免迁移出问题时无法回退
                Storage.MoveFile(legacy, LegacyPath() + ".v3.bak");
                Log.Information($"SkylineLod: migrated legacy SkylineLod.bin → {m_dirtyRegions.Count} regions"
                    + $" ({m_cells.Count} coarse + {m_cellsFine.Count} fine)");
            }
            catch (Exception e) {
                m_regionLastError = "migrate: " + e.Message;
            }
        }

        public static string RegionStoreDescribe() {
            JsonObject o = new() {
                ["enabled"] = RegionStoreEnabled,
                ["regionMetres"] = Math.Round(RegionMetres, 1),
                ["keepMetres"] = (double)RegionKeepMetres,
                ["loadPerTick"] = RegionLoadPerTick,
                ["resident"] = m_residentRegions.Count,
                ["known"] = m_knownRegions.Count,
                ["dirty"] = m_dirtyRegions.Count,
                ["loadedTotal"] = m_regionsLoaded,
                ["savedTotal"] = m_regionsSaved,
                ["evictedTotal"] = m_regionsEvicted,
                ["coarseCells"] = m_cells.Count,
                ["fineCells"] = m_cellsFine.Count,
                ["lastError"] = m_regionLastError
            };
            o["note"] = "LOD 单元按 512 m 区域落盘：相机附近常驻、远的写盘后移出、走回去按需回读";
            return o.ToJsonString();
        }
    }
}
