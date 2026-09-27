using Engine;
using Engine.Graphics;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.34：**阴影贴图采样收口** —— 不透明 pass 使用"地形 shader + 阴影采样"变体，
    /// 让**真实地形**吃到 v0.1.32/33 生成的太阳深度图：
    /// 把片元的线性深度与深度图里同一点的值比较（mapDepth + bias &lt; fragDepth 即判为遮挡），
    /// 命中则把颜色乘 (1 − strength)。线性深度 = dot(eye − worldPos, sunDir) / depthMax。
    ///
    /// 变体 shader = 游戏自己的 `Opaque.vsh/psh` 结构 + 阴影采样（同样支持雾/顶点色/贴图），
    /// 由 `TerrainRenderer.DrawOpaque` 在启用时替代 `m_opaqueShader`（其余参数与绘制流程完全一致）。
    ///
    /// 开关：`skyline.GpuShadowSampleEnabled`（默认关；打开后 `GpuShadowTick` 会自动补一次 `GpuShadowCapture`）、
    /// `GpuShadowSampleStrength`（0.45）、`GpuShadowSampleBias`（0.0002）、`GpuShadowFlipY`（后端 UV 方向；
    /// 先用 A/B 定，默认 false）。诊断：`skyline.GpuShadowSampleDescribe()`。
    /// [v0.1.35] 深度图改为 **16 bit 双通道**（R 高字节 / G 低字节）后，采样侧按**捕获时**的编码解码；
    /// 量化步长 4096 m 范围下 16.1 m → 0.0625 m，才撑得住"米级矮墙的投影"。
    /// [v0.1.38] **近/远两级级联**：片元先投影进近图（默认半径 128 m → 0.25 m/texel），
    /// 命中近盒就用近图（接触阴影更锐），否则回退远图（512 m → 1 m/texel）。
    /// </summary>
    public static partial class SkylineRuntime {
        public static bool GpuShadowSampleEnabled { get; set; }

        public static float GpuShadowSampleStrength { get; set; } = 0.45f;

        /// <summary>深度比较偏置（归一化深度单位）。实测：0.004（≈16 m）会把 16 m 高墙的投影整个抹掉，
        /// 取 0.0002（≈0.8 m）即可；更精细的自阴影要等 16 bit 深度（notes/105）。</summary>
        public static float GpuShadowSampleBias { get; set; } = 0.0002f;

        public static bool GpuShadowFlipY { get; set; }

        /// <summary>[v0.1.64] **软阴影（PCF）开关，默认开**。用户口径（里程碑 3.2）：
        /// "为了降低渲染开销，物体阴影应当为软阴影，而非 Dawnlight 目前的硬阴影"。
        /// 关掉 = 单次采样（v0.1.63 及以前的硬阴影），比较式与旧版**逐位一致**（用同一个已取到的 mapDepth）。
        /// </summary>
        public static bool GpuShadowSoftEnabled { get; set; } = true;

        /// <summary>[v0.1.64] PCF 核半径（texel 数，默认 1.5）。
        /// 偏移在 **UV 空间**累计（1 texel = 1/尺寸），所以这里按 texel 计；
        /// 换算成世界尺度：远图 2×512/1024 = 1.0 m/texel → 半影半径 ≈1.5 m；
        /// 近图 2×128/1024 = 0.25 m/texel → ≈0.375 m。**近场接触阴影天然更锐**（这正是级联想要的效果）。
        /// ⚠️ 第一版把"米"当 UV 传进去（1.0 UV = 整张贴图宽）→ 8 个抽样全落到边界外 → 阴影整片消失；
        /// 实测的软/硬 A/B 立刻暴露（见 notes/143）。
        /// </summary>
        public static float GpuShadowSoftRadius { get; set; } = 1.5f;

        /// <summary>[v0.1.64] **坡度 bias 系数**（默认 1.0 = 按最坏情况补偿）。
        /// 为什么必须有这一项（实测踩到）：地面上相邻 texel 沿太阳方向的深度变化是
        /// `texel米数 / sinθ / depthMax`（θ = 太阳仰角；低太阳角下一个 texel 的**地面足迹**被拉长），
        /// 而 PCF 的抽样半径是 1.5 texel —— 只要这个变化超过固定 bias，"朝太阳那一侧"的抽样就会一律判成
        /// 遮挡，**大片受光地面被压暗**（实测 r=1.5 时 48.5% 像素变暗、亮度 −16.1）。
        /// </summary>
        public static float GpuShadowSoftSlopeBias { get; set; } = 1f;

        /// <summary>[v0.1.64] **relief（起伏）bias 的 texel 数**（默认 0）。
        /// 这一项补偿"地形起伏落在同一个 texel 内"：1 格雪阶会让相邻 texel 的深度差达到 1 m 量级。
        /// **实测结论（不要想当然开大）**：把坡度项改成带 `1/sinθ` 之后，斜坡项本身已经覆盖了起伏误差
        /// —— 扫描 relief = 0/1/2/3/4 时 **0 就已经没有条纹**（见 notes/143 的扫描表），更大的值只是让
        /// 亮度继续上漂（+1.1 → +3.1），也就是**把真阴影也擦掉**（peter-panning）。
        /// 所以默认 0；它同时保证"半径=0 时软路径与硬路径逐位相同"这条可证伪不变量继续成立。
        /// </summary>
        public static float GpuShadowSoftReliefTexels { get; set; }

        /// <summary>[v0.1.64] 太阳仰角下限（sinθ）。低于它按它算，避免日出日落时 bias 发散。</summary>
        public static float GpuShadowSoftSunYFloor { get; set; } = 0.15f;

        /// <summary>[v0.1.34] 调试：0=正常阴影；1=把"采样到的阴影图深度"直接画到颜色（验证 UV/绑定是否正确）。</summary>
        public static int GpuShadowDebugMode { get; set; }

        // [v0.1.64] 捕获时的实际几何量：PCF 的核半径要按**真实 texel 尺寸**换算，不能用配置项想当然。
        static float m_gpuShadowRadiusAtCapture = 512f;
        static float m_gpuShadowNearRadiusAtCapture = 128f;
        static int m_gpuShadowSizeAtCapture = 1024;

        static string m_gpuShadowSampleError = "";
        static Shader m_gpuShadowOpaqueShader;
        static SamplerState m_gpuShadowSampler;
        static long m_gpuShadowSampleResolved;
        static long m_gpuShadowSampleFallbacks;
        static string m_gpuShadowSampleLastReason = "";

        /// <summary>[v0.1.64] 软阴影参数的**机器可读**快照（发版回归门禁的"默认值漂移门"用它）。
        /// 为什么单列一个方法：`...Describe()` 是给人读的字符串，门禁不该去解析它。</summary>
        public static string GpuShadowSoftInfo() {
            JsonObject o = new() {
                ["sampleEnabled"] = GpuShadowSampleEnabled,
                ["softEnabled"] = GpuShadowSoftEnabled,
                ["softRadius"] = (double)GpuShadowSoftRadius,
                ["reliefTexels"] = (double)GpuShadowSoftReliefTexels,
                ["slopeBias"] = (double)GpuShadowSoftSlopeBias,
                ["sunYFloor"] = (double)GpuShadowSoftSunYFloor,
                ["bias"] = (double)GpuShadowSampleBias,
                ["strength"] = (double)GpuShadowSampleStrength,
                ["sunRecaptureDeg"] = (double)GpuShadowSunRecaptureDegrees
            };
            return o.ToJsonString();
        }

        public static string GpuShadowSampleDescribe() =>
            $"gpuShadowSample enabled={GpuShadowSampleEnabled} strength={GpuShadowSampleStrength:0.##} "
            + $"bias={GpuShadowSampleBias:0.####} flipY={GpuShadowFlipY} hasMap={m_gpuShadowHasMap} "
            + $"soft={GpuShadowSoftEnabled}/{GpuShadowSoftRadius:0.##}texel "
            + $"penumbraFar={GpuShadowSoftRadius * 2f * m_gpuShadowRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1):0.###}m "
            + $"penumbraNear={GpuShadowSoftRadius * 2f * m_gpuShadowNearRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1):0.###}m "
            + $"relief={GpuShadowSoftReliefTexels:0.##}tx slopeBias={GpuShadowSoftSlopeBias:0.##} "
            + $"sunY={m_gpuShadowSun.Y:0.###} "
            + $"depth16={m_gpuShadowDepth16AtCapture} "
            + $"cascade={GpuShadowCascadeEnabled} hasNearMap={m_gpuShadowHasNearMap} "
            + $"sunRecaptureDeg={GpuShadowSunRecaptureDegrees:0.#} autoCaptures={m_gpuShadowAutoCaptures} "
            + $"resolved={m_gpuShadowSampleResolved} fallbacks={m_gpuShadowSampleFallbacks} "
            + $"lastReason='{m_gpuShadowSampleLastReason}' debug={GpuShadowDebugMode} err='{m_gpuShadowSampleError}'";

        /// <summary>[v0.1.34] 每帧由 `SkylineRuntime.Tick()` 调用：启用采样但还没深度图时自动补一次捕获。</summary>
        public static void GpuShadowTick() {
            if (!GpuShadowSampleEnabled) {
                return;
            }
            if (GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain == null) {
                return;
            }
            // 采样必须有深度图：捕获开关没开时随采样一起打开（否则 GpuShadowCapture 会直接拒绝）。
            if (!GpuShadowEnabled) {
                GpuShadowEnabled = true;
            }
            if (!m_gpuShadowHasMap) {
                GpuShadowCapture();
                return;
            }
            // [v0.1.65] 太阳追踪：光源转过阈值就自动重捕一次（轻量路径，不读回深度图）。
            // 没有这一条，"太阳追踪"只会在手动 `GpuShadowCapture()` 的那一刻成立 —— 影子不会跟着太阳转。
            if (GpuShadowSunRecaptureDegrees <= 0f) {
                return;
            }
            Vector3 now = TrackedLightDirection();
            float thresholdCos = System.MathF.Cos(GpuShadowSunRecaptureDegrees * System.MathF.PI / 180f);
            if (Vector3.Dot(now, m_gpuShadowSun) < thresholdCos) {
                m_gpuShadowSkipDiagnostics = true;
                try {
                    GpuShadowCapture();
                }
                finally {
                    m_gpuShadowSkipDiagnostics = false;
                }
            }
        }

        /// <summary>
        /// 解析不透明 pass 实际使用的 shader：启用采样且已有深度图时返回"地形+阴影采样"变体，
        /// 否则返回 fallback（游戏原本的 `m_opaqueShader`）→ 默认行为逐位不变。
        /// </summary>
        public static Shader ResolveOpaqueShader(Shader fallback) {
            if (!GpuShadowSampleEnabled || !m_gpuShadowHasMap || m_gpuShadowRt == null) {
                m_gpuShadowSampleFallbacks++;
                m_gpuShadowSampleLastReason = !GpuShadowSampleEnabled ? "disabled"
                    : (!m_gpuShadowHasMap ? "noMap" : "noRt");
                return fallback;
            }
            try {
                if (m_gpuShadowOpaqueShader == null) {
                    m_gpuShadowOpaqueShader = new Shader(GpuShadowOpaqueVsh, GpuShadowOpaquePsh);
                }
                if (m_gpuShadowSampler == null) {
                    m_gpuShadowSampler = new SamplerState {
                        AddressModeU = TextureAddressMode.Clamp,
                        AddressModeV = TextureAddressMode.Clamp,
                        FilterMode = TextureFilterMode.Point,
                        MaxLod = 0f
                    };
                }
                Shader shader = m_gpuShadowOpaqueShader;
                shader.GetParameter("u_shadowMap", true).SetValue(m_gpuShadowRt);
                shader.GetParameter("u_shadowSampler", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjection", true).SetValue(m_gpuShadowViewProjection);
                shader.GetParameter("u_sunOrigin", true).SetValue(m_gpuShadowOrigin);
                shader.GetParameter("u_eye", true).SetValue(m_gpuShadowEye);
                shader.GetParameter("u_sunDir", true).SetValue(m_gpuShadowSun);
                shader.GetParameter("u_depthMax", true).SetValue(m_gpuShadowDepthMax);
                // [v0.1.38] 近图（级联）：没有近图时 u_nearCascade = 0，片元只走远图（行为同 v0.1.37）
                shader.GetParameter("u_shadowMapNear", true).SetValue(m_gpuShadowRtNear ?? m_gpuShadowRt);
                shader.GetParameter("u_shadowSamplerNear", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjectionNear", true).SetValue(m_gpuShadowViewProjectionNear);
                shader.GetParameter("u_sunOriginNear", true).SetValue(m_gpuShadowOriginNear);
                shader.GetParameter("u_eyeNear", true).SetValue(m_gpuShadowEyeNear);
                shader.GetParameter("u_depthMaxNear", true).SetValue(m_gpuShadowDepthMaxNear);
                shader.GetParameter("u_nearCascade", true).SetValue(
                    (m_gpuShadowHasNearMap && m_gpuShadowRtNear != null) ? 1f : 0f);
                shader.GetParameter("u_shadowBias", true).SetValue(GpuShadowSampleBias);
                shader.GetParameter("u_shadowStrength", true).SetValue(GpuShadowSampleStrength);
                shader.GetParameter("u_shadowFlipY", true).SetValue(GpuShadowFlipY ? 1f : 0f);
                shader.GetParameter("u_shadowDepth16", true).SetValue(m_gpuShadowDepth16AtCapture ? 1f : 0f);
                shader.GetParameter("u_shadowDebug", true).SetValue((float)GpuShadowDebugMode);
                // [v0.1.64] PCF：偏移在 **UV 空间**（1 texel = 1/尺寸）。传"米"会把整张贴图当偏移跨过去
                // （实测：8 个抽样全落到边界外 → 阴影整片消失）。级联的世界尺度差异由"覆盖范围不同"自然给出。
                shader.GetParameter("u_shadowSoft", true).SetValue(GpuShadowSoftEnabled ? 1f : 0f);
                shader.GetParameter("u_shadowSoftRadius", true).SetValue(GpuShadowSoftRadius);
                shader.GetParameter("u_shadowTexelFar", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                shader.GetParameter("u_shadowTexelNear", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                // [v0.1.64] 坡度项：每个 texel 的**归一化深度变化**（最坏情况：受光面与太阳方向夹角 0°）
                //   每 texel 的归一化深度 = (2×半径/尺寸) 米 ÷ depthMax ÷ sinθ
                //   （÷sinθ 是因为正交盒沿太阳方向，太阳越低，一个 texel 对应的**地面足迹**越长）
                float sunY = System.MathF.Max(System.MathF.Abs(m_gpuShadowSun.Y), GpuShadowSoftSunYFloor);
                float farRelief = 2f * m_gpuShadowRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                  / System.Math.Max(m_gpuShadowDepthMax, 0.0001f) / sunY;
                float nearRelief = 2f * m_gpuShadowNearRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                   / System.Math.Max(m_gpuShadowDepthMaxNear, 0.0001f) / sunY;
                shader.GetParameter("u_shadowReliefFar", true).SetValue(farRelief);
                shader.GetParameter("u_shadowReliefNear", true).SetValue(nearRelief);
                shader.GetParameter("u_shadowSoftSlopeBias", true).SetValue(GpuShadowSoftSlopeBias);
                shader.GetParameter("u_shadowSoftReliefTexels", true).SetValue(GpuShadowSoftReliefTexels);
                m_gpuShadowSampleError = "";
                m_gpuShadowSampleResolved++;
                m_gpuShadowSampleLastReason = "resolved";
                return shader;
            }
            catch (System.Exception e) {
                m_gpuShadowSampleError = e.Message;
                GpuShadowSampleEnabled = false;
                Log.Warning($"SkylineGpuShadowSample: 采样 shader 构造/绑定失败，已自动关闭：{e.Message}");
                return fallback;
            }
        }

        // ============================================================================================
        // 变体 shader：与游戏 Opaque.vsh/psh 同结构（雾/顶点色/贴图一致），额外：v_world 世界坐标插值
        // + 阴影采样（投影到太阳深度图，比较线性深度）。
        // ============================================================================================
        const string GpuShadowOpaqueVsh = @"#ifdef HLSL

float2 u_origin;
float4x4 u_viewProjectionMatrix;
float3 u_viewPosition;
float u_fogYMultiplier;
float3 u_fogBottomTopDensity;
float2 u_hazeStartDensity;

float fogIntegral(float y)
{
	return smoothstep(u_fogBottomTopDensity.x, u_fogBottomTopDensity.y, y) * (u_fogBottomTopDensity.y - u_fogBottomTopDensity.x) + u_fogBottomTopDensity.x;
}

float calculateFog(float3 position)
{
	float3 fogDelta = u_viewPosition - position;
	fogDelta.y *= u_fogYMultiplier;
	float fogDistance = length(fogDelta);
	float fogFactor = (fogIntegral(u_viewPosition.y) - fogIntegral(position.y)) / (u_viewPosition.y - position.y);
	return saturate(saturate(u_hazeStartDensity.y * (fogDistance - u_hazeStartDensity.x)) + fogFactor * u_fogBottomTopDensity.z * fogDistance);
}

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float4 v_color : COLOR,
	out float2 v_texcoord : TEXCOORD,
	out float3 v_world : TEXCOORD1,
	out float v_fog : FOG,
	out float4 sv_position: SV_POSITION
)
{
	v_texcoord = a_texcoord;
	v_color = a_color;
	v_world = a_position;
	v_fog = calculateFog(a_position);
	sv_position = mul(float4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0), u_viewProjectionMatrix);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_viewPosition;
uniform float u_fogYMultiplier;
uniform vec3 u_fogBottomTopDensity;
uniform vec2 u_hazeStartDensity;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_world;
varying float v_fog;

float fogIntegral(float y)
{
	return smoothstep(u_fogBottomTopDensity.x, u_fogBottomTopDensity.y, y) * (u_fogBottomTopDensity.y - u_fogBottomTopDensity.x) + u_fogBottomTopDensity.x;
}

float calculateFog(vec3 position)
{
	vec3 fogDelta = u_viewPosition - position;
	fogDelta.y *= u_fogYMultiplier;
	float fogDistance = length(fogDelta);
	float fogFactor = (fogIntegral(u_viewPosition.y) - fogIntegral(position.y)) / (u_viewPosition.y - position.y);
	return clamp(clamp(u_hazeStartDensity.y * (fogDistance - u_hazeStartDensity.x), 0.0, 1.0) + fogFactor * u_fogBottomTopDensity.z * fogDistance, 0.0, 1.0);
}

void main()
{
	v_texcoord = a_texcoord;
	v_color = a_color;
	v_world = a_position;
	v_fog = calculateFog(a_position);
	gl_Position = u_viewProjectionMatrix * vec4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0);
	OPENGL_POSITION_FIX;
}

#endif
";

        const string GpuShadowOpaquePsh = @"#ifdef HLSL

Texture2D u_texture;
SamplerState u_samplerState;
float3 u_fogColor;
Texture2D u_shadowMap;
SamplerState u_shadowSampler;
Texture2D u_shadowMapNear;
SamplerState u_shadowSamplerNear;
float4x4 u_sunViewProjection;
float4x4 u_sunViewProjectionNear;
float2 u_sunOrigin;
float2 u_sunOriginNear;
float3 u_eye;
float3 u_eyeNear;
float3 u_sunDir;
float u_depthMax;
float u_depthMaxNear;
float u_nearCascade;
float u_shadowBias;
float u_shadowStrength;
float u_shadowFlipY;
float u_shadowDepth16;
float u_shadowDebug;
float u_shadowSoft;
float u_shadowSoftRadius;
float u_shadowTexelFar;
float u_shadowTexelNear;
float u_shadowReliefFar;
float u_shadowReliefNear;
float u_shadowSoftSlopeBias;
float u_shadowSoftReliefTexels;

// [v0.1.35] R 高字节 / G 低字节：d16 = (R*255)*256 + G*255
float decodeShadowDepth(float4 texel)
{
	if (u_shadowDepth16 > 0.5)
	{
		float hi = floor(texel.r * 255.0 + 0.5);
		float lo = floor(texel.g * 255.0 + 0.5);
		return (hi * 256.0 + lo) / 65535.0;
	}
	return texel.r;
}

float2 shadowUv(float4 clip, float flipY)
{
	float2 uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
	if (flipY > 0.5)
	{
		uv.y = 1.0 - uv.y;
	}
	return uv;
}

void main(
	in float4 v_color : COLOR,
	in float2 v_texcoord: TEXCOORD,
	in float3 v_world: TEXCOORD1,
	in float v_fog: FOG,
	out float4 svTarget: SV_TARGET
)
{
	float4 result = v_color;
	result *= u_texture.Sample(u_samplerState, v_texcoord);
	float3 sunShiftedFar = float3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	float2 uvFar = shadowUv(mul(float4(sunShiftedFar, 1.0), u_sunViewProjection), u_shadowFlipY);
	float3 sunShiftedNear = float3(v_world.x - u_sunOriginNear.x, v_world.y, v_world.z - u_sunOriginNear.y);
	float2 uvNear = shadowUv(mul(float4(sunShiftedNear, 1.0), u_sunViewProjectionNear), u_shadowFlipY);
	float mapDepth = 1.0;
	bool insideFar = uvFar.x >= 0.0 && uvFar.x <= 1.0 && uvFar.y >= 0.0 && uvFar.y <= 1.0;
	bool insideNear = u_nearCascade > 0.5 && uvNear.x >= 0.0 && uvNear.x <= 1.0 && uvNear.y >= 0.0 && uvNear.y <= 1.0;
	bool inside = insideFar || insideNear;
	// [v0.1.38] 近图优先：近图 texel 更细（128 m/1024² = 0.25 m），接触阴影更锐
	if (insideNear)
	{
		mapDepth = decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear));
	}
	else if (insideFar)
	{
		mapDepth = decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar));
	}
	if (u_shadowDebug > 0.5)
	{
		if (u_shadowDebug > 1.5)
		{
			// debug=2：蓝=用近图（级联），红=只用远图，绿=两张都不在范围
			result.rgb = insideNear ? float3(0.1, 0.2, 1.0)
				: insideFar ? float3(1.0, 0.15, 0.15)
				: float3(0.1, 1.0, 0.2);
		}
		else
		{
			result.rgb = float3(mapDepth, mapDepth, mapDepth);
		}
	}
	else if (inside)
	{
		// 近图的深度是按「近图自己的 eye + depthMax」归一化的（两张图的太阳距离不同），
		// 所以比较时必须换成对应的 eye/depthMax —— 用错会整片判成阴影（实测踩到）。
		float depthMax = insideNear ? u_depthMaxNear : u_depthMax;
		float3 eye = insideNear ? u_eyeNear : u_eye;
		float fragDepth = saturate(dot(eye - v_world, u_sunDir) / max(depthMax, 0.0001));
		float lit;
		if (u_shadowSoft > 0.5)
		{
			// [v0.1.64] 软阴影 = 8 抽样八边形核 PCF。要点：**平均的是「遮挡判定」而不是深度**
			// （平均深度只会让明暗过渡跟着深度走，不会产生半影）。
			// 逐像素旋转来自世界坐标哈希 → 确定性、跨帧稳定（截图 A/B 可复现），同时打破八边形的规则性。
			// 只对**自身级联**取样：近图 0.25 m/texel、远图 1 m/texel，半影半径因此天然分层。
			float t = (insideNear ? u_shadowTexelNear : u_shadowTexelFar) * max(u_shadowSoftRadius, 0.0);
			float ang = frac(sin(dot(v_world.xz, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
			// bias 补偿：(relief texel 数 + 抽样半径×1.4142) × 每 texel 归一化深度（已含 1/sinθ）。
			// 没有坡度项 → 大片受光地面被压暗（实测 48.5%）；没有 relief 项 → 残留斜向条纹 acne。
			float relief = insideNear ? u_shadowReliefNear : u_shadowReliefFar;
			float sb = u_shadowBias + (u_shadowSoftReliefTexels
				+ u_shadowSoftSlopeBias * u_shadowSoftRadius * 1.4142136) * relief;
			float ca = cos(ang);
			float sa = sin(ang);
			float acc = 0.0;
			if (insideNear)
			{
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((-ca + sa) * t, (-sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(sa * t, -ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((ca + sa) * t, (sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(-ca * t, -sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(ca * t, sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((-ca - sa) * t, (-sa + ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(-sa * t, ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((ca - sa) * t, (sa + ca) * t))) + sb);
			}
			else
			{
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((-ca + sa) * t, (-sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(sa * t, -ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((ca + sa) * t, (sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(-ca * t, -sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(ca * t, sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((-ca - sa) * t, (-sa + ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(-sa * t, ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((ca - sa) * t, (sa + ca) * t))) + sb);
			}
			lit = acc * 0.125;
		}
		else
		{
			// 关掉软阴影：用已取到的 mapDepth 判（与 v0.1.63 逐位一致）
			lit = step(fragDepth, mapDepth + u_shadowBias);
		}
		result.rgb *= (1.0 - u_shadowStrength * (1.0 - lit));
	}
	result.rgb = lerp(result.rgb, u_fogColor * v_color.a, v_fog);
	svTarget = result;
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;   // [v0.1.35] 16 bit 深度解码需要 fp32（mediump 尾数只有 ~10 bit）
#endif

// <Sampler Name='u_samplerState' Texture='u_texture' />
// <Sampler Name='u_shadowSampler' Texture='u_shadowMap' />
// <Sampler Name='u_shadowSamplerNear' Texture='u_shadowMapNear' />

uniform sampler2D u_texture;
uniform sampler2D u_shadowMap;
uniform sampler2D u_shadowMapNear;
uniform vec3 u_fogColor;
uniform mat4 u_sunViewProjection;
uniform mat4 u_sunViewProjectionNear;
uniform vec2 u_sunOrigin;
uniform vec2 u_sunOriginNear;
uniform vec3 u_eye;
uniform vec3 u_eyeNear;
uniform vec3 u_sunDir;
uniform float u_depthMax;
uniform float u_depthMaxNear;
uniform float u_nearCascade;
uniform float u_shadowBias;
uniform float u_shadowStrength;
uniform float u_shadowFlipY;
uniform float u_shadowDepth16;
uniform float u_shadowDebug;
uniform float u_shadowSoft;
uniform float u_shadowSoftRadius;
uniform float u_shadowTexelFar;
uniform float u_shadowTexelNear;
uniform float u_shadowReliefFar;
uniform float u_shadowReliefNear;
uniform float u_shadowSoftSlopeBias;
uniform float u_shadowSoftReliefTexels;

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_world;
varying float v_fog;

// [v0.1.35] R 高字节 / G 低字节：d16 = (R*255)*256 + G*255
float decodeShadowDepth(vec4 texel)
{
	if (u_shadowDepth16 > 0.5)
	{
		float hi = floor(texel.r * 255.0 + 0.5);
		float lo = floor(texel.g * 255.0 + 0.5);
		return (hi * 256.0 + lo) / 65535.0;
	}
	return texel.r;
}

vec2 shadowUv(vec4 clip, float flipY)
{
	vec2 uv = vec2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
	if (flipY > 0.5)
	{
		uv.y = 1.0 - uv.y;
	}
	return uv;
}

void main()
{
	vec4 result = v_color;
	result *= texture2D(u_texture, v_texcoord);
	vec3 sunShiftedFar = vec3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	vec2 uvFar = shadowUv(u_sunViewProjection * vec4(sunShiftedFar, 1.0), u_shadowFlipY);
	vec3 sunShiftedNear = vec3(v_world.x - u_sunOriginNear.x, v_world.y, v_world.z - u_sunOriginNear.y);
	vec2 uvNear = shadowUv(u_sunViewProjectionNear * vec4(sunShiftedNear, 1.0), u_shadowFlipY);
	float mapDepth = 1.0;
	bool insideFar = uvFar.x >= 0.0 && uvFar.x <= 1.0 && uvFar.y >= 0.0 && uvFar.y <= 1.0;
	bool insideNear = u_nearCascade > 0.5 && uvNear.x >= 0.0 && uvNear.x <= 1.0 && uvNear.y >= 0.0 && uvNear.y <= 1.0;
	bool inside = insideFar || insideNear;
	// [v0.1.38] 近图优先：近图 texel 更细（128 m/1024² = 0.25 m），接触阴影更锐
	if (insideNear)
	{
		mapDepth = decodeShadowDepth(texture2D(u_shadowMapNear, uvNear));
	}
	else if (insideFar)
	{
		mapDepth = decodeShadowDepth(texture2D(u_shadowMap, uvFar));
	}
	if (u_shadowDebug > 0.5)
	{
		if (u_shadowDebug > 1.5)
		{
			// debug=2：蓝=用近图（级联），红=只用远图，绿=两张都不在范围
			result.rgb = insideNear ? vec3(0.1, 0.2, 1.0)
				: insideFar ? vec3(1.0, 0.15, 0.15)
				: vec3(0.1, 1.0, 0.2);
		}
		else
		{
			result.rgb = vec3(mapDepth, mapDepth, mapDepth);
		}
	}
	else if (inside)
	{
		// 近图深度按「近图自己的 eye + depthMax」归一化（两张图太阳距离不同）→ 比较时必须一起换。
		float depthMax = insideNear ? u_depthMaxNear : u_depthMax;
		vec3 eye = insideNear ? u_eyeNear : u_eye;
		float fragDepth = clamp(dot(eye - v_world, u_sunDir) / max(depthMax, 0.0001), 0.0, 1.0);
		float lit;
		if (u_shadowSoft > 0.5)
		{
			// [v0.1.64] 软阴影 = 8 抽样八边形核 PCF（与 HLSL 段同一算法：比较「遮挡判定」、逐像素旋转）
			float t = (insideNear ? u_shadowTexelNear : u_shadowTexelFar) * max(u_shadowSoftRadius, 0.0);
			float ang = fract(sin(dot(v_world.xz, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
			// bias 补偿（与 HLSL 段同一算法）：relief texel + 抽样半径×1.4142，再乘每 texel 归一化深度
			float relief = insideNear ? u_shadowReliefNear : u_shadowReliefFar;
			float sb = u_shadowBias + (u_shadowSoftReliefTexels
				+ u_shadowSoftSlopeBias * u_shadowSoftRadius * 1.4142136) * relief;
			float ca = cos(ang);
			float sa = sin(ang);
			float acc = 0.0;
			if (insideNear)
			{
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((-ca + sa) * t, (-sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(sa * t, -ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((ca + sa) * t, (sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(-ca * t, -sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(ca * t, sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((-ca - sa) * t, (-sa + ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(-sa * t, ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((ca - sa) * t, (sa + ca) * t))) + sb);
			}
			else
			{
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((-ca + sa) * t, (-sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(sa * t, -ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((ca + sa) * t, (sa - ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(-ca * t, -sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(ca * t, sa * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((-ca - sa) * t, (-sa + ca) * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(-sa * t, ca * t))) + sb);
				acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((ca - sa) * t, (sa + ca) * t))) + sb);
			}
			lit = acc * 0.125;
		}
		else
		{
			// 关掉软阴影：用已取到的 mapDepth 判（与 v0.1.63 逐位一致）
			lit = step(fragDepth, mapDepth + u_shadowBias);
		}
		result.rgb *= (1.0 - u_shadowStrength * (1.0 - lit));
	}
	result.rgb = mix(result.rgb, u_fogColor * v_color.a, v_fog);
	gl_FragColor = result;
}

#endif
";
    }
}
