using Engine;
using Engine.Graphics;

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
    /// `GpuShadowSampleStrength`（0.45）、`GpuShadowSampleBias`（0.004）、`GpuShadowFlipY`（后端 UV 方向；
    /// 先用 A/B 定，默认 false）。诊断：`skyline.GpuShadowSampleDescribe()`。
    /// </summary>
    public static partial class SkylineRuntime {
        public static bool GpuShadowSampleEnabled { get; set; }

        public static float GpuShadowSampleStrength { get; set; } = 0.45f;

        /// <summary>深度比较偏置（归一化深度单位）。实测：0.004（≈16 m）会把 16 m 高墙的投影整个抹掉，
        /// 取 0.0002（≈0.8 m）即可；更精细的自阴影要等 16 bit 深度（notes/105）。</summary>
        public static float GpuShadowSampleBias { get; set; } = 0.0002f;

        public static bool GpuShadowFlipY { get; set; }

        /// <summary>[v0.1.34] 调试：0=正常阴影；1=把"采样到的阴影图深度"直接画到颜色（验证 UV/绑定是否正确）。</summary>
        public static int GpuShadowDebugMode { get; set; }

        static string m_gpuShadowSampleError = "";
        static Shader m_gpuShadowOpaqueShader;
        static SamplerState m_gpuShadowSampler;
        static long m_gpuShadowSampleResolved;
        static long m_gpuShadowSampleFallbacks;
        static string m_gpuShadowSampleLastReason = "";

        public static string GpuShadowSampleDescribe() =>
            $"gpuShadowSample enabled={GpuShadowSampleEnabled} strength={GpuShadowSampleStrength:0.##} "
            + $"bias={GpuShadowSampleBias:0.####} flipY={GpuShadowFlipY} hasMap={m_gpuShadowHasMap} "
            + $"resolved={m_gpuShadowSampleResolved} fallbacks={m_gpuShadowSampleFallbacks} "
            + $"lastReason='{m_gpuShadowSampleLastReason}' debug={GpuShadowDebugMode} err='{m_gpuShadowSampleError}'";

        /// <summary>[v0.1.34] 每帧由 `SkylineRuntime.Tick()` 调用：启用采样但还没深度图时自动补一次捕获。</summary>
        public static void GpuShadowTick() {
            if (!GpuShadowSampleEnabled || m_gpuShadowHasMap) {
                return;
            }
            if (GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain == null) {
                return;
            }
            // 采样必须有深度图：捕获开关没开时随采样一起打开（否则 GpuShadowCapture 会直接拒绝）。
            if (!GpuShadowEnabled) {
                GpuShadowEnabled = true;
            }
            GpuShadowCapture();
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
                shader.GetParameter("u_shadowBias", true).SetValue(GpuShadowSampleBias);
                shader.GetParameter("u_shadowStrength", true).SetValue(GpuShadowSampleStrength);
                shader.GetParameter("u_shadowFlipY", true).SetValue(GpuShadowFlipY ? 1f : 0f);
                shader.GetParameter("u_shadowDebug", true).SetValue((float)GpuShadowDebugMode);
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
float4x4 u_sunViewProjection;
float2 u_sunOrigin;
float3 u_eye;
float3 u_sunDir;
float u_depthMax;
float u_shadowBias;
float u_shadowStrength;
float u_shadowFlipY;
float u_shadowDebug;

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
	float3 sunShifted = float3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	float4 sunClip = mul(float4(sunShifted, 1.0), u_sunViewProjection);
	float2 uv = float2(sunClip.x * 0.5 + 0.5, 0.5 - sunClip.y * 0.5);
	if (u_shadowFlipY > 0.5)
	{
		uv.y = 1.0 - uv.y;
	}
	float mapDepth = 1.0;
	bool inside = uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0;
	if (inside)
	{
		mapDepth = u_shadowMap.Sample(u_shadowSampler, uv).r;
	}
	if (u_shadowDebug > 0.5)
	{
		result.rgb = float3(mapDepth, mapDepth, mapDepth);
	}
	else if (inside)
	{
		float fragDepth = saturate(dot(u_eye - v_world, u_sunDir) / max(u_depthMax, 0.0001));
		if (mapDepth + u_shadowBias < fragDepth)
		{
			result.rgb *= (1.0 - u_shadowStrength);
		}
	}
	result.rgb = lerp(result.rgb, u_fogColor * v_color.a, v_fog);
	svTarget = result;
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision mediump float;
#endif

// <Sampler Name='u_samplerState' Texture='u_texture' />
// <Sampler Name='u_shadowSampler' Texture='u_shadowMap' />

uniform sampler2D u_texture;
uniform sampler2D u_shadowMap;
uniform vec3 u_fogColor;
uniform mat4 u_sunViewProjection;
uniform vec2 u_sunOrigin;
uniform vec3 u_eye;
uniform vec3 u_sunDir;
uniform float u_depthMax;
uniform float u_shadowBias;
uniform float u_shadowStrength;
uniform float u_shadowFlipY;
uniform float u_shadowDebug;

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_world;
varying float v_fog;

void main()
{
	vec4 result = v_color;
	result *= texture2D(u_texture, v_texcoord);
	vec3 sunShifted = vec3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	vec4 sunClip = u_sunViewProjection * vec4(sunShifted, 1.0);
	vec2 uv = vec2(sunClip.x * 0.5 + 0.5, 0.5 - sunClip.y * 0.5);
	if (u_shadowFlipY > 0.5)
	{
		uv.y = 1.0 - uv.y;
	}
	float mapDepth = 1.0;
	bool inside = uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0;
	if (inside)
	{
		mapDepth = texture2D(u_shadowMap, uv).r;
	}
	if (u_shadowDebug > 0.5)
	{
		result.rgb = vec3(mapDepth, mapDepth, mapDepth);
	}
	else if (inside)
	{
		float fragDepth = clamp(dot(u_eye - v_world, u_sunDir) / max(u_depthMax, 0.0001), 0.0, 1.0);
		if (mapDepth + u_shadowBias < fragDepth)
		{
			result.rgb *= (1.0 - u_shadowStrength);
		}
	}
	result.rgb = mix(result.rgb, u_fogColor * v_color.a, v_fog);
	gl_FragColor = result;
}

#endif
";
    }
}
