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
        /// [v0.1.77] 返回 **false = 这一帧没画成**（着色器没准备好 / 异常），调用方应**回落到游戏 Opaque 路径**
        /// —— 这样"默认走 GPU 路径"不会因为一次 shader 失败就让远景整层消失。
        /// </summary>
        static bool DrawWithAttributeShader(Camera camera) {
            if (m_vb == null && m_vbFine == null && m_vbNear == null) {
                return false;
            }
            try {
                Shader shader = SkylineLodVolume.PrepareVolumeShader(camera, 0f, SkylineRuntime.LodAttrChannel);
                if (shader == null) {
                    m_lastError = "attr shader not ready";
                    return false;
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
                return true;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLod.DrawWithAttributeShader: {e.Message}");
                return false;
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
        /// **[v0.1.77] 两条口径 + 两类断言**（这是 v0.1.61~v0.1.76 那个 KNOWN 的正解）：
        ///   1. **面因子口径**：把坡向/自阴影临时置 0，比 `属性+Opaque(烘焙)` vs `属性+体积着色器` ——
        ///      断言两者只差量化。**关键前提是"网格口径必须跟着着色器走"**：体积着色器自己会乘明暗，
        ///      所以它必须画**未烘焙**的网格（立面不烘面因子、顶面不烘坡向）。
        ///      旧实现画的是"已烘焙"的网格 → 侧壁被暗化两遍（实测 mean 37.975/255）——
        ///      那看起来像"法线没到片元"，其实是**把同一层明暗乘了两次**。
        ///   2. **坡向口径**：恢复真实强度，比 `属性+Opaque(烘焙坡向+自阴影)` vs `属性+体积着色器` ——
        ///      v0.1.77 起坡向明暗在片元里按**同一个坡面法线**算同一式子、自阴影两条路都烘焙，
        ///      所以这一项也应当只差量化：这才是"这两层 GPU 真的扛住了"的断言。
        /// </summary>
        public static string AttrSelfCheck(int size) {
            JsonObject result = new();
            bool savedAttr = SkylineRuntime.LodVertexAttributes;
            bool savedShader = SkylineRuntime.LodAttrShaderOn;
            float savedSlope = SkylineLod.SlopeShadingStrength;
            float savedShadow = SkylineLod.SelfShadowStrength;
            // [v0.1.95] 自检**必须能看到 LOD 几何**，否则它是一条"空判据"：
            //   `RestrictLod`（v0.1.51 起）让 LOD 在"有壳的立方体"上**让位**，而壳层覆盖越来越完整
            //   ⇒ 平视时画面里几乎没有 LOD 可采。实测（v0.1.94 门禁）：塔上 `comparedPixels=0`（direct SKIP）、
            //   地面 `PASS` 但**只比了 6 个像素** —— 那是"没东西可比的通过"，不是"两路一致"的证据。
            //   这里在自检期间临时关掉它，让 LOD 把整条带画出来；结束时**连网格一起还原**。
            bool savedRestrictLod = SkylineCubeShellStore.RestrictLod;
            // [v0.1.99] 云层阴影的相位含时间（`CloudWind × Time.RealTime`）⇒ 两次抓帧之间会变，
            //   而**这条自检比的就是"两次抓帧必须逐位一致"**（属性开/关只是顶点布局不同）。
            //   所以自检期间把它关掉：它检查的是顶点属性/着色器管道，不是云影。
            bool savedCloudShadow = SkylineLodCloudShadow.Enabled;
            // [v0.1.105] **体积雾/神光也要先关掉**：它们现在也注入 LOD 的体积着色器
            //   （`SkylineRuntime.BindShadowFogParams`），而这条自检比的是"属性着色器 vs 游戏 Opaque"
            //   —— 雾只存在于前者 ⇒ 会把"管道一致"的断言污染成 mean 36/255 的大差（实测踩到）。
            //   自检检查的是**顶点属性/着色器管道**，不是雾；所以期间关掉、结束还原。
            bool savedVolFog = SkylineRuntime.VolumetricFogEnabled;
            // [v0.1.105] **固定太阳**：坡向明暗与自阴影都用"跟踪到的真太阳"，而世界时间一直在走
            //   ⇒ CPU 烘焙那一次与 GPU 渲染那一次之间太阳会动一点点，极端情况下个别单元的
            //   自阴影可见性会翻转 ⇒ 这条自检**偶发 ok=false**（实测：单独跑 3/3 过；放进巡检/门禁
            //   的顺序里偶发 FAIL，`v0177-lod-gpu-shading` 就是被它带崩的）。
            //   这里在**每次抓帧前把时刻钉回同一个值**（只在世界时间是 Changing 时才动，
            //   固定时刻的世界本来就不需要，也不会被改）。
            double pinnedTod = 0.0;
            bool pinned = false;
            try {
                SubsystemTimeOfDay tod = GameManager.Project?.FindSubsystem<SubsystemTimeOfDay>(true);
                SubsystemGameInfo gi = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
                if (tod != null && gi != null && gi.WorldSettings.TimeOfDayMode == TimeOfDayMode.Changing) {
                    pinnedTod = tod.TimeOfDay;
                    pinned = true;
                }
            }
            catch (Exception) {
                pinned = false;
            }
            void PinSun() {
                if (!pinned) {
                    return;
                }
                try {
                    // 与 `SkylineRuntime.SunSetTimeOfDay` 同一套数学（只拨偏移，不动 TimeOfDayMode）
                    SubsystemTimeOfDay tod = GameManager.Project?.FindSubsystem<SubsystemTimeOfDay>(true);
                    SubsystemGameInfo gi = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
                    if (tod == null || gi == null) {
                        return;
                    }
                    double duration = Math.Max(tod.DayDuration, 1f);
                    double u = gi.TotalElapsedGameTime / duration;
                    u -= Math.Floor(u);
                    tod.TimeOfDayOffset = pinnedTod - tod.DayStart - u;
                }
                catch (Exception) {
                }
            }
            try {
                Camera camera = ActiveCamera;
                if (camera == null) {
                    result["ok"] = false;
                    result["err"] = "no camera";
                    return result.ToJsonString();
                }
                size = Math.Clamp(size <= 0 ? 256 : size, 64, Math.Min(Display.MaxTextureSize, 1024));
                SkylineCubeShellStore.RestrictLod = false;
                SkylineLodCloudShadow.Enabled = false;
                SkylineRuntime.VolumetricFogEnabled = false;
                SkylineLod.SlopeShadingStrength = 0f;      // 只比"面因子"这一层
                SkylineLod.SelfShadowStrength = 0f;
                SkylineRuntime.LodAttrShaderOn = false;
                SkylineRuntime.LodVertexAttributes = true;
                PinSun();
                RebuildNow();
                Image attrImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                int attrIndices = m_indexCount + m_indexCountFine + m_indexCountNear;
                SkylineRuntime.LodVertexAttributes = false;
                PinSun();
                RebuildNow();
                Image bakedImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                int bakedIndices = m_indexCount + m_indexCountFine + m_indexCountNear;
                // [v0.1.77] **口径必须一致**：体积着色器会自己乘明暗，所以它必须画"未烘焙"的网格
                // （立面不烘面因子、顶面不烘坡向）。v0.1.61~v0.1.76 的 KNOWN 就是这里画了"已烘焙"的网格
                // → 侧壁被暗化两遍 → 看起来像"法线没到片元"（实测 mean 37.975/255 ≈ 被多乘的那一层）。
                SkylineRuntime.LodVertexAttributes = true;
                SkylineRuntime.LodAttrShaderOn = true;
                PinSun();
                RebuildNow();
                Image gpuImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), true, 0, size, 0f);
                // ④⑤ [v0.1.77] 恢复**真实强度**再各画一遍：这一对比才是"坡向明暗 + 自阴影这一层
                // GPU 有没有扛住"的断言（两条路径同式 ⇒ 差应只有量化）。v0.1.61~v0.1.76 这一项
                // 量的是反过来的东西（"着色器还没实现的那层差多少"），所以它当时必然是 ~38。
                SkylineLod.SlopeShadingStrength = savedSlope;
                SkylineLod.SelfShadowStrength = savedShadow;
                SkylineRuntime.LodAttrShaderOn = false;
                PinSun();
                RebuildNow();
                Image shadedCpuImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), false, 0, size, 0f);
                SkylineRuntime.LodAttrShaderOn = true;
                PinSun();
                RebuildNow();
                Image shadedGpuImage = SkylineLodVolume.RenderLayers(camera, AttrLayers(), true, 0, size, 0f);
                JsonObject slopeGap = shadedCpuImage == null || shadedGpuImage == null
                    ? null : SkylineLodVolume.CompareImages(shadedCpuImage, shadedGpuImage);
                // 恢复现场（开关 + 强度 + 网格）
                SkylineRuntime.LodVertexAttributes = savedAttr;
                SkylineRuntime.LodAttrShaderOn = savedShader;
                SkylineLod.SlopeShadingStrength = savedSlope;
                SkylineLod.SelfShadowStrength = savedShadow;
                SkylineCubeShellStore.RestrictLod = savedRestrictLod;
                SkylineLodCloudShadow.Enabled = savedCloudShadow;
                SkylineRuntime.VolumetricFogEnabled = savedVolFog;
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
                result["slopeStrength"] = Math.Round(savedSlope, 4);
                result["selfShadowStrength"] = Math.Round(savedShadow, 4);
                result["slopeNote"] = "坡向明暗（v0.1.77 起）：CPU 烘焙路径按单元坡面法线算一次；"
                    + "GPU 路径把**同一个坡面法线**作为顶点属性（NORMAL.w = 255 标记顶面），在片元里按同一式子算。"
                    + "自阴影是**可见性**计算（高度场射线步进），两条路径都烘焙进顶点色。";
                bool strideIdentical = strideContract["identical"]?.GetValue<bool>() == true;
                long gpuMax = gpuVsCpu["maxAbsDiff"]?.GetValue<long>() ?? 255;
                long shadedMax = slopeGap?["maxAbsDiff"]?.GetValue<long>() ?? 255;
                result["verdicts"] = new JsonObject {
                    ["strideContract"] = strideIdentical
                        ? "属性开/关在游戏 Opaque 着色器下逐位一致 → 引擎按语义取属性，加属性没有换格式"
                        : "属性开/关的画面不一致 → 顶点格式契约被破坏（属性被 Opaque 着色器误读？）",
                    ["gpuVsCpu"] = gpuMax <= 8
                        ? "GPU 面明暗 == CPU 烘焙面因子（差 ≤ 8/255，纯量化）"
                        : "GPU 面明暗与 CPU 不一致 → 法线没到片元，或两边口径不是同一条式子",
                    ["slopeSelfShadowGap"] = shadedMax <= 8
                        ? "开着坡向明暗 + 自阴影时 GPU 路径与 CPU 烘焙一致（差 ≤ 8/255）→ 这两层 GPU 也扛住了"
                        : "开着坡向明暗/自阴影时两条路径不一致 → 它们没有真正迁到 GPU（或口径不一致）"
                };
                result["ok"] = strideIdentical && gpuMax <= 8 && shadedMax <= 8
                    && attrIndices == bakedIndices && attrIndices > 0;
                result["restoredAttributes"] = SkylineRuntime.LodVertexAttributes == savedAttr
                    && SkylineRuntime.LodAttrShaderOn == savedShader;
            }
            catch (Exception e) {
                SkylineRuntime.LodVertexAttributes = savedAttr;
                SkylineRuntime.LodAttrShaderOn = savedShader;
                SkylineLod.SlopeShadingStrength = savedSlope;
                SkylineLod.SelfShadowStrength = savedShadow;
                SkylineCubeShellStore.RestrictLod = savedRestrictLod;
                SkylineLodCloudShadow.Enabled = savedCloudShadow;
                SkylineRuntime.VolumetricFogEnabled = savedVolFog;
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

        /// <summary>
        /// 远景 LOD 是否用体积着色器画（GPU 面明暗 + v0.1.77 起的 GPU 坡向明暗）。
        /// **[v0.1.77] 默认改为 true** —— 依据是 `LodAttrSelfCheck`：两条路径在
        /// 面因子口径差 ≤ 1/255、在坡向 + 自阴影口径差 ≤ 2/255，也就是"换成 GPU 算"画面不变，
        /// 但省掉了 CPU 侧的坡向烘焙、并为后续把自阴影挪到片元留好了接口。
        /// 开关时立刻重建网格（CPU 烘焙与 GPU 明暗二选一）。
        /// </summary>
        public static bool LodAttrShaderOn { get; set; } = true;

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

        /// <summary>[v0.1.78] 坡向/自阴影当前使用的**太阳方向**（只读；`dotWithTracked` 恒为 1）。</summary>
        public static string LodSlopeSun() => SkylineLod.SlopeSunInfo().ToJsonString();

        /// <summary>
        /// [v0.1.78] **坡向太阳探针**（只读）：抽样 LOD 单元，对"追踪到的太阳"与"游戏固定方向光"
        /// 各算一遍坡向增益 → 报 `meanGainTracked / meanGainFixed / meanAbsDelta`。
        /// </summary>
        public static string LodSlopeSunProbe(int maxCells = 512) =>
            SkylineLod.SlopeSunProbe(maxCells).ToJsonString();
    }
}
