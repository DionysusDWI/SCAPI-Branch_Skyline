using System;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.114：**屏幕空间环境光遮蔽（SSAO / 接触阴影）** —— 相机深度预通道的消费者。
    ///
    /// 出处与口径：Iris 光影包把 AO 放在后处理里（`lib/lighting/*` + 深度重建），本分支**没有后处理链**，
    /// 所以改成**在不透明地形片元里算**：用 `SkylineScreenDepth` 的深度图取 8 个邻居、重建它们的世界坐标，
    /// 与当前片元比较"是否被同侧几何挡住半径内的一段" ⇒ 得到接触阴影因子，直接乘到该片元的颜色上。
    ///
    /// 三条硬口径：
    ///   1. **默认关**（`ScreenAoEnabled=false`）：关掉时片元里 `u_aoEnable=0`，函数立刻返回 1.0，
    ///      画面**逐位不变**（可 A/B）；
    ///   2. **uv 从世界坐标投回来**（`u_aoViewProj` 与预通道同一套矩阵/NDC 约定）⇒ 与分辨率、与
    ///      "渲染到纹理时引擎会翻转 y"（`u_glymul = RenderTarget != null ? -1 : 1`）都无关；
    ///   3. 逐像素旋转来自**世界坐标哈希**（与阴影核同一套）⇒ 跨帧稳定、可复现。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>
        /// 总开关（默认关）。**打开时会自动把相机深度预通道也打开**（否则 `ScreenDepthReady=false`，
        /// AO 拿不到深度图 ⇒ "开了没反应"这种坑不必再让人踩一次）。关掉 AO **不会**顺手关掉预通道
        /// —— 预通道可能是别的消费者（TAA/屏幕空间体积光）在用的。
        /// </summary>
        public static bool ScreenAoEnabled {
            get => m_aoEnabled;
            set {
                m_aoEnabled = value;
                if (value) {
                    ScreenDepthEnabled = true;
                }
            }
        }

        static bool m_aoEnabled;

        /// <summary>采样半径（米，默认 **0.5**）：接触阴影的世界尺度。
        /// 实测（`notes/210`）：半径 2.5~3.5 m 量到的是"整坡压暗"（36 万像素、maxΔ 65~72，观感错），
        /// 而 0.5 m 是"只动 1 万像素但 maxΔ 142"的局部对比 —— 那才是接触阴影该有的形状。</summary>
        public static float ScreenAoRadius { get; set; } = 0.5f;

        /// <summary>遮蔽强度（0..1，默认 0.7）。</summary>
        public static float ScreenAoIntensity { get; set; } = 0.7f;

        /// <summary>同侧阈值（法线与邻居方向的余弦下限，默认 0.05）：避免"背面"误判成遮蔽。</summary>
        public static float ScreenAoBias { get; set; } = 0.05f;

        /// <summary>
        /// **[诊断] 直显模式**：`0` = 正常（乘 AO）；`1` = AO 因子灰阶；`2` = 预通道 uv（红=u、绿=v）
        /// —— 这一档能一眼看出"投影 + y 翻转"对不对；`3` = 世界坐标 fract（看重建/插值是否正常）。
        /// 第一版是个 bool（灰阶），但它没显示出灰阶 ⇒ 换成多档，先确认通道真的生效。
        /// </summary>
        public static int ScreenAoDebugMode { get; set; }

        /// <summary>兼容旧名的布尔开关（`true` = 模式 1）。</summary>
        public static bool ScreenAoDebugShow {
            get => ScreenAoDebugMode == 1;
            set => ScreenAoDebugMode = value ? 1 : 0;
        }

        /// <summary>**屏幕空间半径上限**（uv 比例，默认 0.05 = 屏幕宽的 5%）。
        /// 为什么需要：0.8 m 的世界半径在 2 m 处会占到屏幕 24%，8 个抽样全落到远处几何 ⇒ 近处反而没有 AO。</summary>
        public static float ScreenAoMaxUv { get; set; } = 0.02f;

        /// <summary>**深度图 v 翻转**（默认 1.0 = 翻）：预通道渲染到 RT，引擎在 RT 路径上
        /// `gl_Position.y *= -1`（`Shader.PrepareForDrawing` 的 `u_glymul`），而后台缓冲不翻
        /// ⇒ 深度图的 v 与片元算出的 uv 差一次翻转。做成开关是为了 A/B 一次定死它。</summary>
        public static float ScreenAoFlipV { get; set; } = 1f;

        static RenderTarget2D m_aoDummyRt;
        static SamplerState m_aoSampler;
        static string m_aoLastError = "";
        static long m_aoBinds;
        static long m_aoLastErrorBinds;

        public static string ScreenAoLastError => m_aoLastError;
        public static long ScreenAoBinds => m_aoBinds;

        public static string ScreenAoDescribe() => new JsonObject {
            ["enabled"] = ScreenAoEnabled,
            ["ready"] = ScreenAoEnabled && ScreenDepthReady,
            ["radius"] = (double)ScreenAoRadius,
            ["intensity"] = (double)ScreenAoIntensity,
            ["bias"] = (double)ScreenAoBias,
            ["depthReady"] = ScreenDepthReady,
            ["depthSize"] = ScreenDepthRt == null ? null : new JsonArray(ScreenDepthRt.Width, ScreenDepthRt.Height),
            ["binds"] = m_aoBinds,
            ["lastError"] = m_aoLastError,
            ["note"] = "在不透明地形片元里用相机深度图做 8 抽样 SSAO；关掉时逐位不变"
        }.ToJsonString();

        /// <summary>把 AO 参数绑到地形的不透明变体上（由 `ResolveOpaqueShader` 调用）。</summary>
        public static string BindScreenAo(Shader shader) {
            try {
                // ⚠️ 这个函数被**共用**的 `BindShadowFogParams` 调用，而那一条也服务远景 LOD 着色器
                //   （它没有这些 uniform）。所以先探测一次：**取不到就静默跳过**，绝不让"某个着色器没有 AO"
                //   变成一条会打断后续绑定的异常。
                //   注意 `GetParameter(name, allowNull:true)` **不返回 null**（返回一个 Null 类型的占位参数），
                //   所以这里用 `allowNull:false`（取不到会抛）来探测。
                ShaderParameter pEnable;
                try {
                    pEnable = shader.GetParameter("u_aoEnable");
                }
                catch (Exception) {
                    return "";
                }
                bool on = ScreenAoEnabled && ScreenDepthReady && ScreenDepthRt != null;
                if (m_aoDummyRt == null) {
                    // 纹理参数必须绑一个真的 Texture2D（画面里 u_aoEnable=0 就不会采样它）
                    m_aoDummyRt = new RenderTarget2D(1, 1, 1, ColorFormat.Rgba8888, DepthFormat.None);
                }
                if (m_aoSampler == null) {
                    m_aoSampler = new SamplerState {
                        AddressModeU = TextureAddressMode.Clamp,
                        AddressModeV = TextureAddressMode.Clamp,
                        FilterMode = TextureFilterMode.Point,
                        MaxLod = 0f
                    };
                }
                pEnable.SetValue(on ? 1f : 0f);
                shader.GetParameter("u_aoDepth", true).SetValue(on ? (Texture2D)ScreenDepthRt : m_aoDummyRt);
                shader.GetParameter("u_aoSampler", true).SetValue(m_aoSampler);
                shader.GetParameter("u_aoInvViewProj", true).SetValue(ScreenDepthInvViewProjection);
                shader.GetParameter("u_aoViewProj", true).SetValue(ScreenDepthViewProjection);
                shader.GetParameter("u_aoOrigin", true).SetValue(ScreenDepthOrigin);
                shader.GetParameter("u_aoScale", true).SetValue(ScreenDepthScaleMetres);
                shader.GetParameter("u_aoUvScale", true).SetValue(ScreenAoUvScale());
                shader.GetParameter("u_aoRadius", true).SetValue(Math.Clamp(ScreenAoRadius, 0.05f, 8f));
                shader.GetParameter("u_aoIntensity", true).SetValue(Math.Clamp(ScreenAoIntensity, 0f, 1f));
                shader.GetParameter("u_aoBias", true).SetValue(Math.Clamp(ScreenAoBias, -1f, 1f));
                shader.GetParameter("u_aoDebug", true).SetValue((float)Math.Clamp(ScreenAoDebugMode, 0, 3));
                RenderTarget2D depthRt = ScreenDepthRt;
                shader.GetParameter("u_aoTexel", true).SetValue(depthRt == null
                    ? new Vector2(1f, 1f)
                    : new Vector2(1f / Math.Max(depthRt.Width, 1), 1f / Math.Max(depthRt.Height, 1)));
                shader.GetParameter("u_aoMaxUv", true).SetValue(Math.Clamp(ScreenAoMaxUv, 0.005f, 0.5f));
                shader.GetParameter("u_aoFlipV", true).SetValue(ScreenAoFlipV > 0.5f ? 1f : 0f);
                m_aoBinds++;
                m_aoLastError = "";
                return "";
            }
            catch (Exception e) {
                m_aoLastErrorBinds++;
                m_aoLastError = $"{e.GetType().Name}: {e.Message}";
                return m_aoLastError;
            }
        }

        /// <summary>
        /// uv 偏移换算：`offUv = (世界半径 / 距离) × (1 / (2·tan(fovY/2))) × 0.5`，
        /// 而引擎透视矩阵的 `M22 = 1/tan(fovY/2)`（与 `SkylineGBuffer.ExtendFarPlane` 同一套推导）。
        /// </summary>
        static float ScreenAoUvScale() {
            try {
                Camera camera = GetCamera();
                if (camera != null) {
                    return MathF.Abs(camera.ProjectionMatrix.M22) * 0.5f;
                }
            }
            catch (Exception) {
            }
            return 0.5f;
        }
    }
}
