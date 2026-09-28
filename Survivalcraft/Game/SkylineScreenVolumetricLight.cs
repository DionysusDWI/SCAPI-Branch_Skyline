using Engine;
using Engine.Graphics;
using Engine.Media;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// [v0.1.120] **屏幕空间体积光（Iris `lib/atmospherics/volumetricLight/volumetricLight.glsl` 的迁移）**。
    ///
    /// 为什么要有它（这是里程碑 4 里"最大的一块结构性缺口"）：
    ///   * 本分支原来的体积神光是**塞在不透明地形/远景 LOD 着色器里的 8 步伐进** ——
    ///     它是**按片元**算的，于是有两条硬边界：
    ///       ① **天空片元完全不吃雾**（天空穹顶是另一个着色器）⇒ 对着天空/地平线时**没有光轴**；
    ///       ② 只对"真的被画出来的几何"生效 ⇒ 覆盖取决于那层几何有没有走这段代码
    ///          （v0.1.117 的覆盖探针就是在这条上翻过车）。
    ///   * Iris 的做法是**全屏 pass**：用**场景深度纹理**重建每个像素的世界位置，
    ///     沿视线步进、每步查一次太阳阴影图，再把散射**加法**合成到画面。
    ///     我们的**相机空间深度预通道**（v0.1.113）已经把前置条件补齐了 ⇒ 本轮走正路。
    ///
    /// 判据与证据见 `heightlab/skyline-v0120-screen-volumetric-light.py` 与 `notes/218`。
    /// **默认关**（关掉时这一帧不画任何东西、逐位不变）。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>
        /// 总开关（默认关）。**打开时会自动把相机深度预通道也打开** —— 与 SSAO 同一口径：
        /// 本 pass 需要 `ScreenDepthReady`，否则"开了没反应"这种坑不必再让人踩一次。
        /// </summary>
        public static bool ScreenVolumetricLightEnabled {
            get => m_svdEnabled;
            set {
                m_svdEnabled = value;
                if (value) {
                    ScreenDepthEnabled = true;
                }
            }
        }

        static bool m_svdEnabled;

        /// <summary>步进次数（默认 12：近场 8 + 远场 4 的两段分布，与 Iris 同构）。</summary>
        public static int ScreenVolumetricSteps { get; set; } = 12;

        /// <summary>
        /// 强度（0..2）。**[v0.1.124] 0.8 → 0.3：按"最坏机位"重新标定**。
        ///
        /// 为什么改：`朝太阳时的平均亮度位移 ≤ 10/255` 这条判据**量的是沿视线的雾程积分**，
        /// 于是同一条 pass、同一个强度在三个机位上量出三个数（实测，全在 tod=0.30、太阳仰角 ~20°）：
        /// 雾程短的机位 **+2.25**（7.9% 像素）、v0.1.121 的机位 **+6.05**（27.2%）、
        /// 开阔地/长雾程机位 **+25.67**（67.3%，画面明显发白）。
        /// 0.8 是按中间那个定的 ⇒ 最坏机位会过火。这一版的默认值按**最坏机位**定：
        /// 加法亮度随强度近似线性（实测 0.50→+1.41、0.35→+0.97、0.25→+0.68 ⇒ 斜率 ~2.8/单位），
        /// 0.8 × (10 / 25.67) ≈ **0.31** ⇒ 当时取 **0.3**。
        ///
        /// **[v0.1.126] 0.3 → 0.22**：换机位又量到更亮的场景（0.3 → **+12.67/255**，超过 10/255 的判据），
        /// 说明"最坏机位"还在别处；把这一轮**两处最坏**一起算：+25.67@0.8（斜率 32/单位）、
        /// +12.67@0.3（斜率 42/单位）⇒ 要 ≤10 需要 **≤0.238**，取 **0.22**（该机位 ≈ +9.2）。
        /// 代价：典型机位只剩 ≈ +0.6/255 —— 这条 pass 本来就**默认关**、是观感选项，宁可淡不可爆。
        /// </summary>
        public static float ScreenVolumetricStrength { get; set; } = 0.22f;

        /// <summary>射线最远（米）：天空像素也走满这一段（Iris 的 `sky ? maxDistance : min(...)`）。</summary>
        public static float ScreenVolumetricMaxDistance { get; set; } = 256f;

        /// <summary>前向散射相位指数（越大越只在朝太阳时亮）。</summary>
        public static float ScreenVolumetricPhasePower { get; set; } = 8f;

        /// <summary>入射色（默认暖白，与太阳直射一致）。</summary>
        public static Vector3 ScreenVolumetricSunColor { get; set; } = new(1f, 0.93f, 0.80f);

        /// <summary>诊断：0 正常；1 把散射项直显成灰阶；2 把深度预通道直显成灰阶。</summary>
        public static int ScreenVolumetricDebugMode { get; set; }

        /// <summary>
        /// 深度图 v 翻转（默认 **0 = 不翻**）。为什么单独一项：内置的 `ScreenAoFlipV` 默认是 1，
        /// 但实测**用错方向时"天空那一半完全没有光"**（上半屏采到的是地形深度 ⇒ 射线很短），
        /// 所以本 pass 用独立开关并**按实测定 0/1**（判据见 `skyline-v0120-screen-volumetric-light.py`）。
        /// </summary>
        public static float ScreenVolumetricFlipV { get; set; }

        static Shader m_svdShader;
        static VertexBuffer m_svdQuad;
        static IndexBuffer m_svdQuadIndices;
        const int SvdQuadIndicesCount = 6;
        static SamplerState m_svdSampler;
        static string m_svdLastError = "";
        static long m_svdBound;

        public static string ScreenVolumetricLastError => m_svdLastError;
        public static long ScreenVolumetricBinds => m_svdBound;

        public static string ScreenVolumetricDescribe() => new JsonObject {
            ["enabled"] = ScreenVolumetricLightEnabled,
            ["ready"] = ScreenVolumetricLightEnabled && ScreenDepthReady && ShadowSampleReady,
            ["steps"] = ScreenVolumetricSteps,
            ["strength"] = (double)ScreenVolumetricStrength,
            ["maxDistance"] = (double)ScreenVolumetricMaxDistance,
            ["phasePower"] = (double)ScreenVolumetricPhasePower,
            ["debugMode"] = ScreenVolumetricDebugMode,
            ["depthReady"] = ScreenDepthReady,
            ["shadowReady"] = ShadowSampleReady,
            ["binds"] = m_svdBound,
            ["lastError"] = m_svdLastError,
            ["note"] = "全屏加法 pass：用相机深度图重建世界位置 + 每步查太阳阴影图；"
                     + "天空像素也走满 ScreenVolumetricMaxDistance（这是与旧的地形内 8 步路线最大的差别）"
        }.ToJsonString();

        /// <summary>
        /// 每帧由 `SubsystemTerrain.Draw` 在 **composite 阶段**（不透明 pass 全部结束、透明 pass 之前）调用。
        /// 任何异常只记日志并自动关掉本 pass，绝不打断帧（与 G-buffer / 阴影 / 深度预通道同一纪律）。
        /// </summary>
        public static void ScreenVolumetricLightPass(Camera camera) {
            if (!ScreenVolumetricLightEnabled || camera == null) {
                return;
            }
            if (!ScreenDepthReady || !ShadowSampleReady || ScreenDepthRt == null) {
                m_svdLastError = "depth prepass or shadow map not ready";
                return;
            }
            // ⚠️ **`Display.RenderTarget` 在 composite 阶段通常是 null**（引擎直接画后台缓冲）
            //   —— 第一版把它当"没有目标"直接返回，于是这一 pass 一次都没画过。
            //   正确做法：只在真的绑着 RT 时才按 RT 尺寸设视口；否则沿用调用方的视口/裁剪框
            //   （全屏四边形本身是 NDC，覆盖当前视口）。
            RenderTarget2D target = Display.RenderTarget;
            try {
                BlendState previousBlend = Display.BlendState;
                DepthStencilState previousDepth = Display.DepthStencilState;
                RasterizerState previousRasterizer = Display.RasterizerState;
                Viewport previousViewport = Display.Viewport;
                Rectangle previousScissor = Display.ScissorRectangle;
                try {
                    Display.BlendState = BlendState.Additive;
                    Display.DepthStencilState = DepthStencilState.None;
                    Display.RasterizerState = RasterizerState.CullNoneScissor;
                    // **视口/裁剪框必须显式设成全屏**：不设的话会沿用调用方此刻的状态，
                    // 而这一 pass 现在挂在 draw order 100（云与透明都画完之后），
                    // 上一步留下的**裁剪框可能只覆盖一部分屏幕** ⇒ 全屏四边形被裁掉一半
                    // （实测：上半屏一个字都没画到、信号 0 px）。
                    // 后台缓冲尺寸用 `Display.BackbufferSize`（`Display.RenderTarget == null` 时拿不到 RT 尺寸）。
                    Point2 targetSize = target != null
                        ? new Point2(target.Width, target.Height)
                        : Display.BackbufferSize;
                    Display.Viewport = new Viewport(0, 0, targetSize.X, targetSize.Y);
                    Display.ScissorRectangle = new Rectangle(0, 0, targetSize.X, targetSize.Y);
                    Shader shader = EnsureScreenVolumetricShader();
                    BindScreenVolumetric(shader, camera);
                    EnsureQuad();
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_svdQuad, m_svdQuadIndices,
                                        0, SvdQuadIndicesCount);
                    m_svdBound++;
                    m_svdLastError = "";
                }
                finally {
                    Display.BlendState = previousBlend;
                    Display.DepthStencilState = previousDepth;
                    Display.RasterizerState = previousRasterizer;
                    Display.Viewport = previousViewport;
                    Display.ScissorRectangle = previousScissor;
                }
            }
            catch (Exception e) {
                m_svdLastError = $"{e.GetType().Name}: {e.Message}";
                ScreenVolumetricLightEnabled = false;      // 自己关掉，绝不每帧抛
                Log.Warning($"SkylineScreenVolumetricLight: {m_svdLastError}");
            }
        }

        static Shader EnsureScreenVolumetricShader() {
            m_svdShader ??= new Shader(SvdVsh, SvdPsh);
            return m_svdShader;
        }

        static void EnsureQuad() {
            if (m_svdQuad != null) {
                return;
            }
            TerrainVertex[] vertices = new TerrainVertex[4];
            BlockGeometryGenerator.SetupVertex(-1f, -1f, 0f, Color.White, 0f, 1f, ref vertices[0]);
            BlockGeometryGenerator.SetupVertex(1f, -1f, 0f, Color.White, 1f, 1f, ref vertices[1]);
            BlockGeometryGenerator.SetupVertex(1f, 1f, 0f, Color.White, 1f, 0f, ref vertices[2]);
            BlockGeometryGenerator.SetupVertex(-1f, 1f, 0f, Color.White, 0f, 0f, ref vertices[3]);
            m_svdQuad = new VertexBuffer(TerrainVertex.VertexDeclaration, 4);
            m_svdQuad.SetData(vertices, 0, vertices.Length);
            short[] indices = [0, 1, 2, 0, 2, 3];
            m_svdQuadIndices = new IndexBuffer(IndexFormat.SixteenBits, indices.Length);
            m_svdQuadIndices.SetData(indices, 0, indices.Length);
        }

        static void BindScreenVolumetric(Shader shader, Camera camera) {
            if (m_svdSampler == null) {
                m_svdSampler = new SamplerState {
                    AddressModeU = TextureAddressMode.Clamp,
                    AddressModeV = TextureAddressMode.Clamp,
                    FilterMode = TextureFilterMode.Point,
                    MaxLod = 0f
                };
            }
            RefreshShadowDayFactor();
            shader.GetParameter("u_svdEnable", true).SetValue(1f);
            shader.GetParameter("u_svdDebug", true).SetValue((float)Math.Clamp(ScreenVolumetricDebugMode, 0, 2));
            shader.GetParameter("u_svdSteps", true).SetValue((float)Math.Clamp(ScreenVolumetricSteps, 4, 32));
            shader.GetParameter("u_svdStrength", true).SetValue(Math.Max(ScreenVolumetricStrength, 0f));
            shader.GetParameter("u_svdMaxDistance", true).SetValue(Math.Max(ScreenVolumetricMaxDistance, 16f));
            shader.GetParameter("u_svdPhasePower", true).SetValue(Math.Clamp(ScreenVolumetricPhasePower, 1f, 64f));
            shader.GetParameter("u_svdSunColor", true).SetValue(ScreenVolumetricSunColor
                * Math.Clamp(ShadowDayFactor, 0f, 1f));       // 没有太阳就没有光轴（与 v0.1.119 同一条规则）
            shader.GetParameter("u_svdViewPosition", true).SetValue(camera.ViewPosition);
            shader.GetParameter("u_svdViewProj", true).SetValue(ScreenDepthViewProjection);
            shader.GetParameter("u_svdInvViewProj", true).SetValue(ScreenDepthInvViewProjection);
            shader.GetParameter("u_svdOrigin", true).SetValue(ScreenDepthOrigin);
            shader.GetParameter("u_svdScale", true).SetValue(ScreenDepthScaleMetres);
            shader.GetParameter("u_svdFlipV", true).SetValue(ScreenVolumetricFlipV > 0.5f ? 1f : 0f);
            RenderTarget2D depthRt = ScreenDepthRt;
            shader.GetParameter("u_svdDepth", true).SetValue((Texture2D)depthRt);
            shader.GetParameter("u_svdSampler", true).SetValue(m_svdSampler);
            // 阴影（与体积神光同一条路线：同一张深度图、同一 bias、同一昼光因子）
            shader.GetParameter("u_svdShadow", true).SetValue(m_gpuShadowRt);
            shader.GetParameter("u_svdShadowSampler", true).SetValue(m_svdSampler);
            shader.GetParameter("u_svdSunViewProj", true).SetValue(m_gpuShadowViewProjection);
            shader.GetParameter("u_svdSunOrigin", true).SetValue(m_gpuShadowOrigin);
            shader.GetParameter("u_svdEye", true).SetValue(m_gpuShadowEye);
            shader.GetParameter("u_svdSunDir", true).SetValue(m_gpuShadowSun);
            shader.GetParameter("u_svdDepthMax", true).SetValue(m_gpuShadowDepthMax);
            shader.GetParameter("u_svdShadowBias", true).SetValue(GpuShadowSampleBias);
            shader.GetParameter("u_svdShadowDepth16", true).SetValue(m_gpuShadowDepth16AtCapture ? 1f : 0f);
            shader.GetParameter("u_svdShadowFlipY", true).SetValue(GpuShadowFlipY ? 1f : 0f);
            // 雾（与自研体积雾同一套：这是迁移时"密度场复用"的关键，见 notes/218）
            float fogBottom = Math.Min(FogBottomY, FogTopY - 1f);
            shader.GetParameter("u_svdFogBottom", true).SetValue(fogBottom);
            shader.GetParameter("u_svdFogTop", true).SetValue(Math.Max(FogTopY, fogBottom + 1f));
            shader.GetParameter("u_svdFogDensity", true).SetValue(Math.Max(FogDensity, 0f));
            shader.GetParameter("u_svdFogBase", true).SetValue(Math.Clamp(FogBaseDensity, 0f, 1f));
            shader.GetParameter("u_svdFogThreshold", true).SetValue(Math.Clamp(FogThreshold, 0f, 0.99f));
            shader.GetParameter("u_svdFogScale", true).SetValue(Math.Max(FogScale, 1e-6f));
            shader.GetParameter("u_svdFogWind", true).SetValue(FogWind * (float)Time.RealTime);
            shader.GetParameter("u_svdFogShear", true).SetValue(Math.Max(FogHeightShear, 0f));
            shader.GetParameter("u_svdFogStrength", true).SetValue(Math.Clamp(FogStrength, 0f, 1f));
        }

        const string SvdVsh = @"#ifdef HLSL

