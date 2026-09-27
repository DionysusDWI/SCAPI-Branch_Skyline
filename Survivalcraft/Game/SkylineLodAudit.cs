using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.43：**LOD 交接带审计**（里程碑 4「视距边缘交接带」的可量化口径）。
    ///
    /// 做法：在"视距内真实区块"与"视距外 LOD"之间的**环带**上随机抽列，
    /// 把 **LOD 单元记录的高度/材质** 与 **真实地形的顶面高度/顶面方块** 逐列对照，输出：
    ///   * 高度差：均值 |Δh|、最大 |Δh|、|Δh|>1 / >4 的比例（衡量"地形起伏丢了多少"）；
    ///   * 材质命中率：LOD 单元里的方块 == 该列真实顶面方块的比例（衡量"材质映射准不准"）；
    ///   * 细层/粗层各自的样本数与命中率。
    ///
    /// 桥：`skyline.LodBoundaryAudit(inner, outer, samples)`（默认 128 / 384 m / 4000 个样本）。
    /// </summary>
    public static partial class SkylineLod {
        /// <summary>[v0.1.43] 只读取某列命中的 LOD 单元（先细层后粗层）。</summary>
        public static bool TryGetCellAt(int x, int z, out int cellSize, out int height, out int contents, out bool fine) {
            long fineKey = Key(x >> FineShift, z >> FineShift);
            if (m_cellsFine.TryGetValue(fineKey, out Cell fineCell)) {
                cellSize = FineSize;
                height = fineCell.Height;
                contents = Terrain.ExtractContents(fineCell.Value);
                fine = true;
                return true;
            }
            long coarseKey = Key(x >> CellShift, z >> CellShift);
            if (m_cells.TryGetValue(coarseKey, out Cell coarseCell)) {
                cellSize = CellSize;
                height = coarseCell.Height;
                contents = Terrain.ExtractContents(coarseCell.Value);
                fine = false;
                return true;
            }
            cellSize = 0;
            height = 0;
            contents = 0;
            fine = false;
            return false;
        }

        /// <summary>[v0.1.43] 交接带审计：环带 `[inner, outer]` 米内随机抽列，与真实地形逐列对照。</summary>
        public static string BoundaryAudit(int centerX, int centerZ, int inner, int outer, int samples) {
            return AuditCore(null, centerX, centerZ, inner, outer, samples);
        }

        /// <summary>[v0.1.43] 矩形版审计（配 `EnsureRegionLoaded` 用：把某片区域先驻留，再逐列对照）。</summary>
        public static string RectAudit(int x1, int z1, int x2, int z2, int samples) {
            return AuditCore((x1, z1, x2, z2), 0, 0, 0, 0, samples);
        }

        static string AuditCore((int x1, int z1, int x2, int z2)? rect, int centerX, int centerZ,
                                int inner, int outer, int samples) {
            JsonObject result = new();
            try {
                Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
                if (terrain == null) {
                    result["ok"] = false;
                    result["err"] = "no terrain";
                    return result.ToJsonString();
                }
                int n = Math.Clamp(samples, 64, 200000);
                int lo = rect.HasValue ? 0 : Math.Max(0, Math.Min(inner, outer));
                int hi = rect.HasValue ? 0 : Math.Max(lo + 1, Math.Max(inner, outer));
                Random random = new(20260927);
                int loadedColumns = 0, lodHits = 0, fineHits = 0, coarseHits = 0;
                int heightGt1 = 0, heightGt4 = 0, maxAbs = 0;
                long sumAbs = 0;
                int materialChecked = 0, materialMatch = 0;
                int fineMaterialChecked = 0, fineMaterialMatch = 0;
                int coarseMaterialChecked = 0, coarseMaterialMatch = 0;
                for (int i = 0; i < n; i++) {
                    int x, z;
                    if (rect.HasValue) {
                        x = random.Int(Math.Min(rect.Value.x1, rect.Value.x2), Math.Max(rect.Value.x1, rect.Value.x2));
                        z = random.Int(Math.Min(rect.Value.z1, rect.Value.z2), Math.Max(rect.Value.z1, rect.Value.z2));
                    }
                    else {
                        float radius = MathF.Sqrt(random.Float(lo, hi) / (float)hi) * hi;   // 面积均匀采样
                        float angle = random.Float(0f, MathF.Tau);
                        x = centerX + (int)MathF.Round(radius * MathF.Cos(angle));
                        z = centerZ + (int)MathF.Round(radius * MathF.Sin(angle));
                    }
                    int realTop = terrain.GetTopHeight(x, z);
                    if (realTop <= TerrainChunk.MinHeight) {
                        continue;                                    // 未加载列（或空列）→ 不参与
                    }
                    loadedColumns++;
                    int realContents = Terrain.ExtractContents(terrain.GetCellValue(x, realTop, z));
                    if (!TryGetCellAt(x, z, out int cellSize, out int lodHeight, out int lodContents, out bool fine)) {
                        continue;
                    }
                    lodHits++;
                    if (fine) {
                        fineHits++;
                    }
                    else {
                        coarseHits++;
                    }
                    int diff = lodHeight - realTop;
                    int abs = Math.Abs(diff);
                    sumAbs += abs;
                    if (abs > maxAbs) {
                        maxAbs = abs;
                    }
                    if (abs > 1) {
                        heightGt1++;
                    }
                    if (abs > 4) {
                        heightGt4++;
                    }
                    materialChecked++;
                    bool match = lodContents == realContents;
                    if (match) {
                        materialMatch++;
                    }
                    if (fine) {
                        fineMaterialChecked++;
                        if (match) {
                            fineMaterialMatch++;
                        }
                    }
                    else {
                        coarseMaterialChecked++;
                        if (match) {
                            coarseMaterialMatch++;
                        }
                    }
                    _ = cellSize;
                }
                result["ok"] = true;
                if (rect.HasValue) {
                    result["rect"] = new JsonArray(rect.Value.x1, rect.Value.z1, rect.Value.x2, rect.Value.z2);
                }
                else {
                    result["center"] = new JsonArray(centerX, centerZ);
                    result["ringMetres"] = new JsonArray(lo, hi);
                }
                result["samples"] = n;
                result["loadedColumns"] = loadedColumns;
                result["lodHits"] = lodHits;
                result["fineHits"] = fineHits;
                result["coarseHits"] = coarseHits;
                result["meanAbsHeightDiff"] = lodHits > 0 ? Math.Round((double)sumAbs / lodHits, 3) : 0;
                result["maxAbsHeightDiff"] = maxAbs;
                result["heightDiffGt1Ratio"] = lodHits > 0 ? Math.Round((double)heightGt1 / lodHits, 4) : 0;
                result["heightDiffGt4Ratio"] = lodHits > 0 ? Math.Round((double)heightGt4 / lodHits, 4) : 0;
                result["materialMatchRatio"] = materialChecked > 0
                    ? Math.Round((double)materialMatch / materialChecked, 4) : 0;
                result["fineMaterialMatchRatio"] = fineMaterialChecked > 0
                    ? Math.Round((double)fineMaterialMatch / fineMaterialChecked, 4) : 0;
                result["coarseMaterialMatchRatio"] = coarseMaterialChecked > 0
                    ? Math.Round((double)coarseMaterialMatch / coarseMaterialChecked, 4) : 0;
                result["note"] = "高度=LOD 单元记录值 vs 真实 GetTopHeight；材质=单元方块 vs 真实顶面方块";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }
}
