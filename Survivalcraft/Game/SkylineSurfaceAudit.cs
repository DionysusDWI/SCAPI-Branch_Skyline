using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.59：**"体积感"第一步 —— 数清裸露在外的体素**（用户新目标 1.6 的量化前提）。
    ///
    /// 用户口径："LOD 不应当只采样最上层方块，而是采样**所有裸露在外的方块**
    /// （雪层算一个雪方块，而草方块则算作泥土），以此为基础生成 LOD。"
    ///
    /// 现在的壳（`CubeSurface32`）只按列存"最高实心块"→ 一个立方体最多 1024 个采样点，
    /// 于是台阶、屋檐、树冠、雪层这些**不是列顶**的表面全都丢了 —— 这就是"LOD 太平、没有体积感"的根。
    ///
    /// 本类**不改数据、不改渲染**，只回答三个问题（全部可复核）：
    ///   1. 一个 32³ 立方体里**实心体素**有多少、其中**裸露**（六邻里有空气）的有多少；
    ///   2. 裸露体素里**不是列顶**的有多少（= 现在的壳漏掉的表面比例，1.6 的缺口大小）；
    ///   3. 裸露面的**朝向直方图** + 这些体素的**材质分布**（含"草→泥土 / 雪层→雪块"替换后的结果）。
    /// </summary>
    public static class SkylineSurfaceAudit {
        /// <summary>
        /// **LOD 材质替换规则**（用户口径："雪层算一个雪方块，而草方块则算作泥土"）。
        /// 返回 LOD 里该用的 contents（-1 = 不用替换）。
        /// 规则是**结构材质**口径：草皮的"体"是泥土、雪层的"体"是雪。
        /// </summary>
        public static int LodSubstitute(int contents) {
            string name = BlocksManager.Blocks != null && contents >= 0 && contents < BlocksManager.Blocks.Length
                && BlocksManager.Blocks[contents] != null ? BlocksManager.Blocks[contents].GetType().Name : "";
            switch (name) {
                case "GrassBlock":
                    return DirtContents;
                case "SnowLayerBlock":
                case "SnowBlock":
                    return SnowContents;
                default:
                    return -1;
            }
        }

        static int m_dirtContents = -1;
        static int m_snowContents = -1;

        static int DirtContents => m_dirtContents >= 0 ? m_dirtContents : (m_dirtContents = FindContents("DirtBlock"));
        static int SnowContents => m_snowContents >= 0 ? m_snowContents : (m_snowContents = FindContents("SnowBlock"));

        static int FindContents(string typeName) {
            if (BlocksManager.Blocks == null) {
                return 0;
            }
            for (int i = 1; i < BlocksManager.Blocks.Length; i++) {
                Block block = BlocksManager.Blocks[i];
                if (block != null && block.GetType().Name == typeName) {
                    return i;
                }
            }
            return 0;
        }

        struct Stats {
            public int Solid, Exposed, ExposedNotTop, ExposedFaces;
            public int Substituted;
            public readonly int[] Faces;              // 0:+Y 1:-Y 2:+X 3:-X 4:+Z 5:-Z
            public readonly Dictionary<int, int> Materials;

            public Stats(int _) {
                Solid = Exposed = ExposedNotTop = ExposedFaces = Substituted = 0;
                Faces = new int[6];
                Materials = [];
            }
        }

        /// <summary>统计一个 32³ 立方体（只读；要求该立方体所在列已加载）。</summary>
        public static string Audit(int cx, int cy, int cz, int maxMaterialRows = 12) {
            JsonObject root = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    root["ok"] = false;
                    root["err"] = "no terrain";
                    return root.ToJsonString();
                }
                const int S = CubeSurface32.Size;
                int ox = cx * S, oy = cy * S, oz = cz * S;
                Stopwatch watch = Stopwatch.StartNew();
                // 读 34³（含一圈边界，用来判"立方体外面"是不是空气；未加载的格 GetCellValue 返回 0 = 空气）
                int[] cells = new int[34 * 34 * 34];
                for (int y = -1; y <= S; y++) {
                    for (int z = -1; z <= S; z++) {
                        for (int x = -1; x <= S; x++) {
                            cells[Idx(x, y, z)] = terrain.GetCellValue(ox + x, oy + y, oz + z);
                        }
                    }
                }
                watch.Stop();
                double readMs = watch.Elapsed.TotalMilliseconds;

                int[] columnTop = new int[S * S];
                for (int i = 0; i < columnTop.Length; i++) {
                    columnTop[i] = int.MinValue;
                }
                for (int z = 0; z < S; z++) {
                    for (int x = 0; x < S; x++) {
                        for (int y = S - 1; y >= 0; y--) {
                            if (Terrain.ExtractContents(cells[Idx(x, y, z)]) != 0) {
                                columnTop[x + z * S] = y;
                                break;
                            }
                        }
                    }
                }

                Stats stats = new(0);
                int[] faceDx = [0, 0, 1, -1, 0, 0];
                int[] faceDy = [1, -1, 0, 0, 0, 0];
                int[] faceDz = [0, 0, 0, 0, 1, -1];
                for (int y = 0; y < S; y++) {
                    for (int z = 0; z < S; z++) {
                        for (int x = 0; x < S; x++) {
                            int value = cells[Idx(x, y, z)];
                            int contents = Terrain.ExtractContents(value);
                            if (contents == 0) {
                                continue;
                            }
                            stats.Solid++;
                            int exposedFaces = 0;
                            for (int f = 0; f < 6; f++) {
                                int neighbor = cells[Idx(x + faceDx[f], y + faceDy[f], z + faceDz[f])];
                                if (Terrain.ExtractContents(neighbor) == 0) {
                                    stats.Faces[f]++;
                                    exposedFaces++;
                                }
                            }
                            if (exposedFaces == 0) {
                                continue;
                            }
                            stats.Exposed++;
                            stats.ExposedFaces += exposedFaces;
                            if (columnTop[x + z * S] != y) {
                                stats.ExposedNotTop++;
                            }
                            int sub = LodSubstitute(contents);
                            int lodContents = sub >= 0 ? sub : contents;
                            if (sub >= 0) {
                                stats.Substituted++;
                            }
                            stats.Materials.TryGetValue(lodContents, out int n);
                            stats.Materials[lodContents] = n + 1;
                        }
                    }
                }

                List<KeyValuePair<int, int>> materials = [.. stats.Materials];
                materials.Sort((a, b) => b.Value.CompareTo(a.Value));
                JsonArray materialRows = [];
                for (int i = 0; i < materials.Count && i < maxMaterialRows; i++) {
                    int contents = materials[i].Key;
                    materialRows.Add(new JsonObject {
                        ["contents"] = contents,
                        ["block"] = contents > 0 && contents < BlocksManager.Blocks.Length
                            ? BlocksManager.Blocks[contents]?.GetType().Name ?? "" : "",
                        ["voxels"] = materials[i].Value
                    });
                }

                root["ok"] = true;
                root["cube"] = new JsonArray(cx, cy, cz);
                root["solidVoxels"] = stats.Solid;
                root["exposedVoxels"] = stats.Exposed;
                root["exposedFaces"] = stats.ExposedFaces;
                root["topSamplePoints"] = S * S;
                root["exposedNotTop"] = stats.ExposedNotTop;
                root["exposedNotTopRatio"] = stats.Exposed > 0
                    ? Math.Round(stats.ExposedNotTop / (double)stats.Exposed, 4) : 0.0;
                root["substitutedVoxels"] = stats.Substituted;
                root["faces"] = new JsonObject {
                    ["+Y"] = stats.Faces[0], ["-Y"] = stats.Faces[1],
                    ["+X"] = stats.Faces[2], ["-X"] = stats.Faces[3],
                    ["+Z"] = stats.Faces[4], ["-Z"] = stats.Faces[5]
                };
                root["materialRows"] = materialRows;
                root["readMs"] = Math.Round(readMs, 2);
                // 诊断：把几个原始读数写出来（读成 0 时用它定位是"地形没读到"还是"真的空气"）
                int probeY = oy + 3;
                root["probe"] = new JsonObject {
                    ["at"] = new JsonArray(ox + 16, probeY, oz + 16),
                    ["rawValue"] = terrain.GetCellValue(ox + 16, probeY, oz + 16),
                    ["chunkAllocated"] = terrain.GetChunkAtCell(ox + 16, oz + 16) != null,
                    ["chunkState"] = terrain.GetChunkAtCell(ox + 16, oz + 16)?.ThreadState.ToString() ?? "null",
                    ["minHeight"] = TerrainChunk.MinHeight,
                    ["maxHeight"] = TerrainChunk.HeightMinusOne,
                    ["cellValid"] = terrain.IsCellValid(ox + 16, probeY, oz + 16)
                };
                root["note"] = "暴露 = 实心且六邻里有空气（立方体外面也按真实地形判；未加载格读到 0 = 按空气算）；"
                    + "exposedNotTop = 裸露但不是列顶的体素（= 现有壳漏掉的表面）；"
                    + "材质已按「草→泥土、雪层/雪→雪」替换";
            }
            catch (Exception e) {
                root["ok"] = false;
                root["err"] = e.Message;
            }
            return root.ToJsonString();
        }

        static int Idx(int x, int y, int z) => (x + 1) + (z + 1) * 34 + (y + 1) * 34 * 34;
    }

    /// <summary>桥：`skyline.SurfaceAudit*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>数一个 32³ 立方体的"裸露体素 vs 列顶"（1.6 的量化前提）。</summary>
        public static string SurfaceAudit(int cx, int cy, int cz) => SkylineSurfaceAudit.Audit(cx, cy, cz);

        /// <summary>LOD 材质替换规则探查：某个 contents 会被替换成什么。</summary>
        public static string SurfaceSubstitute(int contents) {
            int sub = SkylineSurfaceAudit.LodSubstitute(contents);
            return new JsonObject {
                ["contents"] = contents,
                ["block"] = contents > 0 && contents < BlocksManager.Blocks.Length
                    ? BlocksManager.Blocks[contents]?.GetType().Name ?? "" : "",
                ["lodContents"] = sub >= 0 ? sub : contents,
                ["substituted"] = sub >= 0,
                ["note"] = "草方块 → 泥土；雪层/雪 → 雪方块（用户口径：LOD 用结构材质）"
            }.ToJsonString();
        }
    }
}
