using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.106：**固定光源（点光源）列表** —— 里程碑 4 里点名两遍的"固定光源照射导致的
    /// 亮度改变斑块"的**逐片元**版本（v0.1.100 只在 LOD 上做了"取上方空气格光的最大值"这一档）。
    ///
    /// Dawnlight 一侧的出处：`compute/PointLights.csh`（聚簇点光源列表）+ `lib/HandLighting.glsl`
    /// （`DistanceAttenuationHL(dist, radius) = (exp(-dist/(1.5r)) − 0.513417) / (1 − 0.513417)`）。
    /// 我们没有它那套 GPU 聚簇（`clusterX/Y/Z` + `totalLights` 的 SSBO），改走**CPU 侧 K 近邻**：
    ///
    ///   1. **登记**：引擎本来就会为每个区块扫一遍"会发光的方块"（`TerrainUpdater.GenerateChunkLightSources`），
    ///      我们**复用同一个扫描**（不额外遍历），把结果按区块记进本注册表；
    ///   2. **收集**：每帧从**相机附近已加载区块**里取最近的 K 个（默认 8 个、半径 160 m）；
    ///   3. **片元**：不透明变体里对这些灯做一次**距离衰减**累加（曲线逐字取 Dawnlight 的
    ///      `DistanceAttenuationHL`）。**如实记**：本分支的地形片元**没有法线**（顶点格式是
    ///      位置/颜色/贴图坐标），所以做不到 Dawnlight 的 `N·L` 那一项 —— 只做距离衰减 + 颜色。
    ///
    /// 硬口径：全部关闭时**不介入任何 uniform**（逐位回上一版）；不抛异常；状态可从桥上读出。
    /// </summary>
    public static class SkylinePointLights {
        /// <summary>总开关。**默认关**（与 `CubeShellPixelTiers` 同一口径）：实现与 A/B 证据都有了，
        /// 但**还没跑过回归门禁/巡检/性能三条线**，所以不擅自翻默认 —— 打开是**一条调用**：
        /// `skyline.PointLights(true)`（或 `type:Game.SkylinePointLights.Enabled = true`）。</summary>
        public static bool Enabled { get; set; }

        /// <summary>片元里的整体强度（0 = 关闭效果；默认 0.6）。</summary>
        public static float Strength { get; set; } = 0.6f;

        /// <summary>收集半径（米，默认 160）。</summary>
        public static float Radius { get; set; } = 160f;

        /// <summary>每帧最多送进着色器的灯数（默认 8；着色器里的数组尺寸必须一致）。</summary>
        public static int MaxLights { get; set; } = 8;

        /// <summary>灯的半径 = 光强 × 这个系数（SC 的光每格衰减 1，光强 15 ≈ 15 格）。</summary>
        public static float RadiusPerLight { get; set; } = 1.0f;

        /// <summary>[v0.1.106] **彩色光源雾**强度（0 = 关）。口径来自 Iris 光影包
        /// **Complementary Reimagined** 的 `lib/atmospherics/fog/coloredLightFog.glsl`：
        /// 那里沿视线在**彩色光照体**（`GetLightVolume`，由 `lib/voxelization/` 写入）里步进积分；
        /// 我们没有体素化那套设施，改用**同一份 K 近邻灯列表**给雾上色（适配，如实标注）。</summary>
        public static float FogTintStrength { get; set; } = 0.6f;

        public const int ShaderMaxLights = 8;

        public sealed class Emitter {
            // ⚠️ **不要用 short**：世界坐标可以到 ±4 万（本机玩家在 x≈41151），short 会溢出成负数，
            //    于是"就近收集"永远找不到灯（本轮实测：41155 存成 −24381，离相机 65 km）。
            public int X, Y, Z;
            public byte Amount;
            public int Contents;
        }

        static readonly Dictionary<(int Cx, int Cz), List<Emitter>> m_byChunk = [];
        static readonly List<(float DistSq, Emitter Emitter)> m_collected = [];
        static readonly Vector4[] m_posRadius = new Vector4[ShaderMaxLights];
        static readonly Vector3[] m_colors = new Vector3[ShaderMaxLights];
        /// <summary>[v0.1.106] 收集到的灯的**包围球**（xyz=中心, w=半径）。着色器先用它做一次
        /// "这个片元到底在不在任何灯的影响范围内"的判定 —— 不在就整段跳过（实测：不加这一步的
        /// 全屏循环代价是 **−8.8%**，见 notes/201）。</summary>
        static Vector4 m_bounds;
        static int m_count;
        static long m_recordedChunks, m_collectFrames;
        static string m_lastError = "";

        public static int RegisteredChunks => m_byChunk.Count;
        public static long RegisteredLights { get; private set; }
        public static int LastCount => m_count;

        /// <summary>区块扫描完自己的发光方块时调用（由 `TerrainUpdater.GenerateChunkLightSources` 挂钩）。</summary>
        /// <summary>
        /// **[必须先调]** 一个区块开始重新扫描时先清空它上一次的记录。
        /// 为什么：`GenerateChunkLightSources` 会在**每一次光照重算**时被再次调用（太阳变化、方块改动、
        /// 区块重载……），只追加不清空的话注册表会**重复累积**（首次实测：259 个区块记出 **480,196** 条）。
        /// 调用点：`TerrainUpdater.GenerateChunkLightSources` 的**方法开头**。
        /// </summary>
        public static void BeginChunk(TerrainChunk chunk) {
            try {
                if (chunk == null) {
                    return;
                }
                var key = (chunk.Coords.X, chunk.Coords.Y);
                if (!m_byChunk.TryGetValue(key, out List<Emitter> list)) {
                    list = [];
                    m_byChunk[key] = list;
                    m_recordedChunks++;
                }
                else {
                    RegisteredLights -= list.Count;
                    list.Clear();
                }
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
        }

        /// <summary>区块扫描完一列发光方块时调用（由 `TerrainUpdater.GenerateChunkLightSources` 挂钩）。</summary>
        public static void NoteChunkSources(TerrainChunk chunk, TerrainUpdater.LightSource[] sources,
                                            int from, int to) {
            try {
                if (chunk == null || sources == null || to <= from) {
                    return;
                }
                var key = (chunk.Coords.X, chunk.Coords.Y);
                if (!m_byChunk.TryGetValue(key, out List<Emitter> list)) {
                    list = [];
                    m_byChunk[key] = list;
                    m_recordedChunks++;
                }
                for (int i = from; i < to; i++) {
                    TerrainUpdater.LightSource s = sources[i];
                    list.Add(new Emitter {
                        X = s.X, Y = s.Y, Z = s.Z,
                        Amount = (byte)Math.Clamp(s.Light, 0, 15),
                        // 顺手把方块 id 记下来（读完这一格就在手上的信息），否则 Describe 里分不清
                        // "这些光是什么方块发的"（本轮就卡在这一点上）。
                        Contents = Terrain.ExtractContents(chunk.GetCellValueFast(
                            TerrainChunk.CalculateCellIndex(s.X & 15, s.Y, s.Z & 15)))
                    });
                    RegisteredLights++;
                }
            }
            catch (Exception e) {
                m_lastError = e.Message;
            }
        }

        /// <summary>区块卸载时丢掉它的记录（按坐标，避免长期堆积）。</summary>
        public static void DropChunk(Point2 coords) {
            if (m_byChunk.Remove((coords.X, coords.Y))) {
                return;
            }
        }

        /// <summary>每帧收集最近的 K 个灯（由 `SkylineRuntime.Tick()` 调用）。</summary>
        public static void Tick(Terrain terrain, Vector3 viewPosition) {
            m_count = 0;
            if (!Enabled || terrain == null || MaxLights <= 0) {
                return;
            }
            try {
                m_collected.Clear();
                float r2 = Radius * Radius;
                int cx0 = (int)MathF.Floor((viewPosition.X - Radius) / TerrainChunk.Size);
                int cx1 = (int)MathF.Floor((viewPosition.X + Radius) / TerrainChunk.Size);
                int cz0 = (int)MathF.Floor((viewPosition.Z - Radius) / TerrainChunk.Size);
                int cz1 = (int)MathF.Floor((viewPosition.Z + Radius) / TerrainChunk.Size);
                for (int cx = cx0; cx <= cx1; cx++) {
                    for (int cz = cz0; cz <= cz1; cz++) {
                        if (terrain.GetChunkAtCoords(cx, cz) == null
                            || !m_byChunk.TryGetValue((cx, cz), out List<Emitter> list)) {
                            continue;
                        }
                        for (int i = 0; i < list.Count; i++) {
                            Emitter l = list[i];
                            float dx = l.X + 0.5f - viewPosition.X;
                            float dy = l.Y + 0.5f - viewPosition.Y;
                            float dz = l.Z + 0.5f - viewPosition.Z;
                            float d2 = dx * dx + dy * dy + dz * dz;
                            if (d2 > r2) {
                                continue;
                            }
                            m_collected.Add((d2, l));
                        }
                    }
                }
                m_collected.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
                int n = Math.Min(Math.Min(MaxLights, ShaderMaxLights), m_collected.Count);
                // 包围球：中心 = 这些灯的均值，半径 = 中心到最远"灯位置 + 灯半径"的距离
                Vector3 sum = Vector3.Zero;
                for (int i = 0; i < n; i++) {
                    Emitter l = m_collected[i].Emitter;
                    sum += new Vector3(l.X + 0.5f, l.Y + 0.5f, l.Z + 0.5f);
                }
                Vector3 center = n > 0 ? sum / n : Vector3.Zero;
                float boundR = 0f;
                for (int i = 0; i < n; i++) {
                    Emitter l = m_collected[i].Emitter;
                    float lr = MathF.Max(l.Amount * RadiusPerLight, 1f);
                    boundR = MathF.Max(boundR, Vector3.Distance(center, new Vector3(l.X + 0.5f, l.Y + 0.5f, l.Z + 0.5f)) + lr);
                }
                m_bounds = new Vector4(center.X, center.Y, center.Z, boundR);
                for (int i = 0; i < n; i++) {
                    Emitter l = m_collected[i].Emitter;
                    float radius = MathF.Max(l.Amount * RadiusPerLight, 1f);
                    m_posRadius[i] = new Vector4(l.X + 0.5f, l.Y + 0.5f, l.Z + 0.5f, radius);
                    m_colors[i] = ColorFor(terrain, l);
                }
                m_count = n;
                m_collectFrames++;
                m_lastError = "";
            }
            catch (Exception e) {
                m_count = 0;
                m_lastError = e.Message;
            }
        }

        /// <summary>
        /// **如实记**：Survivalcraft 的光**没有颜色**（光照网格是标量）。这里按"发光方块类型"给我们自己的
        /// 一组**近似色**（火把/灯笼偏暖、其余中性），不是从数据里读出来的 —— 颜色映射是下一轮的事。
        /// </summary>
        static Vector3 ColorFor(Terrain terrain, Emitter l) {
            if (l.Contents == 0) {
                try {
                    l.Contents = terrain.GetCellContentsFast(l.X, l.Y, l.Z);
                }
                catch (Exception) {
                    l.Contents = 0;
                }
            }
            string name = BlocksManager.Blocks[l.Contents & 1023]?.GetType().Name ?? "";
            if (name.Contains("Torch")) {
                return new Vector3(1.00f, 0.78f, 0.45f);      // 火把：暖黄
            }
            if (name.Contains("Magma") || name.Contains("Lava")) {
                return new Vector3(1.00f, 0.55f, 0.25f);      // 岩浆：橙红（本轮 A/B 就靠它看得出来）
            }
            if (name.Contains("Lamp") || name.Contains("Lantern") || name.Contains("Light")) {
                return new Vector3(1.00f, 0.92f, 0.70f);      // 灯/灯笼：暖白
            }
            if (name.Contains("Fire") || name.Contains("Furnace") || name.Contains("Torch")) {
                return new Vector3(1.00f, 0.62f, 0.32f);      // 火：橙
            }
            return new Vector3(0.92f, 0.94f, 1.00f);          // 其余：略偏冷的中性
        }

        /// <summary>把收集到的灯绑进 shader（由 `BindShadowFogParams` / `ResolveOpaqueShader` 调用）。</summary>
        public static void Bind(Shader shader) {
            shader.GetParameter("u_plCount", true).SetValue((float)m_count);
            shader.GetParameter("u_plStrength", true).SetValue(Enabled ? MathF.Max(Strength, 0f) : 0f);
            float[] flat = new float[ShaderMaxLights * 4];
            float[] cols = new float[ShaderMaxLights * 3];
            for (int i = 0; i < ShaderMaxLights; i++) {
                flat[i * 4] = m_posRadius[i].X;
                flat[i * 4 + 1] = m_posRadius[i].Y;
                flat[i * 4 + 2] = m_posRadius[i].Z;
                flat[i * 4 + 3] = m_posRadius[i].W;
                cols[i * 3] = m_colors[i].X;
                cols[i * 3 + 1] = m_colors[i].Y;
                cols[i * 3 + 2] = m_colors[i].Z;
            }
            shader.GetParameter("u_plPosRadius", true).SetValue(flat);
            shader.GetParameter("u_plColor", true).SetValue(cols);
            shader.GetParameter("u_plBounds", true).SetValue(m_bounds);
            shader.GetParameter("u_plTint", true)
                .SetValue(Enabled && m_count > 0 ? MathF.Max(FogTintStrength, 0f) : 0f);
        }

        public static string Describe() {
            return DescribeObject().ToJsonString();
        }

        /// <summary>[v0.1.106] 诊断：某区块注册在案的光源（数量 + 前 8 个的坐标/光强/方块）。</summary>
        public static string ChunkProbe(int cx, int cz) {
            JsonArray arr = [];
            int total = 0;
            if (m_byChunk.TryGetValue((cx, cz), out List<Emitter> list)) {
                total = list.Count;
                for (int i = 0; i < Math.Min(list.Count, 8); i++) {
                    Emitter e = list[i];
                    string bn = e.Contents > 0 && e.Contents < BlocksManager.Blocks.Length
                        ? BlocksManager.Blocks[e.Contents].GetType().Name : "?";
                    arr.Add(new JsonObject {
                        ["pos"] = new JsonArray(e.X, e.Y, e.Z), ["amount"] = e.Amount, ["block"] = bn
                    });
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["chunk"] = new JsonArray(cx, cz),
                ["registered"] = total,
                ["originBlockX"] = cx * TerrainChunk.Size,
                ["originBlockZ"] = cz * TerrainChunk.Size,
                ["samples"] = arr
            }.ToJsonString();
        }

        static JsonObject DescribeObject() {
            // 诊断：把"注册到的光强分布"与"离相机最近的一盏"报出来 —— 否则"收不到灯"分不清
            // 是"附近真的没有发光方块"还是"注册表坏了"（本轮就踩到过这个歧义）。
            JsonObject amounts = new();
            JsonObject blocks = new();
            float bestD = float.MaxValue;
            Emitter best = null;
            Vector3 view = Vector3.Zero;
            try {
                SubsystemPlayers players = GameManager.Project?.FindSubsystem<SubsystemPlayers>(true);
                ComponentPlayer cp = players != null && players.ComponentPlayers.Count > 0
                    ? players.ComponentPlayers[0] : null;
                Camera cam = cp?.GameWidget?.ActiveCamera;
                if (cam != null) {
                    view = cam.ViewPosition;
                }
            }
            catch (Exception) {
            }
            foreach (List<Emitter> list in m_byChunk.Values) {
                for (int i = 0; i < list.Count; i++) {
                    Emitter e = list[i];
                    string k = e.Amount.ToString();
                    amounts[k] = (amounts[k]?.GetValue<int>() ?? 0) + 1;
                    string bn = e.Contents > 0 && e.Contents < BlocksManager.Blocks.Length
                        ? BlocksManager.Blocks[e.Contents].GetType().Name : "?";
                    blocks[bn] = (blocks[bn]?.GetValue<int>() ?? 0) + 1;
                    float dx = e.X + 0.5f - view.X, dy = e.Y + 0.5f - view.Y, dz = e.Z + 0.5f - view.Z;
                    float d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < bestD) {
                        bestD = d2;
                        best = e;
                    }
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["strength"] = (double)Strength,
                ["radiusMetres"] = (double)Radius,
                ["maxLights"] = MaxLights,
                ["shaderMaxLights"] = ShaderMaxLights,
                ["radiusPerLight"] = (double)RadiusPerLight,
                ["registeredChunks"] = RegisteredChunks,
                ["registeredLights"] = RegisteredLights,
                ["recordedChunks"] = m_recordedChunks,
                ["collectFrames"] = m_collectFrames,
                ["lastCount"] = m_count,
                ["lastLights"] = new JsonArray([.. System.Linq.Enumerable.Select(m_collected.GetRange(0, Math.Min(m_count, m_collected.Count)), e => (JsonNode)new JsonObject {
                    ["pos"] = new JsonArray(e.Emitter.X, e.Emitter.Y, e.Emitter.Z),
                    ["light"] = e.Emitter.Amount,
                    ["dist"] = Math.Round(MathF.Sqrt(e.DistSq), 2)
                })]),
                ["lastError"] = m_lastError,
                ["amountHistogram"] = amounts,
                ["blockHistogram"] = blocks,
                ["nearestLight"] = best == null ? null : new JsonObject {
                    ["pos"] = new JsonArray(best.X, best.Y, best.Z),
                    ["amount"] = best.Amount,
                    ["distMetres"] = Math.Round(MathF.Sqrt(bestD), 1)
                },
                ["note"] = "灯列表来自引擎自己的区块发光扫描（TerrainUpdater.GenerateChunkLightSources）；"
                    + "衰减曲线取 Dawnlight 的 DistanceAttenuationHL；地形片元没有法线 ⇒ 无 N·L 项；"
                    + "光源颜色是**按方块类型的近似**（SC 的光本身没有颜色）"
            };
        }
    }

    /// <summary>桥：`skyline.PointLights*`。</summary>
    public static partial class SkylineRuntime {
        public static string PointLights(bool enabled) {
            SkylinePointLights.Enabled = enabled;
            return SkylinePointLights.Describe();
        }

        public static string PointLightsInfo() => SkylinePointLights.Describe();

        /// <summary>[v0.1.106] 诊断：某个区块里注册了几个发光方块，以及其中前几个的坐标/光强。</summary>
        public static string PointLightsChunkProbe(int cx, int cz) {
            return SkylinePointLights.ChunkProbe(cx, cz);
        }
    }
}
