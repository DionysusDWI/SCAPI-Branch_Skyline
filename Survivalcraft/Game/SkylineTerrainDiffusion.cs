using Engine;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// [v0.1.123] **里程碑 5 的测试接口**：把"吃外部高度场"的生成器**临时**装到当前世界上。
    ///
    /// 口径（与 goal 5 的"暂不实装"一致）：
    ///   * **不碰世界设置**、不写存档、不改 UI —— 只是把 `SubsystemTerrain.TerrainContentsGenerator`
    ///     换成 `TerrainContentsGeneratorHeightmap`（`notes/176 §2` 指出的那个干净接缝）；
    ///   * **已加载的区块保持原样**，只影响**之后新生成**的区块 ⇒ 测试要跑到"还没去过的地方"；
    ///   * `Uninstall` 把原来的生成器**原样装回去**（保存的是引用，不重建）；
    ///   * 生成器本身读一张灰度 PNG 当"模型输出"（真值），`Probe(x,z)` 给出
    ///     "高度图给的高度" 与 "地形里真的长出来的高度" 的对照 —— 这就是"管线能不能吃外部高度场"的证据。
    /// </summary>
    public static partial class SkylineRuntime {
        static ITerrainContentsGenerator m_tdSavedGenerator;
        static TerrainContentsGeneratorHeightmap m_tdGenerator;
        static string m_tdLastError = "";
        static long m_tdInstalls;

        public static string TerrainDiffusionDescribe() => new JsonObject {
            ["installed"] = m_tdGenerator != null,
            ["savedGenerator"] = m_tdSavedGenerator?.GetType().Name,
            ["currentGenerator"] = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainContentsGenerator?.GetType().Name,
            ["heightmap"] = m_tdGenerator?.m_heightmap == null ? null
                : new JsonArray(m_tdGenerator.m_heightmap.Width, m_tdGenerator.m_heightmap.Height),
            ["spanMetres"] = m_tdGenerator == null ? null : (double)m_tdGenerator.SpanMetres,
            ["amplitudeMetres"] = m_tdGenerator == null ? null : (double)m_tdGenerator.AmplitudeMetres,
            ["originXZ"] = m_tdGenerator == null ? null
                : new JsonArray(Math.Round(m_tdGenerator.OriginXZ.X, 1), Math.Round(m_tdGenerator.OriginXZ.Y, 1)),
            ["installs"] = m_tdInstalls,
            ["lastError"] = m_tdLastError,
            ["note"] = "把吃高度图的生成器临时装上：只影响**之后新生成**的区块；Uninstall 原样还原"
        }.ToJsonString();

        /// <summary>
        /// `path` 相对 `app:/`（例如 `Heightmaps/td-1024.png`）。
        /// 采样原点默认**以玩家为中心**（`originX/Z` 省略时用玩家 XZ 减半跨度）⇒ 测试只要平移几百米
        /// 就能走到"高度图覆盖范围内、但还没生成过"的区块，不必飞几十公里。
        /// </summary>
        public static string TerrainDiffusionInstall(string path, float spanMetres = 4000f,
                                                     float amplitudeMetres = 96f,
                                                     float originX = float.NaN,
                                                     float originZ = float.NaN) {
            try {
                SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                if (terrain == null) {
                    return Fail("no SubsystemTerrain");
                }
                if (string.IsNullOrEmpty(path)) {
                    return Fail("path is required (relative to app:/)");
                }
                Vector3 anchor = PlayerAnchor(terrain);
                float ox = float.IsNaN(originX) ? anchor.X - spanMetres * 0.5f : originX;
                float oz = float.IsNaN(originZ) ? anchor.Z - spanMetres * 0.5f : originZ;
                m_tdSavedGenerator = terrain.TerrainContentsGenerator;
                m_tdGenerator = new TerrainContentsGeneratorHeightmap(terrain, path, spanMetres, amplitudeMetres);
                m_tdGenerator.OriginXZ = new Vector2(ox, oz);
                terrain.TerrainContentsGenerator = m_tdGenerator;
                m_tdInstalls++;
                m_tdLastError = "";
                JsonObject ok = JsonObject.Parse(TerrainDiffusionDescribe()).AsObject();
                ok["ok"] = true;
                ok["anchorXZ"] = new JsonArray(Math.Round(anchor.X, 1), Math.Round(anchor.Z, 1));
                return ok.ToJsonString();
            }
            catch (Exception e) {
                m_tdLastError = $"{e.GetType().Name}: {e.Message}";
                Log.Warning($"SkylineTerrainDiffusion.Install: {m_tdLastError}");
                return Fail(m_tdLastError);
            }
        }

        static Vector3 PlayerAnchor(SubsystemTerrain terrain) {
            SubsystemPlayers players = terrain.Project?.FindSubsystem<SubsystemPlayers>(true);
            if (players != null) {
                foreach (ComponentPlayer componentPlayer in players.ComponentPlayers) {
                    if (componentPlayer?.ComponentBody != null) {
                        return componentPlayer.ComponentBody.Position;
                    }
                }
            }
            return Vector3.Zero;
        }

        public static string TerrainDiffusionUninstall() {
            try {
                SubsystemTerrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                if (terrain == null) {
                    return Fail("no SubsystemTerrain");
                }
                if (m_tdSavedGenerator != null) {
                    terrain.TerrainContentsGenerator = m_tdSavedGenerator;
                }
                m_tdGenerator = null;
                m_tdSavedGenerator = null;
                return TerrainDiffusionDescribe();
            }
            catch (Exception e) {
                m_tdLastError = $"{e.GetType().Name}: {e.Message}";
                return Fail(m_tdLastError);
            }
        }

        /// <summary>对照探针：高度图给的高度、生成器给的高度、**地形里真的长出来的顶面高度**（3 个都要一致）。</summary>
        public static string TerrainDiffusionProbe(float x, float z) {
            JsonObject o = new() { ["ok"] = true, ["x"] = (double)x, ["z"] = (double)z };
            try {
                TerrainContentsGeneratorHeightmap gen = m_tdGenerator;
                o["installed"] = gen != null;
                if (gen != null) {
                    o["heightmapMetres"] = Math.Round(gen.SampleHeightMetres(x, z), 3);
                    o["calculateHeight"] = Math.Round(gen.CalculateHeight(x, z), 3);
                }
                SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                Terrain terrain = subsystemTerrain?.Terrain;
                int xi = Terrain.ToCell(x), zi = Terrain.ToCell(z);
                int top = int.MinValue;
                int topContents = 0;
                for (int y = TerrainChunk.HeightMinusOne; y >= TerrainChunk.MinHeight; y--) {
                    int v = terrain.GetCellValue(xi, y, zi);
                    int contents = Terrain.ExtractContents(v);
                    if (contents != 0 && !(BlocksManager.Blocks[contents] is FluidBlock)) {
                        top = y;
                        topContents = contents;
                        break;
                    }
                }
                o["terrainTopY"] = top == int.MinValue ? null : top;
                o["terrainTopBlock"] = topContents == 0 ? null : BlocksManager.Blocks[topContents].GetType().Name;
                TerrainChunk chunk = terrain.GetChunkAtCell(xi, zi);
                o["chunkAllocated"] = chunk != null;
                if (chunk != null) {
                    // 关键证据：**只有被改过的区块才落盘**（TerrainSerializer.SaveChunk 判 `ModificationCounter > 0`），
                    // 纯生成区块在卸载后会**按当前生成器重新生成** —— 这条决定了"换生成器"是"往回追溯"的。
                    o["chunkState"] = chunk.State.ToString();
                    o["chunkModifications"] = chunk.ModificationCounter;
                }
                if (gen != null && top != int.MinValue) {
                    o["deltaMetres"] = Math.Round(top - gen.SampleHeightMetres(x, z), 3);
                }
                // 与"当前生成器"的对照：卸载后重生成时，顶面应该回到**当前**生成器给的高度
                // （纯生成区块不落盘 ⇒ 换生成器是"往回追溯"的，见 notes/221）。
                ITerrainContentsGenerator current = subsystemTerrain?.TerrainContentsGenerator;
                o["currentGenerator"] = current?.GetType().Name;
                if (current != null) {
                    float currentHeight = current.CalculateHeight(x, z);
                    o["currentGeneratorHeight"] = Math.Round(currentHeight, 3);
                    o["deltaVsCurrentGenerator"] = top == int.MinValue
                        ? null : Math.Round(top - currentHeight, 3);
                }
            }
            catch (Exception e) {
                o["ok"] = false;
                o["err"] = $"{e.GetType().Name}: {e.Message}";
            }
            return o.ToJsonString();
        }

        static string Fail(string message) =>
            new JsonObject { ["ok"] = false, ["err"] = message }.ToJsonString();
    }
}
