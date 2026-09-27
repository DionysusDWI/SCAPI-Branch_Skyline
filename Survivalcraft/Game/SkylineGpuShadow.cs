using System;
using System.IO;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.32：**GPU 阴影贴图第一步 —— 自编译深度 shader + 太阳视角深度图（含回读自检）**。
    /// [v0.1.35] 深度编码升级为 **16 bit 双通道（R=高字节, G=低字节）**：竖直量化步长由 depthMax/255
    /// （4096 m 时 ≈16.1 m）降到 depthMax/65535（≈0.06 m）—— 8 bit 下"2 m 矮墙的投影会被量化吃掉"，
    /// 16 bit 才能支撑细粒度自阴影；`GpuShadowDepth16=false` 时逐位回退旧 8 bit 编码。
    ///
    /// 里程碑 5 路线：只读网格访问器 v0.1.11 → 太阳视角 pass v0.1.18（用游戏地形 shader 画"光照图"）→
    /// LOD 自阴影 v0.1.19 → 地形顶点阴影 v0.1.30 → **GPU 深度图（本版）** → 下一步：把深度图采样进着色。
    ///
    /// 关键实现点：
    ///   * **自带 shader 源码**：`Engine.Graphics.Shader` 可直接用字符串构造
    ///     （`new Shader(vsh, psh, macros)`；`ShaderCodeManager.GetFast` 只是"从 Content 取文本"的途径之一），
    ///     因此**无需改 Content.zip** 就能加自定义 pass；
    ///   * 顶点格式与游戏地形一致（`TerrainVertex`：POSITION/TEXCOORD/COLOR），直接画 LOD 粗+细网格；
    ///   * 深度定义**与矩阵约定无关**：`d = dot(worldPos − eye, sunDir)`（沿太阳视线的距离），
    ///     归一化到 0..1 写进颜色通道 —— CPU 侧可用同一公式复核，避免 v0.1.18 遇到的"矩阵自带平移"类坑；
    ///   * 验证三件套：覆盖率、深度 min/max/mean、**深度序自检**（高处单元必须比低处更接近太阳）。
    ///
    /// 默认关闭（`skyline.GpuShadowEnabled`）；`skyline.GpuShadowCapture()` 取证，
    /// `skyline.GpuShadowSavePng()` 存到 `ScreenCapture/`。
    /// </summary>
    public static partial class SkylineRuntime {
        public static bool GpuShadowEnabled { get; set; }

        public static int GpuShadowSize { get; set; } = 1024;

        public static float GpuShadowRadius { get; set; } = 512f;

        /// <summary>[v0.1.33] 是否把**真实区块几何**也画进同一张深度图（默认开）。
        /// v0.1.32 只画 LOD 网格，因此近景（≤视距+8 m，LOD 刻意跳过）是空洞。</summary>
        public static bool GpuShadowIncludeChunks { get; set; } = true;

        /// <summary>[v0.1.35] 深度图编码：true = 16 bit 双通道（R 高字节 / G 低字节，蓝通道仍存 8 bit 预览），
        /// false = 旧 8 bit（R=G=B=depth）。采样侧按**捕获时**的编码解码。</summary>
        public static bool GpuShadowDepth16 { get; set; } = true;

        /// <summary>[v0.1.36] 把**alpha-tested 几何**（子集 5：树叶、草、栅栏这类镂空方块）也画进深度图。
        /// 需要采样地形贴图并按 alpha&lt;0.5 丢弃，否则树叶会投出"整块方盒"的假阴影。</summary>
        public static bool GpuShadowIncludeAlphaTested { get; set; } = true;

        /// <summary>[v0.1.38] 近/远两级级联：近场用**更小的正交盒**（texel 更细）投接触阴影，
        /// 近盒之外回退到远图。关掉即回到 v0.1.37 的单图行为。</summary>
        public static bool GpuShadowCascadeEnabled { get; set; } = true;

        /// <summary>[v0.1.38] 近图半径（米）。texel = 2*半径/尺寸：128 m/1024² = 0.25 m/texel，
        /// 是远图（512 m/1024² = 1 m/texel）的 4 倍细。</summary>
        public static float GpuShadowNearRadius { get; set; } = 128f;

        /// <summary>[v0.1.65] **太阳追踪**：太阳方向相对上次捕获转过这个角度就自动重捕一次深度图
        /// （默认 10°；0 或负数 = 不自动重捕）。1200 s 一天下 10° ≈ 33 s 一次。
        /// 自动重捕走**不读回**的轻量路径（`diagnostics skipped`），因为读回 1024² 才是那 ~90 ms 的大头。</summary>
        public static float GpuShadowSunRecaptureDegrees { get; set; } = 10f;

        const string GpuShadowVsh = @"#ifdef HLSL

float2 u_origin;
float4x4 u_viewProjectionMatrix;
float3 u_eye;
float3 u_sunDir;
float u_depthMax;

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float v_depth : TEXCOORD0,
	out float4 sv_position: SV_POSITION
)
{
	float3 shifted = float3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	float d = dot(u_eye - a_position, u_sunDir);          // 沿太阳视线的深度（eye 处 = 0，远离太阳 = 更大）
	v_depth = saturate(d / max(u_depthMax, 0.0001));
	sv_position = mul(float4(shifted, 1.0), u_viewProjectionMatrix);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_eye;
uniform vec3 u_sunDir;
uniform float u_depthMax;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying float v_depth;

void main()
{
	vec3 shifted = vec3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	float d = dot(u_eye - a_position, u_sunDir);          // 沿太阳视线的深度（eye 处 = 0，远离太阳 = 更大）
	v_depth = clamp(d / max(u_depthMax, 0.0001), 0.0, 1.0);
	gl_Position = u_viewProjectionMatrix * vec4(shifted, 1.0);
	OPENGL_POSITION_FIX;
}

#endif
";

        const string GpuShadowPsh = @"#ifdef HLSL

float u_depth16;

void main(
	in float v_depth : TEXCOORD0,
	out float4 svTarget : SV_TARGET
)
{
	float d = saturate(v_depth);
	if (u_depth16 > 0.5)
	{
		float d16 = floor(d * 65535.0 + 0.5);
		float hi = floor(d16 / 256.0);
		float lo = d16 - hi * 256.0;
		svTarget = float4(hi / 255.0, lo / 255.0, d, 1.0);
	}
	else
	{
		svTarget = float4(d, d, d, 1.0);
	}
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;   // [v0.1.35] 16 bit 编码需要 fp32 才不丢低位（mediump 只有 ~10 bit 尾数）
#endif

uniform float u_depth16;

varying float v_depth;

void main()
{
	float d = clamp(v_depth, 0.0, 1.0);
	if (u_depth16 > 0.5)
	{
		float d16 = floor(d * 65535.0 + 0.5);
		float hi = floor(d16 / 256.0);
		float lo = d16 - hi * 256.0;
		gl_FragColor = vec4(hi / 255.0, lo / 255.0, d, 1.0);
	}
	else
	{
		gl_FragColor = vec4(d, d, d, 1.0);
	}
}

#endif
";

        // ============================================================================================
        // [v0.1.36] alpha-tested 深度变体：结构与 GpuShadowVsh/Psh 相同，多输出 v_texcoord，
        // 片元按贴图 alpha < u_alphaThreshold 丢弃 —— 树叶/草这类镂空方块才能投出"镂空"阴影，
        // 而不是整块方盒阴影（Game 的 alpha-tested 子集是 5，见 TerrainRenderer.DrawAlphaTested 的 subsetsMask=32）。
        // ============================================================================================
        const string GpuShadowAlphaVsh = @"#ifdef HLSL

float2 u_origin;
float4x4 u_viewProjectionMatrix;
float3 u_eye;
float3 u_sunDir;
float u_depthMax;

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float v_depth : TEXCOORD0,
	out float2 v_texcoord : TEXCOORD1,
	out float4 sv_position: SV_POSITION
)
{
	float3 shifted = float3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	float d = dot(u_eye - a_position, u_sunDir);
	v_depth = saturate(d / max(u_depthMax, 0.0001));
	v_texcoord = a_texcoord;
	sv_position = mul(float4(shifted, 1.0), u_viewProjectionMatrix);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_eye;
uniform vec3 u_sunDir;
uniform float u_depthMax;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying float v_depth;
varying vec2 v_texcoord;

void main()
{
	vec3 shifted = vec3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	float d = dot(u_eye - a_position, u_sunDir);
	v_depth = clamp(d / max(u_depthMax, 0.0001), 0.0, 1.0);
	v_texcoord = a_texcoord;
	gl_Position = u_viewProjectionMatrix * vec4(shifted, 1.0);
	OPENGL_POSITION_FIX;
}

#endif
";

        const string GpuShadowAlphaPsh = @"#ifdef HLSL

Texture2D u_texture;
SamplerState u_samplerState;
float u_alphaThreshold;

void main(
	in float v_depth : TEXCOORD0,
	in float2 v_texcoord : TEXCOORD1,
	out float4 svTarget : SV_TARGET
)
{
	if (u_texture.Sample(u_samplerState, v_texcoord).a < u_alphaThreshold)
	{
		discard;
	}
	svTarget = float4(v_depth, v_depth, v_depth, 1.0);
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;
#endif

// <Sampler Name='u_samplerState' Texture='u_texture' />

uniform sampler2D u_texture;
uniform float u_alphaThreshold;

varying float v_depth;
varying vec2 v_texcoord;

void main()
{
	if (texture2D(u_texture, v_texcoord).a < u_alphaThreshold)
	{
		discard;
	}
	gl_FragColor = vec4(v_depth, v_depth, v_depth, 1.0);
}

#endif
";

        static Shader m_gpuShadowShader;
        static Shader m_gpuShadowAlphaShader;
        static SamplerState m_gpuShadowAlphaSampler;
        static RenderTarget2D m_gpuShadowRt;
        static RenderTarget2D m_gpuShadowRtNear;
        static string m_gpuShadowLast = "(never captured)";
        static int m_gpuShadowCaptures;
        // [v0.1.65] 太阳追踪的自动重捕获（轻量：不读回深度图）
        static bool m_gpuShadowSkipDiagnostics;
        static long m_gpuShadowAutoCaptures;

        // [v0.1.34] 深度图相机参数（采样侧用；每次 Capture 刷新）
        static Matrix m_gpuShadowViewProjection;
        static Vector2 m_gpuShadowOrigin;
        static Vector3 m_gpuShadowEye;
        static Vector3 m_gpuShadowSun = Vector3.UnitY;
        static float m_gpuShadowDepthMax = 4096f;
        static bool m_gpuShadowHasMap;
        // [v0.1.38] 近图（级联）参数
        static Matrix m_gpuShadowViewProjectionNear;
        static Vector2 m_gpuShadowOriginNear;
        static Vector3 m_gpuShadowEyeNear;
        static float m_gpuShadowDepthMaxNear = 1024f;
        static bool m_gpuShadowHasNearMap;
        // [v0.1.35] 最近一次捕获用的深度编码（采样侧据此解码，保证"图 ↔ 解码"一致）
        static bool m_gpuShadowDepth16AtCapture;

        public static string GpuShadowDescribe() =>
            $"gpuShadow enabled={GpuShadowEnabled} size={GpuShadowSize} radius={GpuShadowRadius:0} "
            + $"depth16={GpuShadowDepth16} alphaTested={GpuShadowIncludeAlphaTested} "
            + $"cascade={GpuShadowCascadeEnabled} nearRadius={(GpuShadowCascadeEnabled ? GpuShadowNearRadius : 0):0} "
            + $"captures={m_gpuShadowCaptures} last={m_gpuShadowLast}";

        /// <summary>[v0.1.38] 渲染一张太阳视角深度图（LOD 网格 + 真实区块不透明/alpha-tested 子集）。
        /// 同时把该图自己的相机参数写回 out 参数（采样侧要用"捕获时"的矩阵/原点，见 notes/105）。</summary>
        static void RenderDepthMap(SubsystemTerrain subsystemTerrain, Camera camera, RenderTarget2D rt, int size,
                                   float radius, out Matrix viewProjectionShifted, out Vector2 origin2,
                                   out Vector3 eye, out Vector3 sun, out float depthMax,
                                   out int chunksDrawn, out int alphaChunksDrawn) {
            Shader shader = EnsureGpuShadowShader();
            Display.RenderTarget = rt;
            Display.Viewport = new Viewport(0, 0, size, size);
            Display.ScissorRectangle = new Rectangle(0, 0, size, size);
            Display.Clear(new Vector4(1f, 1f, 1f, 1f), 1f, 0);       // 背景 = 最远（depth 1）

            Vector3 center = camera.ViewPosition;
            // [v0.1.65] 太阳追踪：光源方向取**唯一的太阳真值**（与天上那个太阳同式），
            // 而不是游戏本体的固定常量 DirectionToLight1（它是 readonly 的，昼夜不动）。
            sun = SkylineRuntime.TrackedLightDirection();
            float distance = MathF.Max(radius * 4f, 512f);
            depthMax = distance * 2f;
            eye = center + sun * distance;
            Matrix view = Matrix.CreateLookAt(eye, center, Vector3.UnitY);
            Matrix projection = Matrix.CreateOrthographic(radius * 2f, radius * 2f, 1f, distance * 4f);
            Vector3 origin3 = new(MathF.Floor(center.X), 0f, MathF.Floor(center.Z));
            origin2 = new Vector2(origin3.X, origin3.Z);
            Matrix viewShifted = Matrix.CreateTranslation(origin3) * view;
            viewProjectionShifted = viewShifted * projection;
            shader.GetParameter("u_origin", true).SetValue(origin2);
            shader.GetParameter("u_viewProjectionMatrix", true).SetValue(viewProjectionShifted);
            shader.GetParameter("u_eye", true).SetValue(eye);
            shader.GetParameter("u_sunDir", true).SetValue(sun);
            shader.GetParameter("u_depthMax", true).SetValue(depthMax);
            shader.GetParameter("u_depth16", true).SetValue(GpuShadowDepth16 ? 1f : 0f);
            Display.BlendState = BlendState.Opaque;
            Display.DepthStencilState = DepthStencilState.Default;
            Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
            SkylineLod.DrawWithShader(shader);
            // [v0.1.33] 真实区块几何：与 LOD 共用同一个深度 shader / 同一张深度图（不透明子集 0..4）。
            chunksDrawn = 0;
            alphaChunksDrawn = 0;
            if (!GpuShadowIncludeChunks) {
                return;
            }
            // [v0.1.36] alpha-tested 子集（5）：树叶/草等镂空方块，按贴图 alpha 丢弃
            Shader alphaShader = null;
            if (GpuShadowIncludeAlphaTested) {
                alphaShader = EnsureGpuShadowAlphaShader();
                alphaShader.GetParameter("u_origin", true).SetValue(origin2);
                alphaShader.GetParameter("u_viewProjectionMatrix", true).SetValue(viewProjectionShifted);
                alphaShader.GetParameter("u_eye", true).SetValue(eye);
                alphaShader.GetParameter("u_sunDir", true).SetValue(sun);
                alphaShader.GetParameter("u_depthMax", true).SetValue(depthMax);
                alphaShader.GetParameter("u_alphaThreshold", true).SetValue(0.5f);
                alphaShader.GetParameter("u_texture", true)
                    .SetValue(subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                alphaShader.GetParameter("u_samplerState", true).SetValue(m_gpuShadowAlphaSampler);
            }
            int alphaChunks = 0;
            foreach (TerrainChunk chunk in subsystemTerrain.Terrain.AllocatedChunks) {
                if (chunk == null || chunk.State != TerrainChunkState.Valid || chunk.Buffers.Count == 0) {
                    continue;
                }
                DrawChunkSubsets(shader, chunk, 0x1F);
                chunksDrawn++;
                if (alphaShader != null) {
                    DrawChunkSubsets(alphaShader, chunk, 0x20);
                    alphaChunks++;
                }
            }
            if (alphaShader != null) {
                alphaChunksDrawn = alphaChunks;
            }
        }

        /// <summary>回读统计：非背景像素数（背景 = 纯白 65535/255）。</summary>
        static int CountCovered(RenderTarget2D rt, int size) {
            Image image = rt.GetData(new Rectangle(0, 0, size, size));
            bool depth16 = m_gpuShadowDepth16AtCapture;
            int backgroundCut = depth16 ? (65535 * 254 / 255) : 254;
            int covered = 0;
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    Color pixel = image.GetPixel(x, y);
                    int d = depth16 ? (pixel.R * 256 + pixel.G) : pixel.R;
                    if (d < backgroundCut) {
                        covered++;
                    }
                }
            }
            return covered;
        }

        static Shader EnsureGpuShadowShader() {
            if (m_gpuShadowShader == null) {
                m_gpuShadowShader = new Shader(GpuShadowVsh, GpuShadowPsh);
            }
            return m_gpuShadowShader;
        }

        static Shader EnsureGpuShadowAlphaShader() {
            if (m_gpuShadowAlphaShader == null) {
                m_gpuShadowAlphaShader = new Shader(GpuShadowAlphaVsh, GpuShadowAlphaPsh);
            }
            if (m_gpuShadowAlphaSampler == null) {
                m_gpuShadowAlphaSampler = new SamplerState {
                    AddressModeU = TextureAddressMode.Clamp,
                    AddressModeV = TextureAddressMode.Clamp,
                    FilterMode = TextureFilterMode.Point,
                    MaxLod = 0f
                };
            }
            return m_gpuShadowAlphaShader;
        }

        /// <summary>
        /// 渲染一次"太阳视角深度图"并回读自检。返回 JSON：
        /// { ok, size, radius, coverage, minDepth, maxDepth, meanDepth, selfCheckOk, samples:[...], ms }
        /// </summary>
        public static string GpuShadowCapture() {
            JsonObject result = new();
            if (!GpuShadowEnabled) {
                result["ok"] = false;
                result["err"] = "GpuShadowEnabled = false（默认关；先打开再做实验）";
                return result.ToJsonString();
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            Camera camera = GetCamera();
            if (subsystemTerrain?.Terrain == null || camera == null) {
                result["ok"] = false;
                result["err"] = "no terrain/camera";
                return result.ToJsonString();
            }
            int size = Math.Clamp(GpuShadowSize, 64, Math.Min(Display.MaxTextureSize, 4096));
            float radius = Math.Clamp(GpuShadowRadius, 32f, 4096f);
            // [v0.1.64] 采样侧要按**真实捕获几何**换算 PCF 的 texel 尺寸（配置值可能被 clamp 过）
            m_gpuShadowRadiusAtCapture = radius;
            m_gpuShadowSizeAtCapture = size;
            RenderTarget2D previousTarget = Display.RenderTarget;
            Viewport previousViewport = Display.Viewport;
            Rectangle previousScissor = Display.ScissorRectangle;
            double start = Time.RealTime;
            try {
                // ---- [v0.1.38] 远图（主图）----
                if (m_gpuShadowRt == null || m_gpuShadowRt.Width != size) {
                    Utilities.Dispose(ref m_gpuShadowRt);
                    m_gpuShadowRt = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                RenderDepthMap(subsystemTerrain, camera, m_gpuShadowRt, size, radius,
                    out Matrix viewProjectionShifted, out Vector2 origin2, out Vector3 eye, out Vector3 sun,
                    out float depthMax, out int chunksDrawn, out int alphaChunksDrawn);
                Vector3 origin3 = new(origin2.X, 0f, origin2.Y);
                m_gpuShadowViewProjection = viewProjectionShifted;
                m_gpuShadowOrigin = origin2;
                m_gpuShadowEye = eye;
                m_gpuShadowSun = sun;
                m_gpuShadowDepthMax = depthMax;
                m_gpuShadowHasMap = true;
                m_gpuShadowDepth16AtCapture = GpuShadowDepth16;

                // ---- [v0.1.38] 近图（级联）：更小的正交盒 → texel 更细，近场接触阴影更锐 ——
                float nearRadius = 0f;
                int nearCovered = 0;
                float nearStepMeters = 0f;
                if (GpuShadowCascadeEnabled) {
                    nearRadius = Math.Clamp(GpuShadowNearRadius, 16f, radius);
                    m_gpuShadowNearRadiusAtCapture = nearRadius;
                    if (m_gpuShadowRtNear == null || m_gpuShadowRtNear.Width != size) {
                        Utilities.Dispose(ref m_gpuShadowRtNear);
                        m_gpuShadowRtNear = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                    }
                    RenderDepthMap(subsystemTerrain, camera, m_gpuShadowRtNear, size, nearRadius,
                        out m_gpuShadowViewProjectionNear, out m_gpuShadowOriginNear, out m_gpuShadowEyeNear,
                        out _, out m_gpuShadowDepthMaxNear, out _, out _);
                    m_gpuShadowHasNearMap = true;
                    nearCovered = CountCovered(m_gpuShadowRtNear, size);
                    nearStepMeters = m_gpuShadowDepthMaxNear / (GpuShadowDepth16 ? 65535f : 255f);
                }
                else {
                    m_gpuShadowHasNearMap = false;
                }

                if (m_gpuShadowSkipDiagnostics) {
                    // [v0.1.65] **太阳追踪的自动重捕获**路径：不读回深度图、不做自检。
                    // 为什么必须分出来：读回 1024² 两遍（远图计数 + 近图计数）实测占掉 ~90 ms 里的大头，
                    // 那是"太阳一转过阈值就卡一下"的元凶；而跟踪太阳本来就不需要每帧统计覆盖率。
                    m_gpuShadowCaptures++;
                    m_gpuShadowAutoCaptures++;
                    m_gpuShadowLast =
                        $"gpuShadow(auto) size={size} radius={radius:0} nearRadius={nearRadius:0} "
                        + $"sun=({sun.X:0.###},{sun.Y:0.###},{sun.Z:0.###}) "
                        + $"captures={m_gpuShadowCaptures} auto={m_gpuShadowAutoCaptures} "
                        + $"ms={(Time.RealTime - start) * 1000.0:0.0} (diagnostics skipped)";
                    result["ok"] = true;
                    result["auto"] = true;
                    result["diagnostics"] = false;
                    result["size"] = size;
                    result["radius"] = radius;
                    result["sun"] = new JsonArray(sun.X, sun.Y, sun.Z);
                    result["chunksDrawn"] = chunksDrawn;
                    result["alphaChunksDrawn"] = alphaChunksDrawn;
                    result["ms"] = Math.Round((Time.RealTime - start) * 1000.0, 2);
                    return result.ToJsonString();
                }

                Image image = m_gpuShadowRt.GetData(new Rectangle(0, 0, size, size));
                // [v0.1.35] 16 bit 时按 R*256+G 解码（单位 0..65535）；8 bit 沿用 R（单位 0..255）。
                bool depth16 = m_gpuShadowDepth16AtCapture;
                int depthUnits = depth16 ? 65535 : 255;
                int backgroundCut = depth16 ? (65535 * 254 / 255) : 254;
                int covered = 0;
                double sumDepth = 0;
                int minDepth = depthUnits, maxDepth = 0;
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        Color pixel = image.GetPixel(x, y);
                        int d = depth16 ? (pixel.R * 256 + pixel.G) : pixel.R;
                        if (d < backgroundCut) {
                            covered++;
                            sumDepth += d;
                            if (d < minDepth) { minDepth = d; }
                            if (d > maxDepth) { maxDepth = d; }
                        }
                    }
                }
                // 深度序自检：取两列（测试墙附近的高处 + 平坦地面），投影同一点到深度图，
                // 校验"更高的点必须更接近太阳（深度更小）"，并与 CPU 公式（dot(world-eye,sun)/depthMax）对照。
                Terrain terrain = subsystemTerrain.Terrain;
                JsonArray samples = [];
                bool selfCheckOk = true;
                int checkedPairs = 0;
                // 采样点必须**确实在 LOD 网格里**：网格刻意跳过 ≤(视距+8 m) 的单元（近景走真实区块），
                // 又只覆盖"采集过的列"，所以直接从 LOD 的粗层单元里抽（150~400 m 环带）。
                // （2026-09-27 实测踩到两次：先用了近处点、又用了未加载的远点，都落在背景上。）
                Vector3 center = camera.ViewPosition;
                JsonArray probe = JsonNode.Parse(SkylineLod.ProbeCells(center.X, center.Z, 150f, 400f, 6)) as JsonArray
                    ?? [];
                foreach (JsonNode node in probe) {
                    int pcx = node["cx"].GetValue<int>();
                    int pcz = node["cz"].GetValue<int>();
                    int pcell = node["cellSize"].GetValue<int>();
                    int pheight = node["height"].GetValue<int>();
                    Vector3 world = new(pcx * pcell + pcell * 0.5f, pheight + 1.5f, pcz * pcell + pcell * 0.5f);
                    Vector4 clip = Vector4.Transform(new Vector4(world - origin3, 1f), viewProjectionShifted);
                    float sx = (clip.X / clip.W) * 0.5f + 0.5f;
                    float sy = 0.5f - (clip.Y / clip.W) * 0.5f;
                    // 视锥外（比如高得超出正交盒的高点）→ 记 outside，不参与判定（覆盖半径是配置项，不是缺陷）。
                    if (sx < 0f || sx > 1f || sy < 0f || sy > 1f) {
                        samples.Add(new JsonObject {
                            ["cx"] = pcx, ["cz"] = pcz, ["top"] = pheight, ["outside"] = true
                        });
                        continue;
                    }
                    int ix = Math.Clamp((int)(sx * size), 0, size - 1);
                    int iy = Math.Clamp((int)(sy * size), 0, size - 1);
                    int expected = (int)(MathUtils.Clamp(Vector3.Dot(eye - world, sun) / depthMax, 0f, 1f) * depthUnits);
                    int got = -1;
                    int bestErr = int.MaxValue;
                    // Y 轴方向（图像上下翻转）不确定 → 两个方向都搜；窗口 ±8 px 容忍投影/像素取整误差。
                    foreach (int flip in new[] { 0, 1 }) {
                        int cy = flip == 0 ? iy : size - 1 - iy;
                        for (int dy = -8; dy <= 8; dy++) {
                            for (int dx = -8; dx <= 8; dx++) {
                                int qx = Math.Clamp(ix + dx, 0, size - 1);
                                int qy = Math.Clamp(cy + dy, 0, size - 1);
                                Color area = image.GetPixel(qx, qy);
                                int v = depth16 ? (area.R * 256 + area.G) : area.R;
                                int err = Math.Abs(v - expected);
                                if (err < bestErr) { bestErr = err; got = v; }
                            }
                        }
                    }
                    samples.Add(new JsonObject {
                        ["cx"] = pcx, ["cz"] = pcz, ["top"] = pheight,
                        ["pixelX"] = ix, ["pixelY"] = iy,
                        ["expectedDepth"] = expected, ["mapDepth"] = got, ["absErr"] = bestErr
                    });
                    if (bestErr > 24) {
                        selfCheckOk = false;
                    }
                }
                // [v0.1.33] 近景自检：玩家周围几列的**真实地表顶面点**（v0.1.32 时它们会落在背景上，
                // 本版由"真实区块几何"覆盖，应命中）。
                (int x, int z)[] localPairs = [(4290, 9095), (4316, 9099), (4280, 9080), (4300, 9100)];
                foreach ((int lx, int lz) in localPairs) {
                    int top = terrain.GetTopHeight(lx, lz);
                    if (top <= TerrainChunk.MinHeight) {
                        continue;                                       // 未加载 → 跳过
                    }
                    Vector3 world = new(lx + 0.5f, top + 1.5f, lz + 0.5f);
                    Vector4 clip = Vector4.Transform(new Vector4(world - origin3, 1f), viewProjectionShifted);
                    float sx = (clip.X / clip.W) * 0.5f + 0.5f;
                    float sy = 0.5f - (clip.Y / clip.W) * 0.5f;
                    if (sx < 0f || sx > 1f || sy < 0f || sy > 1f) {
                        samples.Add(new JsonObject {
                            ["kind"] = "chunk",
                            ["x"] = lx, ["z"] = lz, ["top"] = top, ["outside"] = true
                        });
                        continue;
                    }
                    int ix = Math.Clamp((int)(sx * size), 0, size - 1);
                    int iy = Math.Clamp((int)(sy * size), 0, size - 1);
                    int expected = (int)(MathUtils.Clamp(Vector3.Dot(eye - world, sun) / depthMax, 0f, 1f) * depthUnits);
                    int got = -1;
                    int bestErr = int.MaxValue;
                    foreach (int flip in new[] { 0, 1 }) {
                        int cy = flip == 0 ? iy : size - 1 - iy;
                        for (int dy = -8; dy <= 8; dy++) {
                            for (int dx = -8; dx <= 8; dx++) {
                                int qx = Math.Clamp(ix + dx, 0, size - 1);
                                int qy = Math.Clamp(cy + dy, 0, size - 1);
                                Color local = image.GetPixel(qx, qy);
                                int v = depth16 ? (local.R * 256 + local.G) : local.R;
                                int err = Math.Abs(v - expected);
                                if (err < bestErr) { bestErr = err; got = v; }
                            }
                        }
                    }
                    samples.Add(new JsonObject {
                        ["kind"] = "chunk",
                        ["x"] = lx, ["z"] = lz, ["top"] = top,
                        ["pixelX"] = ix, ["pixelY"] = iy,
                        ["expectedDepth"] = expected, ["mapDepth"] = got, ["absErr"] = bestErr
                    });
                    if (bestErr > 24) {
                        selfCheckOk = false;
                    }
                }
                checkedPairs = samples.Count;
                m_gpuShadowCaptures++;
                m_gpuShadowLast =
                    $"gpuShadow size={size} radius={radius:0} depth16={depth16} "
                    + $"step={(depthMax / depthUnits):0.####}m covered={covered} "
                    + $"({100.0 * covered / (size * size):0.##}%) depth=[{minDepth},{maxDepth}] "
                    + $"chunks={chunksDrawn} alphaChunks={alphaChunksDrawn} "
                    + $"mean={(covered > 0 ? sumDepth / covered : 0):0.0} selfCheck={selfCheckOk} "
                    + $"captures={m_gpuShadowCaptures} ms={(Time.RealTime - start) * 1000.0:0.0}";
                result["ok"] = true;
                result["size"] = size;
                result["radius"] = radius;
                result["depth16"] = depth16;
                result["depthUnits"] = depthUnits;
                result["depthStepMeters"] = Math.Round(depthMax / depthUnits, 4);
                result["texelMeters"] = Math.Round((radius * 2.0) / size, 4);
                result["cascade"] = GpuShadowCascadeEnabled;
                if (GpuShadowCascadeEnabled) {
                    result["nearRadius"] = nearRadius;
                    result["nearCovered"] = nearCovered;
                    result["nearTexelMeters"] = Math.Round((nearRadius * 2.0) / size, 4);
                    result["nearDepthStepMeters"] = Math.Round(nearStepMeters, 5);
                }
                result["covered"] = covered;
                result["coverage"] = Math.Round((double)covered / (size * size), 5);
                result["chunksDrawn"] = chunksDrawn;
                result["alphaChunksDrawn"] = alphaChunksDrawn;
                result["minDepth"] = minDepth;
                result["maxDepth"] = maxDepth;
                result["meanDepth"] = Math.Round(covered > 0 ? sumDepth / covered : 0, 1);
                result["selfCheckOk"] = selfCheckOk;
                result["checkedSamples"] = checkedPairs;
                result["samples"] = samples;
                result["ms"] = Math.Round((Time.RealTime - start) * 1000.0, 2);
                result["note"] = "沿太阳视线的线性深度（0=最近,1=最远），用自编译 shader 画 LOD 网格";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
                m_gpuShadowLast = "gpuShadow failed: " + e.Message;
            }
            finally {
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
            return result.ToJsonString();
        }

        /// <summary>把最近一次深度图存成 PNG（`ScreenCapture/skyline-gpushadow-*.png`）。</summary>
        public static string GpuShadowSavePng() {
            JsonObject result = new();
            if (m_gpuShadowRt == null) {
                result["ok"] = false;
                result["err"] = "no depth map yet（先调 GpuShadowCapture）";
                return result.ToJsonString();
            }
            try {
                string dir = Path.Combine(Storage.GetSystemPath("app:/"), "ScreenCapture");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"skyline-gpushadow-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                using Stream stream = Storage.OpenFile(file, OpenFileMode.Create);
                Image image = m_gpuShadowRt.GetData(new Rectangle(0, 0, m_gpuShadowRt.Width, m_gpuShadowRt.Height));
                Image.Save(image, stream, ImageFileFormat.Png, false);
                result["ok"] = true;
                result["file"] = file;
                result["bytes"] = new FileInfo(file).Length;
                result["stats"] = m_gpuShadowLast;
            }
            catch (Exception e) {
               result["ok"] = false;
               result["err"] = e.Message;
           }
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.33] 把一个区块的指定子集掩码画进当前深度图。
        /// 逻辑与 `TerrainRenderer.DrawTerrainChunkGeometrySubsets(shader, chunk, mask, false)` 一致：
        /// 相邻子集的索引区间合并成一次 DrawIndexed（减少 draw call）。
        /// 子集约定：0..4 = 不透明（0x1F）、5 = alpha-tested（0x20）、6 = 透明（0x40）。
        /// </summary>
        static void DrawChunkSubsets(Shader shader, TerrainChunk chunk, int subsetMask) {
            foreach (TerrainChunkGeometry.Buffer buffer in chunk.Buffers) {
                int start = int.MaxValue;
                int end = 0;
                for (int i = 0; i < 8; i++) {
                    if (i < 7 && (subsetMask & (1 << i)) != 0) {
                        if (buffer.SubsetIndexBufferEnds[i] > 0) {
                            if (start == int.MaxValue) {
                                start = buffer.SubsetIndexBufferStarts[i];
                            }
                            end = buffer.SubsetIndexBufferEnds[i];
                        }
                    }
                    else {
                        if (end > start) {
                            Display.DrawIndexed(PrimitiveType.TriangleList, shader,
                                buffer.VertexBuffer, buffer.IndexBuffer, start, end - start);
                        }
                        start = int.MaxValue;
                    }
                }
            }
        }
    }
}
