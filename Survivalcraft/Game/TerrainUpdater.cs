using System.Runtime.CompilerServices;
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
    }
}
