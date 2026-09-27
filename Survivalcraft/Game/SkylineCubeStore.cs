using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.40：**P3 存储的底座** ——
    /// （a）立方体页的**共享路由**（列 + 分带 → 32³ 页 → 本地索引）与其自检；
    /// （b）把 v0.1.39 的立方体级判据落成"要分配多少页/多少字节"的**分配计划**（与列式对照）。
    ///
    /// 路由规则（P3 正式版要用的不变量，这里先用 `CubeChunk32` 影子页验证）：
    ///   列 `(colX,colZ)`（16×16）的第 `band = y >> 5` 分带，落在共享页
    ///   **`(colX >> 1, band, colZ >> 1)`**；页内本地坐标
    ///   `lx = (colX &amp; 1) * 16 + (x &amp; 15)`、`ly = y &amp; 31`、`lz = (colZ &amp; 1) * 16 + (z &amp; 15)`。
    ///   即：**一页 32³ = 2×2 列 × 一个 32 层分带**，正好是 v0.1.29 列内分带（32 KiB）的 4 倍。
    ///
    /// 桥：
    ///   `skyline.CubeStoreSelfCheck(cubes, writesPerCube)` —— 共享路由写读自检 + 引用计数分布
    ///   `skyline.CubeAllocationPlanHere(radius,yRadius)` / `CubeAllocationPlanAt(...)` —— 分配计划（P3 决策依据）
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>列 + 分带 → 共享页坐标（P3 路由：一页 = 2×2 列 × 32 层）。</summary>
        public static void CubePageCoordOf(int worldX, int worldY, int worldZ,
                                           out int pageX, out int pageY, out int pageZ) {
            pageX = (worldX >> 4) >> 1;          // 列坐标再折半
            pageY = worldY >> 5;
            pageZ = (worldZ >> 4) >> 1;
        }

        /// <summary>[v0.1.40] 共享页路由自检：随机写 N 个立方体页，再**按列式路径**（列+分带→页）读回来，
        /// 校验"路由到同一页 + 值一致 + 邻居页不串"，并统计每页被多少列写过（P3 的浪费口径）。</summary>
        public static string CubeStoreSelfCheck(int cubes = 64, int writesPerCube = 512) {
            JsonObject result = new();
            try {
                int pageCount = Math.Clamp(cubes, 1, 4096);
                int perPage = Math.Clamp(writesPerCube, 1, CubeChunk32.Cells);
                Dictionary<long, CubeChunk32> pages = [];
                Dictionary<long, int> columnsPerPage = [];
                int writes = 0;
                int checks = 0;
                int mismatches = 0;
                string firstMismatch = null;
                Random random = new(20260927);
                for (int p = 0; p < pageCount; p++) {
                    int pageX = random.Int(-64, 64);
                    int pageY = random.Int(-32, 31);          // 覆盖 -1024..1023 的立方体坐标区间
                    int pageZ = random.Int(-64, 64);
                    long key = CubeChunk32.Key(pageX, pageY, pageZ);
                    if (!pages.TryGetValue(key, out CubeChunk32 page)) {
                        page = new CubeChunk32(pageX, pageY, pageZ);
                        pages[key] = page;
                    }
                    for (int w = 0; w < perPage; w++) {
                        int lx = random.Int(0, CubeChunk32.Size - 1);
                        int ly = random.Int(0, CubeChunk32.Size - 1);
                        int lz = random.Int(0, CubeChunk32.Size - 1);
                        int worldX = pageX * CubeChunk32.Size + lx;
                        int worldY = pageY * CubeChunk32.Size + ly;
                        int worldZ = pageZ * CubeChunk32.Size + lz;
                        int value = 1 + (writes & 0x3F);
                        page.Set(worldX, worldY, worldZ, value);
                        writes++;
                        // ---- 按"列式路径"重新路由：列(16×16) + 分带(32) → 页 ----
                        CubePageCoordOf(worldX, worldY, worldZ, out int routeX, out int routeY, out int routeZ);
                        long routeKey = CubeChunk32.Key(routeX, routeY, routeZ);
                        int got = pages.TryGetValue(routeKey, out CubeChunk32 routed)
                            ? routed.Get(worldX, worldY, worldZ)
                            : 0;
                        // 邻居页必须读不到（不串页）
                        long neighborKey = CubeChunk32.Key(routeX + 1, routeY, routeZ);
                        int neighborGot = pages.TryGetValue(neighborKey, out CubeChunk32 neighbor)
                            ? neighbor.Get(worldX, worldY, worldZ)
                            : 0;
                        checks++;
                        if (routeX != pageX || routeY != pageY || routeZ != pageZ || got != value || neighborGot != 0) {
                            mismatches++;
                            firstMismatch ??= $"world=({worldX},{worldY},{worldZ}) page=({pageX},{pageY},{pageZ}) "
                                + $"route=({routeX},{routeY},{routeZ}) got={got} neighbor={neighborGot}";
                        }
                    }
                }
                // 重新数一遍"每页被多少列写过"（上面的循环里只写值，这里单独统计，语义更清楚）
                foreach (KeyValuePair<long, CubeChunk32> kv in pages) {
                    CubeChunk32 page = kv.Value;
                    HashSet<long> columns = [];
                    for (int lx = 0; lx < CubeChunk32.Size; lx += 3) {
                        for (int lz = 0; lz < CubeChunk32.Size; lz += 3) {
                            for (int ly = 0; ly < CubeChunk32.Size; ly += 7) {
                                int worldX = page.X * CubeChunk32.Size + lx;
                                int worldY = page.Y * CubeChunk32.Size + ly;
                                int worldZ = page.Z * CubeChunk32.Size + lz;
                                if (page.Get(worldX, worldY, worldZ) != 0) {
                                    columns.Add(((long)(worldX >> 4) << 32) | (uint)(worldZ >> 4));
                                }
                            }
                        }
                    }
                    columnsPerPage[kv.Key] = columns.Count;
                }
                JsonObject refCounts = new();
                int pagesWith1 = 0, pagesWith2 = 0, pagesWith3 = 0, pagesWith4 = 0;
                long allocatedBytes = 0;
                foreach (KeyValuePair<long, CubeChunk32> kv in pages) {
                    allocatedBytes += kv.Value.AllocatedBytes;
                    columnsPerPage.TryGetValue(kv.Key, out int n);
                    switch (n) {
                        case 0:
                        case 1: pagesWith1++; break;
                        case 2: pagesWith2++; break;
                        case 3: pagesWith3++; break;
                        default: pagesWith4++; break;
                    }
                }
                refCounts["oneColumn"] = pagesWith1;
                refCounts["twoColumns"] = pagesWith2;
                refCounts["threeColumns"] = pagesWith3;
                refCounts["fourColumns"] = pagesWith4;
                foreach (CubeChunk32 page in pages.Values) {
                    page.Dispose();
                }
                result["ok"] = mismatches == 0;
                result["pages"] = pages.Count;
                result["writes"] = writes;
                result["routeChecks"] = checks;
                result["mismatches"] = mismatches;
                result["firstMismatch"] = firstMismatch;
                result["allocatedBytes"] = allocatedBytes;
                result["bytesPerPage"] = CubeChunk32.Bytes;
                result["columnsPerPage"] = refCounts;
                result["note"] = "路由：列(16×16)+分带(32) → 页 (colX>>1, y>>5, colZ>>1)；一页 = 2×2 列 × 32 层";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>[v0.1.40] 以相机所在立方体为中心的 P3 分配计划。</summary>
        public static string CubeAllocationPlanHere(int radiusCubes = 4, int yRadius = 3) {
            Camera camera = GetCamera();
            if (camera == null) {
                return "cubeAllocationPlan: no camera";
            }
            Vector3 position = camera.ViewPosition;
            return CubeAllocationPlanAt((int)MathF.Floor(position.X / TerrainUpdater.CubeSize),
                (int)MathF.Floor(position.Y / TerrainUpdater.CubeSize),
                (int)MathF.Floor(position.Z / TerrainUpdater.CubeSize), radiusCubes, yRadius);
        }

        /// <summary>[v0.1.40] 指定立方体中心的 P3 分配计划（脚本可对比地面/高空）。</summary>
        public static string CubeAllocationPlanAt(int pcx, int pcy, int pcz,
                                                  int radiusCubes = 4, int yRadius = 3) {
            TerrainUpdater updater = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.TerrainUpdater;
            if (updater == null) {
                return "cubeAllocationPlan: no updater";
            }
            return updater.CubeAllocationPlan(pcx, pcy, pcz,
                Math.Clamp(radiusCubes, 1, 16), Math.Clamp(yRadius, 0, 16));
        }
    }
}
