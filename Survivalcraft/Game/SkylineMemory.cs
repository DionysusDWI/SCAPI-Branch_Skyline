using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.73：**内存归因探针** —— 里程碑 2.2 的仪器。
    ///
    /// 为什么要专门做它：v0.1.70 用"工作集"下过错误结论（WS 含垃圾、且 GC 归还内存是惰性的），
    /// v0.1.73 又发现"存活堆在长距离走行中会涨 ~1.2 GB"，而**所有已知结构都只有 MB 级**。
    /// 光看 GC 总量分不清"谁在持有"，所以把**能查到的持有者**一次列出来：
    ///   * `TerrainChunk.m_cellsCache`（`ArrayCache` of int，每段 16×16×32 = 8192 个 int = 32 KiB）的
    ///     **缓存量 / 租用量** —— 这是本轮的头号嫌疑：它的 `ClearCache` 触发条件是
    ///     "缓存占比**低于**阈值"，也就是**几乎不会清**；
    ///   * 地形已分配区块数；
    ///   * 壳仓（常驻壳数、网格数、字节）；
    ///   * LOD（单元数、区域仓常驻/已知/淘汰）；
    ///   * `GC.GetTotalMemory(false/true)` 与工作集。
    ///
    /// 口径：**这个探针只读**；`ClearCaches` 是显式动作（默认不调用）。
    /// </summary>
    public static partial class SkylineRuntime {
        public static string MemoryProbe() {
            JsonObject o = new();
            long cachedCells = -1, usedCells = -1;
            try {
                cachedCells = TerrainChunk.m_cellsCache.CachedCount;
                usedCells = TerrainChunk.m_cellsCache.UsedCount;
            }
            catch (Exception e) {
                o["cellsCacheError"] = e.Message;
            }
            const int bytesPerCell = 4;                       // ArrayCache<int>
            o["cellsCacheCachedBytes"] = cachedCells * bytesPerCell;
            o["cellsCacheUsedBytes"] = usedCells * bytesPerCell;
            o["cellsCacheCachedMiB"] = cachedCells < 0 ? null : Math.Round(cachedCells * (double)bytesPerCell / 1048576, 2);
            o["cellsCacheUsedMiB"] = usedCells < 0 ? null : Math.Round(usedCells * (double)bytesPerCell / 1048576, 2);
            o["allocatedChunks"] = AllocatedChunkCount;
            JsonObject shell = JsonNode.Parse(SkylineCubeShellStore.Survey()) as JsonObject ?? new JsonObject();
            o["shellCubes"] = Clone(shell["cubes"]);
            o["shellMiB"] = Clone(shell["shellMiB"]);
            o["shellMeshMiB"] = Clone(shell["meshMiB"]);
            JsonObject persist = JsonNode.Parse(SkylineCubeShellStore.Persistence()) as JsonObject ?? new JsonObject();
            o["shellStoreRecords"] = Clone(persist["fileRecords"]);
            o["shellStoreBytes"] = Clone(persist["fileBytes"]);
            JsonObject lod = JsonNode.Parse(SkylineLod.Survey()) as JsonObject ?? new JsonObject();
            o["lodCells"] = Clone(lod["cells"]);
            o["lodFineCells"] = Clone(lod["fineCells"]);
            o["lodRegionsResident"] = Clone(lod["regionsResident"]);
            o["lodRegionsKnown"] = Clone(lod["regionsKnown"]);
            o["lodRegionsEvicted"] = Clone(lod["regionsEvictedTotal"]);
            o["gcTotalBytes"] = GC.GetTotalMemory(false);
            o["gcTotalMiB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1);
            o["gcHeapMiB"] = Math.Round(GC.GetGCMemoryInfo().HeapSizeBytes / 1048576.0, 1);
            // [v0.1.73] **分代/大对象堆组成**：用来区分"大数组（LOH）"与"长期集合（gen2）"。
            try {
                GCMemoryInfo info = GC.GetGCMemoryInfo();
                JsonArray gens = [];
                for (int i = 0; i < info.GenerationInfo.Length; i++) {
                    long bytes = info.GenerationInfo[i].SizeAfterBytes;
                    gens.Add(new JsonObject {
                        ["gen"] = i,                        // 0/1/2 = SOH 各代，3 = LOH，4 = POH
                        ["bytes"] = bytes,
                        ["mib"] = Math.Round(bytes / 1048576.0, 2)
                    });
                }
                o["gcGenerations"] = gens;
            }
            catch (Exception e) {
                o["gcGenerationsError"] = e.Message;
            }
            try {
                o["wsMiB"] = Math.Round(Environment.WorkingSet / 1048576.0, 1);
            }
            catch {
                // 忽略：个别平台取不到
            }
            o["note"] = "只读探针；cellsCache 的 ClearCache 触发条件是「缓存占比低于阈值」⇒ 几乎不会清";
            return o.ToJsonString();
        }

        /// <summary>JsonNode 不能"一仆二主"（同一节点挂到第二棵树会抛 `The node already has a parent`）
        /// —— 所以复制一份。第一版直接赋值就踩了这个。</summary>
        static JsonNode Clone(JsonNode node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

        /// <summary>显式清空地形分带缓存池（诊断/内存压力用）。返回清空前的缓存 MiB。</summary>
        public static string MemoryClearCaches() {
            long before = TerrainChunk.m_cellsCache.CachedCount;
            TerrainChunk.m_cellsCache.Clear();
            JsonObject o = new() {
                ["clearedMiB"] = Math.Round(before * 4.0 / 1048576, 2),
                ["gcTotalMiBAfter"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1)
            };
            return o.ToJsonString();
        }
    }
}
