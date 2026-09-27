using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.51：**32³ 表面壳的生产路径**（新目标 4.3 的第三步）。
    ///
    /// v0.1.49 造出壳（16 KiB vs 128 KiB），v0.1.50 把壳网格化（剔面 + 贪心合并）并证明能画；
    /// 但那时还是"手动采集 + 实验层"。本版接上生产语义：
    ///
    ///   1. **卸载即采集**：区块离开加载范围的那一刻（`TerrainUpdater.AllocateAndFreeChunks`
    ///      的 `NotifyChunkUnloading` 钩子，**主线程**、数据还在），把该区块所属的 32³ 立方体
    ///      —— 只要它的 2×2 个区块都还在、都已 Valid（内容与光照都算好）—— 采成一张壳存下来。
    ///      这就是 Distant Horizons 的"边加载边建 LOD 数据库"在本作的对应机制
    ///      （v0.1.17 进入即采 + v0.1.47 卸载前采，本版把**壳**挂到后一个钩子上）。
    ///   2. **离开加载范围之后才渲染**：真实地形还在时（区块已分配）**一律不画壳**，
    ///      否则两层会互相穿插；只有区块真被释放了，壳才顶上（这就是"距离分层"的第一刀）。
    ///   3. **交接带内替换现有 LOD**：壳只画在 [视距-8, 视距+96] 这一圈（与 v0.1.45 的 4 m 近环同一带），
    ///      并整体抬高 `BandLift`（默认 0.35 m）—— 壳是真高、LOD 是 16 m 平滑面，抬高一点让真高在带内稳定胜出。
    ///
    /// 代价（如实记）：壳 16 KiB/立方体，网格约 48 KiB/立方体（合并口径），两者都按下限设阈值；
    /// 采集与建模都有预算（每 Tick 最多几个立方体 / 几毫秒），避免卸载瞬间卡帧。
    /// </summary>
    public static class SkylineCubeShellStore {
        public const int CubeSize = CubeSurface32.Size;
        public const int ShellBytesPerCube = 16384;

        /// <summary>总开关：关掉即完全不采集、不渲染（默认开）。</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>渲染开关（采集照常，只是不画；A/B 用）。</summary>
        public static bool RenderEnabled { get; set; } = true;
        /// <summary>壳数量上限（4096 × 16 KiB = 64 MiB）；超出按"最久没被用到"淘汰。</summary>
        public static int MaxCubes { get; set; } = 4096;
        /// <summary>
        /// 一次"离开加载范围"事件里最多同步采集几个立方体（防大卸载卡帧；超出的计入 `skippedOverBudget`）。
        /// 实测单个立方体采集 **0.79 ms**（AgentLab）：64 个 ≈ 50 ms，而"改视距"这种一次性大卸载本来就在
        /// 暂停菜单里发生；正常走路/跨区每次只掉几个区块，代价在毫秒级。
        /// </summary>
        public static int InlineHarvestPerCall { get; set; } = 64;
        /// <summary>每 Tick 建网格的时间预算（毫秒）。</summary>
        public static float MeshBudgetMs { get; set; } = 4f;
        /// <summary>交接带宽度（米）：从 `视距 - BandInset` 到 `视距 + BandMetres`。</summary>
        public static float BandMetres { get; set; } = 96f;
        public static float BandInset { get; set; } = 8f;
        /// <summary>带内抬高（米）：让真高在带内压过 16 m 平滑面（0 = 不抬，用于 A/B）。</summary>
        public static float BandLift { get; set; } = 0.35f;
        /// <summary>带内每帧最多画多少个壳（防止一次画几百个 draw call）。</summary>
        public static int MaxDrawPerFrame { get; set; } = 192;
        /// <summary>网格口径：true = 贪心合并（四边形最少）、false = 逐格（纹理精确）。</summary>
        public static bool UseMergedMesh { get; set; } = true;
        /// <summary>壳接管后让现有 LOD 层让位（默认开；false = 两层叠着画，用于 A/B 看穿插）。</summary>
        public static bool RestrictLod { get; set; } = true;
        /// <summary>
        /// **只画"四邻都在"的壳**（默认**关**）。壳是"走过才有的"，覆盖天然是补丁状；
        /// 只画孤立的一张 = 与旁边 LOD 层拼出锯齿缝（v0.1.51 实测画面发花的主因）。
        /// 要求上下左右四个邻居立方体也有壳，画出来的就是**连片**的一整块，与 LOD 的边界是干净的直边。
        /// **但默认不启用**：刚离开加载范围的那一圈壳是**薄环**（径向 2 个立方体厚），
        /// 环上的立方体必然有一侧邻居还是"真实地形"（没有壳）→ 实测 40 个壳里只有 **2** 个满足四邻齐全，
        /// 等于把功能关掉了。它适合"玩家已经走过一大片"（DH 式长期游玩）之后再打开。
        /// </summary>
        public static bool RequireNeighbors { get; set; } = false;

        sealed class Entry {
            public CubeSurface32 Shell;
            public CubeSurfaceMesh32 Mesh;
            public double LastUsed;
        }

        // 用元组当键：立方体坐标（含负坐标）直接可用，不做位打包（省掉一整类符号 bug）
        static readonly Dictionary<(int Cx, int Cy, int Cz), Entry> m_entries = [];
        static readonly Queue<(int Cx, int Cy, int Cz)> m_meshQueue = [];
        static readonly HashSet<(int Cx, int Cy, int Cz)> m_queued = [];

        // ---- 统计（判据都从这里出） ----
        public static int CubeCount => m_entries.Count;
        public static long ShellBytes => (long)m_entries.Count * ShellBytesPerCube;
        public static long MeshVertexBytes { get; private set; }
        public static long HarvestedTotal { get; private set; }
        public static long SkippedNotReady { get; private set; }
        public static long SkippedDuplicate { get; private set; }
        public static long EvictedTotal { get; private set; }
        public static long MeshedTotal { get; private set; }
        public static long SkippedOverBudget { get; private set; }
        public static long DrawnLastFrame { get; private set; }
        public static long SkippedBecauseLoaded { get; private set; }
        public static long SkippedIsolated { get; private set; }
        public static double LastHarvestMs { get; private set; }
        public static double LastMeshMs { get; private set; }
        public static string LastError { get; private set; } = "";

        static Terrain Terrain => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;

        /// <summary>"四邻都在"检查（`RequireNeighbors` 的判定体）。</summary>
        static bool NeighborhoodComplete(int cx, int cy, int cz) {
            if (!RequireNeighbors) {
                return true;
            }
            return m_entries.ContainsKey((cx - 1, cy, cz)) && m_entries.ContainsKey((cx + 1, cy, cz))
                && m_entries.ContainsKey((cx, cy, cz - 1)) && m_entries.ContainsKey((cx, cy, cz + 1));
        }

        /// <summary>
        /// 这个世界坐标处"有没有一张**带内的**壳"。给现有 LOD 层判断"该不该让位"用。
        /// 带规则与 `Draw` 完全一致、且同样用**立方体中心**测距 —— 两边同口径才不会出现
        /// "LOD 让位了、壳却没画"的洞。
        /// </summary>
        public static bool HasShellInBand(int worldX, int height, int worldZ, float cameraX, float cameraZ) {
            if (!Enabled || !RenderEnabled || m_entries.Count == 0) {
                return false;
            }
            (int Cx, int Cy, int Cz) key = (worldX >> 5, height >> 5, worldZ >> 5);
            if (!m_entries.ContainsKey(key)) {
                return false;
            }
            if (!NeighborhoodComplete(key.Cx, key.Cy, key.Cz)) {
                return false;
            }
            float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                ?? SettingsManager.VisibilityRange;
            float near = MathF.Max(viewRange - BandInset, 0f);
            float far = viewRange + BandMetres;
            float wx = key.Cx * CubeSize + CubeSize * 0.5f;
            float wz = key.Cz * CubeSize + CubeSize * 0.5f;
            float dx = wx - cameraX, dz = wz - cameraZ;
            float distSq = dx * dx + dz * dz;
            return distSq >= near * near && distSq <= far * far;
        }

        /// <summary>
        /// **预扫**：一次"离开加载范围"事件开始前，把即将释放的区块列表交进来（此时它们**全都还在**），
        /// 于是可以按 32³ 立方体（2×2 区块）**完整**采集。
        ///
        /// 为什么必须预扫而不是逐个区块释放时采：卸载是**同一个循环里逐个 Free** 的，
        /// 轮到某个区块时它的兄弟区块往往已经被释放（v0.1.51 实测：逐个采时 197 次全部 `skippedNotReady`）。
        ///
        /// 采集在这里**同步**做完（不能推迟到 Tick —— 那时区块已经没了，读回来是"假空"）；
        /// 单次上限 `InlineHarvestPerCall` 个立方体，避免一次大卸载卡帧；建模仍然推迟到 `Tick`。
        /// </summary>
        public static void OnChunksLeavingRange(List<TerrainChunk> leaving) {
            if (!Enabled || leaving == null || leaving.Count == 0) {
                return;
            }
            try {
                Terrain terrain = Terrain;
                if (terrain == null) {
                    return;
                }
                HashSet<(int Cx, int Cz)> cubes = [];
                foreach (TerrainChunk chunk in leaving) {
                    if (chunk == null) {
                        continue;
                    }
                    cubes.Add((chunk.Coords.X >> 1, chunk.Coords.Y >> 1));
                }
                int budget = Math.Max(0, InlineHarvestPerCall);
                foreach ((int Cx, int Cz) cube in cubes) {
                    if (budget <= 0) {
                        SkippedOverBudget++;
                        continue;
                    }
                    // 2×2 区块必须都还在、都已 Valid（内容 + 光照都算好）
                    int minTop = int.MaxValue, maxTop = int.MinValue;
                    bool ready = true;
                    for (int dx = 0; dx < 2 && ready; dx++) {
                        for (int dz = 0; dz < 2 && ready; dz++) {
                            TerrainChunk sibling = terrain.GetChunkAtCoords(cube.Cx * 2 + dx, cube.Cz * 2 + dz);
                            if (sibling == null || sibling.ThreadState < TerrainChunkState.Valid) {
                                ready = false;
                                break;
                            }
                            for (int x = 0; x < TerrainChunk.Size; x++) {
                                for (int z = 0; z < TerrainChunk.Size; z++) {
                                    int top = sibling.GetTopHeightFast(x, z);
                                    if (top > maxTop) {
                                        maxTop = top;
                                    }
                                    if (top < minTop) {
                                        minTop = top;
                                    }
                                }
                            }
                        }
                    }
                    if (!ready) {
                        SkippedNotReady++;
                        continue;
                    }
                    if (maxTop < TerrainChunk.MinHeight) {
                        continue;                            // 全空列（比如高空立方体）：没有表面可存
                    }
                    int cyLo = Math.Max(minTop, TerrainChunk.MinHeight) >> 5;
                    int cyHi = maxTop >> 5;
                    for (int cy = cyLo; cy <= cyHi; cy++) {
                        if (budget <= 0) {
                            SkippedOverBudget++;
                            continue;
                        }
                        (int Cx, int Cy, int Cz) key = (cube.Cx, cy, cube.Cz);
                        if (m_entries.ContainsKey(key)) {
                            SkippedDuplicate++;
                            continue;
                        }
                        Stopwatch watch = Stopwatch.StartNew();
                        CubeSurface32 shell = CubeSurface32.Extract(terrain, cube.Cx, cy, cube.Cz);
                        watch.Stop();
                        LastHarvestMs = watch.Elapsed.TotalMilliseconds;
                        if (shell.QuadCount == 0) {
                            continue;
                        }
                        m_entries[key] = new Entry { Shell = shell, LastUsed = Time.RealTime };
                        HarvestedTotal++;
                        budget--;
                        if (m_queued.Add(key)) {
                            m_meshQueue.Enqueue(key);
                        }
                    }
                }
                EvictIfNeeded();
                if (HarvestedTotal > 0) {
                    SkylineLod.RequestRebuild();          // 壳接管的那片变了 → 让 LOD 立刻重排（让位）
                }
            }
            catch (Exception e) {
                LastError = e.Message;
            }
        }

        /// <summary>每帧调用（由 `SkylineLod.Tick` 转发）：按预算给新采到的壳建网格（采集在卸载预扫里同步完成）。</summary>
        public static void Tick() {
            if (!Enabled) {
                return;
            }
            try {
                EvictIfNeeded();
                BuildMeshes();
            }
            catch (Exception e) {
                LastError = e.Message;
                Log.Warning($"SkylineCubeShellStore.Tick: {e.Message}");
            }
        }

        static void EvictIfNeeded() {
            if (m_entries.Count <= MaxCubes) {
                return;
            }
            // 简单口径：淘汰"最久没被用到"的（LastUsed 最小）。数量不大时一次性排序足够。
            List<KeyValuePair<(int Cx, int Cy, int Cz), Entry>> list = [.. m_entries];
            list.Sort((a, b) => a.Value.LastUsed.CompareTo(b.Value.LastUsed));
            int drop = m_entries.Count - MaxCubes;
            for (int i = 0; i < drop && i < list.Count; i++) {
                list[i].Value.Mesh?.Dispose();
                m_entries.Remove(list[i].Key);
                EvictedTotal++;
            }
        }

        static void BuildMeshes() {
            Stopwatch watch = Stopwatch.StartNew();
            MeshVertexBytes = 0;
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                if (kv.Value.Mesh != null) {
                    MeshVertexBytes += kv.Value.Mesh.VertexBytes;
                }
            }
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                if (watch.Elapsed.TotalMilliseconds > MeshBudgetMs) {
                    return;
                }
                Entry entry = kv.Value;
                if (entry.Mesh != null) {
                    continue;
                }
                CubeSurface32[] one = [entry.Shell];
                CubeSurfaceMesh32 mesh = CubeSurfaceMesh32.Build(one, 1, 1, UseMergedMesh, true);
                entry.Mesh = mesh;
                MeshVertexBytes += mesh.VertexBytes;
                MeshedTotal++;
            }
            LastMeshMs = watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>由 `SubsystemTerrain.Draw` 调用（紧跟现有 LOD 层之后）。</summary>
        public static void Draw(Camera camera) {
            DrawnLastFrame = 0;
            if (!Enabled || !RenderEnabled || m_entries.Count == 0 || camera == null) {
                return;
            }
            try {
                Terrain terrain = Terrain;
                if (terrain == null) {
                    return;
                }
                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                float viewRange = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRange
                    ?? SettingsManager.VisibilityRange;
                float near = MathF.Max(viewRange - BandInset, 0f);
                float far = viewRange + BandMetres;
                float nearSq = near * near, farSq = far * far;
                Shader shader = SkylineCubeSurfaceDemo.PrepareTerrainShader(camera, BandLift);
                if (shader == null) {
                    return;
                }
                int drawn = 0;
                foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                    if (drawn >= MaxDrawPerFrame) {
                        break;
                    }
                    int cx = kv.Key.Cx, cz = kv.Key.Cz;
                    float wx = cx * CubeSize + CubeSize * 0.5f;
                    float wz = cz * CubeSize + CubeSize * 0.5f;
                    float dx = wx - viewPosition.X, dz = wz - viewPosition.Z;
                    float distSq = dx * dx + dz * dz;
                    if (distSq < nearSq || distSq > farSq) {
                        continue;
                    }
                    // 真实地形还在（区块已分配）→ 一律不画壳（否则两层互相穿插）
                    if (terrain.GetChunkAtCoords(cx * 2, cz * 2) != null
                        || terrain.GetChunkAtCoords(cx * 2 + 1, cz * 2) != null
                        || terrain.GetChunkAtCoords(cx * 2, cz * 2 + 1) != null
                        || terrain.GetChunkAtCoords(cx * 2 + 1, cz * 2 + 1) != null) {
                        SkippedBecauseLoaded++;
                        continue;
                    }
                    Entry entry = kv.Value;
                    if (entry.Mesh == null || entry.Mesh.VertexBuffer == null || entry.Mesh.IndexCount == 0) {
                        continue;
                    }
                    if (!NeighborhoodComplete(cx, kv.Key.Cy, cz)) {
                        SkippedIsolated++;
                        continue;
                    }
                    entry.LastUsed = Time.RealTime;
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                        entry.Mesh.VertexBuffer, entry.Mesh.IndexBuffer, 0, entry.Mesh.IndexCount);
                    drawn++;
                }
                DrawnLastFrame = drawn;
            }
            catch (Exception e) {
                LastError = e.Message;
                Log.Warning($"SkylineCubeShellStore.Draw: {e.Message}");
            }
        }

        /// <summary>清空（换世界/测试收尾用）。</summary>
        public static void Clear() {
            foreach (KeyValuePair<(int Cx, int Cy, int Cz), Entry> kv in m_entries) {
                kv.Value.Mesh?.Dispose();
            }
            m_entries.Clear();
            m_meshQueue.Clear();
            m_queued.Clear();
            MeshVertexBytes = 0;
        }

        public static string Survey() {
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["renderEnabled"] = RenderEnabled,
                ["cubes"] = m_entries.Count,
                ["maxCubes"] = MaxCubes,
                ["queued"] = m_meshQueue.Count,
                ["shellBytes"] = ShellBytes,
                ["shellMiB"] = Math.Round(ShellBytes / 1048576.0, 2),
                ["meshVertexBytes"] = MeshVertexBytes,
                ["meshMiB"] = Math.Round(MeshVertexBytes / 1048576.0, 2),
                ["meshedCubes"] = MeshedTotal,
                ["harvestedTotal"] = HarvestedTotal,
                ["evictedTotal"] = EvictedTotal,
                ["drawnLastFrame"] = DrawnLastFrame,
                ["skippedNotReady"] = SkippedNotReady,
                ["skippedDuplicate"] = SkippedDuplicate,
                ["skippedOverBudget"] = SkippedOverBudget,
                ["skippedBecauseLoaded"] = SkippedBecauseLoaded,
                ["skippedIsolated"] = SkippedIsolated,
                ["lastHarvestMs"] = Math.Round(LastHarvestMs, 3),
                ["lastMeshMs"] = Math.Round(LastMeshMs, 3),
                ["bandMetres"] = BandMetres,
                ["bandInset"] = BandInset,
                ["bandLift"] = BandLift,
                ["greedy"] = UseMergedMesh,
                ["restrictLod"] = RestrictLod,
                ["requireNeighbors"] = RequireNeighbors,
                ["budget"] = new JsonObject {
                    ["inlineHarvestPerCall"] = InlineHarvestPerCall,
                    ["meshBudgetMs"] = MeshBudgetMs,
                    ["maxDrawPerFrame"] = MaxDrawPerFrame
                },
                ["lastError"] = LastError
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.CubeShell*`。</summary>
    public static partial class SkylineRuntime {
        public static string CubeShellSurvey() => SkylineCubeShellStore.Survey();
        public static string CubeShellClear() {
            SkylineCubeShellStore.Clear();
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellEnabled(bool enabled) {
            SkylineCubeShellStore.Enabled = enabled;
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellRender(bool enabled) {
            SkylineCubeShellStore.RenderEnabled = enabled;
            return SkylineCubeShellStore.Survey();
        }
        public static string CubeShellGreedy(bool greedy) {
            SkylineCubeShellStore.UseMergedMesh = greedy;
            return SkylineCubeShellStore.Survey();
        }
        /// <summary>壳接管后让现有 LOD 层让位（默认 true；false = 两层叠着画，用于 A/B 看穿插）。</summary>
        public static string CubeShellRestrictLod(bool restrict) {
            SkylineCubeShellStore.RestrictLod = restrict;
            SkylineLod.RequestRebuild();
            return SkylineCubeShellStore.Survey();
        }
        /// <summary>只画"四邻都在"的壳（默认 false；玩家走过一大片之后再打开，边界会干净很多）。</summary>
        public static string CubeShellRequireNeighbors(bool require) {
            SkylineCubeShellStore.RequireNeighbors = require;
            SkylineLod.RequestRebuild();
            return SkylineCubeShellStore.Survey();
        }
    }
}
