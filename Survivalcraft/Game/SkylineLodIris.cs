using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.11：**LOD 只读网格访问器**——里程碑 5「Iris / Dawnlight 真接入」的第 1 步
    /// （路线图见 `notes/73 §5`：只读网格访问器 → 阴影阶段 → G-buffer/法线/材质 id）。
    ///
    /// 设计口径（**默认零影响**）：
    ///   * 本文件只**暴露**已有对象与统计，不改任何绘制/采集行为；光影包不调用它时，行为与 v0.1.10 逐位一致。
    ///   * 外部渲染器（Dawnlight 的 detour / Iris 式管线）通过
    ///     <see cref="SkylineLod.ExternalShaderHooked"/> + <see cref="SkylineLod.CustomDraw"/> 接管绘制，
    ///     用 <see cref="DrawWithShader"/> 把同一批网格用**自己的 Shader** 画出去；
    ///   * 网格顶点格式 = `TerrainVertex`（position + texcoord + color），与游戏地形同格式 ——
    ///     shader 可以直接复用游戏的地形顶点着色器约定。
    ///
    /// 为什么这算"真接入"的第一步：外部光影包需要（a）拿到网格本体（顶点/索引缓冲）、
    /// （b）知道每个 LOD 单元的高度与材质（用于阴影/材质分类）、（c）知道层与单元的几何参数
    /// （用于在 shader 里做距离/LOD 混叠）。这三样分别由 <see cref="CoarseVertexBuffer"/> 等、
    /// `SkylineLod.LodCellAt(cx,cz)`、<see cref="IrisMeshInfo"/> 提供。
    /// </summary>
    public static partial class SkylineLod {
        // ---------------- 网格本体（只读引用，不复制、不分配） ----------------

        /// <summary>粗层（16 m 单元）顶点缓冲；未构建时为 null。顶点格式见 <see cref="TerrainVertex"/>。</summary>
        public static VertexBuffer CoarseVertexBuffer => m_vb;

        /// <summary>粗层索引缓冲；未构建时为 null。</summary>
        public static IndexBuffer CoarseIndexBuffer => m_ib;

        /// <summary>细层（8 m 单元，近环）顶点缓冲；未构建时为 null。</summary>
        public static VertexBuffer FineVertexBuffer => m_vbFine;

        /// <summary>细层索引缓冲；未构建时为 null。</summary>
        public static IndexBuffer FineIndexBuffer => m_ibFine;

        /// <summary>粗层索引数（= 要画的索引个数，`DrawIndexed(..., 0, CoarseIndexCount)`）。</summary>
        public static int CoarseIndexCount => m_indexCount;

        /// <summary>细层索引数。</summary>
        public static int FineIndexCount => m_indexCountFine;

        /// <summary>本次网格重建的序号（外部可据此判断"网格换过一批"，决定是否重建自己的 GPU 资源）。</summary>
        public static int MeshVersion => m_rebuilds;

        /// <summary>
        /// 顶点格式说明（给外部 shader 分配输入布局用）：位置 Vector3 @0、纹理坐标 NormalizedShort2 @12、
        /// 颜色 NormalizedByte4 @16（共 20 字节）。
        /// </summary>
        public static string VertexLayout =>
            "TerrainVertex: Position(float3)@0 TexCoord(normalizedShort2)@12 Color(normalizedByte4)@16 stride=20";

        // ---------------- 元数据（shader 侧的"该画什么/多远"信息） ----------------

        /// <summary>
        /// 给光影包用的完整只读描述：网格统计 + 层参数 + 顶点格式 + 每个单元的元数据来源。
        /// 与 <see cref="MeshMetadata"/> 的区别：后者是最小统计（v0.1.5 就有），这里额外给出
        /// 缓冲尺寸/顶点布局/单元信息入口，供 shader 侧"一次性握手"。
        /// </summary>
        public static string IrisMeshInfo() {
            return new System.Text.Json.Nodes.JsonObject {
                ["api"] = "SkylineLod.IrisMeshAccess/1",
                ["meshVersion"] = m_rebuilds,
                ["cellSize"] = CellSize,
                ["fineCellSize"] = FineSize,
                ["radiusMetres"] = RadiusMetres,
                ["fineRangeMetres"] = RadiusMetres * FineRangeFactor,
                ["coarseCells"] = m_cellsInMesh,
                ["coarseIndices"] = m_indexCount,
                ["coarseVertexBytes"] = (long)(m_indexCount / 3) * 20,   // 每单元 4 顶点 / 12 索引
                ["fineCells"] = m_cellsInMeshFine,
                ["fineIndices"] = m_indexCountFine,
                ["fineVertexBytes"] = (long)(m_indexCountFine / 3) * 20,
                ["vertexLayout"] = VertexLayout,
                ["primitive"] = "TriangleList",
                ["externalHooked"] = ExternalShaderHooked,
                ["cellQuery"] = "SkylineLod.LodCellAt(cx,cz) -> {coarseHeight,coarseValue,coarseContents,fineHeights,...}",
                ["drawEntry"] = "SkylineLod.DrawWithShader(shader)"
            }.ToJsonString();
        }

        /// <summary>
        /// 用外部 Shader 画现有 LOD 网格（粗 + 细）。光影包在 <see cref="CustomDraw"/> 里调它，
        /// 或者自己拿到 <see cref="CoarseVertexBuffer"/> 等按需分帧绘制。
        /// 异常一律吞掉并记入 `SkylineLod` 的 lastError，避免光影包崩溃拖垮游戏。
        /// </summary>
        public static void DrawWithShader(Shader shader) {
            if (shader == null) {
                return;
            }
            try {
                if (m_vbFine != null && m_ibFine != null && m_indexCountFine > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vbFine, m_ibFine, 0, m_indexCountFine);
                }
                if (m_vb != null && m_ib != null && m_indexCount > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vb, m_ib, 0, m_indexCount);
                }
            }
            catch (System.Exception e) {
                m_lastError = "DrawWithShader: " + e.Message;
                Log.Warning($"SkylineLod.DrawWithShader: {e.Message}");
            }
        }

        /// <summary>
        /// [v0.1.32] 取证接口：抽出若干"距 (centerX,centerZ) 在 [minDist,maxDist] 米内"的 LOD 单元
        /// （粗层 `m_cells`，正是当前网格的数据源）。用于 GPU 深度图自检：
        /// 把单元的顶面点投影进深度图，与 CPU 公式算出的深度对照。
        /// 返回 JSON 数组：[{ cx, cz, cellSize, height, contents }]。
        /// </summary>
        public static string ProbeCells(float centerX, float centerZ, float minDist, float maxDist, int max) {
            System.Text.Json.Nodes.JsonArray list = [];
            int cellSize = CellSize;
            float min2 = minDist * minDist;
            float max2 = maxDist * maxDist;
            int limit = Math.Max(1, max);
            int count = 0;
            foreach (KeyValuePair<long, Cell> kv in m_cells) {
                if (count >= limit) {
                    break;
                }
                int cx = (int)(kv.Key >> 32);
                int cz = (int)(kv.Key & 0xFFFFFFFF);
                float wx = cx * cellSize + cellSize * 0.5f;
                float wz = cz * cellSize + cellSize * 0.5f;
                float dx = wx - centerX;
                float dz = wz - centerZ;
                float d2 = dx * dx + dz * dz;
                if (d2 < min2 || d2 > max2) {
                    continue;
                }
                list.Add(new System.Text.Json.Nodes.JsonObject {
                    ["cx"] = cx,
                    ["cz"] = cz,
                    ["cellSize"] = cellSize,
                    ["height"] = kv.Value.Height,
                    ["contents"] = Terrain.ExtractContents(kv.Value.Value)
                });
                count++;
            }
            return list.ToJsonString();
        }
    }
}
