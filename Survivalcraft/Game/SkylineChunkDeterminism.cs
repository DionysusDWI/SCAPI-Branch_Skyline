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
