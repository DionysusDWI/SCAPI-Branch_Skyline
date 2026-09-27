using Engine;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— 面向"创意建筑"的运行时开关与常量（**新挂载的模块**，非 mod）。
    ///
    /// 设计口径（v0.0.3）：
    ///   * 世界竖直范围 = <see cref="TerrainChunk.MinHeight"/> .. <see cref="TerrainChunk.HeightMinusOne"/>
    ///     （当前 -1024..1023，共 2048 层）。
    ///   * **生存余量** <see cref="SurvivalMargin"/> = 64：建筑范围上下各留 64 格，
    ///     角色在 [BuildMinY-64, BuildMaxY+64] = [-1088, 1087] 内不掉血、不缺氧；
    ///     超出这个范围才按原版逻辑扣虚空伤害/缺氧。
    ///   * <see cref="FreeViewMode"/>（取景模式）：打开后**完全不受高度/深度伤害限制**，
    ///     为将来"超限高度飞行取景/漫游"做底层准备（本版暂不加 UI 按钮）。
    ///
    /// 这些静态成员可以被外部直接调用，例如 AgentBridge：
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime.FreeViewMode","value":true}
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime","member":"Describe","args":[]}
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>建筑范围之外、仍允许角色生存的余量（格）。</summary>
        public const int SurvivalMargin = 64;

        /// <summary>取景模式：不受高度/深度伤害与缺氧限制（默认关闭）。</summary>
        public static bool FreeViewMode { get; set; }

        /// <summary>可建造的最低层。</summary>
        public static int BuildMinY => TerrainChunk.MinHeight;

        /// <summary>可建造的最高层。</summary>
        public static int BuildMaxY => TerrainChunk.HeightMinusOne;

        /// <summary>角色生存的下限（含 64 格余量）。</summary>
        public static int SurvivalMinY => TerrainChunk.MinHeight - SurvivalMargin;

        /// <summary>角色生存的上限（含 64 格余量）。</summary>
        public static int SurvivalMaxY => TerrainChunk.HeightMinusOne + SurvivalMargin;

        /// <summary>该 y 是否在建筑范围内。</summary>
        public static bool IsInsideBuildRange(int y) => y >= BuildMinY && y <= BuildMaxY;

        /// <summary>该 y 是否允许角色存活（取景模式恒为真）。</summary>
        public static bool IsInsideSurvivalRange(float y) =>
            FreeViewMode || (y >= SurvivalMinY && y <= SurvivalMaxY);

        /// <summary>一行状态，便于桥/日志读取。</summary>
        public static string Describe() =>
            $"Skyline build=[{BuildMinY},{BuildMaxY}] survival=[{SurvivalMinY},{SurvivalMaxY}] freeView={FreeViewMode} "
            + $"residency={ChunkResidencyMode} regions={Regions.Count} v={m_residencyVersion} "
            + SkylineFurniture.Describe() + " " + SkylineRender.Describe() + " " + SkylineLod.Describe();

        // ==========================================================================================
        // v0.0.9：高复杂度家具的几何预算（实现在 Game/SkylineFurniture.cs，这里只做转发，
        // 好处是桥不用改代码就能通过既有 `skyline` 根读写——桥根是 typeof(SkylineRuntime)）
        // ==========================================================================================

        public static bool FurnitureBudgetEnabled {
            get => SkylineFurniture.BudgetEnabled;
            set => SkylineFurniture.BudgetEnabled = value;
        }

        public static int FurnitureMaxStageVertices {
            get => SkylineFurniture.MaxStageFurnitureVertices;
            set => SkylineFurniture.MaxStageFurnitureVertices = value;
        }

        public static bool FurnitureFallbackBox {
            get => SkylineFurniture.FallbackBox;
            set => SkylineFurniture.FallbackBox = value;
        }

        /// <summary>A/B 调试：强制所有家具渲染成方盒占位（默认关）。</summary>
        public static bool FurnitureForceBox {
            get => SkylineFurniture.ForceBox;
            set => SkylineFurniture.ForceBox = value;
        }

        /// <summary>A/B 调试：只把指定设计索引的家具强制成方盒（-1 = 关）。</summary>
        public static int FurnitureForceBoxDesign {
            get => SkylineFurniture.ForceBoxDesign;
            set => SkylineFurniture.ForceBoxDesign = value;
        }

        public static string FurnitureDescribe() => SkylineFurniture.Describe();

        public static void FurnitureResetStats() => SkylineFurniture.ResetStats();

        // ==========================================================================================
        // v0.0.9 探索：三层球形渲染原型（实现在 Game/SkylineRender.cs）
        //   * RenderEnabled 默认 **false**：关着时只有只读统计，渲染逐位等于现状
        //   * 三层球 = 占位球（MBD/占替距） + 视觉球（视距，二维→三维） + 加载球（被波及的区块都要加载）
        // ==========================================================================================

        /// <summary>是否真的按三层球把远处被埋藏家具替换成占位方盒（默认关）。</summary>
        public static bool RenderEnabled {
            get => SkylineRender.Enabled;
            set => SkylineRender.Enabled = value;
        }

        /// <summary>只读勘察：三层球在当前相机下的规模与占位球能省多少顶点（不改变渲染）。</summary>
        public static string RenderSurvey() => SkylineRender.Survey();

        public static string RenderSurvey(int radiusColumns) => SkylineRender.Survey(radiusColumns);

        public static string RenderDescribe() => SkylineRender.Describe();

        /// <summary>每个 tick 允许的 LOD 区块重建数（默认 1，防跨档重建风暴）。</summary>
        public static int RenderMaxRebakesPerTick {
            get => SkylineRender.MaxRebakesPerTick;
            set => SkylineRender.MaxRebakesPerTick = value;
        }

        public static void RenderResetStats() => SkylineRender.ResetStats();

        public static void RenderResetChunkLod() => SkylineRender.ResetChunkLod();

        // ==========================================================================================
        // v0.1.0：超视距 LOD 层（实现在 Game/SkylineLod.cs，学习 Distant Horizons 的"加载即采样 + 持久化 + 视距外渲染"）
        // ==========================================================================================

        public static bool LodEnabled {
            get => SkylineLod.Enabled;
            set => SkylineLod.Enabled = value;
        }

        public static float LodRadiusMetres {
            get => SkylineLod.RadiusMetres;
            set => SkylineLod.RadiusMetres = value;
        }

        /// <summary>v0.1.1：LOD 开启时把视图雾的跨度拉远到 LOD 半径的 90%，
        /// 让"视距边缘的真实地形"与 LOD 层共用同一条雾曲线（消除交接跳变）。</summary>
        public static bool LodFogExtend {
            get => SkylineAtmosphere.FogExtendEnabled;
            set => SkylineAtmosphere.FogExtendEnabled = value;
        }

        /// <summary>v0.1.2：视觉球"三档"开关——短球内强制全精度 / 两球之间按 d_box / 全球外占位。</summary>
        public static bool VisualSphereEnabled {
            get => SkylineRender.VisualSphereEnabled;
            set => SkylineRender.VisualSphereEnabled = value;
        }

        /// <summary>短球半径系数（× 视距）。</summary>
        public static float VisualSphereShortFactor {
            get => SkylineRender.ShortSphereFactor;
            set => SkylineRender.ShortSphereFactor = value;
        }

        /// <summary>v0.1.5：光影包接管 LOD 绘制（Dawnlight/Iris 式）。</summary>
        public static bool LodExternalShaderHooked {
            get => SkylineLod.ExternalShaderHooked;
            set => SkylineLod.ExternalShaderHooked = value;
        }

        // ---------------------------------------------------------------- v0.1.14：球形加载窗

        /// <summary>
        /// 球形加载窗总开关（**默认关闭** = 与 2D 加载逐位一致）。打开后相机的 update location 走
        /// 椭球判据 `dx² + (dy/m)² + dz² ≤ content²`（m = `SubsystemSky.VisibilityRangeYMultiplier`，
        /// 竖直用"该列内容带 bottom..top"）——相机远高于地形时，正下方的列不再被强制常驻
        /// （远景交给超视距 LOD）；已卸载列的内容带会被记住，避免"卸载→未知→又装回"的抖动。
        /// </summary>
        public static bool SphereLoadingEnabled { get; set; }

        /// <summary>当前已分配的区块列数（验证球形加载窗用）。</summary>
        public static int AllocatedChunkCount =>
            GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain?.AllocatedChunks?.Length ?? -1;

        /// <summary>球形加载窗诊断：开关 / 已分配列数 / 内容带缓存条数 / 竖直系数。</summary>
        public static string SphereLoadingDescribe() {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            float ym = GameManager.Project?.FindSubsystem<SubsystemSky>(true)?.VisibilityRangeYMultiplier ?? 1f;
            return $"sphereLoading enabled={SphereLoadingEnabled} allocatedChunks={AllocatedChunkCount} "
                + $"bandCache={updater?.ColumnBandCacheCount ?? -1} yMultiplier={ym:0.##}";
        }

        /// <summary>LOD 网格元数据（层/单元尺寸/半径/索引数），供光影包读取。</summary>
        public static string LodMeshMetadata() => SkylineLod.MeshMetadata();

        public static string LodDescribe() => SkylineLod.Describe();

        public static string LodSurvey() => SkylineLod.Survey();

        public static void LodReset() => SkylineLod.Reset();

        public static void LodSaveNow() => SkylineLod.Save();

        // ==========================================================================================
        // v0.0.5：区块列驻留（Chunk Residency）
        //
        // 解决的问题："远处建造静默失效"——玩家周围只有约 3 个区块（~48 格）的列常驻内存，
        //   更远的列会被 TerrainUpdater 释放（先存档再 FreeChunk），此时写入不报错但读回是 0、
        //   传送也过不去。大型建筑（特别是"站在中心建一整片"）必须有办法把这批列钉在内存里。
        //
        // 设计口径：
        //   * **独立开关** <see cref="ChunkResidencyMode"/>：与将来"游览模式的球形视距"互不干扰——
        //     本机制只往 TerrainUpdater 的 update locations 里追加"覆盖圆"，不改相机的可见距离，
        //     也不改 <see cref="SubsystemSky.VisibilityRange"/>；关掉开关就完全恢复原版行为。
        //   * 区域用**世界坐标矩形**描述，内部用一串半径 <see cref="ResidencyCircleRadiusBlocks"/>
        //     的圆去覆盖（步长 = 半径，网格排布可无缝覆盖），这样能直接复用引擎既有的
        //     allocate/load/upgrade 流程，不另起一套加载器。
        //   * <see cref="ResidencyFullDetail"/> = true：区域内列提升到 Valid（含几何/光照，可直接取景）；
        //     false：只提升到 InvalidVertices1（内容已加载、可读写，但不生成几何，省 CPU/显存）。
        //   * 内存代价必须如实告知：每个区块 16×16×2048×4B ≈ **2 MB**，<see cref="ResidencyStatus"/>
        //     会给出估算值；超过 <see cref="ResidencyMaxChunks"/> 的请求会被拒绝（默认 192 ≈ 384 MB）。
        // ==========================================================================================

        /// <summary>临时驻留区（<see cref="EnsureRegionLoaded"/> 用）固定使用的名字。</summary>
        public const string EnsureRegionName = "__ensure";

        /// <summary>驻留区域（世界坐标，闭区间）。</summary>
        public sealed class ResidencyRegion {
            public string Name;
            public int MinX;
            public int MinZ;
            public int MaxX;
            public int MaxZ;

            /// <summary>是否为临时区域（<see cref="EnsureRegionLoaded"/> 注册，会按 TTL 自动过期）。</summary>
            public bool Temporary;

            /// <summary>过期时刻（<see cref="Engine.Time.RealTime"/> 口径），仅 Temporary 有效。</summary>
            public double ExpiresAt;

            public override string ToString() => $"{Name} [{MinX},{MinZ}]..[{MaxX},{MaxZ}]";
        }

        /// <summary>用圆覆盖驻留区域的中间表示（TerrainUpdater 直接消费）。</summary>
        public struct ResidencyCircle {
            public Vector2 Center;
            public float RadiusBlocks;
        }

        public static readonly List<ResidencyRegion> Regions = [];

        static int m_residencyVersion = 1;
        static bool m_chunkResidencyMode;
        static bool m_residencyFullDetail = true;
        static int m_residencyCircleRadiusBlocks = 64;
        static int m_residencyMaxChunks = 192;
        static double m_ensureRegionTtlSeconds = 300.0;
        static string m_residencyLastError = "";

        /// <summary>
        /// 区块列驻留总开关（**独立模式开关**，默认关闭）。
        /// 打开后 <see cref="Regions"/> 里的矩形区域会被钉在内存里，远处写入不再静默失效。
        /// </summary>
        public static bool ChunkResidencyMode {
            get => m_chunkResidencyMode;
            set {
                if (m_chunkResidencyMode != value) {
                    m_chunkResidencyMode = value;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>true：驻留列提升到 Valid（含几何，可直接看/取景）；false：只加载内容（省 CPU/显存）。</summary>
        public static bool ResidencyFullDetail {
            get => m_residencyFullDetail;
            set {
                if (m_residencyFullDetail != value) {
                    m_residencyFullDetail = value;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>覆盖圆半径（格），步长 = 半径 → 网格排布可无缝覆盖（默认 64 格 = 4 区块）。</summary>
        public static int ResidencyCircleRadiusBlocks {
            get => m_residencyCircleRadiusBlocks;
            set {
                int clamped = Math.Clamp(value, 16, 1024);
                if (m_residencyCircleRadiusBlocks != clamped) {
                    m_residencyCircleRadiusBlocks = clamped;
                    m_residencyVersion++;
                }
            }
        }

        /// <summary>驻留区块数上限（内存保护；超过就拒绝新增区域）。</summary>
        public static int ResidencyMaxChunks {
            get => m_residencyMaxChunks;
            set => m_residencyMaxChunks = Math.Clamp(value, 1, 65536);
        }

        /// <summary>临时驻留区的存活时间（秒），到期由 <see cref="Tick"/> 自动回收。</summary>
        public static double EnsureRegionTtlSeconds {
            get => m_ensureRegionTtlSeconds;
            set => m_ensureRegionTtlSeconds = Math.Clamp(value, 5.0, 86400.0);
        }

        /// <summary>配置版本号：TerrainUpdater 只在版本变化时重建驻留地点。</summary>
        public static int ResidencyVersion => m_residencyVersion;

        /// <summary>最近一次驻留操作的错误（空串 = 无错）。</summary>
        public static string ResidencyLastError => m_residencyLastError;

        /// <summary>加一个驻留区域（世界坐标闭区间，会自动归一化；同名覆盖）。</summary>
        public static bool AddResidencyRegion(string name, int x1, int z1, int x2, int z2) =>
            AddResidencyRegionInternal(name, x1, z1, x2, z2, false, 0.0);

        /// <summary>移除一个驻留区域。</summary>
        public static bool RemoveResidencyRegion(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                m_residencyLastError = "region name is empty";
                return false;
            }
            int removed = Regions.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) {
                m_residencyVersion++;
                m_residencyLastError = "";
                return true;
            }
            m_residencyLastError = $"region '{name}' not found";
            return false;
        }

        /// <summary>清空全部驻留区域。</summary>
        public static void ClearResidencyRegions() {
            if (Regions.Count > 0) {
                Regions.Clear();
                m_residencyVersion++;
            }
            m_residencyLastError = "";
        }

        /// <summary>
        /// 请求把某个矩形区域加载到"可读写"状态（**非阻塞**）：注册临时区域后立刻返回，
        /// 之后用 <see cref="ResidencyStatus"/> 轮询直到该区域的 <c>contentsReady == chunks</c>。
        /// 需要先打开 <see cref="ChunkResidencyMode"/>（独立开关），否则会明确报错而不是静默不做事。
        /// </summary>
        public static string EnsureRegionLoaded(int x1, int z1, int x2, int z2) {
            if (!ChunkResidencyMode) {
                return new JsonObject {
                    ["ok"] = false,
                    ["error"] = "ChunkResidencyMode_disabled",
                    ["hint"] = "先设置 SkylineRuntime.ChunkResidencyMode=true（独立开关，与游览模式的球形视距互不冲突）"
                }.ToJsonString();
            }
            bool ok = AddResidencyRegionInternal(EnsureRegionName, x1, z1, x2, z2, true, Time.RealTime + EnsureRegionTtlSeconds);
            JsonObject result = ResidencyStatusObject();
            result["ok"] = ok;
            if (!ok) {
                result["error"] = m_residencyLastError;
            }
            return result.ToJsonString();
        }

        /// <summary>临时驻留区是否已过期（由 TerrainUpdater 每帧调用，开销极小）。</summary>
        public static void Tick() {
            // [v0.1.0] 家具 LOD 的动态档位检查（内部自带限频，见 SkylineRender.Tick）
            SkylineRender.Tick();
            // [v0.1.0] 超视距 LOD：采集粗网格 + 定时重建网格/落盘（见 SkylineLod.Tick）
            SkylineLod.Tick();
            // [v0.1.13] 32³ 三维窗口账本（默认关；见 SkylineCubeWindow.cs）
            CubeWindowTick();
            if (Regions.Count == 0) {
                return;
            }
            double now = Time.RealTime;
            int removed = Regions.RemoveAll(r => r.Temporary && now >= r.ExpiresAt);
            if (removed > 0) {
                m_residencyVersion++;
            }
        }

        /// <summary>驻留状态（JSON 字符串）：区域列表 + 每个区域的区块统计 + 内存估算。</summary>
        public static string ResidencyStatus() => ResidencyStatusObject().ToJsonString();

        /// <summary>某个区域的内容是否已经全部可读写。</summary>
        public static bool IsRegionContentsReady(string name) {
            ResidencyRegion region = FindRegion(name);
            if (region == null) {
                return false;
            }
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (terrain == null) {
                return false;
            }
            foreach (Point2 coords in EnumerateRegionChunks(region)) {
                TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(coords);
                if (chunk == null || chunk.ThreadState < TerrainChunkState.InvalidLight) {
                    return false;
                }
            }
            return true;
        }

        public static ResidencyRegion FindRegion(string name) {
            if (string.IsNullOrWhiteSpace(name)) {
                return null;
            }
            foreach (ResidencyRegion region in Regions) {
                if (string.Equals(region.Name, name, StringComparison.OrdinalIgnoreCase)) {
                    return region;
                }
            }
            return null;
        }

        /// <summary>把驻留区域铺成一串"覆盖圆"（TerrainUpdater 用这些圆心+半径当 update locations）。</summary>
        public static List<ResidencyCircle> BuildResidencyCircles() {
            List<ResidencyCircle> result = [];
            if (!ChunkResidencyMode || Regions.Count == 0) {
                return result;
            }
            int radius = ResidencyCircleRadiusBlocks;
            int margin = TerrainChunk.Size * 2;     // 外扩 2 个区块，避免"区块跨边界但圆心不在圆内"
            foreach (ResidencyRegion region in Regions) {
                int minX = region.MinX - margin;
                int maxX = region.MaxX + margin;
                int minZ = region.MinZ - margin;
                int maxZ = region.MaxZ + margin;
                for (int centerX = minX; centerX <= maxX + radius; centerX += radius) {
                    for (int centerZ = minZ; centerZ <= maxZ + radius; centerZ += radius) {
                        result.Add(new ResidencyCircle {
                            Center = new Vector2(centerX, centerZ),
                            RadiusBlocks = radius
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>估算一个区域会占多少区块（用于内存保护）。</summary>
        public static int CountRegionChunks(int x1, int z1, int x2, int z2) {
            Normalize(ref x1, ref x2);
            Normalize(ref z1, ref z2);
            int chunksX = (x2 >> TerrainChunk.SizeBits) - (x1 >> TerrainChunk.SizeBits) + 1;
            int chunksZ = (z2 >> TerrainChunk.SizeBits) - (z1 >> TerrainChunk.SizeBits) + 1;
            return chunksX * chunksZ;
        }

        static bool AddResidencyRegionInternal(string name, int x1, int z1, int x2, int z2, bool temporary, double expiresAt) {
            if (string.IsNullOrWhiteSpace(name)) {
                m_residencyLastError = "region name is empty";
                return false;
            }
            Normalize(ref x1, ref x2);
            Normalize(ref z1, ref z2);
            int chunks = CountRegionChunks(x1, z1, x2, z2);
            int existing = FindRegion(name) is ResidencyRegion previous ? CountRegionChunks(previous.MinX, previous.MinZ, previous.MaxX, previous.MaxZ) : 0;
            if (chunks - existing + TotalRegionChunks() > ResidencyMaxChunks) {
                m_residencyLastError =
                    $"residency chunk budget exceeded: wanted {chunks} (existing same-name {existing}), "
                    + $"total would be {chunks - existing + TotalRegionChunks()} > ResidencyMaxChunks {ResidencyMaxChunks} "
                    + $"(each chunk ≈ {TerrainChunk.Height * TerrainChunk.Size * TerrainChunk.Size * 4 / 1048576.0:0.0} MB)";
                return false;
            }
            Regions.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            Regions.Add(new ResidencyRegion {
                Name = name,
                MinX = x1,
                MinZ = z1,
                MaxX = x2,
                MaxZ = z2,
                Temporary = temporary,
                ExpiresAt = expiresAt
            });
            m_residencyVersion++;
            m_residencyLastError = "";
            return true;
        }

        static JsonObject ResidencyStatusObject() {
            JsonObject root = new() {
                ["mode"] = ChunkResidencyMode,
                ["fullDetail"] = ResidencyFullDetail,
                ["circleRadiusBlocks"] = ResidencyCircleRadiusBlocks,
                ["maxChunks"] = ResidencyMaxChunks,
                ["version"] = m_residencyVersion,
                ["lastError"] = m_residencyLastError
            };
            SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (terrain == null) {
                root["ok"] = false;
                root["error"] = "no world loaded";
                return root;
            }
            double chunkMegabytes = TerrainChunk.Height * TerrainChunk.Size * TerrainChunk.Size * 4 / 1048576.0;
            JsonArray regions = [];
            int totalChunks = 0;
            int totalAllocated = 0;
            int totalContents = 0;
            int totalGeometry = 0;
            foreach (ResidencyRegion region in Regions) {
                int chunks = 0;
                int allocated = 0;
                int contentsReady = 0;
                int geometryReady = 0;
                foreach (Point2 coords in EnumerateRegionChunks(region)) {
                    chunks++;
                    TerrainChunk chunk = terrain.Terrain.GetChunkAtCoords(coords);
                    if (chunk == null) {
                        continue;
                    }
                    allocated++;
                    if (chunk.ThreadState >= TerrainChunkState.InvalidLight) {
                        contentsReady++;
                    }
                    if (chunk.State >= TerrainChunkState.Valid) {
                        geometryReady++;
                    }
                }
                totalChunks += chunks;
                totalAllocated += allocated;
                totalContents += contentsReady;
                totalGeometry += geometryReady;
                regions.Add(new JsonObject {
                    ["name"] = region.Name,
                    ["minX"] = region.MinX,
                    ["minZ"] = region.MinZ,
                    ["maxX"] = region.MaxX,
                    ["maxZ"] = region.MaxZ,
                    ["temporary"] = region.Temporary,
                    ["expiresInSeconds"] = region.Temporary ? Math.Max(0.0, region.ExpiresAt - Time.RealTime) : (double?)null,
                    ["chunks"] = chunks,
                    ["allocated"] = allocated,
                    ["contentsReady"] = contentsReady,
                    ["geometryReady"] = geometryReady,
                    ["estimatedMB"] = Math.Round(chunks * chunkMegabytes, 1)
                });
            }
            root["ok"] = true;
            root["regions"] = regions;
            root["totals"] = new JsonObject {
                ["chunks"] = totalChunks,
                ["allocated"] = totalAllocated,
                ["contentsReady"] = totalContents,
                ["geometryReady"] = totalGeometry,
                ["estimatedMB"] = Math.Round(totalChunks * chunkMegabytes, 1),
                ["chunkMB"] = Math.Round(chunkMegabytes, 2)
            };
            return root;
        }

        static IEnumerable<Point2> EnumerateRegionChunks(ResidencyRegion region) {
            int minChunkX = region.MinX >> TerrainChunk.SizeBits;
            int maxChunkX = region.MaxX >> TerrainChunk.SizeBits;
            int minChunkZ = region.MinZ >> TerrainChunk.SizeBits;
            int maxChunkZ = region.MaxZ >> TerrainChunk.SizeBits;
            for (int x = minChunkX; x <= maxChunkX; x++) {
                for (int z = minChunkZ; z <= maxChunkZ; z++) {
                    yield return new Point2(x, z);
                }
            }
        }

        static int TotalRegionChunks() {
            int total = 0;
            foreach (ResidencyRegion region in Regions) {
                total += CountRegionChunks(region.MinX, region.MinZ, region.MaxX, region.MaxZ);
            }
            return total;
        }

        static void Normalize(ref int a, ref int b) {
            if (a > b) {
                (a, b) = (b, a);
            }
        }
    }
}