void main(
	in float3 a_position: POSITION,
	in float2 a_texcoord: TEXCOORD,
	out float2 v_texcoord : TEXCOORD,
	out float4 sv_position: SV_POSITION
)
{
	v_texcoord = a_texcoord;
	sv_position = float4(a_position.xy, 0.0, 1.0);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

precision highp float;

attribute vec3 a_position;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;

void main()
{
	v_texcoord = a_texcoord;
	gl_Position = vec4(a_position.xy, 0.0, 1.0);
}

#endif
";

        const string SvdPsh = @"#ifdef HLSL

Texture2D u_svdDepth;
SamplerState u_svdSampler;
Texture2D u_svdShadow;
SamplerState u_svdShadowSampler;
float u_svdEnable;
float u_svdDebug;
float u_svdSteps;
float u_svdStrength;
float u_svdMaxDistance;
float u_svdPhasePower;
float3 u_svdSunColor;
float3 u_svdViewPosition;
float4x4 u_svdViewProj;
float4x4 u_svdInvViewProj;
float2 u_svdOrigin;
float u_svdScale;
float u_svdFlipV;
float4x4 u_svdSunViewProj;
float2 u_svdSunOrigin;
float3 u_svdEye;
float3 u_svdSunDir;
float u_svdDepthMax;
float u_svdShadowBias;
float u_svdShadowDepth16;
float u_svdShadowFlipY;
float u_svdFogBottom;
float u_svdFogTop;
float u_svdFogDensity;
float u_svdFogBase;
float u_svdFogThreshold;
float u_svdFogScale;
float2 u_svdFogWind;
float u_svdFogShear;
float u_svdFogStrength;

float svdHash12(float2 p)
{
	float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, float3(p3.y, p3.z, p3.x) + 33.33);
	return frac((p3.x + p3.y) * p3.z);
}

