using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// [v0.1.138 · 里程碑 5.2 第二段前置] **区块生成的确定性探针**。
    ///
    /// 为什么在并行化之前必须先有它（审计口径："先正确性、后性能"）：C2ME 式并行只允许
    /// "同样的输入 ⇒ 同样的输出"。本引擎的生成是按区块坐标播种的（`m_seed + coords`），
    /// 但生成器里存在**跨区块读写**（洞穴/矿脉/树跨边界）与若干静态表（刷子列表）。
    /// 因此任何并行调度都必须先证明：**同一区块反复生成，内容逐格一致**。
    ///
    /// 两个只读/受控探针：
    ///   * `ChunkContentHash(cx,cz)` —— FNV-1a 64 位哈希该区块全部格子的 value（含光照位），
    ///     并附非空格数/最高非空 y，作为"内容指纹"；
    ///   * `ChunkForceRegenerate(cx,cz)` —— **仅当 `ModificationCounter == 0`**（纯生成、无玩家改动）
    ///     才 `FreeChunk`，让更新器按同一坐标重新生成；否则拒绝（保护玩家数据）。
    /// </summary>
    public static class SkylineChunkDeterminism {
        static Terrain Terrain => GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;

        public static string ChunkHash(int cx, int cz) {
            Terrain terrain = Terrain;
            TerrainChunk chunk = terrain?.GetChunkAtCoords(cx, cz);
            if (chunk == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "chunk not allocated" }.ToJsonString();
            }
            TerrainChunkState state = chunk.ThreadState;
            if (state < TerrainChunkState.Valid) {
                return new JsonObject {
                    ["ok"] = false, ["err"] = "chunk not valid", ["state"] = state.ToString()
                }.ToJsonString();
            }
            ulong hash = 14695981039346656037UL;          // FNV-1a 64 offset basis
            long nonAir = 0;
            int topNonAir = TerrainChunk.MinHeight - 1;
            for (int x = 0; x < TerrainChunk.Size; x++) {
                for (int z = 0; z < TerrainChunk.Size; z++) {
                    for (int y = TerrainChunk.MinHeight; y <= TerrainChunk.HeightMinusOne; y++) {
                        int value = chunk.GetCellValueFast(x, y, z);
                        hash ^= (byte)(value & 0xFF);
                        hash *= 1099511628211UL;
                        hash ^= (byte)((value >> 8) & 0xFF);
                        hash *= 1099511628211UL;
                        if (Terrain.ExtractContents(value) != 0) {
                            nonAir++;
                            if (y > topNonAir) {
                                topNonAir = y;
                            }
                        }
                    }
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["chunk"] = new JsonArray(cx, cz),
                ["hash"] = hash.ToString("x16"),
                ["nonAirCells"] = nonAir,
                ["topNonAirY"] = topNonAir,
                ["modificationCounter"] = chunk.ModificationCounter,
                ["state"] = state.ToString(),
            }.ToJsonString();
        }

        /// <summary>
        /// [v0.1.139] **高度图指纹**（只读）：只哈希 `Top/Bottom/SunlightHeight` 三张图。
        ///
        /// 为什么要单独一条：`GenerateChunkSunLightAndHeight` 既写光照位、也写这三张高度图，
        /// 而**光照位会被后续邻居的光照传播改写**（`TerrainUpdater.PropagateLightSources` 会写邻居区块，
        /// `TerrainUpdater.cs:1078-1108 → :1125`）⇒ 跨"卸载-重生成"轮次比对含光照位的内容哈希会漂移
        /// （v0.1.139 实测：同一模式的两趟之间也会差 1 个区块）。高度图**只由本 pass 写**，
        /// 所以它是"这个 pass 输出一致"的稳定判据，用于 `skyline-v0139-parallel-sunlight.py`。
        /// </summary>
        public static string ChunkHeightHash(int cx, int cz) => ChunkHeightHashCore(cx, cz, true);

        /// <summary>
        /// [v0.1.153] **地形形状指纹**（只哈希 `Top/Bottom`，**不含 `SunlightHeight`**）。
        ///
        /// 为什么要单独一条：`SunlightHeight` 由 `GenerateChunkSunLightAndHeight` 按**当时的日照值**
        /// （`m_subsystemSky.SkyLightValue`）推出 ⇒ **会随时间/天气变化**。
        /// 所以跨轮比对"邻居有没有被污染"时，必须用**不含日照高度**的形状指纹，
        /// 否则白天/黄昏切换会让判据假 FAIL（v0.1.153 实测：4 个邻块里恰好 1 个）。
        /// </summary>
        public static string ChunkShapeHash(int cx, int cz) => ChunkHeightHashCore(cx, cz, false);

        static string ChunkHeightHashCore(int cx, int cz, bool includeSunlight) {
            Terrain terrain = Terrain;
            TerrainChunk chunk = terrain?.GetChunkAtCoords(cx, cz);
            if (chunk == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "chunk not allocated" }.ToJsonString();
            }
            TerrainChunkState state = chunk.ThreadState;
            if (state < TerrainChunkState.Valid) {
                return new JsonObject {
                    ["ok"] = false, ["err"] = "chunk not valid", ["state"] = state.ToString()
                }.ToJsonString();
            }
            ulong hash = 14695981039346656037UL;
            int minTop = int.MaxValue, maxTop = int.MinValue;
            int minBottom = int.MaxValue, maxBottom = int.MinValue;
            long sumSunlight = 0;
            for (int x = 0; x < TerrainChunk.Size; x++) {
                for (int z = 0; z < TerrainChunk.Size; z++) {
                    int top = chunk.GetTopHeightFast(x, z);
                    int bottom = chunk.GetBottomHeightFast(x, z);
                    int sun = chunk.GetSunlightHeightFast(x, z);
                    hash = Mix(hash, top);
                    hash = Mix(hash, bottom);
                    if (includeSunlight) {
                        hash = Mix(hash, sun);
                    }
                    minTop = Math.Min(minTop, top);
                    maxTop = Math.Max(maxTop, top);
                    minBottom = Math.Min(minBottom, bottom);
                    maxBottom = Math.Max(maxBottom, bottom);
                    sumSunlight += sun;
                }
            }
            return new JsonObject {
                ["ok"] = true,
                ["chunk"] = new JsonArray(cx, cz),
                ["heightHash"] = hash.ToString("x16"),
                ["includesSunlightHeight"] = includeSunlight,
                ["minTopHeight"] = minTop == int.MaxValue ? 0 : minTop,
                ["maxTopHeight"] = maxTop == int.MinValue ? 0 : maxTop,
                ["minBottomHeight"] = minBottom == int.MaxValue ? 0 : minBottom,
                ["maxBottomHeight"] = maxBottom == int.MinValue ? 0 : maxBottom,
                ["sumSunlightHeight"] = sumSunlight,
                ["modificationCounter"] = chunk.ModificationCounter,
                ["state"] = state.ToString(),
            }.ToJsonString();
        }

        static ulong Mix(ulong hash, int value) {
            hash ^= (byte)(value & 0xFF);
            hash *= 1099511628211UL;
            hash ^= (byte)((value >> 8) & 0xFF);
            hash *= 1099511628211UL;
            hash ^= (byte)((value >> 16) & 0xFF);
            hash *= 1099511628211UL;
            hash ^= (byte)((value >> 24) & 0xFF);
            hash *= 1099511628211UL;
            return hash;
        }

        /// <summary>
        /// [v0.1.142 · 5.2 第二段] **每 worker 一份生成器的构造成本**测量（只 new 对象；不写世界、不生成区块）。
        ///
        /// 为什么需要它：`notes/247 §6` 把"contents 并行"的前置列成两条路——①证明**同实例可并发**；
        /// ②**每 worker 一份生成器**。第②条的可行性取决于"构造一个生成器要多久"：若 ctor 与
        /// 每区块生成时耗同量级，则不可行；若远小于它，则"按 worker 复用实例"就是廉价方案。
        /// 本探针把这个数**量出来**（不断言、不改默认）。
        /// </summary>
        public static string GeneratorCtorProbe(int count) {
            count = Math.Clamp(count, 1, 64);
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "no subsystemTerrain" }.ToJsonString();
            }
            var samples = new List<double>(count);
            var keep = new List<object>(count);        // 防止被优化掉（也让"实例真被建出来"可核对）
            for (int i = 0; i < count; i++) {
                double t0 = Time.RealTime;
                var generator = new TerrainContentsGenerator24(subsystemTerrain);
                double dt = (Time.RealTime - t0) * 1000.0;
                keep.Add(generator);
                if (generator == null) {
                    return new JsonObject { ["ok"] = false, ["err"] = "ctor returned null" }.ToJsonString();
                }
                samples.Add(Math.Round(dt, 3));
            }
            double total = 0, worst = 0;
            foreach (double s in samples) {
                total += s;
                worst = Math.Max(worst, s);
            }
            var array = new JsonArray();
            foreach (double s in samples) {
                array.Add(s);
            }
            return new JsonObject {
                ["ok"] = true,
                ["count"] = samples.Count,
                ["perCtorMs"] = array,
                ["meanMs"] = Math.Round(total / samples.Count, 3),
                ["worstMs"] = Math.Round(worst, 3),
                ["note"] = "只 new TerrainContentsGenerator24（生成器的静态刷子表是共享的，实例本身只填 4 张步骤表）"
                           + "；请与 `skyline.TerrainUpdateStats()` 的 contentsMs/contentsCount 比"
                           + "（那才是每区块的生成时耗）。本探针不生成任何区块、不写世界"
            }.ToJsonString();
        }

        public static string ChunkForceRegenerate(int cx, int cz) {
            Terrain terrain = Terrain;
            TerrainChunk chunk = terrain?.GetChunkAtCoords(cx, cz);
            if (chunk == null) {
                return new JsonObject { ["ok"] = false, ["err"] = "chunk not allocated" }.ToJsonString();
            }
            if (chunk.ModificationCounter > 0) {
                return new JsonObject {
                    ["ok"] = false,
                    ["err"] = "refused: chunk has player modifications (ModificationCounter>0)",
                    ["modificationCounter"] = chunk.ModificationCounter
                }.ToJsonString();
            }
            try {
                terrain.FreeChunk(chunk);
            }
            catch (Exception e) {
                return new JsonObject { ["ok"] = false, ["err"] = "FreeChunk: " + e.Message }.ToJsonString();
            }
            return new JsonObject {
                ["ok"] = true,
                ["chunk"] = new JsonArray(cx, cz),
                ["note"] = "已释放；更新器会按同一坐标重新生成（探针只允许对 ModificationCounter==0 的区块做）"
            }.ToJsonString();
        }
    }
}
