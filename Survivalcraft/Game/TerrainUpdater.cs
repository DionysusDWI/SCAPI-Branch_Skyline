using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    public class TerrainUpdater {
        public class UpdateStatistics {
            public static int m_counter;

            public double FindBestChunkTime;

            public int FindBestChunkCount;

            public double LoadingTime;

            public int LoadingCount;

            public double ContentsTime1;

            public int ContentsCount1;

            public double ContentsTime2;

            public int ContentsCount2;

            public double ContentsTime3;

            public int ContentsCount3;

            public double ContentsTime4;

            public int ContentsCount4;

            public double LightTime;

            public int LightCount;

            public double LightSourcesTime;

            public int LightSourcesCount;

            public double LightPropagateTime;

            public int LightPropagateCount;

            public int LightSourceInstancesCount;

            public double VerticesTime1;

            public int VerticesCount1;

            public double VerticesTime2;

            public int VerticesCount2;

            public int HashCount;

            public double HashTime;

            public int GeneratedSlices;

            public int SkippedSlices;

            public virtual void Log() {
                Engine.Log.Information("Terrain Update #{0}", m_counter++);
                if (FindBestChunkCount > 0) {
                    Engine.Log.Information("    FindBestChunk:          {0:0.0}ms ({1}x)", FindBestChunkTime * 1000.0, FindBestChunkCount);
                }
                if (LoadingCount > 0) {
                    Engine.Log.Information("    Loading:                {0:0.0}ms ({1}x)", LoadingTime * 1000.0, LoadingCount);
                }
                if (ContentsCount1 > 0) {
                    Engine.Log.Information("    Contents1:              {0:0.0}ms ({1}x)", ContentsTime1 * 1000.0, ContentsCount1);
                }
                if (ContentsCount2 > 0) {
                    Engine.Log.Information("    Contents2:              {0:0.0}ms ({1}x)", ContentsTime2 * 1000.0, ContentsCount2);
                }
                if (ContentsCount3 > 0) {
                    Engine.Log.Information("    Contents3:              {0:0.0}ms ({1}x)", ContentsTime3 * 1000.0, ContentsCount3);
                }
                if (ContentsCount4 > 0) {
                    Engine.Log.Information("    Contents4:              {0:0.0}ms ({1}x)", ContentsTime4 * 1000.0, ContentsCount4);
                }
                if (LightCount > 0) {
                    Engine.Log.Information("    Light:                  {0:0.0}ms ({1}x)", LightTime * 1000.0, LightCount);
                }
                if (LightSourcesCount > 0) {
                    Engine.Log.Information("    LightSources:           {0:0.0}ms ({1}x)", LightSourcesTime * 1000.0, LightSourcesCount);
                }
                if (LightPropagateCount > 0) {
                    Engine.Log.Information(
                        "    LightPropagate:         {0:0.0}ms ({1}x) {2} ls",
                        LightPropagateTime * 1000.0,
                        LightPropagateCount,
                        LightSourceInstancesCount
                    );
                }
                if (VerticesCount1 > 0) {
                    Engine.Log.Information("    Vertices1:              {0:0.0}ms ({1}x)", VerticesTime1 * 1000.0, VerticesCount1);
                }
                if (VerticesCount2 > 0) {
                    Engine.Log.Information("    Vertices2:              {0:0.0}ms ({1}x)", VerticesTime2 * 1000.0, VerticesCount2);
                }
                if (VerticesCount1 + VerticesCount2 > 0) {
                    Engine.Log.Information(
                        "    AllVertices:            {0:0.0}ms ({1}x)",
                        (VerticesTime1 + VerticesTime2) * 1000.0,
                        VerticesCount1 + VerticesCount2
                    );
                }
                if (HashCount > 0) {
                    Engine.Log.Information("        Hash:               {0:0.0}ms ({1}x)", HashTime * 1000.0, HashCount);
                }
                if (GeneratedSlices > 0) {
                    Engine.Log.Information("        Generated Slices:   {0}/{1}", GeneratedSlices, GeneratedSlices + SkippedSlices);
                }
            }
        }

        public struct UpdateLocation {
            public Vector2 Center;

            /// <summary>[v0.1.14] 相机/玩家的世界 y —— 球形加载窗要用（2D 调用方保持 0）。</summary>
            public float CenterY;

            /// <summary>[v0.1.14] 是否对该更新地点启用**球形（椭球）加载窗**：竖直方向按"列内容带"裁剪。</summary>
            public bool SphereWindow;

            // [v0.1.28] 球窗判据版本位：`SphereLoadingCubeBands` 改变时也要触发一次窗口重算
            // （否则开关切换要等相机移动 >8 格才生效，A/B 会被"上一次的位置更新"污染）。
            public bool CubeBands;

            public Vector2? LastChunksUpdateCenter;

            public float VisibilityDistance;

            public float ContentDistance;
        }

        public struct UpdateParameters {
            public TerrainChunk[] Chunks;

            public Dictionary<int, UpdateLocation> Locations;
        }

        public struct LightSource {
            public int X;

            public int Y;

            public int Z;

            public int Light;
        }

        public FloatCurve TemperatureCurve = new(
            new Vector2(0f, 0f),
            new Vector2(0.125f, 0f),
            new Vector2(0.25f, 0f),
            new Vector2(0.375f, -4f),
            new Vector2(0.5f, -12f),
            new Vector2(0.625f, -24f),
            new Vector2(0.75f, -12f),
            new Vector2(0.875f, -4f),
            new Vector2(1f, 0f)
        );

        public FloatCurve HumidityCurve = new(
            new Vector2(0f, 0f),
            new Vector2(0.25f, 0f),
            new Vector2(0.5f, 0f),
            new Vector2(0.75f, 0f),
            new Vector2(1f, 0f)
        );

        public const int m_lightAttenuationWithDistance = 1;

        public const float m_updateHysteresis = 8f;

        public SubsystemTerrain m_subsystemTerrain;

        public SubsystemGameInfo m_subsystemGameInfo;

        public SubsystemSky m_subsystemSky;

        public SubsystemSeasons m_subsystemSeasons;

        public SubsystemAnimatedTextures m_subsystemAnimatedTextures;

        public SubsystemBlockBehaviors m_subsystemBlockBehaviors;

        public Terrain m_terrain;

        public DynamicArray<LightSource> m_lightSources = [];

        public UpdateStatistics m_statistics = new();

        // ===== [v0.1.139 · 里程碑 5.2 第二段] 有界并行的日照/高度 pass 的账本 =====
        // 只由地形工作线程写，主线程只读（探针），所以不需要 interlock。
        long m_parallelSunLightBatches;
        long m_parallelSunLightChunks;
        int m_parallelSunLightLastBatch;
        int m_parallelSunLightMaxBatch;
        int m_parallelSunLightWorkerCeiling;      // 用过的最大并发度（bounded 判定要用它，不是当前设置）
        long m_parallelSunLightInjectedFailures;  // [v0.1.144] 注入异常实际抛出的次数（验收用）
        TerrainChunk[] m_parallelPicks;
        float[] m_parallelPickDist;

        // ===== [v0.1.156 · 里程碑 5.2 第二段] **lockstep 批推进**的账本与开关 =====
        // 与上面的"日照单段并行"账本分开记，避免两套读数混在一起（本轮教训：混口径会误判）。
        long m_lockstepBatches;
        long m_lockstepChunks;
        int m_lockstepLastBatch;
        int m_lockstepMaxBatch;
        int m_lockstepWorkerCeiling;
        long m_lockstepInjectedFailures;
        long m_lockstepRejectedAdjacent;     // I2 因"与批内已有候选过近"而被跳过的候选数
        long m_lockstepContents1;
        long m_lockstepContents2;
        long m_lockstepContents3;
        long m_lockstepContents4;
        long m_lockstepLight;
        // [v0.1.156 · 候选可用性账本] 回答"为什么凑不齐批"（`notes/278 §8.2` 指向的下一步杠杆）：
        //   调用次数 / 回退次数 / 候选数累计与峰值 / 被扫描上限截断的次数 / 候选的段分布。
        long m_lockstepCalls;
        long m_lockstepFallbacks;
        long m_lockstepCandidatesTotal;
        int m_lockstepCandidatesMax;
        long m_lockstepTruncated;
        long m_lockstepCandContents;
        long m_lockstepCandLight;
        TerrainChunk[] m_lockstepCandidates;   // 按距离升序的候选缓冲（比 picks 长，供 I2 挑）
        float[] m_lockstepCandidateDist;
        TerrainChunk[] m_lockstepPicks;        // I2 之后真正并行执行的批

        // ===== [v0.1.157 · 里程碑 5.2 第二段 · CC `12:16Z` 裁定] **分量组批（I2'）** =====
        // I2'：把"批"的单位从"互不相邻的块"换成"**连通分量**"——分量内由**同一个 worker 串行**、
        // 按**全局距离序**做；分量之间仍要求 Chebyshev ≥ LockstepMinChunkGap（写邻域 3×3 不相交）。
        // 动机：`notes/281` 实测回退率 84.5%~92.5%、候选几乎总在相邻扎堆 ⇒ 旧 I2 把"扎堆"当拒绝理由。
        int[] m_lockstepParent;                // 并查集：候选下标 -> 父（路径压缩）
        int[] m_lockstepCompId;                // 候选 -> 分量号（按"成员最小下标"升序编号，跨轮稳定）
        int[] m_lockstepRootComp;              // 并查集根 -> 分量号（-1 = 还没编号）
        int[] m_lockstepCompSize;              // 分量 -> 成员数
        int[] m_lockstepSegStart;              // 段 k 在 m_lockstepBatchChunks 中的起始下标
        int[] m_lockstepSegCount;              // 段 k 的块数
        TerrainChunk[] m_lockstepBatchChunks;  // 平铺的批：段内按全局距离序
        TerrainChunkState[] m_lockstepBatchStates; // 各块**被处理时**的状态（join 后按同一顺序推进）
        int[] m_lockstepBatchSrcIdx;           // 各块在候选数组里的下标（顺序断言用）
        int[] m_lockstepCompSizeHist;          // 分量大小分布（每次调用累加；下标 1..16，16 = "≥16"）
        int[] m_lockstepSegmentsHist;          // 每次调用"用掉几个分量"（1..8）
        int[] m_lockstepBatchSizeHist;         // 每个提交批的块数分布（下标 1..64，64 = "≥64"）
        long m_lockstepFullK;                  // "段数用满 workers"的批数（判据4 同报项）
        long m_lockstepSegAtMax;               // "段内块数用满 M"的段数（M 是否真的在卡）
        int m_lockstepLastSegments;            // 最近一批的段数
        int m_lockstepMaxSegments;             // 历史最大段数（bounded 判定用它，而不是当前 workers）
        double m_lockstepLastCapacity;         // 最近一批的"平均段容量"= 块数 / 段数
        long m_lockstepComponentsTotal;        // 分量数累计（每次调用）
        long m_lockstepComponentsUsed;         // 真正交给 worker 的分量数累计
        long m_lockstepComponentsDeferred;     // 因"分量数 > K"被留到下一批的分量数累计
        long m_lockstepTruncatedComponents;    // 因"单分量上限 M"被截断的分量次数累计
        long m_lockstepTruncatedByMax;         // 因 M 被留到下一批的**块数**累计
        int m_lockstepComponentSizeMax;        // 见过的最大分量
        long m_lockstepLastInjectSegSize;      // 注入时那个段的块数（证明注入面覆盖到多块分量）
        long m_lockstepAssertOrderViolations;      // I2'(b) 分量内顺序断言违反次数
        long m_lockstepAssertPairViolations;       // 跨段逐块 Chebyshev 断言违反次数
        long m_lockstepAssertNeighborhoodViolations; // I2'(a) 分量间写邻域相交断言违反次数
        long m_lockstepPackSkipped;                // 装箱模式下"所有列都放不进 ⇒ 留到下一批"的块数
        long m_lockstepBatchesAbortedByAssert;     // 断言失败 ⇒ 放弃并行、回退串行

        /// <summary>
        /// [v0.1.138 · 里程碑 5.2 第二段前置] **只读**：把逐 pass 的耗时/次数账本导出成 JSON
        /// （不打印、不重置 —— 与 `LogTerrainUpdateStats` 那条日志路径解耦，供 5.2 的
        /// "该并行哪一段"做决策）。字段名与 `UpdateStatistics` 一致，单位统一成**毫秒**。
        /// </summary>
        public virtual string DescribeStatistics() {
            UpdateStatistics s = m_statistics;
            return new System.Text.Json.Nodes.JsonObject {
                ["findBestChunkMs"] = Math.Round(s.FindBestChunkTime * 1000.0, 2),
                ["findBestChunkCount"] = s.FindBestChunkCount,
                ["loadingMs"] = Math.Round(s.LoadingTime * 1000.0, 2),
                ["loadingCount"] = s.LoadingCount,
                ["contents1Ms"] = Math.Round(s.ContentsTime1 * 1000.0, 2),
                ["contents1Count"] = s.ContentsCount1,
                ["contents2Ms"] = Math.Round(s.ContentsTime2 * 1000.0, 2),
                ["contents2Count"] = s.ContentsCount2,
                ["contents3Ms"] = Math.Round(s.ContentsTime3 * 1000.0, 2),
                ["contents3Count"] = s.ContentsCount3,
                ["contents4Ms"] = Math.Round(s.ContentsTime4 * 1000.0, 2),
                ["contents4Count"] = s.ContentsCount4,
                ["lightMs"] = Math.Round(s.LightTime * 1000.0, 2),
                ["lightCount"] = s.LightCount,
                ["lightSourcesMs"] = Math.Round(s.LightSourcesTime * 1000.0, 2),
                ["lightSourcesCount"] = s.LightSourcesCount,
                ["lightPropagateMs"] = Math.Round(s.LightPropagateTime * 1000.0, 2),
                ["lightPropagateCount"] = s.LightPropagateCount,
                ["vertices1Ms"] = Math.Round(s.VerticesTime1 * 1000.0, 2),
                ["vertices1Count"] = s.VerticesCount1,
                ["vertices2Ms"] = Math.Round(s.VerticesTime2 * 1000.0, 2),
                ["vertices2Count"] = s.VerticesCount2,
                ["budgetMs"] = SkylineRuntime.TerrainUpdateBudgetMs,
                ["note"] = "自上次 LogTerrainUpdateStats 重置以来的累计；本探针不重置、不打日志"
            }.ToJsonString();
        }

        public Task m_task;

        public AutoResetEvent m_updateEvent = new(true);

        public ManualResetEvent m_pauseEvent = new(true);

        public volatile bool m_quitUpdateThread;

        public bool m_unpauseUpdateThread;

        public object m_updateParametersLock = new();

        public object m_unpauseLock = new();

        public UpdateParameters m_updateParameters;

        public UpdateParameters m_threadUpdateParameters;

        public int m_lastSkylightValue;

        public int m_synchronousUpdateFrame;

        public Dictionary<int, UpdateLocation?> m_pendingLocations = [];

        // [Skyline v0.0.5] 区块列驻留：上一次已生效的 SkylineRuntime.ResidencyVersion，
        // 以及我们塞进 Locations 字典的哨兵键（负数，与玩家下标 0..3 不冲突）。
        public int m_appliedResidencyVersion = -1;

        public List<int> m_residencyLocationKeys = [];

        public static int ChunkUpdates;

        public static int SlowTerrainUpdate;

        public static bool LogTerrainUpdateStats = false;

        public AutoResetEvent UpdateEvent => m_updateEvent;

        public event Action<TerrainChunk> ChunkInitialized;

        public TerrainUpdater() {}

        public TerrainUpdater(SubsystemTerrain subsystemTerrain) {
            ChunkUpdates = 0;
            m_subsystemTerrain = subsystemTerrain;
            m_subsystemGameInfo = m_subsystemTerrain.Project.FindSubsystem<SubsystemGameInfo>(true);
            m_subsystemSky = m_subsystemTerrain.Project.FindSubsystem<SubsystemSky>(true);
            m_subsystemSeasons = m_subsystemTerrain.Project.FindSubsystem<SubsystemSeasons>(true);
            m_subsystemBlockBehaviors = m_subsystemTerrain.Project.FindSubsystem<SubsystemBlockBehaviors>(true);
            m_subsystemAnimatedTextures = m_subsystemTerrain.Project.FindSubsystem<SubsystemAnimatedTextures>(true);
            m_terrain = subsystemTerrain.Terrain;
            m_updateParameters.Chunks = [];
            m_updateParameters.Locations = [];
            m_threadUpdateParameters.Chunks = [];
            m_threadUpdateParameters.Locations = [];
            SettingsManager.SettingChanged += SettingsManager_SettingChanged;
        }

        public virtual void Dispose() {
            SettingsManager.SettingChanged -= SettingsManager_SettingChanged;
            m_quitUpdateThread = true;
            UnpauseUpdateThread();
            m_updateEvent.Set();
            if (m_task != null) {
                m_task.Wait();
                m_task = null;
            }
            m_pauseEvent.Dispose();
            m_updateEvent.Dispose();
        }

        public virtual void RequestSynchronousUpdate() {
            m_synchronousUpdateFrame = Time.FrameIndex;
        }

        public virtual void SetUpdateLocation(int locationIndex, Vector2 center, float visibilityDistance, float contentDistance) {
            contentDistance = MathUtils.Max(contentDistance, visibilityDistance);
            m_updateParameters.Locations.TryGetValue(locationIndex, out UpdateLocation value);
            if (contentDistance != value.ContentDistance
                || visibilityDistance != value.VisibilityDistance
                || !value.LastChunksUpdateCenter.HasValue
                || Vector2.DistanceSquared(center, value.LastChunksUpdateCenter.Value) > 64f) {
                value.Center = center;
                value.CenterY = 0f;
                value.SphereWindow = false;
                value.CubeBands = false;
                value.VisibilityDistance = visibilityDistance;
                value.ContentDistance = contentDistance;
                value.LastChunksUpdateCenter = center;
                m_pendingLocations[locationIndex] = value;
            }
        }

        /// <summary>
        /// [v0.1.14] 带 y 的更新地点：`SkylineRuntime.SphereLoadingEnabled` 打开时启用**球形加载窗**——
        /// 椭球判据 `dx² + (dy/m)² + dz² ≤ content²`（m = `SubsystemSky.VisibilityRangeYMultiplier`，
        /// 与雾/`notes/64` 视觉球一致），竖直方向用"该列的内容带 bottom..top"，
        /// 未加载过的新列按 2D 保守处理（见 `IsChunkInRangeForUpdate`）。
        /// 默认关闭 → 与 2D 行为逐位一致。
        /// </summary>
        public virtual void SetUpdateLocation(int locationIndex, Vector3 center, float visibilityDistance, float contentDistance) {
            contentDistance = MathUtils.Max(contentDistance, visibilityDistance);
            m_updateParameters.Locations.TryGetValue(locationIndex, out UpdateLocation value);
            Vector2 centerXZ = center.XZ;
            if (contentDistance != value.ContentDistance
                || visibilityDistance != value.VisibilityDistance
                || MathF.Abs(center.Y - value.CenterY) > 8f
                || value.SphereWindow != SkylineRuntime.SphereLoadingEnabled
                || value.CubeBands != SkylineRuntime.SphereLoadingCubeBands
                || !value.LastChunksUpdateCenter.HasValue
                || Vector2.DistanceSquared(centerXZ, value.LastChunksUpdateCenter.Value) > 64f) {
                value.Center = centerXZ;
                value.CenterY = center.Y;
                value.SphereWindow = SkylineRuntime.SphereLoadingEnabled;
                value.CubeBands = SkylineRuntime.SphereLoadingCubeBands;
                value.VisibilityDistance = visibilityDistance;
                value.ContentDistance = contentDistance;
                value.LastChunksUpdateCenter = centerXZ;
                m_pendingLocations[locationIndex] = value;
            }
        }

        public virtual void RemoveUpdateLocation(int locationIndex) {
            m_pendingLocations[locationIndex] = null;
        }

        public virtual float GetUpdateProgress(int locationIndex, float visibilityDistance, float contentDistance) {
            int num = 0;
            int num2 = 0;
            if (m_updateParameters.Locations.TryGetValue(locationIndex, out UpdateLocation value)) {
                visibilityDistance = MathUtils.Max(MathUtils.Min(visibilityDistance, value.VisibilityDistance) - m_updateHysteresis - 0.1f, 0f);
                contentDistance = MathUtils.Max(MathUtils.Min(contentDistance, value.ContentDistance) - m_updateHysteresis - 0.1f, 0f);
                float num3 = MathUtils.Sqr(visibilityDistance);
                float num4 = MathUtils.Sqr(contentDistance);
                float v = MathUtils.Max(visibilityDistance, contentDistance);
                Point2 point = Terrain.ToChunk(value.Center - new Vector2(v));
                Point2 point2 = Terrain.ToChunk(value.Center + new Vector2(v));
                for (int i = point.X; i <= point2.X; i++) {
                    for (int j = point.Y; j <= point2.Y; j++) {
                        TerrainChunk chunkAtCoords = m_terrain.GetChunkAtCoords(i, j);
                        float num5 = Vector2.DistanceSquared(
                            v2: new Vector2((i + 0.5f) * TerrainChunk.Size, (j + 0.5f) * TerrainChunk.Size),
                            v1: value.Center
                        );
                        if (num5 <= num3) {
                            if (chunkAtCoords == null
                                || chunkAtCoords.State < TerrainChunkState.Valid) {
                                num2++;
                            }
                            else {
                                num++;
                            }
                        }
                        else if (num5 <= num4) {
                            if (chunkAtCoords == null
                                || chunkAtCoords.State < TerrainChunkState.InvalidLight) {
                                num2++;
                            }
                            else {
                                num++;
                            }
                        }
                    }
                }
                return num2 <= 0 ? 1f : num / (float)(num2 + num);
            }
            return 0f;
        }

        public virtual void Update() {
            if (m_subsystemSky.SkyLightValue != m_lastSkylightValue) {
                m_lastSkylightValue = m_subsystemSky.SkyLightValue;
                DowngradeAllChunksState(TerrainChunkState.InvalidLight, false);
            }
            int num = (int)MathF.Round(TemperatureCurve.Sample(m_subsystemGameInfo.WorldSettings.TimeOfYear));
            int num2 = (int)MathF.Round(HumidityCurve.Sample(m_subsystemGameInfo.WorldSettings.TimeOfYear));
            if (num != m_terrain.SeasonTemperature
                || num2 != m_terrain.SeasonHumidity) {
                m_terrain.SeasonTemperature = num;
                m_terrain.SeasonHumidity = num2;
                DowngradeAllChunksState(TerrainChunkState.InvalidVertices1, false);
            }
            if (!SettingsManager.MultithreadedTerrainUpdate) {
                if (m_task != null) {
                    m_quitUpdateThread = true;
                    UnpauseUpdateThread();
                    m_updateEvent.Set();
                    m_task.Wait();
                    m_task = null;
                }
                double realTime = Time.RealTime;
                while (!SynchronousUpdateFunction()
                    && Time.RealTime - realTime < 0.0099999997764825821) { }
            }
            else if (m_task == null) {
                m_quitUpdateThread = false;
                m_task = Task.Run(ThreadUpdateFunction);
                UnpauseUpdateThread();
                m_updateEvent.Set();
            }
            SkylineRuntime.Tick();
            bool residencyChanged = SkylineRuntime.ResidencyVersion != m_appliedResidencyVersion;
            if (m_pendingLocations.Count > 0 || residencyChanged) {
                m_pauseEvent.Reset();
                if (m_updateEvent.WaitOne(0)) {
                    m_pauseEvent.Set();
                    try {
                        foreach (KeyValuePair<int, UpdateLocation?> pendingLocation in m_pendingLocations) {
                            if (pendingLocation.Value.HasValue) {
                                m_updateParameters.Locations[pendingLocation.Key] = pendingLocation.Value.Value;
                            }
                            else {
                                m_updateParameters.Locations.Remove(pendingLocation.Key);
                            }
                        }
                        if (residencyChanged) {
                            ApplySkylineResidencyLocations();
                        }
                        if (AllocateAndFreeChunks(m_updateParameters.Locations.Values.ToArray())) {
                            m_updateParameters.Chunks = m_terrain.AllocatedChunks;
                        }
                        m_pendingLocations.Clear();
                    }
                    finally {
                        m_updateEvent.Set();
                    }
                }
            }
            if (Monitor.TryEnter(m_updateParametersLock, 0)) {
                try {
                    if (SendReceiveChunkStates()) {
                        UnpauseUpdateThread();
                    }
                }
                finally {
                    Monitor.Exit(m_updateParametersLock);
                }
            }
            TerrainChunk[] allocatedChunks = m_terrain.AllocatedChunks;
            foreach (TerrainChunk terrainChunk in allocatedChunks) {
                if (terrainChunk.State >= TerrainChunkState.InvalidVertices1
                    && !terrainChunk.AreBehaviorsNotified) {
                    terrainChunk.AreBehaviorsNotified = true;
                    NotifyBlockBehaviors(terrainChunk);
                }
            }
        }

        public virtual void PrepareForDrawing(Camera camera) {
            // [v0.1.14] 相机的更新地点带 y：`SkylineRuntime.SphereLoadingEnabled` 打开时走球形加载窗
            // （默认关 → 内部按 2D 处理，行为与原来逐位一致）。
            // [v0.1.16] 内容距离可调（`SphereLoadingContentRadius`）：默认 0 = 沿用 64；
            // 调大能让超视距 LOD 的细环被采到（LOD 只能采样已加载区块），代价是常驻列数/内存上升。
            float contentDistance = SkylineRuntime.SphereLoadingContentRadius > 0
                ? SkylineRuntime.SphereLoadingContentRadius
                : 64f;
            SetUpdateLocation(camera.GameWidget.PlayerData.PlayerIndex, camera.ViewPosition, m_subsystemSky.VisibilityRange, contentDistance);
            if (m_synchronousUpdateFrame == Time.FrameIndex) {
                List<TerrainChunk> list = DetermineSynchronousUpdateChunks(camera.ViewPosition, camera.ViewDirection);
                if (list.Count > 0) {
                    m_updateEvent.WaitOne();
                    try {
                        SendReceiveChunkStates();
                        SendReceiveChunkStatesThread();
                        foreach (TerrainChunk item in list) {
                            while (item.ThreadState < TerrainChunkState.Valid) {
                                UpdateChunkSingleStep(item, m_subsystemSky.SkyLightValue);
                            }
                        }
                        SendReceiveChunkStatesThread();
                        SendReceiveChunkStates();
                    }
                    finally {
                        m_updateEvent.Set();
                    }
                }
            }
        }

        public virtual void DowngradeChunkNeighborhoodState(Point2 coordinates, int radius, TerrainChunkState state, bool forceGeometryRegeneration) {
            for (int i = -radius; i <= radius; i++) {
                for (int j = -radius; j <= radius; j++) {
                    TerrainChunk chunkAtCoords = m_terrain.GetChunkAtCoords(coordinates.X + i, coordinates.Y + j);
                    if (chunkAtCoords == null) {
                        continue;
                    }
                    if (chunkAtCoords.State > state) {
                        chunkAtCoords.State = state;
                        if (forceGeometryRegeneration) {
                            chunkAtCoords.InvalidateSliceContentsHashes();
                        }
                    }
                    chunkAtCoords.WasDowngraded = true;
                }
            }
        }

        public virtual void DowngradeAllChunksState(TerrainChunkState state, bool forceGeometryRegeneration) {
            TerrainChunk[] allocatedChunks = m_terrain.AllocatedChunks;
            foreach (TerrainChunk terrainChunk in allocatedChunks) {
                if (terrainChunk.State > state) {
                    terrainChunk.State = state;
                    if (forceGeometryRegeneration) {
                        terrainChunk.InvalidateSliceContentsHashes();
                    }
                }
                terrainChunk.WasDowngraded = true;
            }
        }

        public static bool IsChunkInRange(Vector2 chunkCenter, ref UpdateLocation location) =>
            Vector2.DistanceSquared(location.Center, chunkCenter) <= (double)MathUtils.Sqr(location.ContentDistance);

        public static bool IsChunkInRange(Vector2 chunkCenter, UpdateLocation[] locations) {
            for (int i = 0; i < locations.Length; i++) {
                if (IsChunkInRange(chunkCenter, ref locations[i])) {
                    return true;
                }
            }
            return false;
        }

        // ============================================================================================
        // [v0.1.14] 球形加载窗（里程碑 3："球形视距 + 32³ 球形加载"的第一步）
        //   * 2D 判据（原版）＝ |Δxz| ≤ content；
        //   * 球形判据（`SkylineRuntime.SphereLoadingEnabled`）＝ dx²+(dy/m)²+dz² ≤ content²，
        //     其中 dy 取"相机 y 到该列**内容带** bottom..top 的距离"（相机在带内 → dy=0，退化为 2D），
        //     m = `SubsystemSky.VisibilityRangeYMultiplier`（与雾/notes/64 视觉球同一常数）；
        //   * 列被卸载时把它的内容带记进 `m_columnBandCache`，避免"卸载→未知→又装回来"的抖动；
        //   * 从未加载过的新列按 2D 保守处理（世界生成不会因为"高度未知"而被跳过）。
        // ============================================================================================

        readonly Dictionary<long, int> m_columnBandCache = [];

        // [v0.1.28] 32³ 分带内容掩码的"列缓存"：区块被卸载/释放后仍能按新判据（分带）重新评估，
        // 否则"被丢弃过一次"的列会因为拿不到掩码而永久丢掉（实测踩到，见 notes/98）。
        readonly Dictionary<long, ulong> m_columnBandMask32Cache = [];

        // ============================================================================================
        // [v0.1.22] **编辑 → 几何追平** 的直接量测（notes/91 的瓶颈：写 100 ms、几何 4~8 s）。
        //   写入侧（SubsystemTerrain.ChangeCell）调用 NotifyChunkEdited 记下坐标与时刻；
        //   该区块在状态机里再次到达 Valid 时结算时延（last/last10 平均/样本数）。
        //   这样不受"切片哈希恰好相同 → 根本没重建"的干扰（那正是间接测量的坑：notes/92）。
        // ============================================================================================
        Point2? m_editCoords;
        double m_editTime;
        double m_lastEditSettleMs = -1;
        double m_sumEditSettleMs;
        int m_editSettleSamples;

        public double LastEditSettleMs => m_lastEditSettleMs;
        public double MeanEditSettleMs => m_editSettleSamples > 0 ? m_sumEditSettleMs / m_editSettleSamples : -1;
        public int EditSettleSamples => m_editSettleSamples;

        /// <summary>写入侧通知：某坐标的格子刚被改过（只记最近一次，够用且零分配）。</summary>
        public void NotifyChunkEdited(int x, int z) {
            m_editCoords = new Point2(x >> TerrainChunk.SizeBits, z >> TerrainChunk.SizeBits);
            m_editTime = Time.RealTime;
        }

        /// <summary>已缓存"内容带"的列数（诊断用）。</summary>
        public int ColumnBandCacheCount => m_columnBandCache.Count;

        bool IsChunkInRangeForUpdate(Vector2 chunkCenter, ref UpdateLocation location) {
            if (!location.SphereWindow) {
                return IsChunkInRange(chunkCenter, ref location);
            }
            float dx = chunkCenter.X - location.Center.X;
            float dz = chunkCenter.Y - location.Center.Y;
            float h2 = dx * dx + dz * dz;
            float r = location.ContentDistance;
            if (h2 > r * r) {
                return false;
            }
            int cx = (int)MathF.Floor(chunkCenter.X) >> TerrainChunk.SizeBits;
            int cz = (int)MathF.Floor(chunkCenter.Y) >> TerrainChunk.SizeBits;
            float m = MathF.Max(m_subsystemSky?.VisibilityRangeYMultiplier ?? 1f, 0.05f);
            // [v0.1.28] 立方体粒度（默认开，见 notes/98）：用 32³ 分带内容掩码 ∩ 椭球竖直覆盖 求交。
            // 掩码只在"内容 + 高度已就绪"（State >= InvalidVertices1）时可用；否则退回 v0.1.14 的老判据。
            // 掩码可来自"存活区块"或"卸载时记住的列缓存"（后者让被丢弃的列仍能按新判据重新评估）。
            if (SkylineRuntime.SphereLoadingCubeBands && TryGetContentBandMask32(cx, cz, out ulong bandMask)) {
                float reach = MathF.Sqrt(MathUtils.Max(r * r - h2, 0f)) * m;
                m_sphereBandChecks++;
                if (!BandMaskHasContentInRange(bandMask, location.CenterY - reach, location.CenterY + reach)) {
                    m_sphereBandDrops++;
                    return false;
                }
                return true;
            }
            if (!TryGetColumnBand(cx, cz, out int top, out int bottom)) {
                return true;                                  // 高度未知（从未加载过）→ 保守
            }
            float dy = location.CenterY < bottom
                ? bottom - location.CenterY
                : (location.CenterY > top ? location.CenterY - top : 0f);
            dy /= m;
            return h2 + dy * dy <= r * r;
        }

        // ============================================================================================
        // [v0.1.28] 32³ 分带内容掩码（里程碑 3 的 P2 剩余：球窗的"立方体粒度"判据，见 notes/98）
        //   把区块 256 列的 top/bottom 聚合成 64 位掩码（每 bit = 一个 32 层高的分带是否有内容），
        //   球窗竖直方向改成"掩码 ∩ 椭球竖直覆盖"求交：
        //     * 比 v0.1.14 的"5 点采样内容带"细：不会被一个角的采样保留整列，也不会把"内容带之间
        //       的空气分带"算成内容（浮空岛/中空场景省列）；
        //     * 保守性：top/bottom 是内容带（含夹层空气），掩码只会多置位不会少置位 → 不会掉地。
        // ============================================================================================

        long m_sphereBandChecks;
        long m_sphereBandDrops;

        /// <summary>球窗分带判据的检查次数 / 丢弃次数（A/B 统计口径）。</summary>
        public long SphereBandChecks => m_sphereBandChecks;

        public long SphereBandDrops => m_sphereBandDrops;

        /// <summary>分带内容掩码（惰性 + 缓存）：bit i = 该区块某列内容带与 [MinHeight+32i, +31] 相交。</summary>
        ulong GetContentBandMask32(TerrainChunk chunk) {
            int stamp = (int)chunk.State * 31 + chunk.ModificationCounter;
            if (chunk.ContentBandMask32Stamp == stamp) {
                return chunk.ContentBandMask32;
            }
            ulong mask = 0;
            for (int lz = 0; lz < TerrainChunk.Size; lz++) {
                for (int lx = 0; lx < TerrainChunk.Size; lx++) {
                    int top = chunk.GetTopHeightFast(lx, lz);
                    int bottom = chunk.GetBottomHeightFast(lx, lz);
                    int i0 = MathUtils.Clamp((bottom - TerrainChunk.MinHeight) >> 5, 0, 63);
                    int i1 = MathUtils.Clamp((top - TerrainChunk.MinHeight) >> 5, 0, 63);
                    for (int i = i0; i <= i1; i++) {
                        mask |= 1UL << i;
                    }
                }
            }
            chunk.ContentBandMask32 = mask;
            chunk.ContentBandMask32Stamp = stamp;
            return mask;
        }

        /// <summary>椭球在某个区块处的竖直覆盖 [yLo,yHi] 是否与"有内容的分带"相交。</summary>
        static bool BandMaskHasContentInRange(ulong mask, float yLo, float yHi) {
            int iLo = (int)MathF.Floor((yLo - TerrainChunk.MinHeight) / 32f);
            int iHi = (int)MathF.Floor((yHi - TerrainChunk.MinHeight) / 32f);
            if (iHi < 0 || iLo > 63) {
                return false;                                 // 椭球竖直覆盖完全在世界之外 → 无内容
            }
            iLo = MathUtils.Max(iLo, 0);
            iHi = MathUtils.Min(iHi, 63);
            ulong window = iHi - iLo >= 63
                ? ulong.MaxValue
                : (((1UL << (iHi - iLo + 1)) - 1) << iLo);
            return (mask & window) != 0;
        }

        /// <summary>取某列的分带掩码：存活且就绪的区块现场算（顺手更新缓存）；否则用卸载时记住的；
        /// 都没有 → false（调用方退回老判据，保守）。</summary>
        bool TryGetContentBandMask32(int cx, int cz, out ulong mask) {
            long key = ((long)cx << 32) | (uint)cz;
            TerrainChunk chunk = m_terrain.GetChunkAtCoords(cx, cz);
            if (chunk != null && chunk.State >= TerrainChunkState.InvalidVertices1) {
                mask = GetContentBandMask32(chunk);
                if (m_columnBandMask32Cache.Count > 40000) {
                    m_columnBandMask32Cache.Clear();
                }
                m_columnBandMask32Cache[key] = mask;
                return true;
            }
            return m_columnBandMask32Cache.TryGetValue(key, out mask);
        }

        /// <summary>
        /// [v0.1.28] 球窗判定诊断（notes/98）：对指定区块输出 allocated/state/掩码/采样列与标记列的
        /// top·bottom、以及每个球形更新地点的 inRange 结果。用于 A/B 与"5 点采样盲区"取证。
        /// </summary>
        public virtual string DescribeChunkWindowDecision(int cx, int cz) {
            System.Text.StringBuilder sb = new();
            TerrainChunk chunk = m_terrain.GetChunkAtCoords(cx, cz);
            sb.Append($"chunk=({cx},{cz}) allocated={chunk != null} ");
            if (chunk != null) {
                sb.Append($"state={chunk.State} mod={chunk.ModificationCounter} ");
                sb.Append($"mask=0x{GetContentBandMask32(chunk):X16} ");
                sb.Append($"sample(8,8) top={chunk.GetTopHeightFast(8, 8)} bottom={chunk.GetBottomHeightFast(8, 8)} ");
                sb.Append($"col(3,3) top={chunk.GetTopHeightFast(3, 3)} bottom={chunk.GetBottomHeightFast(3, 3)} ");
            }
            sb.Append(TryGetContentBandMask32(cx, cz, out ulong knownMask)
                ? $"maskKnown=0x{knownMask:X16} "
                : "maskKnown=none ");
            Vector2 center = new(cx * TerrainChunk.Size + TerrainChunk.Size / 2f,
                                 cz * TerrainChunk.Size + TerrainChunk.Size / 2f);
            foreach (KeyValuePair<int, UpdateLocation> kv in m_updateParameters.Locations) {
                UpdateLocation location = kv.Value;
                if (!location.SphereWindow) {
                    continue;
                }
                bool inRange = IsChunkInRangeForUpdate(center, ref location);
                float dx = center.X - location.Center.X;
                float dz = center.Y - location.Center.Y;
                float yMul = MathF.Max(m_subsystemSky?.VisibilityRangeYMultiplier ?? 1f, 0.05f);
                float reach = MathF.Sqrt(MathUtils.Max(location.ContentDistance * location.ContentDistance - dx * dx - dz * dz, 0f)) * yMul;
                bool bandOk = TryGetContentBandMask32(cx, cz, out ulong bandMask)
                    && BandMaskHasContentInRange(bandMask, location.CenterY - reach, location.CenterY + reach);
                sb.Append($"| loc[{kv.Key}] cy={location.CenterY:0.#} r={location.ContentDistance} ");
                sb.Append($"yMul={yMul:0.##} reach={reach:0.#} ySlice=[{location.CenterY - reach:0.#},{location.CenterY + reach:0.#}] ");
                sb.Append($"bandOk={bandOk} inRange={inRange} ");
            }
            return sb.ToString();
        }

        bool IsChunkInRangeForUpdate(Vector2 chunkCenter, UpdateLocation[] locations) {
            for (int i = 0; i < locations.Length; i++) {
                if (IsChunkInRangeForUpdate(chunkCenter, ref locations[i])) {
                    return true;
                }
            }
            return false;
        }

        /// <summary>取某列的"内容带"（bottom..top）：已加载且光照/高度已算好 → 现场采样 5 个角/中心；
        /// 否则用卸载时缓存的带；都没有 → 返回 false（调用方按保守处理）。</summary>
        bool TryGetColumnBand(int cx, int cz, out int top, out int bottom) {
            TerrainChunk chunk = m_terrain.GetChunkAtCoords(cx, cz);
            if (chunk != null && chunk.State >= TerrainChunkState.InvalidVertices1) {
                int tTop = TerrainChunk.MinHeight;
                int tBottom = TerrainChunk.HeightMinusOne;
                for (int i = 0; i < 5; i++) {
                    int lx = i == 0 ? TerrainChunk.Size / 2 : (i <= 2 ? 0 : TerrainChunk.SizeMinusOne);
                    int lz = i == 0 ? TerrainChunk.Size / 2 : (i == 1 || i == 3 ? 0 : TerrainChunk.SizeMinusOne);
                    int h = chunk.GetTopHeightFast(lx, lz);
                    if (h < TerrainChunk.MinHeight) {
                        h = TerrainChunk.MinHeight;
                    }
                    tTop = MathUtils.Max(tTop, h);
                    tBottom = MathUtils.Min(tBottom, chunk.GetBottomHeightFast(lx, lz));
                }
                top = tTop;
                bottom = tBottom;
                return true;
            }
            long key = ((long)cx << 32) | (uint)cz;
            if (m_columnBandCache.TryGetValue(key, out int packed)) {
                top = (packed >> 11) + TerrainChunk.MinHeight;
                bottom = (packed & 0x7FF) + TerrainChunk.MinHeight;
                return true;
            }
            top = bottom = 0;
            return false;
        }

        void RememberColumnBand(TerrainChunk chunk) {
            if (!TryGetColumnBand(chunk.Coords.X, chunk.Coords.Y, out int top, out int bottom)) {
                return;
            }
            if (m_columnBandCache.Count > 40000) {
                m_columnBandCache.Clear();                    // 简单上限：极端飞行时不要无限增长
            }
            long key = ((long)chunk.Coords.X << 32) | (uint)chunk.Coords.Y;
            m_columnBandCache[key] = ((top - TerrainChunk.MinHeight) << 11) | (bottom - TerrainChunk.MinHeight);
            // [v0.1.28] 同时记住 32³ 分带掩码（卸载后新判据仍可用）
            if (m_columnBandMask32Cache.Count > 40000) {
                m_columnBandMask32Cache.Clear();
            }
            m_columnBandMask32Cache[key] = GetContentBandMask32(chunk);
        }

        public virtual bool AllocateAndFreeChunks(UpdateLocation[] locations) {
            bool result = false;
            TerrainChunk[] allocatedChunks = m_terrain.AllocatedChunks;
            // [v0.1.51] 4.3 第三步的**预扫**：卸载是下面这个循环里逐个 `FreeChunk` 的，
            // 轮到某个区块时它的兄弟区块往往已经被释放 → 32³ 立方体壳就采不全
            // （v0.1.51 实测：逐个采时 197 次全部 skippedNotReady）。
            // 所以先扫一遍"这一轮要离开的区块"，在**谁都还没被释放**的时候把壳采下来。
            // 只是多一次只读扫描（200 个区块量级），不改任何释放语义（下面循环照旧逐块判断/释放）。
            if (SkylineCubeShellStore.Enabled) {
                List<TerrainChunk> leaving = null;
                foreach (TerrainChunk terrainChunk in allocatedChunks) {
                    if (!IsChunkInRangeForUpdate(terrainChunk.Center, locations)) {
                        (leaving ??= []).Add(terrainChunk);
                    }
                }
                if (leaving != null) {
                    // [v0.1.129] **离开加载范围 = LOD 的最后一次采样机会**。
                    // 脏队列里"区块已卸载且从未采过样"的单元永远等不到重采（实测静止 2 分钟仍挂 921 个）
                    // ⇒ 在数据还在的这一帧把它们采进 LOD 单元并销账。
                    SkylineLod.OnChunksLeavingRange(leaving);
                    SkylineCubeShellStore.OnChunksLeavingRange(leaving);
                }
            }
            foreach (TerrainChunk terrainChunk in allocatedChunks) {
                if (!IsChunkInRangeForUpdate(terrainChunk.Center, locations)) {
                    bool noToFree = false;
                    ModsManager.HookAction(
                        "ToFreeChunks",
                        modLoader => {
                            modLoader.ToFreeChunks(this, terrainChunk, out bool keepWorking);
                            noToFree |= keepWorking;
                            return false;
                        }
                    );
                    if (noToFree) {
                        continue;
                    }
                    result = true;
                    foreach (SubsystemBlockBehavior blockBehavior in m_subsystemBlockBehaviors.BlockBehaviors) {
                        blockBehavior.OnChunkDiscarding(terrainChunk);
                    }
                    // [v0.1.47] 卸载前把 LOD 采一次（4.2：进入→离开加载范围都要刷新 LOD 状态）
                    SkylineLod.NotifyChunkUnloading(terrainChunk);
                    RememberColumnBand(terrainChunk);          // [v0.1.14] 卸载前记住内容带（球形窗用）
                    m_subsystemTerrain.TerrainSerializer.SaveChunk(terrainChunk);
                    m_terrain.FreeChunk(terrainChunk);
                }
            }
            for (int j = 0; j < locations.Length; j++) {
                Point2 point = Terrain.ToChunk(locations[j].Center - new Vector2(locations[j].ContentDistance));
                Point2 point2 = Terrain.ToChunk(locations[j].Center + new Vector2(locations[j].ContentDistance));
                for (int k = point.X; k <= point2.X; k++) {
                    for (int l = point.Y; l <= point2.Y; l++) {
                        Vector2 chunkCenter = new((k + 0.5f) * TerrainChunk.Size, (l + 0.5f) * TerrainChunk.Size);
                        TerrainChunk chunkAtCoords = m_terrain.GetChunkAtCoords(k, l);
                        if (chunkAtCoords == null) {
                            if (IsChunkInRangeForUpdate(chunkCenter, ref locations[j])) {
                                result = true;
                                m_terrain.AllocateChunk(k, l);
                                DowngradeChunkNeighborhoodState(new Point2(k, l), 0, TerrainChunkState.NotLoaded, false);
                                DowngradeChunkNeighborhoodState(new Point2(k, l), 1, TerrainChunkState.InvalidLight, false);
                            }
                        }
                        else if (chunkAtCoords.Coords.X != k
                            || chunkAtCoords.Coords.Y != l) {
                            Log.Error("Chunk wraparound detected at {0}", chunkAtCoords.Coords);
                        }
                    }
                }
            }
            ModsManager.HookAction(
                "ToAllocateChunks",
                modLoader => {
                    bool modification = modLoader.ToAllocateChunks(this, locations);
                    result |= modification;
                    return false;
                }
            );
            return result;
        }

        // ------------------------------------------------------------------------------------------
        // [Skyline v0.0.5] 区块列驻留：把 SkylineRuntime 的驻留区域铺成一串"覆盖圆"，
        // 作为额外的 update locations 塞进 m_updateParameters.Locations（哨兵键 -1000 起）。
        //
        // 这样完全复用引擎既有的 allocate / load / upgrade 流程：
        //   * AllocateAndFreeChunks 不再释放区域内的区块（列被钉在内存里，远处写入不再静默失效）；
        //   * FindBestChunkToUpdate 会把区域内的列推进到 InvalidVertices1（内容可读写）
        //     或 Valid（ResidencyFullDetail=true，连几何一起生成）。
        // 只在配置版本号变化时重建；开关关闭时把哨兵键全部移除，回到原版行为。
        // ------------------------------------------------------------------------------------------
        public virtual void ApplySkylineResidencyLocations() {
            m_appliedResidencyVersion = SkylineRuntime.ResidencyVersion;
            foreach (int key in m_residencyLocationKeys) {
                m_updateParameters.Locations.Remove(key);
            }
            m_residencyLocationKeys.Clear();
            if (!SkylineRuntime.ChunkResidencyMode) {
                return;
            }
            int nextKey = -1000;
            foreach (SkylineRuntime.ResidencyCircle circle in SkylineRuntime.BuildResidencyCircles()) {
                float visibility = SkylineRuntime.ResidencyFullDetail ? circle.RadiusBlocks : 0f;
                m_updateParameters.Locations[nextKey] = new UpdateLocation {
                    Center = circle.Center,
                    LastChunksUpdateCenter = circle.Center,
                    VisibilityDistance = visibility,
                    ContentDistance = MathUtils.Max(circle.RadiusBlocks, visibility)
                };
                m_residencyLocationKeys.Add(nextKey);
                nextKey--;
            }
        }

        public virtual bool SendReceiveChunkStates() {
            bool result = false;
            TerrainChunk[] chunks = m_updateParameters.Chunks;
            foreach (TerrainChunk terrainChunk in chunks) {
                if (terrainChunk.WasDowngraded) {
                    terrainChunk.DowngradedState = terrainChunk.State;
                    terrainChunk.WasDowngraded = false;
                    result = true;
                }
                else if (terrainChunk.UpgradedState.HasValue) {
                    terrainChunk.State = terrainChunk.UpgradedState.Value;
                }
                terrainChunk.UpgradedState = null;
            }
            return result;
        }

        public virtual void SendReceiveChunkStatesThread() {
            TerrainChunk[] chunks = m_threadUpdateParameters.Chunks;
            foreach (TerrainChunk terrainChunk in chunks) {
                if (terrainChunk.DowngradedState.HasValue) {
                    terrainChunk.ThreadState = terrainChunk.DowngradedState.Value;
                    terrainChunk.DowngradedState = null;
                }
                else if (terrainChunk.WasUpgraded) {
                    terrainChunk.UpgradedState = terrainChunk.ThreadState;
                }
                terrainChunk.WasUpgraded = false;
            }
        }

        public virtual void ThreadUpdateFunction() {
            while (!m_quitUpdateThread) {
                m_pauseEvent.WaitOne();
                m_updateEvent.WaitOne();
                try {
                    if (SynchronousUpdateFunction()) {
                        lock (m_unpauseLock) {
                            if (!m_unpauseUpdateThread) {
                                m_pauseEvent.Reset();
                            }
                            m_unpauseUpdateThread = false;
                        }
                    }
                }
                catch (Exception e) {
                    Log.Error(e.ToString());
                }
                finally {
                    m_updateEvent.Set();
                }
            }
        }

        public virtual bool SynchronousUpdateFunction() {
            lock (m_updateParametersLock) {
                m_threadUpdateParameters = m_updateParameters;
                SendReceiveChunkStatesThread();
            }
            // [v0.1.139 · 里程碑 5.2 第二段] 有界并行：默认关（0/1）。只在"状态正好是 InvalidLight"
            // 的区块上并行做日照/高度（源码级核实：该 pass 只碰本区块，见 notes/248）；做不到就回退串行。
            // [v0.1.156 · 里程碑 5.2 第二段] **lockstep 批推进**（默认关）：把窗口扩到
            // `InvalidContents1..4` + `InvalidLight`，一次凑一批**非邻近**区块并行做纯计算。
            // 与上一行的关系：上一行是它在"单段"上的特例；两者都关时走原串行路径（逐位不变）。
            // 默认翻开需三条判据同时满足（CC 080120Z）：六条不变量含 I5 注入全 PASS、
            // 同 seed 交叉轮次哈希逐位相同、同负载墙钟中位数改善 ≥ 15%。
            if (SkylineRuntime.ParallelLockstepWorkers >= 2
                && TryParallelLockstepStep(SkylineRuntime.ParallelLockstepWorkers) > 0) {
                return false;
            }
            if (SkylineRuntime.ParallelSunLightWorkers >= 2
                && TryParallelSunLightStep(SkylineRuntime.ParallelSunLightWorkers) > 0) {
                return false;
            }
            TerrainChunk terrainChunk = FindBestChunkToUpdate(out TerrainChunkState desiredState);
            if (terrainChunk != null) {
                double realTime = Time.RealTime;
                do {
                    UpdateChunkSingleStep(terrainChunk, m_subsystemSky.SkyLightValue);
                }
                while (terrainChunk.ThreadState < desiredState
                    // [v0.1.22] 帧内地形更新预算可调（默认 10 ms = 原硬编码值）：
                    // 大规模建筑/家具满场时几何重建是瓶颈（notes/91 实测 settle 4~8 s），
                    // 把它调大能显著缩短"几何追平"的等待，代价是这段时间帧时间上升（fps 略降）。
                    && Time.RealTime - realTime < SkylineRuntime.TerrainUpdateBudgetMs / 1000.0);
                return false;
            }
            if (LogTerrainUpdateStats) {
                m_statistics.Log();
                m_statistics = new UpdateStatistics();
            }
            return true;
        }

        /// <summary>
        /// [v0.1.139 · 里程碑 5.2 第二段] **有界并行**：把若干"状态正好是 InvalidLight"的区块的
        /// `GenerateChunkSunLightAndHeight` 并发执行（每个 worker 只写自己那个区块）。
        ///
        /// 为什么是这一段（源码级核实，`notes/248`）：
        ///   * 它只**读写本区块**（`Get/SetCellValueFast`、`SetTop/Bottom/SunlightHeightFast`），
        ///     只读 `BlocksManager.Blocks`，不碰邻居、不碰 `m_lightSources`、不碰 Storage、无 mod hook；
        ///   * 同一 light 阶段的 `lightSources`/`propagate` **必须串行**（共享 `m_lightSources` + 跨区块写），
        ///     所以本方法只覆盖 `InvalidLight` 这一种状态，其余状态一律回退给串行路径。
        ///
        /// 有界性：每批最多 `maxWorkers` 个区块，`Parallel.For` **join 之后**才推进状态 —— 状态推进留在
        /// 工作线程上（不是 worker），因此状态机语义与串行路径完全一致。
        /// </summary>
        public virtual int TryParallelSunLightStep(int maxWorkers) {
            if (maxWorkers < 2) {
                return 0;
            }
            // 注意：`UpdateParameters` 是本引擎的**结构体**，所以没有"null 参数块"这回事；
            // 但要防它还没初始化（`Chunks`/`Locations` 都是引用字段）。
            TerrainChunk[] chunks = m_threadUpdateParameters.Chunks;
            if (chunks == null || m_threadUpdateParameters.Locations == null) {
                return 0;
            }
            UpdateLocation[] locations = m_threadUpdateParameters.Locations.Values.ToArray();
            if (locations.Length == 0) {
                return 0;
            }
            if (m_parallelPicks == null || m_parallelPicks.Length < maxWorkers) {
                m_parallelPicks = new TerrainChunk[maxWorkers];
                m_parallelPickDist = new float[maxWorkers];
            }
            int n = 0;
            foreach (TerrainChunk c in chunks) {
                if (c == null || c.ThreadState != TerrainChunkState.InvalidLight) {
                    continue;
                }
                // 与 FindBestChunkToUpdate 同一距离口径：取到最近 update location 的距离，
                // 且必须落在该 location 的可见距离之内（否则这一段还不该做）。
                float best = float.MaxValue;
                bool inRange = false;
                for (int j = 0; j < locations.Length; j++) {
                    float d2 = Vector2.DistanceSquared(locations[j].Center, c.Center);
                    if (d2 < best) {
                        best = d2;
                        inRange = d2 <= MathUtils.Sqr(locations[j].VisibilityDistance);
                    }
                }
                if (!inRange) {
                    continue;
                }
                int pos = n < maxWorkers ? n : maxWorkers;
                while (pos > 0 && m_parallelPickDist[pos - 1] > best) {
                    if (pos < maxWorkers) {
                        m_parallelPicks[pos] = m_parallelPicks[pos - 1];
                        m_parallelPickDist[pos] = m_parallelPickDist[pos - 1];
                    }
                    pos--;
                }
                if (pos < maxWorkers) {
                    m_parallelPicks[pos] = c;
                    m_parallelPickDist[pos] = best;
                    if (n < maxWorkers) {
                        n++;
                    }
                }
            }
            if (n < 2) {
                return 0;      // 只有一个候选就别起并行开销（回退串行路径）
            }
            int skyLightValue = m_subsystemSky.SkyLightValue;
            double t0 = Time.RealTime;
            Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, k => {
                // [v0.1.144 · 审计验收] 异常注入（默认 -1 = 关）：见 SkylineRuntime.ParallelSunLightInjectFailure。
                // 抛在 Parallel.For 内部 ⇒ 状态推进段不会执行 ⇒ **本批整体不提交**（这正是要证明的性质）。
                if (SkylineRuntime.ParallelSunLightInjectFailure >= 0
                    && k == SkylineRuntime.ParallelSunLightInjectFailure) {
                    m_parallelSunLightInjectedFailures++;
                    // **一次性**：抛一次后自动关掉。否则每个批都会失败 ⇒ 该状态永远推进不了 ⇒
                    // 预加载永远走不完（验收会卡满 300 s 超时）。一次性既能证明"整批不提交"，
                    // 又能让后续批次把世界推进到可比对的状态。
                    SkylineRuntime.ParallelSunLightInjectFailure = -1;
                    throw new InvalidOperationException(
                        $"skyline: injected parallel-batch failure (worker {k}/{n}, "
                        + "skyline.ParallelSunLightInjectFailure)");
                }
                GenerateChunkSunLightAndHeight(m_parallelPicks[k], skyLightValue);
            });
            // 状态推进留在工作线程：worker 只做重活，状态机推进仍然串行且按 picks 顺序。
            for (int k = 0; k < n; k++) {
                TerrainChunk c = m_parallelPicks[k];
                c.ThreadState = TerrainChunkState.InvalidPropagatedLight;
                c.WasUpgraded = true;
                m_parallelPicks[k] = null;
            }
            double dt = Time.RealTime - t0;
            m_statistics.LightCount += n;
            m_statistics.LightTime += dt;
            m_parallelSunLightBatches++;
            m_parallelSunLightChunks += n;
            m_parallelSunLightLastBatch = n;
            m_parallelSunLightMaxBatch = Math.Max(m_parallelSunLightMaxBatch, n);
            // [v0.1.139 门禁抓出的修正] `maxBatch` 是**历史最大值**，而 `workers` 可能已经被关回去；
            // 用当前 workers 去比历史 maxBatch 会在"关掉开关之后"永远判不 bounded（假 FAIL）。
            // 正确口径：与**用过的最大并发度**比。
            m_parallelSunLightWorkerCeiling = Math.Max(m_parallelSunLightWorkerCeiling, maxWorkers);
            return n;
        }

        /// <summary>
        /// [v0.1.150 · CC P3「日照位比对」] **日照/高度 pass 的同轮次幂等性探针**。
        ///
        /// 为什么要它：v0.1.139 的验收只比**高度图指纹**，而同一次 `GenerateChunkSunLightAndHeight`
        /// 还会写**光照位**；跨轮比对含光内容哈希会因**邻居传播**漂移（`notes/248 §4-1`）。
        /// 本探针直接测"这一段是区块自身的确定函数"：
        ///   ① 算一次**列内光照摘要**（FNV over `Terrain.ExtractLight`，覆盖整列全高）；
        ///   ② **再跑一次同一个 pass**（与并行路径调用的是**同一个函数**）；
        ///   ③ 再算摘要比较 —— 相等 ⇒ 同输入同输出，且不受中间传播影响。
        ///
        /// ⚠️ 副作用与还原：重跑会把该区块光照位改写成"只按日照/衰减"的值（点光源的传播光会被抹掉）。
        /// `restore=true`（默认）时比较完把它标成 `InvalidLight`，让引擎下一轮**自己重走完整光照**
        /// （含光源扫描与传播）。**建议只在没有点光源的测试区使用。**
        /// </summary>
        public virtual string SunLightReplay(int cx, int cz, bool restore = true) {
            TerrainChunk chunk = m_terrain?.GetChunkAtCoords(cx, cz);
            if (chunk == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "chunk not allocated" }.ToJsonString();
            }
            if (chunk.ThreadState < TerrainChunkState.Valid) {
                return new JsonObject {
                    ["ok"] = false, ["err"] = "chunk not valid",
                    ["state"] = chunk.ThreadState.ToString()
                }.ToJsonString();
            }
            ulong digestBefore = LightDigest(chunk);
            GenerateChunkSunLightAndHeight(chunk, m_subsystemSky.SkyLightValue);
            ulong digestFirst = LightDigest(chunk);
            // **幂等的正确形式是 f(f(x)) == f(x)**：第一次重跑会把该区块的**传播光**抹掉
            // （它只按日照/衰减写），所以 `digestBefore != digestFirst` 是正常的、不是缺陷；
            // 真正要证明的是"**再跑一次，结果不再变**"——这才说明这一段是区块自身的确定函数。
            GenerateChunkSunLightAndHeight(chunk, m_subsystemSky.SkyLightValue);
            ulong digestSecond = LightDigest(chunk);
            if (restore) {
                // 让引擎自己重走完整光照（含光源扫描 + 传播），从而把"只按日照"的重跑结果覆盖回正确值。
                chunk.ThreadState = TerrainChunkState.InvalidLight;
            }
            return new JsonObject {
                ["ok"] = true,
                ["chunk"] = new JsonArray(cx, cz),
                ["lightDigestBefore"] = digestBefore.ToString("x16"),
                ["lightDigestAfterFirstReplay"] = digestFirst.ToString("x16"),
                ["lightDigestAfterSecondReplay"] = digestSecond.ToString("x16"),
                ["idempotent"] = digestFirst == digestSecond,
                ["changedByFirstReplay"] = digestBefore != digestFirst,
                ["restoredByInvalidLight"] = restore,
                ["stateAfter"] = chunk.ThreadState.ToString(),
                ["modificationCounter"] = chunk.ModificationCounter,
                ["note"] = "同轮次幂等性：重跑同一个 GenerateChunkSunLightAndHeight 后列内光照摘要必须相等；"
                           + "restore=true 会把该区块标成 InvalidLight，让引擎重走完整光照（含传播）"
            }.ToJsonString();
        }

        /// <summary>[v0.1.150] 列内光照摘要（FNV-1a 64，覆盖整列全高，只读）。</summary>
        static ulong LightDigest(TerrainChunk chunk) {
            ulong hash = 14695981039346656037UL;
            for (int x = 0; x < TerrainChunk.Size; x++) {
                for (int z = 0; z < TerrainChunk.Size; z++) {
                    int index = TerrainChunk.CalculateCellIndex(x, TerrainChunk.MinHeight, z);
                    for (int y = TerrainChunk.MinHeight; y <= TerrainChunk.HeightMinusOne; y++, index++) {
                        hash ^= (byte)Terrain.ExtractLight(chunk.GetCellValueFast(index));
                        hash *= 1099511628211UL;
                    }
                }
            }
            return hash;
        }

        /// <summary>[v0.1.139] 并行日照 pass 的只读账本（桥：`skyline.ParallelSunLightStats()`）。</summary>
        /// <summary>
        /// [v0.1.156 · 里程碑 5.2 第二段] **lockstep 批推进**：把候选窗口从单个 `InvalidLight`
        /// 扩到**全部白名单段**（`InvalidContents1..4` + `InvalidLight`），一次凑一批**互不邻近**的区块
        /// 并行做**纯计算**，`join` 之后由工作线程**串行**推进状态机。
        ///
        /// 设计与裁定：`notes/254`（交审草案）+ CC `080120Z` 四问裁定（I2 保留、vertices 暂缓、
        /// **I5 先做**、默认翻开需三条判据同时满足）。
        ///
        /// 不变量（`notes/254 §3`，逐条在此落地）：
        /// * **I2**：批内任意两块 **Chebyshev 距离 ≥ `LockstepMinChunkGap`（=3）**。
        ///   为什么是 3 而不是草案写的 2：**生成器确实跨块写** —— 树/刷子走
        ///   `Terrain.SetCellValueFast` → `GetChunkAtCell(...)?.SetCellValueFast`
        ///   （`TerrainContentsGenerator23.cs:1193-1217`、`TerrainBrush.cs:263/328`），
        ///   实测那些写入半径 ≤ 8 格（不到 1 个区块）⇒ 每块的**写邻域是 3×3 区块**，
        ///   取间距 ≥ 3 才能保证写邻域**两两不重叠**（间距 2 时两块会在中间那块上互相踩）。
        /// * **I3**：状态推进只在 `Parallel.For` **join 之后**、由工作线程按 `picks` 顺序串行做。
        /// * **I4**：每 worker 只写自己那块；共享的 `m_statistics` **只由工作线程在 join 后**更新。
        /// * **I6**：批 ≤ `maxWorkers`、无队列；异常沿 `SynchronousUpdateFunction` 抛到
        ///   `ThreadUpdateFunction` 的 `catch (Exception e) { Log.Error(...) }`。
        /// * **I5**（注入）：`ParallelLockstepInjectFailure >= 0` 时第 N 个 worker 抛异常 ⇒ **整批不提交**。
        /// * **`InvalidContents4` 的 mod hook 串行化**：worker 只做 `GenerateChunkContentsPass4`，
        ///   `ModsManager.HookAction("OnTerrainContentsGenerated", ...)` 留到 join 之后的串行段
        ///   —— 那是**全局回调**，并行调用它不满足"只写本区块"。
        ///
        /// 凑不到 2 个候选就返回 0 ⇒ 调用方**完全回退现有串行路径**（逐位不变）。
        /// </summary>
        public const int LockstepMinChunkGap = 3;

        /// <summary>
        /// [v0.1.156] **候选窗口系数**（默认 12）：`scanCap = max(workers × 系数, 48)`。
        /// 为什么做成可写：`notes/278 §8.2` 把"改进被压在天花板下"归因到**候选可用性**
        /// （`rejectedAdjacent` 常上千、部分轮次完全凑不齐批），这条系数就是那条杠杆的旋钮——
        /// 调大能看见更多候选，代价是每次调用多扫一点（O(chunks) 的纯读比较，可测）。
        /// </summary>
        public static int ParallelLockstepScanFactor { get; set; } = 12;

        /// <summary>
        /// [v0.1.157 · CC `121304Z` 裁定④] **单分量的批内上限 M**（默认 **4**）：
        /// 一个分量一次最多做 M 块，剩下的留到下一批。它是**单分量尾长**旋钮，与
        /// `ParallelLockstepWorkers`（并行**宽度**旋钮）**正交**——CC 明确要求不要把它绑到 workers 上。
        /// 是否要加大 M 看 **P95 分量大小 ÷ M**：P95 ≫ M ⇒ 长尾被截断吃掉利用率 ⇒ 加大 M。
        /// </summary>
        public static int ParallelLockstepComponentMax { get; set; } = 4;

        /// <summary>
        /// [v0.1.157] **分量组批（I2'）开关**（默认 **true**）：false 时退回旧的 I2 贪心挑选
        /// （批内两两 Chebyshev ≥ 3、凑不齐就回退）。留这个开关是为了**同一份二进制**里
        /// 能跑"只换调度"的 A/B 对照（CC 裁定③ 要求先跑改前基线）。
        /// </summary>
        public static bool ParallelLockstepComponentBatching {
            get => ParallelLockstepScheduling >= 1;
            set => ParallelLockstepScheduling = value ? 1 : 0;
        }

        /// <summary>
        /// [v0.1.157] **批调度算法**：`0` = 旧 I2 贪心（每块自成一段，批内两两 ≥ gap）；
        /// `1` = **I2' 连通分量**（分量内串行、分量间 ≥ gap —— CC `121304Z` 批准的那一版）；
        /// `2` = **按 worker 装箱**（每个 worker 一份"够得着互不冲突"的块列、列内按全局距离序串行；
        /// 跨 worker 必须两两 ≥ gap）—— **待 CC 审**，默认仍是 1。
        /// 为什么要有 2：实测 `1` 的回退率反而**高于**旧调度（86.2% → 97.9%），因为预加载时的候选
        /// 本来就是**一整片连通块**，传递闭包把它并成"一个分量" ⇒ 段数常常只有 1 ⇒ 回退。
        /// 而正确性真正需要的是**跨 worker 两两 ≥ gap**（同 worker 顺序执行不受此限），装箱正是按这条写的。
        /// </summary>
        public static int ParallelLockstepScheduling { get; set; } = 1;

        static bool IsLockstepWhitelisted(TerrainChunkState st) =>
            st == TerrainChunkState.InvalidContents1 || st == TerrainChunkState.InvalidContents2
            || st == TerrainChunkState.InvalidContents3 || st == TerrainChunkState.InvalidContents4
            || st == TerrainChunkState.InvalidLight;

        static int CompareCoords(TerrainChunk a, TerrainChunk b) =>
            a.Coords.X != b.Coords.X ? a.Coords.X.CompareTo(b.Coords.X) : a.Coords.Y.CompareTo(b.Coords.Y);

        /// <summary>
        /// [v0.1.157] **候选收集**（抽出来给批调度与只读探针 `DescribeLockstepComponents` 共用）：
        /// 白名单状态 + 落在 update location 可见距离内，按 `(distance, x, z)` **升序**排列
        /// （同距用坐标做**确定的 tie-break** ⇒ 跨轮可比，判据②要求）。
        /// `account=false`（探针路径）不写任何账本 —— 探针是**纯只读**，不能污染验收读数。
        /// </summary>
        int CollectLockstepCandidates(UpdateLocation[] locations, int scanCap, bool account) {
            if (m_lockstepCandidates == null || m_lockstepCandidates.Length < scanCap) {
                m_lockstepCandidates = new TerrainChunk[scanCap];
                m_lockstepCandidateDist = new float[scanCap];
            }
            int nCand = 0;
            foreach (TerrainChunk c in m_threadUpdateParameters.Chunks) {
                if (c == null || !IsLockstepWhitelisted(c.ThreadState)) {
                    continue;
                }
                float best = float.MaxValue;
                bool inRange = false;
                for (int j = 0; j < locations.Length; j++) {
                    float d2 = Vector2.DistanceSquared(locations[j].Center, c.Center);
                    if (d2 < best) {
                        best = d2;
                        inRange = d2 <= MathUtils.Sqr(locations[j].VisibilityDistance);
                    }
                }
                if (!inRange) {
                    continue;
                }
                // 距离升序插入；同距用坐标做确定的 tie-break。
                int pos = nCand < scanCap ? nCand : scanCap;
                while (pos > 0 && (m_lockstepCandidateDist[pos - 1] > best
                        || (m_lockstepCandidateDist[pos - 1] == best
                            && CompareCoords(m_lockstepCandidates[pos - 1], c) > 0))) {
                    if (pos < scanCap) {
                        m_lockstepCandidates[pos] = m_lockstepCandidates[pos - 1];
                        m_lockstepCandidateDist[pos] = m_lockstepCandidateDist[pos - 1];
                    }
                    pos--;
                }
                if (pos < scanCap) {
                    m_lockstepCandidates[pos] = c;
                    m_lockstepCandidateDist[pos] = best;
                    if (nCand < scanCap) {
                        nCand++;
                    }
                    else if (account) {
                        m_lockstepTruncated++;   // 候选多于扫描上限：这一格是被"截断"丢的
                    }
                }
            }
            if (account) {
                m_lockstepCandidatesTotal += nCand;
                m_lockstepCandidatesMax = Math.Max(m_lockstepCandidatesMax, nCand);
                for (int i = 0; i < nCand; i++) {
                    if (m_lockstepCandidates[i].ThreadState == TerrainChunkState.InvalidLight) {
                        m_lockstepCandLight++;
                    }
                    else {
                        m_lockstepCandContents++;
                    }
                }
            }
            return nCand;
        }

        /// <summary>[v0.1.157] 分量组批的 scratch（一次性按需扩容，之后零分配）。</summary>
        void EnsureLockstepScratch(int nCand, int maxWorkers) {
            int cap = Math.Max(nCand, 1);
            if (m_lockstepParent == null || m_lockstepParent.Length < cap) {
                m_lockstepParent = new int[cap];
                m_lockstepCompId = new int[cap];
                m_lockstepRootComp = new int[cap];
                m_lockstepCompSize = new int[cap];
                m_lockstepBatchSrcIdx = new int[cap];
            }
            int perSeg = Math.Max(1, ParallelLockstepComponentMax);
            if (m_lockstepSegStart == null || m_lockstepSegStart.Length < maxWorkers) {
                m_lockstepSegStart = new int[maxWorkers];
                m_lockstepSegCount = new int[maxWorkers];
            }
            int flatCap = Math.Max(1, maxWorkers * perSeg);
            if (m_lockstepBatchChunks == null || m_lockstepBatchChunks.Length < flatCap) {
                m_lockstepBatchChunks = new TerrainChunk[flatCap];
                m_lockstepBatchStates = new TerrainChunkState[flatCap];
            }
            if (m_lockstepCompSizeHist == null) {
                m_lockstepCompSizeHist = new int[17];
                m_lockstepSegmentsHist = new int[9];
                m_lockstepBatchSizeHist = new int[65];
            }
        }

        int LockstepFind(int i) {
            while (m_lockstepParent[i] != i) {
                m_lockstepParent[i] = m_lockstepParent[m_lockstepParent[i]];   // 路径对折
                i = m_lockstepParent[i];
            }
            return i;
        }

        /// <summary>[v0.1.157] 直方图（int[]）转 JSON 数组（null 安全）。</summary>
        static JsonArray IntArrayToJson(int[] xs) {
            JsonArray a = new JsonArray();
            if (xs != null) {
                for (int i = 0; i < xs.Length; i++) {
                    a.Add(JsonValue.Create(xs[i]));
                }
            }
            return a;
        }

        /// <summary>
        /// [v0.1.157 · CC `121304Z` ①②/P3 裁定] **候选 → 连通分量**（并查集）：
        /// 合并判据 = 两块 Chebyshev &lt; `LockstepMinChunkGap`（**与旧 I2 的拒绝判据同一条件**），
        /// 因为那正是"写邻域 3×3 会重叠"的充要条件 ⇒ 这些块必须**同分量串行**。
        /// 并查集在**按 (distance,x,z) 升序的候选枚举**上做，根取小下标 ⇒ 与遍历顺序无关；
        /// 分量号按"成员最小下标"升序发（= 成员最小 (distance,x,z)），跨轮稳定（CC P3）。
        /// 填好 `m_lockstepCompId`/`m_lockstepCompSize`，返回分量数。
        /// </summary>
        int BuildLockstepComponents(int nCand) {
            for (int i = 0; i < nCand; i++) {
                m_lockstepParent[i] = i;
            }
            for (int i = 0; i < nCand; i++) {
                TerrainChunk ci = m_lockstepCandidates[i];
                for (int j = i + 1; j < nCand; j++) {
                    TerrainChunk cj = m_lockstepCandidates[j];
                    if (Math.Abs(ci.Coords.X - cj.Coords.X) < LockstepMinChunkGap
                        && Math.Abs(ci.Coords.Y - cj.Coords.Y) < LockstepMinChunkGap) {
                        int ri = LockstepFind(i), rj = LockstepFind(j);
                        if (ri != rj) {
                            if (ri < rj) {
                                m_lockstepParent[rj] = ri;
                            }
                            else {
                                m_lockstepParent[ri] = rj;
                            }
                        }
                    }
                }
            }
            for (int i = 0; i < nCand; i++) {
                m_lockstepRootComp[i] = -1;
            }
            int compCount = 0;
            for (int i = 0; i < nCand; i++) {
                int r = LockstepFind(i);
                int cid = m_lockstepRootComp[r];
                if (cid < 0) {
                    cid = compCount++;
                    m_lockstepRootComp[r] = cid;
                    m_lockstepCompSize[cid] = 0;
                }
                m_lockstepCompId[i] = cid;
                m_lockstepCompSize[cid]++;
            }
            return compCount;
        }

        /// <summary>
        /// [v0.1.157 · CC 条件 5/6 的**可调证据**] **只读**分量探针（桥：`skyline.ParallelLockstepComponents()`）：
        /// 用与调度**同一段代码**（`CollectLockstepCandidates` + `BuildLockstepComponents`）算出当前候选的
        /// 连通分量，并直接报出三件事：
        ///   ① 相邻块是否**全部**落在同一分量（CC 条件 5 的前置：I2' 把"扎堆"吃进分量）；
        ///   ② 分量之间**写邻域（bbox 外扩 1 区块）是否两两不相交**（CC 条件 6）；
        ///   ③ 若真起批，各段的成员与块数（M 截断可见）。
        /// **不改世界、不写账本**（`account:false`）⇒ 可以在任何时刻安全调用、事后再调一次做对照。
        /// </summary>
        public virtual string DescribeLockstepComponents() {
            if (m_threadUpdateParameters.Chunks == null || m_threadUpdateParameters.Locations == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "no thread update parameters" }.ToJsonString();
            }
            UpdateLocation[] locations = m_threadUpdateParameters.Locations.Values.ToArray();
            if (locations.Length == 0) {
                return new JsonObject { ["ok"] = false, ["err"] = "no update locations" }.ToJsonString();
            }
            int scanCap = Math.Max(2 * ParallelLockstepScanFactor, 48);
            int nCand = CollectLockstepCandidates(locations, scanCap, account: false);
            EnsureLockstepScratch(nCand, 2);
            int compCount = BuildLockstepComponents(nCand);
            // ① 相邻对（Chebyshev < gap）必须同分量
            int adjacentPairs = 0, adjacentSameComp = 0;
            for (int i = 0; i < nCand; i++) {
                for (int j = i + 1; j < nCand; j++) {
                    TerrainChunk a = m_lockstepCandidates[i], b = m_lockstepCandidates[j];
                    if (Math.Abs(a.Coords.X - b.Coords.X) < LockstepMinChunkGap
                        && Math.Abs(a.Coords.Y - b.Coords.Y) < LockstepMinChunkGap) {
                        adjacentPairs++;
                        if (m_lockstepCompId[i] == m_lockstepCompId[j]) {
                            adjacentSameComp++;
                        }
                    }
                }
            }
            // ② 分量间写邻域不相交（bbox 各外扩 1 区块）
            int overlaps = 0;
            for (int a = 0; a < compCount; a++) {
                for (int b = a + 1; b < compCount; b++) {
                    int minXa = int.MaxValue, maxXa = int.MinValue, minYa = int.MaxValue, maxYa = int.MinValue;
                    int minXb = int.MaxValue, maxXb = int.MinValue, minYb = int.MaxValue, maxYb = int.MinValue;
                    for (int i = 0; i < nCand; i++) {
                        TerrainChunk c = m_lockstepCandidates[i];
                        if (m_lockstepCompId[i] == a) { Upd(ref minXa, ref maxXa, ref minYa, ref maxYa, c); }
                        else if (m_lockstepCompId[i] == b) { Upd(ref minXb, ref maxXb, ref minYb, ref maxYb, c); }
                    }
                    if (!(maxXa + 1 < minXb - 1 || maxXb + 1 < minXa - 1
                          || maxYa + 1 < minYb - 1 || maxYb + 1 < minYa - 1)) {
                        overlaps++;
                    }
                }
            }
            // ③ 真起批会拿到什么（前 K=2 个分量、每段最多 M）
            int M = Math.Max(1, ParallelLockstepComponentMax);
            JsonArray comps = new JsonArray();
            for (int cid = 0; cid < compCount && cid < 8; cid++) {
                JsonArray members = new JsonArray();
                int shown = 0;
                for (int i = 0; i < nCand && shown < 8; i++) {
                    if (m_lockstepCompId[i] != cid) {
                        continue;
                    }
                    members.Add(new JsonArray(m_lockstepCandidates[i].Coords.X, m_lockstepCandidates[i].Coords.Y));
                    shown++;
                }
                comps.Add(new JsonObject {
                    ["id"] = cid,
                    ["size"] = m_lockstepCompSize[cid],
                    ["wouldTakeBlocks"] = Math.Min(m_lockstepCompSize[cid], M),
                    ["membersShown"] = members
                });
            }
            return new JsonObject {
                ["ok"] = true,
                ["candidates"] = nCand,
                ["components"] = compCount,
                ["minChunkGap"] = LockstepMinChunkGap,
                ["componentMax"] = M,
                ["componentBatching"] = ParallelLockstepComponentBatching,
                ["scheduling"] = ParallelLockstepScheduling,
                ["packingPlan"] = PackingPlanJson(nCand, M),
                ["adjacentPairs"] = adjacentPairs,
                ["adjacentPairsSameComponent"] = adjacentSameComp,
                ["adjacentAllSameComponent"] = adjacentPairs == adjacentSameComp,
                ["componentNeighborhoodOverlaps"] = overlaps,
                ["neighborhoodsDisjoint"] = overlaps == 0,
                ["top"] = comps,
                ["note"] = "只读：不推进状态、不写账本。①相邻块必须同分量（I2' 把扎堆吃进分量）；"
                           + "②分量间写邻域（bbox 外扩 1 区块）必须不相交；③wouldTakeBlocks 体现单分量上限 M 的截断；"
                           + "④packingPlan = scheduling=2 时『按 worker 装箱』会怎么分（含 skipped）"
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.157b] 装箱模式的**只读预演**（探测用）：`scheduling=2` 时给出"每个 worker 会拿到哪些块"。
        /// 用 K = 2 个列（与探针的默认并发度同阶），不改世界、不写账本。
        /// </summary>
        JsonObject PackingPlanJson(int nCand, int cap) {
            if (ParallelLockstepScheduling != 2) {
                return new JsonObject { ["applicable"] = false };
            }
            int K = 2;
            int[] colStart = new int[K];
            int[] colCount = new int[K];
            for (int k = 0; k < K; k++) {
                colStart[k] = k * cap;
            }
            int skipped = 0;
            for (int i = 0; i < nCand; i++) {
                TerrainChunk cand = m_lockstepCandidates[i];
                int chosen = -1;
                for (int k = 0; k < K; k++) {
                    if (colCount[k] >= cap) {
                        continue;
                    }
                    bool clash = false;
                    for (int t = 0; t < colCount[k]; t++) {
                        TerrainChunk p = m_lockstepBatchChunks[colStart[k] + t];
                        if (Math.Abs(p.Coords.X - cand.Coords.X) < LockstepMinChunkGap
                            && Math.Abs(p.Coords.Y - cand.Coords.Y) < LockstepMinChunkGap) {
                            clash = true;
                            break;
                        }
                    }
                    if (!clash) {
                        chosen = k;
                        break;
                    }
                }
                if (chosen < 0) {
                    skipped++;
                    continue;
                }
                m_lockstepBatchChunks[colStart[chosen] + colCount[chosen]] = cand;
                colCount[chosen]++;
            }
            JsonArray cols = new JsonArray();
            for (int k = 0; k < K; k++) {
                JsonArray members = new JsonArray();
                for (int t = 0; t < colCount[k]; t++) {
                    TerrainChunk c = m_lockstepBatchChunks[colStart[k] + t];
                    members.Add(new JsonArray(c.Coords.X, c.Coords.Y));
                }
                cols.Add(new JsonObject { ["worker"] = k, ["blocks"] = colCount[k], ["members"] = members });
            }
            for (int k = 0; k < K; k++) {
                for (int t = 0; t < colCount[k]; t++) {
                    m_lockstepBatchChunks[colStart[k] + t] = null;      // 预演也要擦干净
                }
            }
            return new JsonObject {
                ["applicable"] = true, ["capPerWorker"] = cap, ["workers"] = K,
                ["columns"] = cols, ["skipped"] = skipped
            };
        }

        static void Upd(ref int minX, ref int maxX, ref int minY, ref int maxY, TerrainChunk c) {
            minX = Math.Min(minX, c.Coords.X);
            maxX = Math.Max(maxX, c.Coords.X);
            minY = Math.Min(minY, c.Coords.Y);
            maxY = Math.Max(maxY, c.Coords.Y);
        }

        /// <summary>
        /// [v0.1.157 · CC `121304Z` 条件] **I2' 组批**：把候选并成**连通分量**，
        /// 取前 K 个分量（K = `maxWorkers`）交给 K 个 worker，每个分量**内部串行**、**按全局距离序**；
        /// 每段最多 `ParallelLockstepComponentMax` 块（多出来的留到下一批）。
        ///
        /// 合并判据与**旧 I2 的拒绝判据是同一个**：两块 Chebyshev &lt; `LockstepMinChunkGap` 时写邻域重叠
        /// ⇒ 现在不是"拒绝一个"，而是"**归到同一分量串行**"——这就是 I2' 的放宽。
        /// 于是分量之间必然满足"写邻域（3×3 区块）不相交"，这是 CC ①②(a) 要求钉死的第一点；
        /// 第二点（分量内 = 全局距离序）由构造保证，并在下面**断言自检**。
        ///
        /// 返回本批块数；**断言失败返回 -1**（调用方回退串行 —— 宁可慢，不可错）。
        /// </summary>
        int BuildLockstepSegments(int nCand, int maxWorkers, out int segCountOut) {
            int M = Math.Max(1, ParallelLockstepComponentMax);
            EnsureLockstepScratch(nCand, maxWorkers);
            segCountOut = 0;
            if (ParallelLockstepScheduling == 2) {
                int skipped;
                int total2 = BuildLockstepPacking(nCand, maxWorkers, M, account: true, out segCountOut, out skipped);
                m_lockstepPackSkipped += skipped;
                if (total2 < 0) {
                    return -1;
                }
                return total2;
            }
            int compCount = BuildLockstepComponents(nCand);
            for (int cid = 0; cid < compCount; cid++) {
                int sz = m_lockstepCompSize[cid];
                m_lockstepCompSizeHist[Math.Min(sz, 16)]++;
                m_lockstepComponentSizeMax = Math.Max(m_lockstepComponentSizeMax, sz);
            }
            m_lockstepComponentsTotal += compCount;
            // ---- 3) 取前 K 个分量，每段最多 M 块 ----
            int segCount = 0;
            int total = 0;
            long truncatedComps = 0, truncatedBlocks = 0, deferredComps = 0;
            for (int cid = 0; cid < compCount; cid++) {
                if (segCount >= maxWorkers) {
                    deferredComps++;      // 分量数 > K：这一整个分量留到下一批
                    continue;
                }
                int sz = m_lockstepCompSize[cid];
                int take = Math.Min(sz, M);
                if (sz > M) {
                    truncatedComps++;                 // M 截断：会**安静地吃掉利用率**，必须可见
                    truncatedBlocks += sz - M;
                }
                // 段 k 的存储是**定长跨步**：`[k*M, k*M+count)`（与装箱模式共用同一套缓冲）
                int baseIdx = segCount * M;
                m_lockstepSegStart[segCount] = baseIdx;
                int taken = 0;
                for (int i = 0; i < nCand && taken < take; i++) {
                    if (m_lockstepCompId[i] != cid) {
                        continue;
                    }
                    m_lockstepBatchChunks[baseIdx + taken] = m_lockstepCandidates[i];
                    m_lockstepBatchStates[baseIdx + taken] = m_lockstepCandidates[i].ThreadState;
                    m_lockstepBatchSrcIdx[baseIdx + taken] = i;
                    taken++;
                    total++;
                }
                m_lockstepSegCount[segCount] = taken;
                segCount++;
            }
            m_lockstepComponentsUsed += segCount;
            m_lockstepComponentsDeferred += deferredComps;
            m_lockstepTruncatedComponents += truncatedComps;
            m_lockstepTruncatedByMax += truncatedBlocks;
            m_lockstepSegmentsHist[Math.Min(segCount, 8)]++;
            // ---- 4) 断言（CC 条件 5/6）：失败 ⇒ 计数 + 放弃并行（回退串行）----
            if (ValidateLockstepBatch(segCount) < 0) {
                return -1;
            }
            segCountOut = segCount;
            return total;
        }

        /// <summary>
        /// [v0.1.157] **批的正确性自检**（两个模式共用）：违反就回退串行。三条：
        /// ① 段内 = **全局距离序**（下标递增 + 距离非降）—— CC ①②(b)：顺序翻转 = 内容翻转；
        /// ② **跨段逐块** Chebyshev ≥ `LockstepMinChunkGap` —— 真正确保"同时写"的块写邻域不重叠；
        /// ③ 跨段 **bbox 外扩 1 区块**两两不相交 —— CC ①②(a) 的 bbox 形式（②的推论，但换个算法再查一遍）。
        /// </summary>
        int ValidateLockstepBatch(int segCount) {
            bool badOrder = false;
            for (int k = 0; k < segCount && !badOrder; k++) {
                int start = m_lockstepSegStart[k], cnt = m_lockstepSegCount[k];
                for (int t = 1; t < cnt; t++) {
                    int a = m_lockstepBatchSrcIdx[start + t - 1], b = m_lockstepBatchSrcIdx[start + t];
                    if (a >= b || m_lockstepCandidateDist[a] > m_lockstepCandidateDist[b]) {
                        badOrder = true;
                        break;
                    }
                }
            }
            bool badPair = false;
            for (int a = 0; a < segCount && !badPair; a++) {
                int sa = m_lockstepSegStart[a], ca = m_lockstepSegCount[a];
                for (int b = a + 1; b < segCount && !badPair; b++) {
                    int sb = m_lockstepSegStart[b], cb = m_lockstepSegCount[b];
                    for (int t = 0; t < ca && !badPair; t++) {
                        TerrainChunk x = m_lockstepBatchChunks[sa + t];
                        for (int u = 0; u < cb; u++) {
                            TerrainChunk y = m_lockstepBatchChunks[sb + u];
                            if (Math.Abs(x.Coords.X - y.Coords.X) < LockstepMinChunkGap
                                && Math.Abs(x.Coords.Y - y.Coords.Y) < LockstepMinChunkGap) {
                                badPair = true;
                                break;
                            }
                        }
                    }
                }
            }
            bool badNeighborhood = false;
            // ⚠️ [v0.1.157b 修] **bbox 检查只在"分量"模式下做**：bbox 外扩不相交是 CC ①②(a) 为
            //   I2' 指定的**充分条件形式**（分量本来就是 8 邻接闭包 ⇒ 必然成立，等于把设计意图再验一遍）。
            //   装箱模式**不需要** bbox 不相交：同一列里的块可以天各一方（列内串行），
            //   真正要保证的是**跨列逐块 ≥ gap**（上面那条 badPair 检查，两个模式都跑）。
            //   本轮实测：装箱模式错用 bbox 判据 ⇒ 468 个批**全部被弃**（"太严的断言把功能判死"）。
            for (int a = 0; a < segCount && ParallelLockstepScheduling != 2 && !badNeighborhood; a++) {
                int sa = m_lockstepSegStart[a], ca = m_lockstepSegCount[a];
                int minXa = int.MaxValue, maxXa = int.MinValue, minYa = int.MaxValue, maxYa = int.MinValue;
                for (int t = 0; t < ca; t++) {
                    Upd(ref minXa, ref maxXa, ref minYa, ref maxYa, m_lockstepBatchChunks[sa + t]);
                }
                for (int b = a + 1; b < segCount; b++) {
                    int sb = m_lockstepSegStart[b], cb = m_lockstepSegCount[b];
                    int minXb = int.MaxValue, maxXb = int.MinValue, minYb = int.MaxValue, maxYb = int.MinValue;
                    for (int t = 0; t < cb; t++) {
                        Upd(ref minXb, ref maxXb, ref minYb, ref maxYb, m_lockstepBatchChunks[sb + t]);
                    }
                    // 写邻域 = bbox 各向外扩 1 个区块（生成器写半径 ≤8 格 < 1 区块）⇒ 必须不相交
                    if (!(maxXa + 1 < minXb - 1 || maxXb + 1 < minXa - 1
                          || maxYa + 1 < minYb - 1 || maxYb + 1 < minYa - 1)) {
                        badNeighborhood = true;
                        break;
                    }
                }
            }
            if (badOrder) {
                m_lockstepAssertOrderViolations++;
            }
            if (badPair) {
                m_lockstepAssertPairViolations++;
            }
            if (badNeighborhood) {
                m_lockstepAssertNeighborhoodViolations++;
            }
            return (badOrder || badPair || badNeighborhood) ? -1 : 0;
        }

        /// <summary>
        /// [v0.1.157b · **待 CC 审**] **按 worker 装箱**（`ParallelLockstepScheduling == 2`）：
        /// 按**全局距离序**扫候选，把每块放进"**第一个还没装满、且与已放块都不冲突**"的 worker 列；
        /// 放不进任何一列（都满/都冲突）就**留到下一批**（`skipped`）。
        /// 与 I2' 的区别只在"同 worker 内允不允许相邻块"：允许（列内串行执行、按全局距离序）。
        /// 正确性依赖的仍是**跨列两两 ≥ gap**（`ValidateLockstepBatch` 逐块查），
        /// 以及"跨列写邻域不相交 ⇒ 跨列先后无所谓"（列内严格按全局距离序 ⇒ 树冠溢出顺序与串行一致）。
        /// </summary>
        int BuildLockstepPacking(int nCand, int maxWorkers, int cap, bool account,
                                 out int segCountOut, out int skippedOut) {
            int K = Math.Max(1, maxWorkers);
            EnsureLockstepScratch(nCand, maxWorkers);
            segCountOut = 0;
            skippedOut = 0;
            for (int k = 0; k < K; k++) {
                m_lockstepSegStart[k] = k * cap;
                m_lockstepSegCount[k] = 0;
            }
            int total = 0;
            for (int i = 0; i < nCand; i++) {
                TerrainChunk cand = m_lockstepCandidates[i];
                int chosen = -1;
                for (int k = 0; k < K; k++) {
                    if (m_lockstepSegCount[k] >= cap) {
                        continue;                       // 这一列已装满
                    }
                    // ⚠️ [v0.1.157b 修] 冲突要查的是**别的列**：块放进 k 列后，它与 k 列内的块是
                    //   **串行**关系（允许相邻），但与**其他所有列**的块是**并发**关系（必须 ≥ gap）。
                    //   上一版只查了 k 列自己 ⇒ 跨列相邻的批被放了出去（实测 260 个批被断言拦下，
                    //   说明断言本身有效、而放置逻辑漏了这一步）。
                    bool clash = false;
                    for (int m = 0; m < K && !clash; m++) {
                        if (m == k) {
                            continue;
                        }
                        int startM = m_lockstepSegStart[m];
                        for (int t = 0; t < m_lockstepSegCount[m]; t++) {
                            TerrainChunk p = m_lockstepBatchChunks[startM + t];
                            if (Math.Abs(p.Coords.X - cand.Coords.X) < LockstepMinChunkGap
                                && Math.Abs(p.Coords.Y - cand.Coords.Y) < LockstepMinChunkGap) {
                                clash = true;           // 与别的列冲突 ⇒ 这一列不能放
                                break;
                            }
                        }
                    }
                    if (!clash) {
                        chosen = k;
                        break;
                    }
                }
                if (chosen < 0) {
                    skippedOut++;                       // 留到下一批
                    continue;
                }
                int idx = m_lockstepSegStart[chosen] + m_lockstepSegCount[chosen];
                m_lockstepBatchChunks[idx] = cand;
                m_lockstepBatchStates[idx] = cand.ThreadState;
                m_lockstepBatchSrcIdx[idx] = i;
                m_lockstepSegCount[chosen]++;
                total++;
            }
            int segCount = 0;
            for (int k = 0; k < K; k++) {
                if (m_lockstepSegCount[k] > 0) {
                    segCount++;
                }
            }
            if (account) {
                m_lockstepSegmentsHist[Math.Min(segCount, 8)]++;
                m_lockstepComponentsUsed += segCount;
                for (int k = 0; k < K; k++) {
                    if (m_lockstepSegCount[k] >= cap) {
                        m_lockstepSegAtMax++;           // 该列被上限 M 卡住（利用率旋钮）
                    }
                }
            }
            if (ValidateLockstepBatch(K) < 0) {
                return -1;
            }
            segCountOut = segCount;
            return total;
        }

        public virtual int TryParallelLockstepStep(int maxWorkers) {
            if (maxWorkers < 2) {
                return 0;
            }
            TerrainChunk[] chunks = m_threadUpdateParameters.Chunks;
            if (chunks == null || m_threadUpdateParameters.Locations == null) {
                return 0;
            }
            UpdateLocation[] locations = m_threadUpdateParameters.Locations.Values.ToArray();
            if (locations.Length == 0) {
                return 0;
            }
            m_lockstepCalls++;
            // 候选窗口：`maxWorkers × 系数`。系数可写（`ParallelLockstepScanFactor`，默认 12）——
            // 它直接决定"能看见多少候选"，是 `notes/278 §8.2` 指出的那条杠杆的旋钮。
            int scanCap = Math.Max(maxWorkers * ParallelLockstepScanFactor, 48);
            if (m_lockstepPicks == null || m_lockstepPicks.Length < maxWorkers) {
                m_lockstepPicks = new TerrainChunk[maxWorkers];
            }
            int nCand = CollectLockstepCandidates(locations, scanCap, account: true);
            // ---- I2'（v0.1.157）/ I2（旧，开关控制）：挑出本批的"**段**"----
            //   段 = 一个**连通分量**（分量内由同一 worker 串行、按全局距离序）；
            //   旧路径 = 每块自成一段（等价于旧 I2 的"批内两两 ≥ gap"）。
            //   两条路径共用下面的 worker 循环与状态推进 ⇒ A/B 只差**调度**（CC 裁定③）。
            int n, segCount;
            if (ParallelLockstepComponentBatching) {
                n = BuildLockstepSegments(nCand, maxWorkers, out segCount);
                if (n < 0) {
                    // 断言失败（顺序 / 写邻域不相交）⇒ 本批作废、回退串行
                    m_lockstepBatchesAbortedByAssert++;
                    m_lockstepFallbacks++;
                    return 0;
                }
            }
            else {
                // ---- 旧 I2：按距离顺序贪心挑选，批内两两 Chebyshev ≥ LockstepMinChunkGap ----
                EnsureLockstepScratch(nCand, maxWorkers);
                n = 0;
                for (int i = 0; i < nCand && n < maxWorkers; i++) {
                    TerrainChunk cand = m_lockstepCandidates[i];
                    bool clash = false;
                    for (int k = 0; k < n; k++) {
                        TerrainChunk p = m_lockstepBatchChunks[k];
                        if (Math.Abs(p.Coords.X - cand.Coords.X) < LockstepMinChunkGap
                            && Math.Abs(p.Coords.Y - cand.Coords.Y) < LockstepMinChunkGap) {
                            clash = true;
                            break;
                        }
                    }
                    if (clash) {
                        m_lockstepRejectedAdjacent++;
                        continue;
                    }
                    int baseIdx = n * Math.Max(1, ParallelLockstepComponentMax);
                    m_lockstepSegStart[n] = baseIdx;
                    m_lockstepSegCount[n] = 1;
                    m_lockstepBatchChunks[baseIdx] = cand;
                    m_lockstepBatchStates[baseIdx] = cand.ThreadState;
                    m_lockstepBatchSrcIdx[baseIdx] = i;
                    n++;
                }
                segCount = n;
                m_lockstepComponentsUsed += segCount;
                m_lockstepSegmentsHist[Math.Min(segCount, 8)]++;
            }
            if (segCount < 2) {
                m_lockstepFallbacks++;              // 凑不齐两个段 ⇒ 回退串行（改进被天花板压住的地方）
                for (int k = 0; k < segCount; k++) {
                    for (int t = 0; t < m_lockstepSegCount[k]; t++) {
                        m_lockstepBatchChunks[m_lockstepSegStart[k] + t] = null;
                    }
                }
                return 0;
            }
            int skyLightValue = m_subsystemSky.SkyLightValue;
            double t0 = Time.RealTime;
            // 并行宽度仍是 maxWorkers：**段数 ≤ workers**，每段内串行（分量内顺序即全局距离序）。
            Parallel.For(0, segCount, new ParallelOptions { MaxDegreeOfParallelism = maxWorkers }, k => {
                // I5 注入（默认 -1 = 关）：抛在 Parallel.For 内部 ⇒ 推进段不执行 ⇒ 本批整体不提交。
                // 一次性：抛一次后自动关掉，否则该状态永远推进不了、预加载走不完。
                // [v0.1.157 · CC「注入面扩到多块分量」] 注入点若落在**多块段**的中段，本批同样整体不提交。
                if (SkylineRuntime.ParallelLockstepInjectFailure >= 0
                    && k == SkylineRuntime.ParallelLockstepInjectFailure) {
                    m_lockstepInjectedFailures++;
                    m_lockstepLastInjectSegSize = m_lockstepSegCount[k];
                    SkylineRuntime.ParallelLockstepInjectFailure = -1;
                    throw new InvalidOperationException(
                        $"skyline: injected lockstep-batch failure (segment {k}/{segCount}, "
                        + $"segSize {m_lockstepSegCount[k]}, batch {n}, "
                        + "skyline.ParallelLockstepInjectFailure)");
                }
                int start = m_lockstepSegStart[k];
                int cnt = m_lockstepSegCount[k];
                for (int t = 0; t < cnt; t++) {
                    TerrainChunk c = m_lockstepBatchChunks[start + t];
                    switch (m_lockstepBatchStates[start + t]) {
                        case TerrainChunkState.InvalidContents1:
                            m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass1(c);
                            break;
                        case TerrainChunkState.InvalidContents2:
                            m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass2(c);
                            break;
                        case TerrainChunkState.InvalidContents3:
                            m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass3(c);
                            break;
                        case TerrainChunkState.InvalidContents4:
                            m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass4(c);
                            break;      // mod hook 留到下面的串行段
                        case TerrainChunkState.InvalidLight:
                            GenerateChunkSunLightAndHeight(c, skyLightValue);
                            break;
                    }
                }
            });
            // ---- I3：join 之后，由工作线程串行推进状态机（与串行路径同一语义）----
            double dt = Time.RealTime - t0;
            // 顺序 = **段序 → 段内全局距离序**。跨段顺序无关紧要（跨段两两 ≥ gap、写邻域不相交，
            // 且断言已逐块验过）；段内必须严格按全局距离序（树冠溢出的先后决定内容）。
            for (int k = 0; k < segCount; k++) {
              int baseIdx = m_lockstepSegStart[k];
              int cnt = m_lockstepSegCount[k];
              for (int t = 0; t < cnt; t++) {
                TerrainChunk c = m_lockstepBatchChunks[baseIdx + t];
                TerrainChunkState st = m_lockstepBatchStates[baseIdx + t];
                switch (st) {
                    case TerrainChunkState.InvalidContents1:
                        c.ThreadState = TerrainChunkState.InvalidContents2;
                        m_statistics.ContentsCount1++;
                        m_statistics.ContentsTime1 += dt / n;
                        m_lockstepContents1++;
                        break;
                    case TerrainChunkState.InvalidContents2:
                        c.ThreadState = TerrainChunkState.InvalidContents3;
                        m_statistics.ContentsCount2++;
                        m_statistics.ContentsTime2 += dt / n;
                        m_lockstepContents2++;
                        break;
                    case TerrainChunkState.InvalidContents3:
                        c.ThreadState = TerrainChunkState.InvalidContents4;
                        m_statistics.ContentsCount3++;
                        m_statistics.ContentsTime3 += dt / n;
                        m_lockstepContents3++;
                        break;
                    case TerrainChunkState.InvalidContents4:
                        ModsManager.HookAction(
                            "OnTerrainContentsGenerated",
                            modLoader => {
                                modLoader.OnTerrainContentsGenerated(c);
                                return false;
                            }
                        );
                        c.ThreadState = TerrainChunkState.InvalidLight;
                        m_statistics.ContentsCount4++;
                        m_statistics.ContentsTime4 += dt / n;
                        m_lockstepContents4++;
                        break;
                    case TerrainChunkState.InvalidLight:
                        c.ThreadState = TerrainChunkState.InvalidPropagatedLight;
                        m_statistics.LightCount++;
                        m_statistics.LightTime += dt / n;
                        m_lockstepLight++;
                        break;
                }
                c.WasUpgraded = true;
                m_lockstepBatchChunks[baseIdx + t] = null;
            }
            }
            m_lockstepBatches++;
            m_lockstepChunks += n;
            m_lockstepLastBatch = n;
            m_lockstepLastSegments = segCount;
            m_lockstepMaxSegments = Math.Max(m_lockstepMaxSegments, segCount);
            m_lockstepLastCapacity = segCount > 0 ? (double)n / segCount : 0.0;
            m_lockstepBatchSizeHist[Math.Min(n, 64)]++;
            if (segCount >= maxWorkers) {
                m_lockstepFullK++;
            }
            if (ParallelLockstepScheduling != 2) {
                for (int k = 0; k < segCount; k++) {
                    if (m_lockstepSegCount[k] >= Math.Max(1, ParallelLockstepComponentMax)) {
                        m_lockstepSegAtMax++;
                    }
                }
            }
            m_lockstepMaxBatch = Math.Max(m_lockstepMaxBatch, n);
            m_lockstepWorkerCeiling = Math.Max(m_lockstepWorkerCeiling, maxWorkers);
            return n;
        }

        public virtual string DescribeParallelLockstep() => new JsonObject {
            ["workers"] = SkylineRuntime.ParallelLockstepWorkers,
            ["minChunkGap"] = LockstepMinChunkGap,
            ["batches"] = m_lockstepBatches,
            ["chunks"] = m_lockstepChunks,
            ["lastBatch"] = m_lockstepLastBatch,
            ["maxBatch"] = m_lockstepMaxBatch,
            ["workerCeiling"] = m_lockstepWorkerCeiling,
            // [v0.1.157 · I6 口径修正] 批的单位从"块"变成"**段（分量）**"：
            //   有界性 = **段数** ≤ 用过的最大并发度，**且**块数 ≤ 段数 × 单分量上限 M。
            //   （旧口径 `maxBatch ≤ workerCeiling` 是"批=块"时代的写法，在 I2' 下会把
            //    合法的"2 段 × 4 块 = 8 块"误判成越界 —— 这正是门禁抓口径偏差的地方。）
            ["bounded"] = m_lockstepMaxSegments <= Math.Max(1, m_lockstepWorkerCeiling)
                && m_lockstepMaxBatch <= Math.Max(1, m_lockstepMaxSegments)
                    * Math.Max(1, ParallelLockstepComponentMax),
            ["componentBatching"] = ParallelLockstepComponentBatching,
            ["scheduling"] = ParallelLockstepScheduling,
            ["componentMax"] = Math.Max(1, ParallelLockstepComponentMax),
            ["lastSegments"] = m_lockstepLastSegments,
            ["maxSegments"] = m_lockstepMaxSegments,
            ["lastCapacity"] = Math.Round(m_lockstepLastCapacity, 3),
            ["segmentsPerCallHist"] = IntArrayToJson(m_lockstepSegmentsHist),
            ["batchSizeHist"] = IntArrayToJson(m_lockstepBatchSizeHist),
            ["fullK"] = m_lockstepFullK,
            ["segAtMax"] = m_lockstepSegAtMax,
            ["componentsTotal"] = m_lockstepComponentsTotal,
            ["componentsUsed"] = m_lockstepComponentsUsed,
            ["componentsDeferred"] = m_lockstepComponentsDeferred,
            ["truncatedComponents"] = m_lockstepTruncatedComponents,
            ["truncatedBlocksByMax"] = m_lockstepTruncatedByMax,
            ["componentSizeMax"] = m_lockstepComponentSizeMax,
            ["componentSizeHist"] = IntArrayToJson(m_lockstepCompSizeHist),
            ["injectSegmentSize"] = m_lockstepLastInjectSegSize,
            ["assertOrderViolations"] = m_lockstepAssertOrderViolations,
            ["assertPairViolations"] = m_lockstepAssertPairViolations,
            ["assertNeighborhoodViolations"] = m_lockstepAssertNeighborhoodViolations,
            ["batchesAbortedByAssert"] = m_lockstepBatchesAbortedByAssert,
            ["packSkippedBlocks"] = m_lockstepPackSkipped,
            ["rejectedAdjacent"] = m_lockstepRejectedAdjacent,
            // [v0.1.156 · 候选可用性账本] 回答"为什么凑不齐批"
            ["scanFactor"] = ParallelLockstepScanFactor,
            ["calls"] = m_lockstepCalls,
            ["fallbacks"] = m_lockstepFallbacks,
            ["fallbackRate"] = m_lockstepCalls > 0
                ? Math.Round((double)m_lockstepFallbacks / m_lockstepCalls, 4) : 0.0,
            ["candidatesTotal"] = m_lockstepCandidatesTotal,
            ["candidatesPerCall"] = m_lockstepCalls > 0
                ? Math.Round((double)m_lockstepCandidatesTotal / m_lockstepCalls, 3) : 0.0,
            ["candidatesMax"] = m_lockstepCandidatesMax,
            ["truncatedByScanCap"] = m_lockstepTruncated,
            ["candidatesContents"] = m_lockstepCandContents,
            ["candidatesLight"] = m_lockstepCandLight,
            ["contents1"] = m_lockstepContents1,
            ["contents2"] = m_lockstepContents2,
            ["contents3"] = m_lockstepContents3,
            ["contents4"] = m_lockstepContents4,
            ["light"] = m_lockstepLight,
            ["injectFailureAt"] = SkylineRuntime.ParallelLockstepInjectFailure,
            ["injectedFailures"] = m_lockstepInjectedFailures,
            ["note"] = "[v0.1.157] I2' = 分量内串行、分量间 Chebyshev ≥ 3（生成器确有跨块写，"
                       + "写邻域 3×3 必须不重叠；componentBatching=false 时退回旧 I2 贪心挑选）；"
                       + "InvalidContents4 的 mod hook 串行化；join 后由工作线程按**全局距离序**串行推进；"
                       + "非白名单段（NotLoaded / PropagatedLight / Vertices）一律回退串行；"
                       + "顺序/写邻域两条断言失败即弃批回退（assertOrderViolations/assertNeighborhoodViolations）"
        }.ToJsonString();

        public virtual string DescribeParallelSunLight() => new JsonObject {
            ["workers"] = SkylineRuntime.ParallelSunLightWorkers,
            ["batches"] = m_parallelSunLightBatches,
            ["chunks"] = m_parallelSunLightChunks,
            ["lastBatch"] = m_parallelSunLightLastBatch,
            ["maxBatch"] = m_parallelSunLightMaxBatch,
            ["workerCeiling"] = m_parallelSunLightWorkerCeiling,
            ["bounded"] = m_parallelSunLightMaxBatch <= Math.Max(1, m_parallelSunLightWorkerCeiling),
            ["injectFailureAt"] = SkylineRuntime.ParallelSunLightInjectFailure,
            ["injectedFailures"] = m_parallelSunLightInjectedFailures,
            ["note"] = "只覆盖 InvalidLight（日照/高度）pass；lightSources/propagate 因共享 m_lightSources "
                       + "与跨区块写而保持串行（见 notes/248）"
        }.ToJsonString();

        public virtual TerrainChunk FindBestChunkToUpdate(out TerrainChunkState desiredState) {
            double realTime = Time.RealTime;
            TerrainChunk[] chunks = m_threadUpdateParameters.Chunks;
            UpdateLocation[] array = m_threadUpdateParameters.Locations.Values.ToArray();
            float num = float.MaxValue;
            TerrainChunk result = null;
            desiredState = TerrainChunkState.NotLoaded;
            foreach (TerrainChunk terrainChunk in chunks) {
                if (terrainChunk.ThreadState >= TerrainChunkState.Valid) {
                    continue;
                }
                for (int j = 0; j < array.Length; j++) {
                    float num2 = Vector2.DistanceSquared(array[j].Center, terrainChunk.Center);
                    if (num2 < num) {
                        if (num2 <= MathUtils.Sqr(array[j].VisibilityDistance)) {
                            desiredState = TerrainChunkState.Valid;
                            num = num2;
                            result = terrainChunk;
                        }
                        else if (terrainChunk.ThreadState < TerrainChunkState.InvalidVertices1
                            && num2 <= MathUtils.Sqr(array[j].ContentDistance)) {
                            desiredState = TerrainChunkState.InvalidVertices1;
                            num = num2;
                            result = terrainChunk;
                        }
                    }
                }
            }
            double realTime2 = Time.RealTime;
            m_statistics.FindBestChunkTime += realTime2 - realTime;
            m_statistics.FindBestChunkCount++;
            return result;
        }

        public virtual List<TerrainChunk> DetermineSynchronousUpdateChunks(Vector3 viewPosition, Vector3 viewDirection) {
            Vector3 vector = Vector3.Normalize(Vector3.Cross(viewDirection, Vector3.UnitY));
            Vector3 v = Vector3.Normalize(Vector3.Cross(viewDirection, vector));
            Vector3[] obj = [
                viewPosition,
                viewPosition + 6f * viewDirection,
                viewPosition + 6f * viewDirection - 6f * vector,
                viewPosition + 6f * viewDirection + 6f * vector,
                viewPosition + 6f * viewDirection - 2f * v,
                viewPosition + 6f * viewDirection + 2f * v
            ];
            List<TerrainChunk> list = [];
            Vector3[] array = obj;
            foreach (Vector3 vector2 in array) {
                TerrainChunk chunkAtCell = m_terrain.GetChunkAtCell(Terrain.ToCell(vector2.X), Terrain.ToCell(vector2.Z));
                if (chunkAtCell != null
                    && chunkAtCell.State < TerrainChunkState.Valid
                    && !list.Contains(chunkAtCell)) {
                    list.Add(chunkAtCell);
                }
            }
            return list;
        }

        public virtual void UpdateChunkSingleStep(TerrainChunk chunk, int skylightValue) {
            switch (chunk.ThreadState) {
                case TerrainChunkState.NotLoaded: {
                    double realTime19 = Time.RealTime;
                    if (m_subsystemTerrain.TerrainSerializer.LoadChunk(chunk)) {
                        chunk.ThreadState = TerrainChunkState.InvalidLight;
                        chunk.WasUpgraded = true;
                        double realTime20 = Time.RealTime;
                        chunk.IsLoaded = true;
                        m_statistics.LoadingCount++;
                        m_statistics.LoadingTime += realTime20 - realTime19;
                    }
                    else {
                        chunk.ThreadState = TerrainChunkState.InvalidContents1;
                        chunk.WasUpgraded = true;
                    }
                    break;
                }
                case TerrainChunkState.InvalidContents1: {
                    double realTime17 = Time.RealTime;
                    m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass1(chunk);
                    chunk.ThreadState = TerrainChunkState.InvalidContents2;
                    chunk.WasUpgraded = true;
                    double realTime18 = Time.RealTime;
                    m_statistics.ContentsCount1++;
                    m_statistics.ContentsTime1 += realTime18 - realTime17;
                    break;
                }
                case TerrainChunkState.InvalidContents2: {
                    double realTime15 = Time.RealTime;
                    m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass2(chunk);
                    chunk.ThreadState = TerrainChunkState.InvalidContents3;
                    chunk.WasUpgraded = true;
                    double realTime16 = Time.RealTime;
                    m_statistics.ContentsCount2++;
                    m_statistics.ContentsTime2 += realTime16 - realTime15;
                    break;
                }
                case TerrainChunkState.InvalidContents3: {
                    double realTime13 = Time.RealTime;
                    m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass3(chunk);
                    chunk.ThreadState = TerrainChunkState.InvalidContents4;
                    chunk.WasUpgraded = true;
                    double realTime14 = Time.RealTime;
                    m_statistics.ContentsCount3++;
                    m_statistics.ContentsTime3 += realTime14 - realTime13;
                    break;
                }
                case TerrainChunkState.InvalidContents4: {
                    double realTime7 = Time.RealTime;
                    m_subsystemTerrain.TerrainContentsGenerator.GenerateChunkContentsPass4(chunk);
                    ModsManager.HookAction(
                        "OnTerrainContentsGenerated",
                        modLoader => {
                            modLoader.OnTerrainContentsGenerated(chunk);
                            return false;
                        }
                    );
                    chunk.ThreadState = TerrainChunkState.InvalidLight;
                    chunk.WasUpgraded = true;
                    double realTime8 = Time.RealTime;
                    m_statistics.ContentsCount4++;
                    m_statistics.ContentsTime4 += realTime8 - realTime7;
                    break;
                }
                case TerrainChunkState.InvalidLight: {
                    double realTime3 = Time.RealTime;
                    GenerateChunkSunLightAndHeight(chunk, skylightValue);
                    chunk.ThreadState = TerrainChunkState.InvalidPropagatedLight;
                    chunk.WasUpgraded = true;
                    double realTime4 = Time.RealTime;
                    m_statistics.LightCount++;
                    m_statistics.LightTime += realTime4 - realTime3;
                    break;
                }
                case TerrainChunkState.InvalidPropagatedLight: {
                    for (int i = -1; i <= 1; i++) {
                        for (int j = -1; j <= 1; j++) {
                            TerrainChunk chunkAtCoords = m_terrain.GetChunkAtCoords(chunk.Coords.X + i, chunk.Coords.Y + j);
                            if (chunkAtCoords != null
                                && chunkAtCoords.ThreadState < TerrainChunkState.InvalidPropagatedLight) {
                                UpdateChunkSingleStep(chunkAtCoords, skylightValue);
                                return;
                            }
                        }
                    }
                    double realTime9 = Time.RealTime;
                    m_lightSources.Count = 0;
                    GenerateChunkLightSources(chunk);
                    GenerateChunkEdgeLightSources(chunk, 0);
                    GenerateChunkEdgeLightSources(chunk, 1);
                    GenerateChunkEdgeLightSources(chunk, 2);
                    GenerateChunkEdgeLightSources(chunk, 3);
                    double realTime10 = Time.RealTime;
                    m_statistics.LightSourcesCount++;
                    m_statistics.LightSourcesTime += realTime10 - realTime9;
                    double realTime11 = Time.RealTime;
                    PropagateLightSources();
                    chunk.ThreadState = TerrainChunkState.InvalidVertices1;
                    chunk.WasUpgraded = true;
                    double realTime12 = Time.RealTime;
                    m_statistics.LightPropagateCount++;
                    m_statistics.LightSourceInstancesCount += m_lightSources.Count;
                    m_statistics.LightPropagateTime += realTime12 - realTime11;
                    break;
                }
                case TerrainChunkState.InvalidVertices1: {
                    for (int k = -1; k <= 1; k++) {
                        for (int l = -1; l <= 1; l++) {
                            TerrainChunk chunkAtCoords2 = m_terrain.GetChunkAtCoords(chunk.Coords.X + k, chunk.Coords.Y + l);
                            if (chunkAtCoords2 != null
                                && chunkAtCoords2.ThreadState < TerrainChunkState.InvalidVertices1) {
                                UpdateChunkSingleStep(chunkAtCoords2, skylightValue);
                                return;
                            }
                        }
                    }
                    CalculateChunkSliceContentsHashes(chunk);
                    double realTime5 = Time.RealTime;
                    lock (chunk.Geometry) {
                        chunk.NewGeometryData = false;
                        GenerateChunkVertices(chunk, 0);
                        ModsManager.HookAction(
                            "GenerateChunkVertices",
                            modLoader => {
                                modLoader.GenerateChunkVertices(chunk, true);
                                return true;
                            }
                        );
                    }
                    chunk.ThreadState = TerrainChunkState.InvalidVertices2;
                    chunk.WasUpgraded = true;
                    double realTime6 = Time.RealTime;
                    m_statistics.VerticesCount1++;
                    m_statistics.VerticesTime1 += realTime6 - realTime5;
                    break;
                }
                case TerrainChunkState.InvalidVertices2: {
                    double realTime = Time.RealTime;
                    lock (chunk.Geometry) {
                        GenerateChunkVertices(chunk, 1);
                        ModsManager.HookAction(
                            "GenerateChunkVertices",
                            modLoader => {
                                modLoader.GenerateChunkVertices(chunk, true);
                                return false;
                            }
                        );
                        // [v0.1.30] 近景地形真阴影（CPU；默认关）：给刚生成的顶点光照乘 LOD 高度场的
                        // 遮挡系数（只改顶点颜色、不改方块/光照数据 → 重建一次即恢复，见 SkylineTerrainShadow.cs）
                        if (SkylineRuntime.TerrainShadowEnabled) {
                            SkylineRuntime.ApplyTerrainShadow(chunk);
                        }
                        chunk.NewGeometryData = true;
                    }
                    chunk.ThreadState = TerrainChunkState.Valid;
                    chunk.WasUpgraded = true;
                    // [v0.1.17] 区块刚达到 Valid：立刻通知超视距 LOD 补采这个 16 m 单元。
                    // 轮转游标在 800+ 列的场面要十几秒才轮到一次（实测批量加载时脏队列 99.99% 都在等
                    // 区块 Valid），"就地补采"能把"玩家刚走过/刚加载出来的地形"立刻反映到远景，
                    // 直接改善视距边缘交接带的覆盖度（notes/86 §4）。
                    SkylineLod.NotifyChunkValid(chunk);
                    // [v0.1.22] 编辑→几何追平 的直接结算（详见字段处注释）
                    if (m_editCoords.HasValue && m_editCoords.Value == chunk.Coords) {
                        m_lastEditSettleMs = (Time.RealTime - m_editTime) * 1000.0;
                        m_sumEditSettleMs += m_lastEditSettleMs;
                        m_editSettleSamples++;
                        m_editCoords = null;
                    }
                    double realTime2 = Time.RealTime;
                    ChunkUpdates++;
                    m_statistics.VerticesCount2++;
                    m_statistics.VerticesTime2 += realTime2 - realTime;
                    break;
                }
            }
        }

        public virtual void GenerateChunkSunLightAndHeight(TerrainChunk chunk, int skylightValue) {
            for (int i = 0; i < TerrainChunk.Size; i++) {
                for (int j = 0; j < TerrainChunk.Size; j++) {
                    int num = 0;
                    int num2 = TerrainChunk.HeightMinusOne;
                    int num4 = TerrainChunk.HeightMinusOne;
                    int num5 = TerrainChunk.CalculateCellIndex(i, TerrainChunk.HeightMinusOne, j);
                    while (num4 >= TerrainChunk.MinHeight) {   // [负高度实验] 原来到 0 就停
                        int cellValueFast = chunk.GetCellValueFast(num5);
                        if (Terrain.ExtractContents(cellValueFast) != 0) {
                            num = num4;
                            break;
                        }
                        cellValueFast = Terrain.ReplaceLight(cellValueFast, skylightValue);
                        chunk.SetCellValueFast(num5, cellValueFast);
                        num4--;
                        num5--;
                    }
                    num4 = TerrainChunk.MinHeight;
                    num5 = TerrainChunk.CalculateCellIndex(i, TerrainChunk.MinHeight, j);
                    while (num4 <= num + 1) {
                        int cellValueFast2 = chunk.GetCellValueFast(num5);
                        int num6 = Terrain.ExtractContents(cellValueFast2);
                        if (BlocksManager.Blocks[num6].IsTransparent_(cellValueFast2)) {
                            num2 = num4;
                            break;
                        }
                        cellValueFast2 = Terrain.ReplaceLight(cellValueFast2, 0);
                        chunk.SetCellValueFast(num5, cellValueFast2);
                        num4++;
                        num5++;
                    }
                    int num7 = skylightValue;
                    num4 = num;
                    num5 = TerrainChunk.CalculateCellIndex(i, num, j);
                    if (num7 > 0) {
                        while (num4 >= num2) {
                            int cellValueFast3 = chunk.GetCellValueFast(num5);
                            int num8 = Terrain.ExtractContents(cellValueFast3);
                            if (num8 != 0) {
                                Block block = BlocksManager.Blocks[num8];
                                if (!block.IsTransparent_(cellValueFast3)
                                    || block.LightAttenuation >= num7) {
                                    break;
                                }
                                num7 -= block.LightAttenuation;
                            }
                            cellValueFast3 = Terrain.ReplaceLight(cellValueFast3, num7);
                            chunk.SetCellValueFast(num5, cellValueFast3);
                            num4--;
                            num5--;
                        }
                    }
                    int num3 = num4 + 1;
                    while (num4 >= num2) {
                        int cellValueFast4 = chunk.GetCellValueFast(num5);
                        cellValueFast4 = Terrain.ReplaceLight(cellValueFast4, 0);
                        chunk.SetCellValueFast(num5, cellValueFast4);
                        num4--;
                        num5--;
                    }
                    chunk.SetTopHeightFast(i, j, num);
                    chunk.SetBottomHeightFast(i, j, num2);
                    chunk.SetSunlightHeightFast(i, j, num3);
                }
            }
        }

        public virtual void GenerateChunkLightSources(TerrainChunk chunk) {
            // [v0.1.106] 先清空这个区块上一次的点光源记录（同一区块会被反复重扫，只追加会重复累积）
            SkylinePointLights.BeginChunk(chunk);
            ModsManager.HookAction(
                "GenerateChunkLightSources",
                loader => {
                    loader.GenerateChunkLightSources(m_lightSources, chunk);
                    return false;
                }
            );
            Block[] blocks = BlocksManager.Blocks;
            for (int i = 0; i < TerrainChunk.Size; i++) {
                for (int j = 0; j < TerrainChunk.Size; j++) {
                    int topHeightFast = chunk.GetTopHeightFast(i, j);
                    int bottomHeightFast = chunk.GetBottomHeightFast(i, j);
                    int num = i + chunk.Origin.X;
                    int num2 = j + chunk.Origin.Y;
                    int k = bottomHeightFast;
                    int num3 = TerrainChunk.CalculateCellIndex(i, bottomHeightFast, j);
                    // [v0.1.106] 里程碑 4"固定光源"：**复用这一遍扫描**（不额外遍历），
                    // 把"会发光的方块"按区块记进 `SkylinePointLights` 的注册表，供片元做点光源衰减。
                    // 只取下面这个 while 循环里新增的条目 —— 它后面的邻居块写入的是**传播队列**，
                    // 记进去会让注册表里塞满成千上万个普通格（见 notes/201）。
                    int plEmitFrom = m_lightSources.Count;
                    while (k <= topHeightFast) {
                        int cellValueFast = chunk.GetCellValueFast(num3);
                        Block block = blocks[Terrain.ExtractContents(cellValueFast)];
                        if (block.DefaultEmittedLightAmount > 0) {
                            int emittedLightAmount = block.GetEmittedLightAmount(cellValueFast);
                            if (emittedLightAmount > Terrain.ExtractLight(cellValueFast)) {
                                chunk.SetCellValueFast(num3, Terrain.ReplaceLight(cellValueFast, emittedLightAmount));
                                if (emittedLightAmount > 1) {
                                    m_lightSources.Add(new LightSource { X = num, Y = k, Z = num2, Light = emittedLightAmount });
                                }
                            }
                        }
                        k++;
                        num3++;
                    }
                    if (m_lightSources.Count > plEmitFrom) {
                        SkylinePointLights.NoteChunkSources(chunk, m_lightSources.Array, plEmitFrom,
                                                            m_lightSources.Count);
                    }
                    TerrainChunk chunkAtCell = m_terrain.GetChunkAtCell(num - 1, num2);
                    TerrainChunk chunkAtCell2 = m_terrain.GetChunkAtCell(num + 1, num2);
                    TerrainChunk chunkAtCell3 = m_terrain.GetChunkAtCell(num, num2 - 1);
                    TerrainChunk chunkAtCell4 = m_terrain.GetChunkAtCell(num, num2 + 1);
                    if (chunkAtCell != null
                        && chunkAtCell2 != null
                        && chunkAtCell3 != null
                        && chunkAtCell4 != null) {
                        int num4 = num - 1 - chunkAtCell.Origin.X;
                        int num5 = num2 - chunkAtCell.Origin.Y;
                        int num6 = num + 1 - chunkAtCell2.Origin.X;
                        int num7 = num2 - chunkAtCell2.Origin.Y;
                        int num8 = num - chunkAtCell3.Origin.X;
                        int num9 = num2 - 1 - chunkAtCell3.Origin.Y;
                        int num10 = num - chunkAtCell4.Origin.X;
                        int num11 = num2 + 1 - chunkAtCell4.Origin.Y;
                        int num12 = Terrain.ExtractSunlightHeight(chunkAtCell.GetShaftValueFast(num4, num5));
                        int num13 = Terrain.ExtractSunlightHeight(chunkAtCell2.GetShaftValueFast(num6, num7));
                        int num14 = Terrain.ExtractSunlightHeight(chunkAtCell3.GetShaftValueFast(num8, num9));
                        int num15 = Terrain.ExtractSunlightHeight(chunkAtCell4.GetShaftValueFast(num10, num11));
                        int num16 = MathUtils.Min(num12, num13, num14, num15);
                        int l = num16;
                        int num17 = TerrainChunk.CalculateCellIndex(i, num16, j);
                        while (l <= topHeightFast) {
                            int cellValueFast2 = chunk.GetCellValueFast(num17);
                            Block block2 = blocks[Terrain.ExtractContents(cellValueFast2)];
                            if (block2.IsTransparent_(cellValueFast2)) {
                                int cellLightFast = chunkAtCell.GetCellLightFast(num4, l, num5);
                                int cellLightFast2 = chunkAtCell2.GetCellLightFast(num6, l, num7);
                                int cellLightFast3 = chunkAtCell3.GetCellLightFast(num8, l, num9);
                                int cellLightFast4 = chunkAtCell4.GetCellLightFast(num10, l, num11);
                                int num18 = MathUtils.Max(cellLightFast, cellLightFast2, cellLightFast3, cellLightFast4)
                                    - m_lightAttenuationWithDistance
                                    - block2.LightAttenuation;
                                if (num18 > Terrain.ExtractLight(cellValueFast2)) {
                                    chunk.SetCellValueFast(num17, Terrain.ReplaceLight(cellValueFast2, num18));
                                    if (num18 > 1) {
                                        m_lightSources.Add(new LightSource { X = num, Y = l, Z = num2, Light = num18 });
                                    }
                                }
                            }
                            l++;
                            num17++;
                        }
                    }
                }
            }
        }

        public virtual void GenerateChunkEdgeLightSources(TerrainChunk chunk, int face) {
            Block[] blocks = BlocksManager.Blocks;
            int num = 0;
            int num2 = 0;
            int num3 = 0;
            int num4 = 0;
            TerrainChunk terrainChunk;
            switch (face) {
                case 0:
                    terrainChunk = chunk.Terrain.GetChunkAtCoords(chunk.Coords.X, chunk.Coords.Y + 1);
                    num2 = TerrainChunk.SizeMinusOne;
                    num4 = 0;
                    break;
                case 1:
                    terrainChunk = chunk.Terrain.GetChunkAtCoords(chunk.Coords.X + 1, chunk.Coords.Y);
                    num = TerrainChunk.SizeMinusOne;
                    num3 = 0;
                    break;
                case 2:
                    terrainChunk = chunk.Terrain.GetChunkAtCoords(chunk.Coords.X, chunk.Coords.Y - 1);
                    num2 = 0;
                    num4 = TerrainChunk.SizeMinusOne;
                    break;
                default:
                    terrainChunk = chunk.Terrain.GetChunkAtCoords(chunk.Coords.X - 1, chunk.Coords.Y);
                    num = 0;
                    num3 = TerrainChunk.SizeMinusOne;
                    break;
            }
            if (terrainChunk == null
                || terrainChunk.ThreadState < TerrainChunkState.InvalidPropagatedLight) {
                return;
            }
            for (int i = 0; i < TerrainChunk.Size; i++) {
                switch (face) {
                    case 0:
                        num = i;
                        num3 = i;
                        break;
                    case 1:
                        num2 = i;
                        num4 = i;
                        break;
                    case 2:
                        num = i;
                        num3 = i;
                        break;
                    default:
                        num2 = i;
                        num4 = i;
                        break;
                }
                int num5 = num + chunk.Origin.X;
                int num6 = num2 + chunk.Origin.Y;
                int bottomHeightFast = chunk.GetBottomHeightFast(num, num2);
                int num7 = TerrainChunk.CalculateCellIndex(num, 0, num2);
                int num8 = TerrainChunk.CalculateCellIndex(num3, 0, num4);
                for (int j = bottomHeightFast; j <= TerrainChunk.HeightMinusOne; j++) {
                    int cellValueFast = chunk.GetCellValueFast(num7 + j);
                    int num9 = Terrain.ExtractContents(cellValueFast);
                    if (blocks[num9].IsTransparent_(cellValueFast)) {
                        int num10 = Terrain.ExtractLight(cellValueFast);
                        int num11 = Terrain.ExtractLight(terrainChunk.GetCellValueFast(num8 + j)) - 1;
                        if (num11 > num10) {
                            chunk.SetCellValueFast(num7 + j, Terrain.ReplaceLight(cellValueFast, num11));
                            if (num11 > 1) {
                                m_lightSources.Add(new LightSource { X = num5, Y = j, Z = num6, Light = num11 });
                            }
                        }
                    }
                }
            }
        }

        public virtual void PropagateLightSource(int x, int y, int z, int light) {
            TerrainChunk chunkAtCell = m_terrain.GetChunkAtCell(x, z);
            if (chunkAtCell == null) {
                return;
            }
            int index = TerrainChunk.CalculateCellIndex(x & 0xF, y, z & 0xF);
            int cellValueFast = chunkAtCell.GetCellValueFast(index);
            int num = Terrain.ExtractContents(cellValueFast);
            Block block = BlocksManager.Blocks[num];
            if (block.IsTransparent_(cellValueFast)) {
                int num2 = light - block.LightAttenuation - m_lightAttenuationWithDistance;
                if (num2 > Terrain.ExtractLight(cellValueFast)) {
                    m_lightSources.Add(new LightSource { X = x, Y = y, Z = z, Light = num2 });
                    chunkAtCell.SetCellValueFast(index, Terrain.ReplaceLight(cellValueFast, num2));
                }
            }
        }

        public virtual void PropagateLightSources() {
            for (int i = 0; i < m_lightSources.Count && i < 120000; i++) {
                LightSource lightSource = m_lightSources.Array[i];
                int light = lightSource.Light;
                if (light > 1) {
                    PropagateLightSource(lightSource.X - 1, lightSource.Y, lightSource.Z, light);
                    PropagateLightSource(lightSource.X + 1, lightSource.Y, lightSource.Z, light);
                    if (lightSource.Y > TerrainChunk.MinHeight) {   // [负高度实验] 原来 > 0
                        PropagateLightSource(lightSource.X, lightSource.Y - 1, lightSource.Z, light);
                    }
                    if (lightSource.Y < TerrainChunk.HeightMinusOne) {
                        PropagateLightSource(lightSource.X, lightSource.Y + 1, lightSource.Z, light);
                    }
                    PropagateLightSource(lightSource.X, lightSource.Y, lightSource.Z - 1, light);
                    PropagateLightSource(lightSource.X, lightSource.Y, lightSource.Z + 1, light);
                }
            }
            for (int i = 0; i < m_lightSources.Count && i < 120000; i++) {
                LightSource lightSource = m_lightSources.Array[i];
                int light = lightSource.Light;
                int x = lightSource.X;
                int y = lightSource.Y;
                int z = lightSource.Z;
                int num2 = x & TerrainChunk.SizeMinusOne;
                int num3 = z & TerrainChunk.SizeMinusOne;
                TerrainChunk chunkAtCell = m_terrain.GetChunkAtCell(x, z);
                if (num2 == 0) {
                    PropagateLightSource(m_terrain.GetChunkAtCell(x - 1, z), x - 1, y, z, light);
                }
                else {
                    PropagateLightSource(chunkAtCell, x - 1, y, z, light);
                }
                if (num2 == TerrainChunk.SizeMinusOne) {
                    PropagateLightSource(m_terrain.GetChunkAtCell(x + 1, z), x + 1, y, z, light);
                }
                else {
                    PropagateLightSource(chunkAtCell, x + 1, y, z, light);
                }
                if (num3 == 0) {
                    PropagateLightSource(m_terrain.GetChunkAtCell(x, z - 1), x, y, z - 1, light);
                }
                else {
                    PropagateLightSource(chunkAtCell, x, y, z - 1, light);
                }
                if (num3 == TerrainChunk.SizeMinusOne) {
                    PropagateLightSource(m_terrain.GetChunkAtCell(x, z + 1), x, y, z + 1, light);
                }
                else {
                    PropagateLightSource(chunkAtCell, x, y, z + 1, light);
                }
                if (y > TerrainChunk.MinHeight) {   // [负高度实验] 原来 > 0
                    PropagateLightSource(chunkAtCell, x, y - 1, z, light);
                }
                if (y < TerrainChunk.HeightMinusOne) {
                    PropagateLightSource(chunkAtCell, x, y + 1, z, light);
                }
            }
        }

        [MethodImpl(256)]
        public virtual void PropagateLightSource(TerrainChunk chunk, int x, int y, int z, int light) {
            if (chunk != null) {
                int num = TerrainChunk.CalculateCellIndex(x & TerrainChunk.SizeMinusOne, y, z & TerrainChunk.SizeMinusOne);
                int cellValueFast = chunk.GetCellValueFast(num);
                int num2 = Terrain.ExtractContents(cellValueFast);
                Block block = BlocksManager.Blocks[num2];
                if (block.IsTransparent_(cellValueFast)) {
                    int num3 = light - block.LightAttenuation - m_lightAttenuationWithDistance;
                    if (num3 > Terrain.ExtractLight(cellValueFast)) {
                        if (num3 > 1) {
                            m_lightSources.Add(new LightSource { X = x, Y = y, Z = z, Light = num3 });
                        }
                        chunk.SetCellValueFast(num, Terrain.ReplaceLight(cellValueFast, num3));
                    }
                }
            }
        }

        public virtual void GenerateChunkVertices(TerrainChunk chunk, int stage) {
            // [v0.0.9] 家具几何预算：以"区块 + 阶段"为窗口重新计数（见 Game/SkylineFurniture.cs）
            SkylineFurniture.BeginStage($"{chunk.Coords.X},{chunk.Coords.Y}/s{stage}");
            // [v0.1.0] 家具 LOD：stage 0 时重置该区块的实例统计（见 Game/SkylineRender.cs）
            SkylineRender.BeginChunkStage(chunk, stage);
            m_subsystemTerrain.BlockGeometryGenerator.ResetCache();
            TerrainChunk chunkAtCoords1 = m_terrain.GetChunkAtCoords(chunk.Coords.X - 1, chunk.Coords.Y - 1);
            TerrainChunk chunkAtCoords2 = m_terrain.GetChunkAtCoords(chunk.Coords.X, chunk.Coords.Y - 1);
            TerrainChunk chunkAtCoords3 = m_terrain.GetChunkAtCoords(chunk.Coords.X + 1, chunk.Coords.Y - 1);
            TerrainChunk chunkAtCoords4 = m_terrain.GetChunkAtCoords(chunk.Coords.X - 1, chunk.Coords.Y);
            TerrainChunk chunkAtCoords5 = m_terrain.GetChunkAtCoords(chunk.Coords.X + 1, chunk.Coords.Y);
            TerrainChunk chunkAtCoords6 = m_terrain.GetChunkAtCoords(chunk.Coords.X - 1, chunk.Coords.Y + 1);
            TerrainChunk chunkAtCoords7 = m_terrain.GetChunkAtCoords(chunk.Coords.X, chunk.Coords.Y + 1);
            TerrainChunk chunkAtCoords8 = m_terrain.GetChunkAtCoords(chunk.Coords.X + 1, chunk.Coords.Y + 1);
            int num1 = 0;
            int num2 = 0;
            int num3 = TerrainChunk.Size;
            int num4 = TerrainChunk.Size;
            if (chunkAtCoords4 == null) {
                ++num1;
            }
            if (chunkAtCoords2 == null) {
                ++num2;
            }
            if (chunkAtCoords5 == null) {
                --num3;
            }
            if (chunkAtCoords7 == null) {
                --num4;
            }
            for (int index = 0; index < TerrainChunk.SlicesCount; ++index) {
                if (index % 2 == stage) {
                    int generateHash = chunk.GeneratedSliceContentsHashes[index];
                    if (generateHash != 0
                        && generateHash == chunk.SliceContentsHashes[index]) {
                        m_statistics.SkippedSlices++;
                        continue;
                    }
                    chunk.GeneratedSliceContentsHashes[index] = 0;
                    ++m_statistics.GeneratedSlices;
                    TerrainGeometry geometry = chunk.ChunkSliceGeometries[index];
                    if (geometry == null) {
                        geometry = new TerrainGeometry(m_subsystemAnimatedTextures.AnimatedBlocksTexture);
                        chunk.ChunkSliceGeometries[index] = geometry;
                    }
                    geometry.ClearGeometry();
                    for (int x1 = num1; x1 < num3; ++x1) {
                        for (int z1 = num2; z1 < num4; ++z1) {
                            switch (x1) {
                                case 0:
                                    if ((z1 == 0 && chunkAtCoords1 == null)
                                        || (z1 == TerrainChunk.SizeMinusOne && chunkAtCoords6 == null)) {
                                        break;
                                    }
                                    goto default;
                                case TerrainChunk.SizeMinusOne:
                                    if ((z1 == 0 && chunkAtCoords3 == null)
                                        || (z1 == TerrainChunk.SizeMinusOne && chunkAtCoords8 == null)) {
                                        break;
                                    }
                                    goto default;
                                default:
                                    int x2 = x1 + chunk.Origin.X;
                                    int z2 = z1 + chunk.Origin.Y;
                                    int x2_1 = MathUtils.Min(
                                        chunk.GetBottomHeightFast(x1, z1) - 1,
                                        MathUtils.Min(
                                            m_terrain.GetBottomHeight(x2 - 1, z2),
                                            m_terrain.GetBottomHeight(x2 + 1, z2),
                                            m_terrain.GetBottomHeight(x2, z2 - 1),
                                            m_terrain.GetBottomHeight(x2, z2 + 1)
                                        )
                                    );
                                    int x2_2 = chunk.GetTopHeightFast(x1, z1) + 1;
                                    // [负高度实验] 片区间要跟着 MinHeight 走（这片 = MinHeight + 16*index .. +16）
                                    int num5 = MathUtils.Max(TerrainChunk.MinHeight + TerrainChunk.SliceHeight * index, x2_1, TerrainChunk.MinHeight);
                                    // [高度实验] 原来写死 byte.MaxValue(255) —— 这就是"y>255 的方块不显示"的直接原因
                                    int num6 = MathUtils.Min(TerrainChunk.MinHeight + TerrainChunk.SliceHeight * (index + 1), x2_2, TerrainChunk.HeightMinusOne);
                                    int cellIndex = TerrainChunk.CalculateCellIndex(x1, 0, z1);
                                    for (int y = num5; y < num6; ++y) {
                                        int cellValueFast = chunk.GetCellValueFast(cellIndex + y);
                                        int contents = Terrain.ExtractContents(cellValueFast);
                                        if (contents != 0) {
                                            BlocksManager.Blocks[contents]
                                                .GenerateTerrainVertices(
                                                    m_subsystemTerrain.BlockGeometryGenerator,
                                                    geometry,
                                                    cellValueFast,
                                                    x2,
                                                    y,
                                                    z2
                                                );
                                        }
                                    }
                                    break;
                            }
                        }
                    }
                    chunk.GeneratedSliceContentsHashes[index] = chunk.SliceContentsHashes[index];
                }
            }
        }

        public virtual void CalculateChunkSliceContentsHashes(TerrainChunk chunk) {
            double realTime = Time.RealTime;
            int hash1 = 1;
            hash1 += m_terrain.SeasonTemperature;
            hash1 *= 31;
            hash1 += m_terrain.SeasonHumidity;
            hash1 *= 31;
            for (int i = 0; i < TerrainChunk.SlicesCount; i++) {
                chunk.SliceContentsHashes[i] = hash1;
            }
            int startOriginX = chunk.Origin.X - 1;
            int endOriginX = chunk.Origin.X + TerrainChunk.Size + 1;
            int startOriginY = chunk.Origin.Y - 1;
            int endOriginY = chunk.Origin.Y + TerrainChunk.Size + 1;
            for (int originX = startOriginX; originX < endOriginX; originX++) {
                for (int originY = startOriginY; originY < endOriginY; originY++) {
                    TerrainChunk chunkAtCell = m_terrain.GetChunkAtCell(originX, originY);
                    if (chunkAtCell != null) {
                        int x = originX & TerrainChunk.SizeMinusOne;
                        int z = originY & TerrainChunk.SizeMinusOne;
                        long shaftValueFast = chunkAtCell.GetShaftValueFast(x, z);
                        int topHeight = Terrain.ExtractTopHeight(shaftValueFast);
                        int bottomHeight = Terrain.ExtractBottomHeight(shaftValueFast);
                        int neighborBottomHeight1 = x > 0
                            ? chunkAtCell.GetBottomHeightFast(x - 1, z)
                            : m_terrain.GetBottomHeight(originX - 1, originY);
                        int neighborBottomHeight2 = z > 0
                            ? chunkAtCell.GetBottomHeightFast(x, z - 1)
                            : m_terrain.GetBottomHeight(originX, originY - 1);
                        int neighborBottomHeight3 = x < TerrainChunk.SizeMinusOne
                            ? chunkAtCell.GetBottomHeightFast(x + 1, z)
                            : m_terrain.GetBottomHeight(originX + 1, originY);
                        int neighborBottomHeight4 = z < TerrainChunk.SizeMinusOne
                            ? chunkAtCell.GetBottomHeightFast(x, z + 1)
                            : m_terrain.GetBottomHeight(originX, originY + 1);
                        int minBottomHeight = MathUtils.Min(
                            MathUtils.Min(neighborBottomHeight1, neighborBottomHeight2, neighborBottomHeight3, neighborBottomHeight4),
                            bottomHeight - 1
                        );
                        int topHeight2 = topHeight + 2;
                        minBottomHeight = MathUtils.Max(minBottomHeight, TerrainChunk.MinHeight);
                        topHeight2 = MathUtils.Min(topHeight2, TerrainChunk.HeightMinusOne);
                        // [v0.0.9 修复] 切片区间必须覆盖到 SlicesCount（分支里 = 128），
                        // 且要用 MinHeight 做偏移（几何生成 GenerateChunkVertices 用的就是
                        // MinHeight + SliceHeight*index）。原来写的是 `SliceHeight - 1`（=15）——
                        // 那是上游 16 切片时代的值；高度扩到 2048/128 片后没同步，导致
                        // **16 号以上的切片内容哈希永不变化 → 写在 y≈-768 以上的方块
                        // 永远不会触发几何重建 → "有碰撞、无渲染"**（2026-09-26 实测）。
                        int startSlice = MathUtils.Max((minBottomHeight - TerrainChunk.MinHeight - 1) / TerrainChunk.SliceHeight, 0);
                        int endSlice = MathUtils.Min((topHeight2 - TerrainChunk.MinHeight + 1) / TerrainChunk.SliceHeight,
                            TerrainChunk.SlicesCount - 1);
                        int hash2 = 1;
                        hash2 += Terrain.ExtractTemperature(shaftValueFast);
                        hash2 *= 31;
                        hash2 += Terrain.ExtractHumidity(shaftValueFast);
                        hash2 *= 31;
                        for (int slice = startSlice; slice <= endSlice; slice++) {
                            int hash3 = hash2;
                            int startY = MathUtils.Max(TerrainChunk.MinHeight + slice * TerrainChunk.SliceHeight - 1, minBottomHeight);
                            int endY = MathUtils.Min(TerrainChunk.MinHeight + slice * TerrainChunk.SliceHeight + TerrainChunk.SliceHeight + 1, topHeight2);
                            int cellIndex = TerrainChunk.CalculateCellIndex(x, startY, z);
                            int endCellIndex = cellIndex + endY - startY;
                            while (cellIndex < endCellIndex) {
                                hash3 += chunkAtCell.GetCellValueFast(cellIndex++);
                                hash3 *= 31;
                            }
                            hash3 += startY;
                            hash3 *= 31;
                            chunk.SliceContentsHashes[slice] += hash3;
                        }
                    }
                }
            }
            double realTime2 = Time.RealTime;
            m_statistics.HashCount++;
            m_statistics.HashTime += realTime2 - realTime;
        }

        public virtual void NotifyBlockBehaviors(TerrainChunk chunk) {
            ChunkInitialized?.Invoke(chunk);
            foreach (SubsystemBlockBehavior blockBehavior in m_subsystemBlockBehaviors.BlockBehaviors) {
                blockBehavior.OnChunkInitialized(chunk);
            }
            bool isLoaded = chunk.IsLoaded;
            for (int i = 0; i < TerrainChunk.Size; i++) {
                for (int j = 0; j < TerrainChunk.Size; j++) {
                    int x = i + chunk.Origin.X;
                    int z = j + chunk.Origin.Y;
                    int num = TerrainChunk.CalculateCellIndex(i, 0, j);
                    int num2 = 0;
                    while (num2 < TerrainChunk.HeightMinusOne) {
                        int cellValueFast = chunk.GetCellValueFast(num);
                        int contents = Terrain.ExtractContents(cellValueFast);
                        if (contents != 0) {
                            SubsystemBlockBehavior[] blockBehaviors = m_subsystemBlockBehaviors.GetBlockBehaviors(contents);
                            for (int k = 0; k < blockBehaviors.Length; k++) {
                                blockBehaviors[k].OnBlockGenerated(cellValueFast, x, num2, z, isLoaded);
                            }
                        }
                        num2++;
                        num++;
                    }
                }
            }
        }

        public virtual void UnpauseUpdateThread() {
            lock (m_unpauseLock) {
                m_unpauseUpdateThread = true;
                m_pauseEvent.Set();
            }
        }

        public virtual void SettingsManager_SettingChanged(string name) {
            if (name == "Brightness") {
                DowngradeAllChunksState(TerrainChunkState.InvalidVertices1, true);
            }
        }

        // ============================================================================================
        // [v0.1.39] 里程碑 3「32³ 第 2 步」：**立方体级（三维坐标）窗口判定**
        //
        // v0.1.28 的判据是"列级"：椭球竖直切片里**该列任何一层**有内容 → 整列保留（保守，浪费内存）。
        // 立方体级只认"这个 32³ 立方体所在那一段"（列分带掩码的**对应位**）——
        // 这正是 P3（跨列共享 32³ 立方体分页）将来要用的分配粒度，先把判据与诊断做实，
        // 不动任何存储布局（可随时回退）。
        // ============================================================================================
        public const int CubeSize = 32;

        static int CubeBandIndex(int cy) => cy - TerrainChunk.MinHeight / CubeSize;   // MinHeight=-1024 → +32

        /// <summary>一个 32³ 立方体横跨 2×2 个 16×16 列 —— 分带掩码是**列级**的，所以内容判定要 OR 这 4 列。</summary>
        static void CubeColumns(int cx, int cz, out int colX0, out int colX1, out int colZ0, out int colZ1) {
            colX0 = cx * 2; colX1 = colX0 + 1;
            colZ0 = cz * 2; colZ1 = colZ0 + 1;
        }

        /// <summary>[v0.1.39] 单立方体判定：三维坐标 (cx,cy,cz)，一格 = 32³。
        /// 输出：是否在球窗椭球内 / 该立方体那一段是否真有内容 / 旧"列级"判据会不会保留它 / 最终保留与否 / 原因。</summary>
        public virtual void CubeWindowDecide(int cx, int cy, int cz,
                                             out bool inSphere, out bool hasContent, out bool columnRuleKeep,
                                             out bool kept, out string reason) {
            inSphere = false;
            kept = false;
            reason = "outOfWindow";
            Vector3 cubeCenter = new(cx * CubeSize + CubeSize * 0.5f, cy * CubeSize + CubeSize * 0.5f,
                                     cz * CubeSize + CubeSize * 0.5f);
            int bandIndex = CubeBandIndex(cy);
            CubeColumns(cx, cz, out int colX0, out int colX1, out int colZ0, out int colZ1);
            bool maskKnown = false;
            hasContent = false;
            for (int colX = colX0; colX <= colX1; colX++) {
                for (int colZ = colZ0; colZ <= colZ1; colZ++) {
                    if (!TryGetContentBandMask32(colX, colZ, out ulong columnMask)) {
                        continue;
                    }
                    maskKnown = true;
                    if (bandIndex >= 0 && bandIndex < 64 && ((columnMask >> bandIndex) & 1UL) != 0) {
                        hasContent = true;
                    }
                }
            }
            columnRuleKeep = false;
            float yMul = MathF.Max(m_subsystemSky?.VisibilityRangeYMultiplier ?? 1f, 0.05f);
            foreach (KeyValuePair<int, UpdateLocation> kv in m_updateParameters.Locations) {
                UpdateLocation location = kv.Value;
                if (!location.SphereWindow) {
                    continue;
                }
                float dx = cubeCenter.X - location.Center.X;
                float dz = cubeCenter.Z - location.Center.Y;
                float horiz2 = dx * dx + dz * dz;
                float cd2 = location.ContentDistance * location.ContentDistance;
                if (horiz2 > cd2) {
                    continue;
                }
                float reach = MathF.Sqrt(MathUtils.Max(cd2 - horiz2, 0f)) * yMul;
                if (MathF.Abs(cubeCenter.Y - location.CenterY) > reach) {
                    continue;
                }
                inSphere = true;
                for (int colX = colX0; colX <= colX1 && !columnRuleKeep; colX++) {
                    for (int colZ = colZ0; colZ <= colZ1; colZ++) {
                        if (TryGetContentBandMask32(colX, colZ, out ulong columnMask)
                            && BandMaskHasContentInRange(columnMask, location.CenterY - reach, location.CenterY + reach)) {
                            columnRuleKeep = true;               // 旧列级判据（v0.1.28）会保留整列
                            break;
                        }
                    }
                }
            }
            kept = inSphere && hasContent;
            reason = !inSphere ? "outOfWindow"
                : !maskKnown ? "noMask"
                : hasContent ? "kept"
                : columnRuleKeep ? "cubeBandEmpty(oldRuleWouldKeep)"
                : "cubeBandEmpty";
        }

        /// <summary>[v0.1.39] 单立方体判定的文本版（桥：`skyline.CubeWindowDecision(cx,cy,cz)`）。</summary>
        public virtual string DescribeCubeWindowDecision(int cx, int cy, int cz) {
            System.Text.StringBuilder sb = new();
            sb.Append($"cube=({cx},{cy},{cz}) y=[{cy * CubeSize},{cy * CubeSize + CubeSize - 1}] ");
            sb.Append($"cubeSize={CubeSize} ");
            CubeColumns(cx, cz, out int colX0, out int colX1, out int colZ0, out int colZ1);
            int bandIndex = CubeBandIndex(cy);
            sb.Append($"columns=({colX0},{colZ0})..({colX1},{colZ1}) bandIndex={bandIndex} ");
            for (int colX = colX0; colX <= colX1; colX++) {
                for (int colZ = colZ0; colZ <= colZ1; colZ++) {
                    TerrainChunk column = m_terrain.GetChunkAtCoords(colX, colZ);
                    if (TryGetContentBandMask32(colX, colZ, out ulong columnMask)) {
                        int bit = bandIndex >= 0 && bandIndex < 64 ? (int)((columnMask >> bandIndex) & 1UL) : -1;
                        sb.Append($"[col({colX},{colZ}) allocated={column != null} mask=0x{columnMask:X16} bit={bit}] ");
                    }
                    else {
                        sb.Append($"[col({colX},{colZ}) allocated={column != null} maskKnown=none] ");
                    }
                }
            }
            CubeWindowDecide(cx, cy, cz, out bool inSphere, out bool hasContent, out bool columnRuleKeep,
                             out bool kept, out string reason);
            sb.Append($"inSphere={inSphere} cubeHasContent={hasContent} columnRuleKeep={columnRuleKeep} "
                + $"kept={kept} reason={reason}");
            return sb.ToString();
        }

        /// <summary>[v0.1.39] **专项窗口报告**：以相机所在立方体为中心，统计 ±radiusCubes（横）/±yRadius（竖）
        /// 范围内立方体级判据的分布，并给出"旧列级判据会多保留多少"的对照 —— 这是 P3 的收益口径。</summary>
        public virtual string CubeWindowSurvey(int pcx, int pcy, int pcz, int radiusCubes, int yRadius) {
            int total = 0, inSphere = 0, hasContent = 0, kept = 0, columnRuleKeep = 0;
            Dictionary<int, int> keptByCy = [];
            Dictionary<int, int> sphereByCy = [];
            for (int cx = pcx - radiusCubes; cx <= pcx + radiusCubes; cx++) {
                for (int cz = pcz - radiusCubes; cz <= pcz + radiusCubes; cz++) {
                    for (int cy = pcy - yRadius; cy <= pcy + yRadius; cy++) {
                        CubeWindowDecide(cx, cy, cz, out bool iS, out bool hC, out bool cR, out bool k, out _);
                        total++;
                        if (iS) {
                            inSphere++;
                            sphereByCy.TryGetValue(cy, out int sN);
                            sphereByCy[cy] = sN + 1;
                        }
                        if (hC) {
                            hasContent++;
                        }
                        if (k) {
                            kept++;
                            keptByCy.TryGetValue(cy, out int kN);
                            keptByCy[cy] = kN + 1;
                        }
                        if (cR) {
                            columnRuleKeep++;
                        }
                    }
                }
            }
            JsonArray byCy = [];
            for (int cy = pcy - yRadius; cy <= pcy + yRadius; cy++) {
                sphereByCy.TryGetValue(cy, out int sN);
                keptByCy.TryGetValue(cy, out int kN);
                byCy.Add(new JsonObject { ["cy"] = cy, ["inSphere"] = sN, ["kept"] = kN });
            }
            JsonObject result = new() {
                ["ok"] = true,
                ["cameraCube"] = new JsonArray(pcx, pcy, pcz),
                ["radiusCubes"] = radiusCubes,
                ["yRadius"] = yRadius,
                ["total"] = total,
                ["inSphere"] = inSphere,
                ["cubeHasContent"] = hasContent,
                ["kept"] = kept,
                ["columnRuleKeep"] = columnRuleKeep,
                ["emptyButColumnRuleKeeps"] = columnRuleKeep - kept,
                ["byCy"] = byCy,
                ["sphereLoadingEnabled"] = SkylineRuntime.SphereLoadingEnabled,
                ["cubeBands"] = SkylineRuntime.SphereLoadingCubeBands,
                ["note"] = "kept = 立方体级（三维坐标 + 该段掩码位）；columnRuleKeep = v0.1.28 列级判据"
            };
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.40] **P3 分配计划**：把 v0.1.39 的立方体级判据落到"真要分配多少页/多少字节"，
        /// 并与当前**列式**（16×16 列 × 32 层分带，每段 32 KiB）对照。这是 P3 正式版决策的数字依据。
        ///
        /// 口径：
        ///   * 立方体页：每个"保留的立方体"一页 128 KiB（`CubeChunk32.Bytes`）；
        ///   * 列式分带：窗口内**需要**的分带数 × 32 KiB（`16×16×32` 单元格 = 32 KiB）；
        ///   * `partialCubes`：在它那 4 个列里**只有部分列有内容**的立方体 —— 共享页在这种地方是浪费，
        ///     浪费量 = 共享页字节 − 列式分带字节（输出里的 `wasteBytes`）。
        /// </summary>
        public virtual string CubeAllocationPlan(int pcx, int pcy, int pcz, int radiusCubes, int yRadius) {
            int inSphere = 0, kept = 0, columnRuleKeep = 0;
            int ref1 = 0, ref2 = 0, ref3 = 0, ref4 = 0;
            HashSet<long> columnBandKeys = [];
            HashSet<long> columnKeys = [];
            for (int cx = pcx - radiusCubes; cx <= pcx + radiusCubes; cx++) {
                for (int cz = pcz - radiusCubes; cz <= pcz + radiusCubes; cz++) {
                    for (int cy = pcy - yRadius; cy <= pcy + yRadius; cy++) {
                        CubeWindowDecide(cx, cy, cz, out bool iS, out bool hC, out bool cR, out bool k, out _);
                        if (iS) {
                            inSphere++;
                        }
                        if (cR) {
                            columnRuleKeep++;
                        }
                        if (!k) {
                            continue;
                        }
                        kept++;
                        int colsWithContent = 0;
                        CubeColumns(cx, cz, out int colX0, out int colX1, out int colZ0, out int colZ1);
                        int bandIndex = CubeBandIndex(cy);
                        for (int colX = colX0; colX <= colX1; colX++) {
                            for (int colZ = colZ0; colZ <= colZ1; colZ++) {
                                if (!TryGetContentBandMask32(colX, colZ, out ulong columnMask)
                                    || bandIndex < 0 || bandIndex >= 64
                                    || ((columnMask >> bandIndex) & 1UL) == 0) {
                                    continue;
                                }
                                colsWithContent++;
                                long columnKey = ((long)colX << 32) | (uint)colZ;
                                columnKeys.Add(columnKey);
                                columnBandKeys.Add((columnKey << 6) | (uint)bandIndex);
                            }
                        }
                        switch (colsWithContent) {
                            case 1: ref1++; break;
                            case 2: ref2++; break;
                            case 3: ref3++; break;
                            default: ref4++; break;
                        }
                    }
                }
            }
            long pageBytes = (long)kept * CubeChunk32.Bytes;
            long columnBytes = (long)columnBandKeys.Count * 32L * 1024L;
            JsonObject result = new() {
                ["ok"] = true,
                ["cameraCube"] = new JsonArray(pcx, pcy, pcz),
                ["radiusCubes"] = radiusCubes,
                ["yRadius"] = yRadius,
                ["inSphere"] = inSphere,
                ["keptCubes"] = kept,
                ["columnRuleKeep"] = columnRuleKeep,
                ["columnsTouched"] = columnKeys.Count,
                ["columnBands"] = columnBandKeys.Count,
                ["cubePages"] = kept,
                ["cubePageBytes"] = pageBytes,
                ["columnBytes"] = columnBytes,
                ["differenceBytes"] = pageBytes - columnBytes,
                ["ratio"] = columnBytes > 0 ? Math.Round((double)pageBytes / columnBytes, 3) : 0,
                ["refCounts"] = new JsonObject {
                    ["oneColumnWithContent"] = ref1,
                    ["twoColumns"] = ref2,
                    ["threeColumns"] = ref3,
                    ["fourColumns"] = ref4
                },
                ["wasteNote"] = "共享页在'只有部分列有内容'的立方体上是浪费：waste = cubePageBytes − columnBytes",
                ["pageBytesPerCube"] = CubeChunk32.Bytes,
                ["columnBandBytes"] = 32 * 1024
            };
            return result.ToJsonString();
        }
    }
}