float svdNoise2(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = svdHash12(i);
	float b = svdHash12(i + float2(1.0, 0.0));
	float c = svdHash12(i + float2(0.0, 1.0));
	float d = svdHash12(i + float2(1.0, 1.0));
	return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float svdDepthAt(float2 uv)
{
	float4 s = u_svdDepth.Sample(u_svdSampler, uv);
	return (floor(s.r * 255.0 + 0.5) * 256.0 + floor(s.g * 255.0 + 0.5)) / 65535.0;
}

float3 svdWorldAt(float2 uv, float linearDist)
{
	float2 ndc = uv * 2.0 - 1.0;
	float4 p0 = mul(u_svdInvViewProj, float4(ndc, 0.0, 1.0));
	float4 p1 = mul(u_svdInvViewProj, float4(ndc, 1.0, 1.0));
	p0 /= p0.w;
	p1 /= p1.w;
	float3 near = p0.xyz + float3(u_svdOrigin.x, 0.0, u_svdOrigin.y);
	float3 dir = normalize(p1.xyz - p0.xyz);
	return near + dir * linearDist;
}

float svdDensityAt(float3 p)
{
	float h = (p.y - u_svdFogBottom) / max(u_svdFogTop - u_svdFogBottom, 0.001);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.15, h) * smoothstep(1.0, 0.7, h);
	float2 q = (p.xz + p.y * u_svdFogShear * float2(1.7, 1.1)) * u_svdFogScale + u_svdFogWind;
	float n = svdNoise2(q) * 0.7 + svdNoise2(q * 2.3 + float2(5.1, 9.7)) * 0.3;
	return max(u_svdFogBase, max(0.0, n - u_svdFogThreshold)) * prof;
}

