using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**里程碑 1.3 接进生产路径**——远景 LOD 三层的顶点带上法线与材质 id，
    /// 并可选地把明暗从"CPU 烘焙进顶点色"改成"GPU 按法线算"（`SkylineLodVolume`）。
    ///
    /// 两个开关：
    ///   * <see cref="SkylineRuntime.LodVertexAttributes"/>（默认 **true**）：建网格时用 `SkylineLodVertex`（28 B，带法线 + 材质 id）。
    ///     前 20 B 与 `TerrainVertex` 逐位相同、引擎按语义取偏移，所以游戏 `Opaque` 着色器照画不误 ——
    ///     "属性开/关像素逐位一致"就是这个契约的断言（`skyline.LodAttrSelfCheck`）。
    ///   * <see cref="SkylineRuntime.LodAttrShaderOn"/>（默认 false）：绘制改走 `SkylineLodVolume` 的体积着色器，
    ///     片元里按**面法线**算 `LightingManager.CalculateLighting` 同式的明暗 → 顶面/侧壁亮度分开，
    ///     远景不再是"一块平色板"。打开后 CPU 侧不再烘焙面因子（避免算两遍）。
    ///
    /// 为什么这算"体积感"：LOD 现在的几何是"顶面平板 + 高度差处的裙边"，本来只有**顶面**被照亮；
    /// 侧壁一直是常数色（220），于是起伏与建筑体量在远处全被抹平。有了法线，侧壁拿到 ±X/±Z 的
    /// 游戏光照因子（实测 0.62/0.84），远景的"墙"才立得起来。
    /// </summary>
    public static partial class SkylineLod {
        /// <summary>
        /// 用属性格式（`SkylineLodVertex`：法线 + 材质 id）建三层远端 LOD 网格。默认 **true**；
        /// 关掉即逐位回到 v0.1.59 的 `TerrainVertex`（20 B）路径，用于 A/B。
        /// </summary>
        public static bool VertexAttributes {
            get => SkylineRuntime.LodVertexAttributes;
            set {
                if (SkylineRuntime.LodVertexAttributes == value) {
                    return;
                }
                SkylineRuntime.LodVertexAttributes = value;
                RequestRebuild();                      // 顶点格式换了 → 必须重建网格
            }
        }

        /// <summary>着色器路径：true = 用体积着色器（属性 + GPU 面明暗），false = 游戏 `Opaque`。</summary>
        public static bool AttrShader {
            get => SkylineRuntime.LodAttrShaderOn;
            set {
                if (SkylineRuntime.LodAttrShaderOn == value) {
                    return;
                }
                SkylineRuntime.LodAttrShaderOn = value;
                RequestRebuild();                      // CPU 烘焙与 GPU 明暗二选一，切换要重建
            }
        }

        /// <summary>
        /// [v0.1.60] GPU 面明暗路径：三层网格交给 `SkylineLodVolume` 画。
        /// 语义与游戏地形着色器对齐（图集 + 雾带 + 雾起始密度都取同一组参数），只是明暗按法线来。
        /// </summary>
        static void DrawWithAttributeShader(Camera camera) {
            if (m_vb == null && m_vbFine == null && m_vbNear == null) {
                return;
            }
            try {
                Shader shader = SkylineLodVolume.PrepareVolumeShader(camera, 0f, SkylineRuntime.LodAttrChannel);
                if (shader == null) {
                    m_lastError = "attr shader not ready";
                    return;
                }
                if (m_vb != null && m_ib != null && m_indexCount > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vb, m_ib, 0, m_indexCount);
                }
                if (m_vbFine != null && m_ibFine != null && m_indexCountFine > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vbFine, m_ibFine, 0, m_indexCountFine);
                }
                if (m_vbNear != null && m_ibNear != null && m_indexCountNear > 0) {
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_vbNear, m_ibNear, 0, m_indexCountNear);
                }
                m_attrShaderDraws++;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLod.DrawWithAttributeShader: {e.Message}");
            }
        }

        static int m_attrShaderDraws;

        /// <summary>[v0.1.60] 立刻重建三层网格（不等 Tick 节拍）——取证与回归用。</summary>
        public static void RebuildNow() {
            try {
                RebuildMesh();
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLod.RebuildNow: {e.Message}");
            }
        }

        static List<(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int IndexCount)> AttrLayers() => [
            (m_vb, m_ib, m_indexCount),
            (m_vbFine, m_ibFine, m_indexCountFine),
            (m_vbNear, m_ibNear, m_indexCountNear)
        ];

        /// <summary>
        /// [v0.1.60] **属性接生产路径的自检**（`skyline.LodAttrSelfCheck`），在引擎内做三次离屏回读：
        ///   ① 属性格式（28 B）+ 游戏 `Opaque` 着色器；
        ///   ② 老格式（20 B）+ 游戏 `Opaque` 着色器；
        ///   ③ 属性格式 + 体积着色器（GPU 面明暗）。
        /// 判据：①与②必须**逐位一致**（这就是"加属性没有换格式"的运行时证据）；
        /// ①与③只应有 8 位量化误差（CPU 烘焙面因子 == GPU 按法线算明暗）。
        /// 离屏回读没有云/水面，所以这是**确定性**比较，不受画面动画干扰。
        ///
        /// **[v0.1.61] 同类比同类**：CPU 烘焙路径除了"面因子"还会乘**坡向明暗**（`SlopeShadingStrength`）
        /// 与**自阴影**（`SelfShadowStrength`），而体积着色器目前**只实现了面因子** ——
        /// 第一次实测就因此报 `ok:false`（`meanAbsDiff 7.2 / maxAbsDiff 51 / diffPxGt8 1500`），
        /// 看起来像"法线没到片元"，其实是**两条路径算的不是同一个式子**。
        /// 所以本自检在比较期间把坡向/自阴影**临时置 0**（这才是它能断言的命题：属性路径的面因子 == CPU 面因子），
        /// 然后把"坡向+自阴影"的差距**单独量一遍**记在 `slopeSelfShadowGap` 里，不藏起来。
        /// </summary>
        public static string AttrSelfCheck(int size) {
            JsonObject result = new();
            bool savedAttr = SkylineRuntime.LodVertexAttributes;
            float savedSlope = SkylineLod.SlopeShadingStrength;
            float savedShadow = SkylineLod.SelfShadowStrength;
            try {
                Camera camera = ActiveCamera;
                if (camera == null) {
                    result["ok"] = false;
                    result["err"] = "no camera";
                    return result.ToJsonString();
                }
                size = Math.Clamp(size <= 0 ? 256 : size, 64, Math.Min(Display.MaxTextureSize, 1024));
                SkylineLod.SlopeShadingStrength = 0f;      // 只比"面因子"这一层（着色器目前只实到这一层）
                SkylineLod.SelfShadowStrength = 0f;
                SkylineRuntime.LodVertexAttributes = true;
                RebuildNow();
                Image attrImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                int attrIndices = m_indexCount + m_indexCountFine + m_indexCountNear;
                SkylineRuntime.LodVertexAttributes = false;
                RebuildNow();
                Image bakedImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                int bakedIndices = m_indexCount + m_indexCountFine + m_indexCountNear;
                SkylineRuntime.LodVertexAttributes = true;
                RebuildNow();
                Image gpuImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), true, 0, size, 0f);
                // ④ 把坡向明暗 + 自阴影**恢复**后再烘焙一次（属性格式 + Opaque）：用来量"着色器还没实现的那一层"差多少
                SkylineLod.SlopeShadingStrength = savedSlope;
                SkylineLod.SelfShadowStrength = savedShadow;
                RebuildNow();
                Image shadedImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                SkylineLod.SlopeShadingStrength = 0f;
                SkylineLod.SelfShadowStrength = 0f;
                JsonObject slopeGap = shadedImage == null ? null : SkylineLodVolume.CompareImages(shadedImage, gpuImage);
                SkylineRuntime.LodVertexAttributes = savedAttr;
                SkylineLod.SlopeShadingStrength = savedSlope;
                SkylineLod.SelfShadowStrength = savedShadow;
                RebuildNow();
                if (attrImage == null || bakedImage == null || gpuImage == null) {
                    result["ok"] = false;
                    result["err"] = "shader not ready（Opaque 或体积着色器没准备好）";
                    return result.ToJsonString();
                }
                JsonObject strideContract = SkylineLodVolume.CompareImages(attrImage, bakedImage);
                JsonObject gpuVsCpu = SkylineLodVolume.CompareImages(attrImage, gpuImage);
                result["ok"] = true;
                result["size"] = size;
                result["attrIndices"] = attrIndices;
                result["bakedIndices"] = bakedIndices;
                result["sameGeometry"] = attrIndices == bakedIndices && attrIndices > 0;
                result["strideContract"] = strideContract;
                result["gpuVsCpu"] = gpuVsCpu;
                if (slopeGap != null) {
                    result["slopeSelfShadowGap"] = slopeGap;
                }
                bool strideIdentical = strideContract["identical"]?.GetValue<bool>() == true;
                long gpuMax = gpuVsCpu["maxAbsDiff"]?.GetValue<long>() ?? 255;
                result["verdicts"] = new JsonObject {
                    ["strideContract"] = strideIdentical
                        ? "属性开/关在游戏 Opaque 着色器下逐位一致 → 引擎按语义取属性，加属性没有换格式"
                        : "属性开/关的画面不一致 → 顶点格式契约被破坏（属性被 Opaque 着色器误读？）",
                    ["gpuVsCpu"] = gpuMax <= 8
                        ? "GPU 面明暗 == CPU 烘焙（差 ≤ 8/255，纯量化）"
                        : "GPU 面明暗与 CPU 不一致 → 法线没到片元，或两条公式不同（本自检已把坡向/自阴影置 0，所以这里只可能是面因子本身不一致）",
                    ["slopeSelfShadowGap"] = "坡向明暗 + 自阴影目前**只有 CPU 烘焙路径**实现：体积着色器只到面因子。"
                        + "这一项量的是【如果直接开着属性着色器画，会丢掉多少明暗】，不是属性通道的失败。"
                };
                result["ok"] = strideIdentical && gpuMax <= 8 && attrIndices == bakedIndices && attrIndices > 0;
                result["restoredAttributes"] = SkylineRuntime.LodVertexAttributes == savedAttr;
            }
            catch (Exception e) {
                SkylineRuntime.LodVertexAttributes = savedAttr;
                m_lastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>顶点/渲染路径的当前状态（给回归清单与 A/B 脚本断言）。</summary>
        public static JsonObject AttrState() {
            bool attr = SkylineRuntime.LodVertexAttributes;
            int stride = attr ? SkylineLodVertex.Stride : 20;
            long bytes = (long)(m_vertexCount + m_vertexCountFine + m_vertexCountNear) * stride;
            return new JsonObject {
                ["ok"] = true,
                ["vertexAttributes"] = attr,
                ["attrShader"] = SkylineRuntime.LodAttrShaderOn,
                ["attrChannel"] = SkylineRuntime.LodAttrChannel,
                ["vertexStride"] = stride,
                ["oldStride"] = 20,
                ["coarseVertices"] = m_vertexCount,
                ["fineVertices"] = m_vertexCountFine,
                ["nearVertices"] = m_vertexCountNear,
                ["meshVertexBytes"] = bytes,
                ["meshVertexMiB"] = Math.Round(bytes / 1048576.0, 3),
                ["attrShaderDraws"] = m_attrShaderDraws,
                ["faceShadingBaked"] = SkylineFaceShading.Enabled && !(attr && SkylineRuntime.LodAttrShaderOn),
                ["faceFactors"] = new JsonObject {
                    ["+Y"] = Math.Round(SkylineFaceShading.Factor(4), 4),
                    ["-Y"] = Math.Round(SkylineFaceShading.Factor(5), 4),
                    ["+X"] = Math.Round(SkylineFaceShading.Factor(1), 4),
                    ["-X"] = Math.Round(SkylineFaceShading.Factor(3), 4),
                    ["+Z"] = Math.Round(SkylineFaceShading.Factor(0), 4),
                    ["-Z"] = Math.Round(SkylineFaceShading.Factor(2), 4)
                },
                ["vertexLayout"] = SkylineLod.VertexLayout,
                ["note"] = "属性格式前 20 B 与 TerrainVertex 逐位相同（引擎按语义取偏移+声明步长）→ "
                    + "游戏 Opaque 着色器照画；多出的 NORMAL@20 / TEXCOORD1@24 由 SkylineLodVolume 消费。"
            };
        }
    }

    /// <summary>桥：`skyline.LodVertexAttributes / LodAttrShader / LodAttrInfo / LodLayerCapture`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>远景 LOD 顶点是否带属性（法线 + 材质 id）。默认 true。</summary>
        public static bool LodVertexAttributes { get; set; } = true;

        /// <summary>远景 LOD 是否用体积着色器画（GPU 面明暗）。默认 false；打开会立刻重建网格。</summary>
        public static bool LodAttrShaderOn { get; set; }

        /// <summary>属性着色器的调试通道（0=着色 1=法线 2=材质 id 3=面因子）。</summary>
        public static int LodAttrChannel { get; set; }

        /// <summary>开关远景 LOD 的顶点属性（会立刻重建网格）。</summary>
        public static string LodAttrVertexAttributes(bool enabled) {
            SkylineLod.VertexAttributes = enabled;
            return SkylineLod.AttrState().ToJsonString();
        }

        /// <summary>[兼容别名] 与 `LodAttrVertexAttributes` 同一开关。</summary>
        public static string LodVertexAttr(bool enabled) => LodAttrVertexAttributes(enabled);

        /// <summary>开关"用属性着色器画远景 LOD"（会立刻重建网格）。</summary>
        public static string LodAttrShader(bool enabled) {
            SkylineLod.AttrShader = enabled;
            return SkylineLod.AttrState().ToJsonString();
        }

        /// <summary>属性着色器通道（只影响画，不用重建）。</summary>
        public static string LodAttrSetChannel(int channel) {
            LodAttrChannel = Math.Clamp(channel, 0, 3);
            return SkylineLod.AttrState().ToJsonString();
        }

        /// <summary>当前顶点格式 / 着色器路径 / 常驻顶点字节。</summary>
        public static string LodAttrInfo() => SkylineLod.AttrState().ToJsonString();

        /// <summary>
        /// 属性接生产路径的自检（引擎内三次离屏回读，确定性比较）：
        /// ①属性+Opaque vs ②老格式+Opaque 必须逐位一致；①与③（体积着色器 GPU 面明暗）只应有量化误差。
        /// </summary>
        public static string LodAttrSelfCheck(int size = 256) => SkylineLod.AttrSelfCheck(size);

        /// <summary>立刻重建远景 LOD 网格（不等 Tick）；返回重建后的状态。</summary>
        public static string LodAttrRebuild() {
            SkylineLod.RebuildNow();
            return SkylineLod.AttrState().ToJsonString();
        }

        /// <summary>
        /// 把**生产层三层远端 LOD 网格**离屏画一遍并回读（通道 1/2 证明法线与材质 id 到了片元）。
        /// `attrShader=true` 走体积着色器（要求属性格式），false 走游戏 Opaque。
        /// </summary>
        public static string LodLayerCapture(int channel, int size, bool attrShader) =>
            SkylineLodVolume.CaptureLodLayers(channel, size, attrShader);
    }
}
