using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.11：**寻址一致性自检**。
    ///
    /// 由来（2026-09-27 的 v0.1.8 修复）：v0.1.4 把区块存储改成"8×256 层惰性段"时，
    /// 索引访问器与 (x,y,z) 访问器被写成了**两套不同的物理布局**，于是光照/顶面高度/切片哈希/
    /// 地形生成器读写"别的格子"——表现为"透明但有碰撞""手持方块全黑""LOD 采到基岩"，
    /// 而且因为**写入方与读取方各自自洽**，藏了 4 个版本才被抓到（见 `notes/79`）。
    ///
    /// 这个自检把那条不变量变成可执行断言：**`CalculateCellIndex` + 索引访问器**必须与
    /// **(x,y,z) 访问器**落到同一格（写读往返）。用**临时区块**做，不动真实世界数据。
    ///
    /// 用法（桥）：
    ///   {"op":"invoke","target":"type:SkylineInvariants","member":"CheckChunkAddressing","action":"call"}
    /// 建议：动存储/寻址的改动（例如 32³ 第 2 步）在施工前后各跑一次，作为"落地门"。
    /// </summary>
    public static class SkylineInvariants {
        /// <summary>写读往返自检：返回 JSON（ok / checkedCells / mismatches / firstMismatch / note）。</summary>
        public static string CheckChunkAddressing() {
            JsonObject result = new();
            TerrainChunk chunk = null;
            try {
                chunk = new TerrainChunk(new Terrain(), 0, 0);
                int[] ys = [
                    TerrainChunk.MinHeight, TerrainChunk.MinHeight + 1, -1000, -767, -512, -256, -1, 0, 1,
                    TerrainChunk.HeightMinusOne - 1, TerrainChunk.HeightMinusOne
                ];
                int checkedCells = 0;
                int mismatches = 0;
                string firstMismatch = null;
                for (int x = 0; x < TerrainChunk.Size; x++) {
                    for (int z = 0; z < TerrainChunk.Size; z++) {
                        foreach (int y in ys) {
                            int contents = 1 + ((x * 7 + z * 13 + y) & 0x3F);
                            int value = Terrain.MakeBlockValue(contents, 15, 0);
                            int index = TerrainChunk.CalculateCellIndex(x, y, z);
                            chunk.SetCellValueFast(index, value);
                            int byIndex = chunk.GetCellValueFast(index);
                            int byXyz = chunk.GetCellValueFast(x, y, z);
                            checkedCells++;
                            if (byIndex != value || byXyz != value || byIndex != byXyz) {
                                mismatches++;
                                firstMismatch ??=
                                    $"({x},{y},{z}) index={index} write={value} byIndex={byIndex} byXyz={byXyz}";
                            }
                        }
                    }
                }
                // 顺带核对"索引减一 = y 减一"这条被大量循环依赖的性质（跨 256 层段边界也要成立）。
                int walkChecks = 0;
                int walkMismatches = 0;
                for (int x = 0; x < TerrainChunk.Size; x += 5) {
                    for (int z = 0; z < TerrainChunk.Size; z += 5) {
                        for (int y = TerrainChunk.MinHeight + 1; y < TerrainChunk.HeightMinusOne; y += 97) {
                            int indexHigh = TerrainChunk.CalculateCellIndex(x, y, z);
                            int indexLow = TerrainChunk.CalculateCellIndex(x, y - 1, z);
                            walkChecks++;
                            if (indexHigh - 1 != indexLow) {
                                walkMismatches++;
                            }
                        }
                    }
                }
                result["ok"] = mismatches == 0 && walkMismatches == 0;
                result["checkedCells"] = checkedCells;
                result["mismatches"] = mismatches;
                result["firstMismatch"] = firstMismatch;
                result["indexWalkChecks"] = walkChecks;
                result["indexWalkMismatches"] = walkMismatches;
                result["note"] = "索引访问器 ↔ (x,y,z) 访问器 必须同格；索引减一 = y 减一";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            finally {
                chunk?.Dispose();
            }
            return result.ToJsonString();
        }
    }
}