float svdShadowUvY(float y)
{
	return lerp(y, 1.0 - y, u_svdShadowFlipY);
}

float svdDecodeShadow(float4 texel)
{
	if (u_svdShadowDepth16 > 0.5)
	{
		float hi = floor(texel.r * 255.0 + 0.5);
		float lo = floor(texel.g * 255.0 + 0.5);
		return (hi * 256.0 + lo) / 65535.0;
	}
	return texel.r;
}

float svdSunVisibility(float3 p)
{
	float3 rel = float3(p.x - u_svdSunOrigin.x, p.y, p.z - u_svdSunOrigin.y);
	float4 clip = mul(float4(rel, 1.0), u_svdSunViewProj);
	if (clip.w <= 0.001)
	{
		return 1.0;
	}
	float2 uv = clip.xy / clip.w * 0.5 + 0.5;
	uv.y = svdShadowUvY(uv.y);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
	{
		return 1.0;                                  // 阴影图之外：当作被太阳照到（与体积神光同一口径）
	}
	float depth = saturate(dot(u_svdEye - p, u_svdSunDir) / max(u_svdDepthMax, 0.0001));
	float map = svdDecodeShadow(u_svdShadow.Sample(u_svdShadowSampler, uv));
	return depth <= map + u_svdShadowBias ? 1.0 : 0.0;
}

void main(
	in float2 v_texcoord : TEXCOORD,
	out float4 svTarget : SV_TARGET0
)
{
	float2 uv = v_texcoord;
	uv.y = lerp(uv.y, 1.0 - uv.y, u_svdFlipV);
	float d = svdDepthAt(uv);
	float3 far = svdWorldAt(uv, u_svdMaxDistance);
	float3 worldPos = far;
	if (d < 0.999)
	{
		worldPos = svdWorldAt(uv, d * u_svdScale);
	}
	float3 delta = worldPos - u_svdViewPosition;
	float len = length(delta);
	float3 rd = len > 0.001 ? delta / len : float3(0.0, 0.0, -1.0);
	float rayEnd = min(len, u_svdMaxDistance);
	if (u_svdDebug > 1.5)
	{
		svTarget = float4(d, d, d, 1.0);
		return;
	}
	float od = 0.0;
	float scat = 0.0;
	float steps = max(u_svdSteps, 1.0);
	float ign = frac(52.9829189 * frac(dot(floor(uv * 1024.0), float2(0.06711056, 0.00583715))));
	float lastT = 0.0;
	for (int i = 0; i < 32; i++)
	{
		if (float(i) >= steps)
		{
			break;
		}
		float t = pow((float(i) + ign) / steps, 2.0) * rayEnd;
		if (t <= lastT)
		{
			continue;
		}
		float dt = t - lastT;
		lastT = t;
		float3 p = u_svdViewPosition + rd * t;
		float den = svdDensityAt(p);
		od += den * dt;
		scat += den * dt * svdSunVisibility(p);
	}
	float lit = scat / max(od, 1e-5);
	float alpha = (1.0 - exp(-od * u_svdFogDensity)) * u_svdFogStrength;
	float phase = 0.25 + 0.75 * pow(saturate(dot(rd, u_svdSunDir)), u_svdPhasePower);
	float3 color = u_svdSunColor * (lit * alpha * phase * u_svdStrength);
	if (u_svdDebug > 0.5)
	{
		svTarget = float4(min(od * 0.05, 1.0), lit, min(rayEnd / 256.0, 1.0), 1.0);
		return;
	}
	svTarget = float4(color.rgb, alpha);
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;
#endif

// <Sampler Name='u_svdSampler' Texture='u_svdDepth' />
// <Sampler Name='u_svdShadowSampler' Texture='u_svdShadow' />

uniform sampler2D u_svdDepth;
uniform sampler2D u_svdShadow;
uniform float u_svdEnable;
uniform float u_svdDebug;
uniform float u_svdSteps;
uniform float u_svdStrength;
uniform float u_svdMaxDistance;
uniform float u_svdPhasePower;
uniform vec3 u_svdSunColor;
uniform vec3 u_svdViewPosition;
uniform mat4 u_svdViewProj;
uniform mat4 u_svdInvViewProj;
uniform vec2 u_svdOrigin;
uniform float u_svdScale;
uniform float u_svdFlipV;
uniform mat4 u_svdSunViewProj;
uniform vec2 u_svdSunOrigin;
uniform vec3 u_svdEye;
uniform vec3 u_svdSunDir;
uniform float u_svdDepthMax;
uniform float u_svdShadowBias;
uniform float u_svdShadowDepth16;
uniform float u_svdShadowFlipY;
uniform float u_svdFogBottom;
uniform float u_svdFogTop;
uniform float u_svdFogDensity;
uniform float u_svdFogBase;
uniform float u_svdFogThreshold;
uniform float u_svdFogScale;
uniform vec2 u_svdFogWind;
uniform float u_svdFogShear;
uniform float u_svdFogStrength;

varying vec2 v_texcoord;

float svdHash12(vec2 p)
{
	vec3 p3 = fract(vec3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, vec3(p3.y, p3.z, p3.x) + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float svdNoise2(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = svdHash12(i);
	float b = svdHash12(i + vec2(1.0, 0.0));
	float c = svdHash12(i + vec2(0.0, 1.0));
	float d = svdHash12(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float svdDepthAt(vec2 uv)
{
	vec4 s = texture2D(u_svdDepth, uv);
	return (floor(s.r * 255.0 + 0.5) * 256.0 + floor(s.g * 255.0 + 0.5)) / 65535.0;
}

vec3 svdWorldAt(vec2 uv, float linearDist)
{
	vec2 ndc = uv * 2.0 - 1.0;
	vec4 p0 = u_svdInvViewProj * vec4(ndc, 0.0, 1.0);
	vec4 p1 = u_svdInvViewProj * vec4(ndc, 1.0, 1.0);
	p0 /= p0.w;
	p1 /= p1.w;
	vec3 near = p0.xyz + vec3(u_svdOrigin.x, 0.0, u_svdOrigin.y);
	vec3 dir = normalize(p1.xyz - p0.xyz);
	return near + dir * linearDist;
}

float svdDensityAt(vec3 p)
{
	float h = (p.y - u_svdFogBottom) / max(u_svdFogTop - u_svdFogBottom, 0.001);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.15, h) * smoothstep(1.0, 0.7, h);
	vec2 q = (p.xz + p.y * u_svdFogShear * vec2(1.7, 1.1)) * u_svdFogScale + u_svdFogWind;
	float n = svdNoise2(q) * 0.7 + svdNoise2(q * 2.3 + vec2(5.1, 9.7)) * 0.3;
	return max(u_svdFogBase, max(0.0, n - u_svdFogThreshold)) * prof;
}

float svdSunVisibility(vec3 p)
{
	vec3 rel = vec3(p.x - u_svdSunOrigin.x, p.y, p.z - u_svdSunOrigin.y);
	vec4 clip = u_svdSunViewProj * vec4(rel, 1.0);
	if (clip.w <= 0.001)
	{
		return 1.0;
	}
	vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
	uv.y = mix(uv.y, 1.0 - uv.y, u_svdShadowFlipY);
	if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
	{
		return 1.0;
	}
	float depth = clamp(dot(u_svdEye - p, u_svdSunDir) / max(u_svdDepthMax, 0.0001), 0.0, 1.0);
	vec4 texel = texture2D(u_svdShadow, uv);
	float map = (u_svdShadowDepth16 > 0.5)
		? (floor(texel.r * 255.0 + 0.5) * 256.0 + floor(texel.g * 255.0 + 0.5)) / 65535.0
		: texel.r;
	return depth <= map + u_svdShadowBias ? 1.0 : 0.0;
}

void main()
{
	vec2 uv = v_texcoord;
	uv.y = mix(uv.y, 1.0 - uv.y, u_svdFlipV);
	float d = svdDepthAt(uv);
	vec3 far = svdWorldAt(uv, u_svdMaxDistance);
	vec3 worldPos = far;
	if (d < 0.999)
	{
		worldPos = svdWorldAt(uv, d * u_svdScale);
	}
	vec3 delta = worldPos - u_svdViewPosition;
	float len = length(delta);
	vec3 rd = len > 0.001 ? delta / len : vec3(0.0, 0.0, -1.0);
	float rayEnd = min(len, u_svdMaxDistance);
	if (u_svdDebug > 1.5)
	{
		gl_FragColor = vec4(d, d, d, 1.0);
		return;
	}
	float od = 0.0;
	float scat = 0.0;
	float steps = max(u_svdSteps, 1.0);
	float ign = fract(52.9829189 * fract(dot(floor(uv * 1024.0), vec2(0.06711056, 0.00583715))));
	float lastT = 0.0;
	for (int i = 0; i < 32; i++)
	{
		if (float(i) >= steps)
		{
			break;
		}
		float t = pow((float(i) + ign) / steps, 2.0) * rayEnd;
		if (t <= lastT)
		{
			continue;
		}
		float dt = t - lastT;
		lastT = t;
		vec3 p = u_svdViewPosition + rd * t;
		float den = svdDensityAt(p);
		od += den * dt;
		scat += den * dt * svdSunVisibility(p);
	}
	float lit = scat / max(od, 1e-5);
	float alpha = (1.0 - exp(-od * u_svdFogDensity)) * u_svdFogStrength;
	float phase = 0.25 + 0.75 * pow(clamp(dot(rd, u_svdSunDir), 0.0, 1.0), u_svdPhasePower);
	vec3 color = u_svdSunColor * (lit * alpha * phase * u_svdStrength);
	if (u_svdDebug > 0.5)
	{
		gl_FragColor = vec4(min(od * 0.05, 1.0), lit, min(rayEnd / 256.0, 1.0), 1.0);
		return;
	}
	gl_FragColor = vec4(color, alpha);
}

#endif
";
    }
}
