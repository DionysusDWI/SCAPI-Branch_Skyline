using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**消费新顶点属性的 LOD/壳着色器**（里程碑 1.3 的第二半）。
    ///
    /// 与 `SkylineFaceShading`（CPU 侧把面因子烘焙进顶点色）是**同一条公式的两条实现路径**：
    ///   * CPU 路径：建网格时算 `CalculateLighting(faceNormal)/CalculateLighting(+Y)` 乘进顶点色 → 用游戏 `Opaque` 着色器画。
    ///   * GPU 路径：顶点只带**原始光照**，法线作为**顶点属性**进着色器，片元里算同一个式子。
    /// 两条路径的差 = 8 位量化误差，所以 `CaptureCompare()` 能做"GPU 面明暗 == CPU 面明暗"的数值断言；
    /// 只要顶点属性没真的到 GPU，这个断言就会失败（这是它存在的意义）。
    ///
    /// 通道（`skyline.LodVolumeChannel(n)`）：0 = 面明暗着色（正常观感）、1 = 法线可视化、
    /// 2 = 材质 id 以 16 进制编码上色（读回来能精确反解 id）、3 = 面因子灰度。
    /// 通道 1/2 就是"顶点属性真的到了片元"的直接证据（`Capture()` 回读统计 distinct colours）。
    /// </summary>
    public static class SkylineLodVolume {
        public static bool Enabled { get; set; }
        /// <summary>0=着色 1=法线 2=材质 id 3=面因子。</summary>
        public static int Channel { get; set; }
        public static int Size { get; set; } = 256;

        /// <summary>[v0.1.66] 显存预算表用（只读）：体积感着色 pass 的 RT，未分配时为 null。</summary>
        public static RenderTarget2D LodVolumeRt => m_rt;

        static Shader m_shader;
        static RenderTarget2D m_rt;
        static SamplerState m_sampler;
        static string m_lastError = "";
        static int m_draws, m_captures;

        public static string LastError => m_lastError;
        public static int Draws => m_draws;
        public static int Captures => m_captures;
        public static bool HasTarget => m_rt != null;

        // ---------------------------------------------------------------- 着色器

        /// <summary>
        /// 顶点：位置 / 颜色 / 图集坐标 / **法线** / **材质 id**。
        /// 片元：与 `LightingManager.CalculateLighting` 完全同式的方向光（环境 0.5 + 两盏方向光），
        /// 再按 `u_topLight` 归一化（顶面 = 1.0），最后乘入 albedo。
        /// </summary>
        const string VolumeVsh = @"#ifdef HLSL

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
	in float4 a_normal: NORMAL,
	in float a_matid: TEXCOORD1,
	out float4 v_color : COLOR,
	out float2 v_texcoord : TEXCOORD,
	out float3 v_normal : TEXCOORD2,
	out float v_matid : TEXCOORD3,
	out float v_slopeTop : TEXCOORD4,
	out float3 v_world : TEXCOORD5,
	out float v_fog : FOG,
	out float4 sv_position: SV_POSITION
)
{
	v_color = a_color;
	v_texcoord = a_texcoord;
	v_normal = a_normal.xyz * 2.0 - 1.0;
	v_matid = a_matid;
	v_slopeTop = a_normal.w;
	v_world = a_position;
	v_fog = calculateFog(a_position);
	sv_position = mul(float4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0), u_viewProjectionMatrix);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />
// <Semantic Name='NORMAL' Attribute='a_normal' />
// <Semantic Name='TEXCOORD1' Attribute='a_matid' />

precision highp float;

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_viewPosition;
uniform float u_fogYMultiplier;
uniform vec3 u_fogBottomTopDensity;
uniform vec2 u_hazeStartDensity;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;
attribute vec4 a_normal;
attribute float a_matid;

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_normal;
varying float v_matid;
varying float v_slopeTop;
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
	v_color = a_color;
	v_texcoord = a_texcoord;
	v_normal = a_normal.xyz * 2.0 - 1.0;
	v_matid = a_matid;
	v_slopeTop = a_normal.w;
	v_world = a_position;
	v_fog = calculateFog(a_position);
	gl_Position = u_viewProjectionMatrix * vec4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0);
	OPENGL_POSITION_FIX;
}

#endif";

        const string VolumePsh = @"#ifdef HLSL

Texture2D u_texture;
SamplerState u_samplerState;
float3 u_light1;
float3 u_light2;
float3 u_sunDir;
float u_topLight;
float u_slopeStrength;
float u_sunAmount;
float u_channel;
float3 u_fogColor;
// [v0.1.105] 体积雾 + 体积神光 + 阴影图（由 `SkylineRuntime.BindShadowFogParams` 统一绑定；
// 本文件只负责**消费**，口径与真地形的不透明变体逐字相同）
float3 u_viewPosition;
float u_vfEnable;
float u_vfBottomY;
float u_vfTopY;
float u_vfDensity;
float u_vfBase;
float u_vfHaze;
float u_vfScale;
float2 u_vfWind;
float u_vfThreshold;
float u_vfStrength;
float3 u_vfColor;
float u_vfSkyMix;
float u_vfMaxDistance;
float u_vfShear;
float u_vfSunShaft;
float3 u_vfSunColor;
float u_vfPhasePower;
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
float u_depthMax;
float u_depthMaxNear;
float u_nearCascade;
float u_shadowBias;
float u_shadowStrength;
// [v0.1.112] 昼光因子（阴影强度按昼光缩放；本层只用于体积神光的可见性判定，但 uniform 必须声明，
//   否则共享的 BindShadowFogParams 会在缺参数处抛异常、后面那一串雾 uniform 全部绑不上）
float u_shadowDayFactor;
// [v0.1.115] 里程碑 2.3：**LOD 的体素也接收太阳阴影**（同一张太阳深度图）
float u_lodShadowReceive;
float u_shadowFadeStart;
float u_shadowFadeEnd;
float u_shadowFlipY;
float u_shadowDepth16;
float u_shadowEnable;
float u_shadowDebug;
float u_shadowSoft;
float u_shadowSoftRadius;
float u_shadowTexelFar;
float u_shadowTexelNear;
float u_shadowReliefFar;
float u_shadowReliefNear;
float u_shadowSoftSlopeBias;
float u_shadowSoftReliefTexels;
float u_shadowKernel;
float u_shadowKernelSlopeScale;

// [v0.1.109] DH 规格的两项观感（抖动淡出 + 噪声补细节；见 SkylineLodLook）
float u_lodDitherFade;
float u_lodFadeStart;
float u_lodNoiseEnable;
float u_lodNoiseSteps;
float u_lodNoiseIntensity;
float u_lodNoiseDropoff;

// DH 用 4×4 Bayer 常量数组做抖动；本引擎 GLES 路径不接受 const 数组，
// 改用 Iris 光影包 Complementary 的 IGN（屏幕空间稳定有序噪声，同一用途）。
float lodIgn(float2 fragCoord)
{
	return frac(52.9829189 * frac(0.06711056 * fragCoord.x + 0.00583715 * fragCoord.y));
}

// DH `applyNoise` 里的 `rand(vec3) = rand(co.xy + rand(co.z))`，两种方言都按这个写（不用常量数组）
float lodRand1(float co) { return frac(sin(co * 91.3458) * 47453.5453); }
float lodRand2(float2 co) { return frac(sin(dot(co, float2(12.9898, 78.233))) * 43758.5453); }
float lodRand3(float3 co) { return lodRand2(co.xy + lodRand1(co.z)); }

float vfHash12(float2 p)
{
	float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, float3(p3.y, p3.z, p3.x) + 33.33);
	return frac((p3.x + p3.y) * p3.z);
}

float vfNoise2(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = vfHash12(i);
	float b = vfHash12(i + float2(1.0, 0.0));
	float c = vfHash12(i + float2(0.0, 1.0));
	float d = vfHash12(i + float2(1.0, 1.0));
	return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float vfHeightAt(float3 p)
{
	return (p.y - u_vfBottomY) / max(u_vfTopY - u_vfBottomY, 0.001);
}

float vfDensityAt(float3 p)
{
	float h = vfHeightAt(p);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.15, h) * smoothstep(1.0, 0.7, h);
	float2 q = (p.xz + p.y * u_vfShear * float2(1.7, 1.1)) * u_vfScale + u_vfWind;
	float n = vfNoise2(q) * 0.7 + vfNoise2(q * 2.3 + float2(5.1, 9.7)) * 0.3;
	return max(u_vfBase, max(0.0, n - u_vfThreshold)) * prof;
}

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

float sunShaftVisibility(float3 p)
{
	if (u_shadowEnable < 0.5)
	{
		return 1.0;
	}
	float3 relFar = float3(p.x - u_sunOrigin.x, p.y, p.z - u_sunOrigin.y);
	float2 uvFarS = shadowUv(mul(float4(relFar, 1.0), u_sunViewProjection), u_shadowFlipY);
	float3 relNear = float3(p.x - u_sunOriginNear.x, p.y, p.z - u_sunOriginNear.y);
	float2 uvNearS = shadowUv(mul(float4(relNear, 1.0), u_sunViewProjectionNear), u_shadowFlipY);
	bool inFarS = uvFarS.x >= 0.0 && uvFarS.x <= 1.0 && uvFarS.y >= 0.0 && uvFarS.y <= 1.0;
	bool inNearS = u_nearCascade > 0.5 && uvNearS.x >= 0.0 && uvNearS.x <= 1.0
		&& uvNearS.y >= 0.0 && uvNearS.y <= 1.0;
	if (!inFarS && !inNearS)
	{
		return 1.0;
	}
	float shaftDepthMax = inNearS ? u_depthMaxNear : u_depthMax;
	float3 shaftEye = inNearS ? u_eyeNear : u_eye;
	float shaftDepth = saturate(dot(shaftEye - p, u_sunDir) / max(shaftDepthMax, 0.0001));
	float shaftMap = inNearS
		? decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNearS))
		: decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFarS));
	float shaftRelief = inNearS ? u_shadowReliefNear : u_shadowReliefFar;
	float shaftBias = u_shadowBias
		+ u_shadowSoftSlopeBias * u_shadowSoftRadius * u_shadowKernelSlopeScale * shaftRelief;
	return shaftDepth <= shaftMap + shaftBias ? 1.0 : 0.0;
}

void main(
	in float4 v_color : COLOR,
	in float2 v_texcoord: TEXCOORD,
	in float3 v_normal : TEXCOORD2,
	in float v_matid : TEXCOORD3,
	in float v_slopeTop : TEXCOORD4,
	in float3 v_world : TEXCOORD5,
	in float v_fog: FOG,
	out float4 svTarget: SV_TARGET
)
{
	float4 albedo = v_color * u_texture.Sample(u_samplerState, v_texcoord);
	// [v0.1.109] DH 规格 · 噪声补细节（`flat_shaded.frag` 的 `applyNoise`）：改的是**基色**，
	// 且只在正常颜色通道生效（通道 1/2/3 是自检/G-buffer 调试通道，不能被动）。
	if (u_channel < 0.5 && u_lodNoiseEnable > 0.5)
	{
		float lodLum = (albedo.r + albedo.g + albedo.b) / 3.0;
		float lodAmp = u_lodNoiseIntensity * 0.01
			* (1.0 - (2.0 * lodLum - 1.0) * (2.0 * lodLum - 1.0)) * albedo.a;
		float3 lodQ = floor(v_world * max(u_lodNoiseSteps, 0.001)) / max(u_lodNoiseSteps, 0.001);
		float lodRnd = lodRand3(lodQ) * 2.0 * lodAmp - lodAmp;
		float3 lodNoisy = albedo.rgb + (1.0 - albedo.rgb) * lodRnd;
		if (u_lodNoiseDropoff > 0.5)
		{
			float lodDistF = min(length(v_world - u_viewPosition) / u_lodNoiseDropoff, 1.0);
			lodNoisy = lerp(lodNoisy, albedo.rgb, lodDistF);
		}
		albedo.rgb = saturate(lodNoisy);
	}
	// [v0.1.109] DH 规格 · 抖动淡出（`flat_shaded.frag` 的 `ditherDhFade`）：靠近时按屏幕空间
	// 抖动概率**丢弃**片元 ⇒ 与真地形的交界是渐隐而不是硬切。抖动用 Iris 侧的 IGN（见 SkylineLodLook）。
	if (u_channel < 0.5 && u_lodDitherFade > 0.5)
	{
		float lodViewDist = length(v_world - u_viewPosition);
		float lodFade = smoothstep(u_lodFadeStart, u_lodFadeStart * 1.5, lodViewDist);
		if (lodFade <= lodIgn(sv_position.xy) + 0.001)
		{
			discard;
		}
	}
	float3 n = normalize(v_normal);
	float lit;
	if (v_slopeTop > 0.5)
	{
		// [v0.1.77] 顶面：**坡向明暗**（单盏太阳；与 SkylineLod.SlopeGainFromNormal 逐字同式）
		// [v0.1.78] 太阳方向改成**真太阳**（u_sunDir = TrackedLightDirection），不再用固定的 u_light1
		float3 sun = normalize(u_sunDir);
		float slopeDot = dot(n, sun) / max(dot(float3(0.0, 1.0, 0.0), sun), 0.0001);
		float slopeGain = lerp(1.0, clamp(slopeDot, 0.35, 1.0), u_slopeStrength);
		lit = lerp(1.0, slopeGain, u_sunAmount);
	}
	else
	{
		// 立面：**六面因子**（环境 0.5 + 两盏镜像方向光，再按顶面归一化）
		lit = 0.5 + max(dot(n, u_light1), 0.0) + max(dot(n, u_light2), 0.0);
		lit = saturate(lit / max(u_topLight, 0.0001));
	}
	if (u_channel > 1.5 && u_channel < 2.5)
	{
		int mid = int(v_matid + 0.5);
		int id = mid - (mid / 4096) * 4096;
		if (id < 0) id += 4096;
		svTarget = float4(floor(id / 256.0) / 15.0, floor(id / 16.0 - floor(id / 256.0) * 16.0) / 15.0, (id - (id / 16) * 16) / 15.0, 1.0);
		return;
	}
	if (u_channel > 0.5 && u_channel < 1.5)
	{
		svTarget = float4(n * 0.5 + 0.5, 1.0);
		return;
	}
	if (u_channel > 2.5)
	{
		svTarget = float4(lit, lit, lit, 1.0);
		return;
	}
	float3 rgb = lerp(albedo.rgb * lit, u_fogColor * v_color.a, v_fog);
	// [v0.1.115] 里程碑 2.3：**LOD 的体素也接收太阳阴影**（与地形同一张深度图、同一 bias、同一昼光因子）。
	//   出图 = 照到（与地形「图外即受光」同口径）；`u_shadowFade*` 用与地形同一条远处淡出。
	if (u_lodShadowReceive > 0.5)
	{
		float lodShadowLit = sunShaftVisibility(v_world);
		float lodShadowDist = length(v_world - u_viewPosition);
		float lodShadowFade = 1.0;
		if (u_shadowFadeEnd > u_shadowFadeStart)
		{
			lodShadowFade = 1.0 - smoothstep(u_shadowFadeStart, u_shadowFadeEnd, lodShadowDist);
		}
		lit *= 1.0 - u_shadowStrength * u_shadowDayFactor * lodShadowFade * (1.0 - lodShadowLit);
		rgb = lerp(albedo.rgb * lit, u_fogColor * v_color.a, v_fog);   // 阴影改的是 lit ⇒ 重算一次
	}
	// [v0.1.105] **体积雾 + 体积神光接到 LOD 层**：原来这一层只有「被 FogDisabled 置 0 的原版雾」
	//   ⇒ 远景 LOD 上完全没有我们的体积雾（`notes/169 §3`、`notes/194 §6` 都记过这条缺口）。
	//   现在与真地形的不透明变体用**逐字相同的算法**（8 步积分 + 单次散射神光）。
	if (u_vfEnable > 0.5)
	{
		float3 vfDelta = v_world - u_viewPosition;
		float vfLen = length(vfDelta);
		if (vfLen > 0.001)
		{
			float3 vfRd = vfDelta / vfLen;
			float vfT0 = 0.0;
			float vfT1 = min(vfLen, u_vfMaxDistance);
			if (abs(vfRd.y) > 1e-5)
			{
				float vfTa = (u_vfBottomY - u_viewPosition.y) / vfRd.y;
				float vfTb = (u_vfTopY - u_viewPosition.y) / vfRd.y;
				vfT0 = max(vfT0, min(vfTa, vfTb));
				vfT1 = min(vfT1, max(vfTa, vfTb));
			}
			else if (u_viewPosition.y < u_vfBottomY || u_viewPosition.y > u_vfTopY)
			{
				vfT1 = 0.0;
			}
			if (vfT1 > vfT0)
			{
				float vfDt = (vfT1 - vfT0) * 0.125;
				float vfJit = vfHash12(v_world.xz * 41.0) * vfDt;
				float vfOd = 0.0;
				float vfScat = 0.0;
				for (int vfI = 0; vfI < 8; vfI++)
				{
					float3 vfP = u_viewPosition + vfRd * (vfT0 + vfJit + vfDt * (float(vfI) + 0.5));
					float vfD = vfDensityAt(vfP);
					vfOd += vfD;
					vfScat += vfD * sunShaftVisibility(vfP);
				}
				float vfOdScale = vfDt * u_vfDensity;
				float vfOdRaw = max(vfOd, 1e-5);
				vfOd *= vfOdScale;
				float vfAlpha = saturate((1.0 - exp(-vfOd)) * u_vfStrength);
				float3 vfCol = lerp(u_vfColor, max(u_fogColor, float3(0.02, 0.02, 0.02)), u_vfSkyMix);
				// [v0.1.117] 霾 / 光轴解耦（与地形段同一算法，见 SkylineVolumetricFog）
				rgb = lerp(rgb, vfCol, vfAlpha * u_vfHaze);
				if (u_vfSunShaft > 0.0)
				{
					float vfPhase = 0.25 + 0.75 * pow(saturate(dot(vfRd, u_sunDir)), u_vfPhasePower);
					float vfLit = saturate(vfScat / vfOdRaw);
					rgb += u_vfSunColor * (vfLit * vfAlpha * vfPhase * u_vfSunShaft);
				}
			}
		}
	}
	svTarget = float4(rgb, albedo.a);
}

#endif
#ifdef GLSL

// <Sampler Name='u_samplerState' Texture='u_texture' />
// [v0.1.105] 阴影图的两个采样器必须在这里登记（引擎的 shader 元数据要求），否则绑定时会报
// 「Texture u_shadowMap has no sampler defined in shader metadata」并让整支 LOD 着色器准备失败。
// <Sampler Name='u_shadowSampler' Texture='u_shadowMap' />
// <Sampler Name='u_shadowSamplerNear' Texture='u_shadowMapNear' />

precision highp float;

uniform sampler2D u_texture;
uniform vec3 u_light1;
uniform vec3 u_light2;
uniform vec3 u_sunDir;
uniform float u_topLight;
uniform float u_slopeStrength;
uniform float u_sunAmount;
uniform float u_channel;
uniform vec3 u_fogColor;

// [v0.1.105] 体积雾 + 体积神光 + 阴影图（由 `SkylineRuntime.BindShadowFogParams` 统一绑定）
uniform vec3 u_viewPosition;
uniform float u_vfEnable;
uniform float u_vfBottomY;
uniform float u_vfTopY;
uniform float u_vfDensity;
uniform float u_vfBase;
uniform float u_vfHaze;
uniform float u_vfScale;
uniform vec2 u_vfWind;
uniform float u_vfThreshold;
uniform float u_vfStrength;
uniform vec3 u_vfColor;
uniform float u_vfSkyMix;
uniform float u_vfMaxDistance;
uniform float u_vfShear;
uniform float u_vfSunShaft;
uniform vec3 u_vfSunColor;
uniform float u_vfPhasePower;
uniform sampler2D u_shadowMap;
uniform sampler2D u_shadowMapNear;
uniform mat4 u_sunViewProjection;
uniform mat4 u_sunViewProjectionNear;
uniform vec2 u_sunOrigin;
uniform vec2 u_sunOriginNear;
uniform vec3 u_eye;
uniform vec3 u_eyeNear;
uniform float u_depthMax;
uniform float u_depthMaxNear;
uniform float u_nearCascade;
uniform float u_shadowBias;
uniform float u_shadowStrength;
uniform float u_shadowDayFactor;
// [v0.1.115] 里程碑 2.3：LOD 的体素也接收太阳阴影（同一张太阳深度图）
uniform float u_lodShadowReceive;
uniform float u_shadowFadeStart;
uniform float u_shadowFadeEnd;
uniform float u_shadowFlipY;
uniform float u_shadowDepth16;
uniform float u_shadowEnable;
uniform float u_shadowDebug;
uniform float u_shadowSoft;
uniform float u_shadowSoftRadius;
uniform float u_shadowTexelFar;
uniform float u_shadowTexelNear;
uniform float u_shadowReliefFar;
uniform float u_shadowReliefNear;
uniform float u_shadowSoftSlopeBias;
uniform float u_shadowSoftReliefTexels;
uniform float u_shadowKernel;
uniform float u_shadowKernelSlopeScale;

// [v0.1.109] DH 规格的两项观感（见 SkylineLodLook 的文档）
uniform float u_lodDitherFade;
uniform float u_lodFadeStart;
uniform float u_lodNoiseEnable;
uniform float u_lodNoiseSteps;
uniform float u_lodNoiseIntensity;
uniform float u_lodNoiseDropoff;

// 与 HLSL 段同一组辅助函数（IGN 抖动 + DH 的 rand(vec3)）
float lodIgn(vec2 fragCoord)
{
	return fract(52.9829189 * fract(0.06711056 * fragCoord.x + 0.00583715 * fragCoord.y));
}

float lodRand1(float co) { return fract(sin(co * 91.3458) * 47453.5453); }
float lodRand2(vec2 co) { return fract(sin(dot(co, vec2(12.9898, 78.233))) * 43758.5453); }
float lodRand3(vec3 co) { return lodRand2(co.xy + lodRand1(co.z)); }

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_normal;
varying float v_matid;
varying float v_slopeTop;
varying float v_fog;
varying vec3 v_world;

float vfHash12(vec2 p)
{
	vec3 p3 = fract(vec3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, vec3(p3.y, p3.z, p3.x) + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vfNoise2(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = vfHash12(i);
	float b = vfHash12(i + vec2(1.0, 0.0));
	float c = vfHash12(i + vec2(0.0, 1.0));
	float d = vfHash12(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float vfHeightAt(vec3 p)
{
	return (p.y - u_vfBottomY) / max(u_vfTopY - u_vfBottomY, 0.001);
}

float vfDensityAt(vec3 p)
{
	float h = vfHeightAt(p);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.15, h) * smoothstep(1.0, 0.7, h);
	vec2 q = (p.xz + p.y * u_vfShear * vec2(1.7, 1.1)) * u_vfScale + u_vfWind;
	float n = vfNoise2(q) * 0.7 + vfNoise2(q * 2.3 + vec2(5.1, 9.7)) * 0.3;
	return max(u_vfBase, max(0.0, n - u_vfThreshold)) * prof;
}

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

float sunShaftVisibility(vec3 p)
{
	if (u_shadowEnable < 0.5)
	{
		return 1.0;
	}
	vec3 relFar = vec3(p.x - u_sunOrigin.x, p.y, p.z - u_sunOrigin.y);
	vec2 uvFarS = shadowUv(u_sunViewProjection * vec4(relFar, 1.0), u_shadowFlipY);
	vec3 relNear = vec3(p.x - u_sunOriginNear.x, p.y, p.z - u_sunOriginNear.y);
	vec2 uvNearS = shadowUv(u_sunViewProjectionNear * vec4(relNear, 1.0), u_shadowFlipY);
	bool inFarS = uvFarS.x >= 0.0 && uvFarS.x <= 1.0 && uvFarS.y >= 0.0 && uvFarS.y <= 1.0;
	bool inNearS = u_nearCascade > 0.5 && uvNearS.x >= 0.0 && uvNearS.x <= 1.0
		&& uvNearS.y >= 0.0 && uvNearS.y <= 1.0;
	if (!inFarS && !inNearS)
	{
		return 1.0;
	}
	float shaftDepthMax = inNearS ? u_depthMaxNear : u_depthMax;
	vec3 shaftEye = inNearS ? u_eyeNear : u_eye;
	float shaftDepth = clamp(dot(shaftEye - p, u_sunDir) / max(shaftDepthMax, 0.0001), 0.0, 1.0);
	float shaftMap = inNearS
		? decodeShadowDepth(texture2D(u_shadowMapNear, uvNearS))
		: decodeShadowDepth(texture2D(u_shadowMap, uvFarS));
	float shaftRelief = inNearS ? u_shadowReliefNear : u_shadowReliefFar;
	float shaftBias = u_shadowBias
		+ u_shadowSoftSlopeBias * u_shadowSoftRadius * u_shadowKernelSlopeScale * shaftRelief;
	return shaftDepth <= shaftMap + shaftBias ? 1.0 : 0.0;
}

void main()
{
	vec4 albedo = v_color * texture2D(u_texture, v_texcoord);
	// [v0.1.109] DH 规格 · 噪声补细节（与 HLSL 段同一算法）
	if (u_channel < 0.5 && u_lodNoiseEnable > 0.5)
	{
		float lodLum = (albedo.r + albedo.g + albedo.b) / 3.0;
		float lodAmp = u_lodNoiseIntensity * 0.01
			* (1.0 - (2.0 * lodLum - 1.0) * (2.0 * lodLum - 1.0)) * albedo.a;
		vec3 lodQ = floor(v_world * max(u_lodNoiseSteps, 0.001)) / max(u_lodNoiseSteps, 0.001);
		float lodRnd = lodRand3(lodQ) * 2.0 * lodAmp - lodAmp;
		vec3 lodNoisy = albedo.rgb + (1.0 - albedo.rgb) * lodRnd;
		if (u_lodNoiseDropoff > 0.5)
		{
			float lodDistF = min(length(v_world - u_viewPosition) / u_lodNoiseDropoff, 1.0);
			lodNoisy = mix(lodNoisy, albedo.rgb, lodDistF);
		}
		albedo.rgb = clamp(lodNoisy, 0.0, 1.0);
	}
	// [v0.1.109] DH 规格 · 抖动淡出（与 HLSL 段同一算法）
	if (u_channel < 0.5 && u_lodDitherFade > 0.5)
	{
		float lodViewDist = length(v_world - u_viewPosition);
		float lodFade = smoothstep(u_lodFadeStart, u_lodFadeStart * 1.5, lodViewDist);
		if (lodFade <= lodIgn(gl_FragCoord.xy) + 0.001)
		{
			discard;
		}
	}
	vec3 n = normalize(v_normal);
	float lit;
	if (v_slopeTop > 0.5)
	{
		// [v0.1.77] top face: slope shading, single sun, same formula as SkylineLod.SlopeGainFromNormal
		// [v0.1.78] sun direction is the tracked sun (u_sunDir), not the fixed u_light1
		vec3 sun = normalize(u_sunDir);
		float slopeDot = dot(n, sun) / max(dot(vec3(0.0, 1.0, 0.0), sun), 0.0001);
		float slopeGain = mix(1.0, clamp(slopeDot, 0.35, 1.0), u_slopeStrength);
		lit = mix(1.0, slopeGain, u_sunAmount);
	}
	else
	{
		// wall face: six-face factor from the game's own lighting formula
		lit = 0.5 + max(dot(n, u_light1), 0.0) + max(dot(n, u_light2), 0.0);
		lit = clamp(lit / max(u_topLight, 0.0001), 0.0, 1.0);
	}
	if (u_channel > 1.5 && u_channel < 2.5)
	{
		int mid = int(v_matid + 0.5);
		int id = mid - (mid / 4096) * 4096;
		if (id < 0) id += 4096;
		float r = float(id / 256) / 15.0;
		float g = float((id / 16) - (id / 256) * 16) / 15.0;
		float b = float(id - (id / 16) * 16) / 15.0;
		gl_FragColor = vec4(r, g, b, 1.0);
		return;
	}
	if (u_channel > 0.5 && u_channel < 1.5)
	{
		gl_FragColor = vec4(n * 0.5 + 0.5, 1.0);
		return;
	}
	if (u_channel > 2.5)
	{
		gl_FragColor = vec4(lit, lit, lit, 1.0);
		return;
	}
	vec3 rgb = mix(albedo.rgb * lit, u_fogColor * v_color.a, v_fog);
	// [v0.1.115] 里程碑 2.3：**LOD 的体素也接收太阳阴影**（与 HLSL 段/地形同一口径：
	//   同一张太阳深度图、同一 bias、同一昼光因子与远处淡出；出图 = 照到）
	if (u_lodShadowReceive > 0.5)
	{
		float lodShadowLit = sunShaftVisibility(v_world);
		float lodShadowDist = length(v_world - u_viewPosition);
		float lodShadowFade = 1.0;
		if (u_shadowFadeEnd > u_shadowFadeStart)
		{
			lodShadowFade = 1.0 - smoothstep(u_shadowFadeStart, u_shadowFadeEnd, lodShadowDist);
		}
		lit *= 1.0 - u_shadowStrength * u_shadowDayFactor * lodShadowFade * (1.0 - lodShadowLit);
		rgb = mix(albedo.rgb * lit, u_fogColor * v_color.a, v_fog);
	}
	// [v0.1.105] 体积雾 + 体积神光接到 LOD 层（与 HLSL 段同一算法）
	if (u_vfEnable > 0.5)
	{
		vec3 vfDelta = v_world - u_viewPosition;
		float vfLen = length(vfDelta);
		if (vfLen > 0.001)
		{
			vec3 vfRd = vfDelta / vfLen;
			float vfT0 = 0.0;
			float vfT1 = min(vfLen, u_vfMaxDistance);
			if (abs(vfRd.y) > 1e-5)
			{
				float vfTa = (u_vfBottomY - u_viewPosition.y) / vfRd.y;
				float vfTb = (u_vfTopY - u_viewPosition.y) / vfRd.y;
				vfT0 = max(vfT0, min(vfTa, vfTb));
				vfT1 = min(vfT1, max(vfTa, vfTb));
			}
			else if (u_viewPosition.y < u_vfBottomY || u_viewPosition.y > u_vfTopY)
			{
				vfT1 = 0.0;
			}
			if (vfT1 > vfT0)
			{
				float vfDt = (vfT1 - vfT0) * 0.125;
				float vfJit = vfHash12(v_world.xz * 41.0) * vfDt;
				float vfOd = 0.0;
				float vfScat = 0.0;
				for (int vfI = 0; vfI < 8; vfI++)
				{
					vec3 vfP = u_viewPosition + vfRd * (vfT0 + vfJit + vfDt * (float(vfI) + 0.5));
					float vfD = vfDensityAt(vfP);
					vfOd += vfD;
					vfScat += vfD * sunShaftVisibility(vfP);
				}
				float vfOdScale = vfDt * u_vfDensity;
				float vfOdRaw = max(vfOd, 1e-5);
				vfOd *= vfOdScale;
				float vfAlpha = clamp((1.0 - exp(-vfOd)) * u_vfStrength, 0.0, 1.0);
				vec3 vfCol = mix(u_vfColor, max(u_fogColor, vec3(0.02, 0.02, 0.02)), u_vfSkyMix);
				// [v0.1.117] 霾 / 光轴解耦（与 HLSL 段同一算法）
				rgb = mix(rgb, vfCol, vfAlpha * u_vfHaze);
				if (u_vfSunShaft > 0.0)
				{
					float vfPhase = 0.25 + 0.75 * pow(clamp(dot(vfRd, u_sunDir), 0.0, 1.0), u_vfPhasePower);
					float vfLit = clamp(vfScat / vfOdRaw, 0.0, 1.0);
					rgb += u_vfSunColor * (vfLit * vfAlpha * vfPhase * u_vfSunShaft);
				}
			}
		}
	}
	gl_FragColor = vec4(rgb, albedo.a);
}

#endif";

        // ---------------------------------------------------------------- 状态

        static Shader EnsureShader() {
            m_shader ??= new Shader(VolumeVsh, VolumePsh);
            return m_shader;
        }

        static SamplerState EnsureSampler() {
            m_sampler ??= SettingsManager.TerrainMipmapsEnabled
                ? new SamplerState {
                    AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                    FilterMode = TextureFilterMode.PointMipLinear, MaxLod = 4f
                }
                : new SamplerState {
                    AddressModeU = TextureAddressMode.Clamp, AddressModeV = TextureAddressMode.Clamp,
                    FilterMode = TextureFilterMode.Point, MaxLod = 0f
                };
            return m_sampler;
        }

        /// <summary>设置体积着色器的全部 uniform（相机/雾/光照/通道）。返回 null = 现在不能画。</summary>
        public static Shader PrepareVolumeShader(Camera camera, float yOffset, int channel) {
            try {
                SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
                if (subsystemTerrain == null || sky == null || camera == null) {
                    return null;
                }
                SkylineFaceShading.Refresh();     // 与 CPU 侧同源：每次画之前按同一公式取一遍因子
                Shader shader = EnsureShader();
                // [v0.1.105] **体积雾 + 体积神光 + 阴影图**绑到 LOD 的着色器上（与真地形同一个绑定函数）。
                // 必须在下面 LOD 自己的绑定**之前**调用 —— 后面会把 `u_sunDir` 覆盖成 LOD 自己的
                // 坡向太阳方向（`SlopeSunDirection()`），那是这条管线要的口径。
                string bindErr = SkylineRuntime.BindShadowFogParams(shader, SkylineRuntime.ShadowSampleReady);
                if (bindErr.Length > 0) {
                    m_lastError = "BindShadowFogParams: " + bindErr;
                }
                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                Vector3 v = new(MathF.Floor(viewPosition.X), 0f, MathF.Floor(viewPosition.Z));
                Matrix matrix = Matrix.CreateTranslation(0f, yOffset, 0f)
                    * Matrix.CreateTranslation(v - viewPosition)
                    * camera.ViewMatrix.OrientationMatrix * camera.ProjectionMatrix;
                shader.GetParameter("u_origin", true).SetValue(new Vector2(v.X, v.Z));
                shader.GetParameter("u_viewProjectionMatrix", true).SetValue(matrix);
                shader.GetParameter("u_viewPosition", true).SetValue(viewPosition);
                shader.GetParameter("u_texture", true).SetValue(
                    subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                shader.GetParameter("u_samplerState", true).SetValue(EnsureSampler());
                // 光照常量**直接取自游戏**（不是我们自己编的数）：方向光与归一化基准
                shader.GetParameter("u_light1", true).SetValue(LightingManager.DirectionToLight1);
                shader.GetParameter("u_light2", true).SetValue(LightingManager.DirectionToLight2);
                // [v0.1.78] 坡向明暗用**真太阳**（与 CPU 侧 `SkylineLod.SlopeSunDirection()` 同源）；
                // 立面的六面因子仍用游戏本体的固定方向光 u_light1/u_light2（那是游戏自己的光照模型）。
                shader.GetParameter("u_sunDir", true).SetValue(SkylineLod.SlopeSunDirection());
                shader.GetParameter("u_topLight", true).SetValue(SkylineFaceShading.TopFactor);
                // [v0.1.77] 坡向明暗的两个 uniform：强度与日照量 —— 与 CPU 侧 `SlopeGainFromNormal` 同源，
                // 所以两条路径算的必然是同一个数（这就是"坡向明暗迁到 GPU"能断言到量化误差的前提）。
                shader.GetParameter("u_slopeStrength", true).SetValue(SkylineLod.SlopeShadingStrength);
                shader.GetParameter("u_sunAmount", true).SetValue(SkylineLod.SunAmount);
                shader.GetParameter("u_channel", true).SetValue((float)channel);
                shader.GetParameter("u_fogYMultiplier", true).SetValue(sky.VisibilityRangeYMultiplier);
                shader.GetParameter("u_fogColor", true).SetValue(new Vector3(sky.ViewFogColor));
                shader.GetParameter("u_fogBottomTopDensity", true)
                    .SetValue(SkylineRuntime.FogBand(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity), "lodVolume"));
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(SkylineRuntime.HazeStartDensity(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity), "lodVolume"));
                // [v0.1.109] DH 规格的两项"观感"：抖动淡出 + 噪声补细节
                {
                    bool look = SkylineLodLook.Enabled;
                    // [v0.1.111] DH 的 `overdrawPrevention` 把 LOD 的内边界从"视距"推到"视距 × 0.4"，
                    // 抖动淡出必须跟着**新的内边界**起算（默认取 `SkylineLod.LastSkipRadius`），否则
                    // 重叠带（0.4R..R）会露出一条硬边；显式设了 `DitherFadeStartMetres` 就听它的。
                    float fadeStart = SkylineLodLook.DitherFadeStartMetres > 0.5f
                        ? SkylineLodLook.DitherFadeStartMetres
                        : MathF.Max(SkylineLod.LastSkipRadius, 1f);
                    shader.GetParameter("u_lodDitherFade", true)
                        .SetValue(look && SkylineLodLook.DitherFade ? 1f : 0f);
                    shader.GetParameter("u_lodFadeStart", true).SetValue(fadeStart);
                    shader.GetParameter("u_lodNoiseEnable", true)
                        .SetValue(look && SkylineLodLook.NoiseEnabled && SkylineLodLook.NoiseIntensity > 0f ? 1f : 0f);
                    shader.GetParameter("u_lodNoiseSteps", true)
                        .SetValue(Math.Max(SkylineLodLook.NoiseSteps, 0.25f));
                    shader.GetParameter("u_lodNoiseIntensity", true)
                        .SetValue(Math.Max(SkylineLodLook.NoiseIntensity, 0f));
                    shader.GetParameter("u_lodNoiseDropoff", true)
                        .SetValue(Math.Max(SkylineLodLook.NoiseDropoff, 0f));
                }
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                return shader;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLodVolume.PrepareVolumeShader: {e.Message}");
                return null;
            }
        }

        /// <summary>在真实画面里画一张**属性格式**的壳网格（由演示层在体素模式下调用）。</summary>
        public static void Draw(Camera camera, SurfaceVoxelMesh mesh, float yOffset) {
            if (mesh?.VertexBuffer == null || mesh.IndexCount == 0) {
                return;
            }
            if (!mesh.HasAttributes) {
                m_lastError = "网格不是属性格式（SurfaceVoxelMesh.Build(..., attributes:true)）";
                return;
            }
            Shader shader = PrepareVolumeShader(camera, yOffset, Channel);
            if (shader == null) {
                return;
            }
            try {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, mesh.VertexBuffer, mesh.IndexBuffer,
                    0, mesh.IndexCount);
                m_draws++;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineLodVolume.Draw: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- 离屏取证

        /// <summary>
        /// 把一张壳网格离屏画一次并回读。`volume=true` 用体积着色器（要求属性格式），
        /// `volume=false` 用游戏 `Opaque` 着色器 + CPU 烘焙的面因子（要求烘焙格式）。
        /// </summary>
        static Image RenderOffscreen(Camera camera, SurfaceVoxelMesh mesh, bool volume, int channel,
                                     int size, float yOffset) {
            return RenderLayers(camera, [(mesh.VertexBuffer, mesh.IndexBuffer, mesh.IndexCount)],
                volume, channel, size, yOffset);
        }

        /// <summary>
        /// [v0.1.60] 把**任意一组** (顶点缓冲, 索引缓冲, 索引数) 离屏画一次并回读。
        /// 生产层的三个 LOD 网格就是用它取证的（`skyline.LodLayerCapture`）——
        /// 通道 1/2 直接证明远景 LOD 的**法线/材质 id 属性到了片元**。
        /// </summary>
        public static Image RenderLayers(
            Camera camera,
            IReadOnlyList<(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int IndexCount)> layers,
            bool volume, int channel, int size, float yOffset) {
            RenderTarget2D previousTarget = Display.RenderTarget;
            Viewport previousViewport = Display.Viewport;
            Rectangle previousScissor = Display.ScissorRectangle;
            try {
                if (m_rt == null || m_rt.Width != size) {
                    Utilities.Dispose(ref m_rt);
                    m_rt = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                Display.RenderTarget = m_rt;
                // 必须显式清深度（v0.1.58 的坑：只清颜色不清深度 → 片元全被深度测试丢掉）
                Display.Clear(new Vector4(0f, 0f, 0f, 0f), 1f, 0);
                Display.Viewport = new Viewport(0, 0, size, size);
                Display.ScissorRectangle = new Rectangle(0, 0, size, size);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                Shader shader = volume
                    ? PrepareVolumeShader(camera, yOffset, channel)
                    : SkylineCubeSurfaceDemo.PrepareTerrainShader(camera, yOffset);
                if (shader == null) {
                    return null;
                }
                foreach ((VertexBuffer layerVb, IndexBuffer layerIb, int layerIndices) in layers) {
                    if (layerVb == null || layerIb == null || layerIndices <= 0) {
                        continue;
                    }
                    Display.DrawIndexed(PrimitiveType.TriangleList, shader, layerVb, layerIb, 0, layerIndices);
                }
                return m_rt.GetData(new Rectangle(0, 0, size, size));
            }
            finally {
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
        }

        /// <summary>
        /// `skyline.LodVolumeCapture(channel, mode)` 的实现：
        /// 把演示层的体素壳离屏画一次（可选**用属性格式/体积着色器**，也可以**用烘焙格式/Opaque**），
        /// 回读并统计覆盖率 / 平均亮度 / 去重颜色数。
        /// 通道 1/2 的"去重颜色数"分别证明**法线**与**材质 id**真的到了片元着色器。
        /// </summary>
        public static string Capture(bool useAttributes, int channel, int size) {
            JsonObject result = new();
            try {
                Camera camera = SkylineLod.ActiveCamera;
                SurfaceVoxelMesh mesh = SkylineCubeSurfaceDemo.FirstVoxelMesh(useAttributes);
                if (camera == null || mesh == null) {
                    result["ok"] = false;
                    result["err"] = camera == null ? "no camera" : "no voxel mesh (先 skyline.CubeSurfaceHarvest)";
                    return result.ToJsonString();
                }
                size = Math.Clamp(size <= 0 ? Size : size, 64, Math.Min(Display.MaxTextureSize, 1024));
                Image image = RenderOffscreen(camera, mesh, useAttributes, channel, size,
                    SkylineCubeSurfaceDemo.DrawYOffset);
                if (image == null) {
                    result["ok"] = false;
                    result["err"] = "shader not ready";
                    return result.ToJsonString();
                }
                m_captures++;
                result["ok"] = true;
                result["attributes"] = useAttributes;
                result["channel"] = channel;
                result["size"] = size;
                result["stats"] = Stats(image, out int distinct);
                result["distinctColors"] = distinct;
                result["mesh"] = mesh.DescribeObject();
                result["note"] = "attributes=true 走 SkylineLodVolume（法线/材质 id 属性），false 走游戏 Opaque + CPU 烘焙；"
                    + "channel 1 = 法线可视化（去重颜色数应 ≤ 6 面 × 量化）、channel 2 = 材质 id 的 16 进制编码（可精确反解）";
            }
            catch (Exception e) {
                m_lastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>
        /// `skyline.LodVolumeCompare()`：把**同一片壳**分别用
        /// ①CPU 烘焙面因子 + 游戏 Opaque、②GPU 法线属性 + SkylineLodVolume 画一遍，
        /// 逐像素比较 —— 两条路径是同一条公式，差应当只有 8 位量化误差。
        /// 这是"顶点属性真的接上了、而且算法没写歪"的数值断言。
        /// </summary>
        public static string CaptureCompare(int size) {
            JsonObject result = new();
            try {
                Camera camera = SkylineLod.ActiveCamera;
                SurfaceVoxelMesh baked = SkylineCubeSurfaceDemo.FirstVoxelMesh(false);
                SurfaceVoxelMesh attr = SkylineCubeSurfaceDemo.FirstVoxelMesh(true);
                if (camera == null || baked == null || attr == null) {
                    result["ok"] = false;
                    result["err"] = camera == null ? "no camera"
                        : "先 skyline.CubeSurfaceHarvest（两套网格：烘焙格式 + 属性格式）";
                    return result.ToJsonString();
                }
                size = Math.Clamp(size <= 0 ? Size : size, 64, Math.Min(Display.MaxTextureSize, 1024));
                Image a = RenderOffscreen(camera, baked, false, 0, size, SkylineCubeSurfaceDemo.DrawYOffset);
                Image b = RenderOffscreen(camera, attr, true, 0, size, SkylineCubeSurfaceDemo.DrawYOffset);
                if (a == null || b == null) {
                    result["ok"] = false;
                    result["err"] = "render failed";
                    return result.ToJsonString();
                }
                result["ok"] = true;
                result["size"] = size;
                result["cpuBaked"] = Stats(a, out _);
                result["gpuVolume"] = Stats(b, out _);
                long sum = 0, max = 0, over8 = 0, both = 0;
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        Color ca = a.GetPixel(x, y);
                        Color cb = b.GetPixel(x, y);
                        if (ca.A == 0 && cb.A == 0) {
                            continue;
                        }
                        both++;
                        int dr = Math.Abs(ca.R - cb.R), dg = Math.Abs(ca.G - cb.G), db = Math.Abs(ca.B - cb.B);
                        int d = Math.Max(dr, Math.Max(dg, db));
                        sum += d;
                        max = Math.Max(max, d);
                        if (d > 8) {
                            over8++;
                        }
                    }
                }
                result["comparedPixels"] = both;
                result["meanAbsDiff"] = both > 0 ? Math.Round(sum / (double)both, 3) : 0.0;
                result["maxAbsDiff"] = max;
                result["diffPxGt8"] = over8;
                result["verdict"] = both > 0 && max <= 4
                    ? "GPU 法线面明暗与 CPU 烘焙一致（差 ≤ 4/255，纯量化）"
                    : "两条路径有明显差异 —— 要么属性没到 GPU，要么公式不一致";
            }
            catch (Exception e) {
                m_lastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.60] `skyline.LodLayerCapture(channel, size, attrShader)` 的实现：
        /// 把**生产层的三个远端 LOD 网格**（粗 16 m / 细 8 m / 近环 4 m）离屏画一遍并回读。
        /// `attrShader=true` 要求 `LodVertexAttributes=true`（网格是属性格式），
        /// 通道 1（法线）/ 通道 2（材质 id）若回读到非空内容，就是"属性真的到了片元"的凭证。
        /// </summary>
        public static string CaptureLodLayers(int channel, int size, bool attrShader) {
            JsonObject result = new();
            bool savedShader = SkylineRuntime.LodAttrShaderOn;
            bool flip = savedShader != attrShader;
            try {
                Camera camera = SkylineLod.ActiveCamera;
                if (camera == null) {
                    result["ok"] = false;
                    result["err"] = "no camera";
                    return result.ToJsonString();
                }
                if (attrShader && !SkylineRuntime.LodVertexAttributes) {
                    result["ok"] = false;
                    result["err"] = "要求属性格式网格：先 skyline.LodVertexAttributes(true)";
                    return result.ToJsonString();
                }
                size = Math.Clamp(size <= 0 ? Size : size, 64, Math.Min(Display.MaxTextureSize, 1024));
                // [v0.1.77] **网格口径必须跟着着色器走**：体积着色器自己会乘明暗，所以它要画"未烘焙"的网格。
                // 以前没做 → `LodLayerCapture(..., attrShader: true)` 画的是"已烘焙"的网格 →
                // 立面被暗化两遍（实测 meanLuma 87 对 125），看起来像"GPU 面明暗算错了"。
                if (flip) {
                    SkylineRuntime.LodAttrShaderOn = attrShader;
                    SkylineLod.RebuildNow();          // 重建后再取缓冲引用（下面的 layers 必须用新缓冲）
                }
                List<(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int IndexCount)> layers = [
                    (SkylineLod.CoarseVertexBuffer, SkylineLod.CoarseIndexBuffer, SkylineLod.CoarseIndexCount),
                    (SkylineLod.FineVertexBuffer, SkylineLod.FineIndexBuffer, SkylineLod.FineIndexCount),
                    (SkylineLod.NearVertexBuffer, SkylineLod.NearIndexBuffer, SkylineLod.NearIndexCount)
                ];
                int drawnIndices = 0;
                foreach ((_, _, int n) in layers) {
                    drawnIndices += Math.Max(0, n);
                }
                if (drawnIndices == 0) {
                    result["ok"] = false;
                    result["err"] = "三层 LOD 网格都是空的（等 LOD 建好网格再采）";
                    return result.ToJsonString();
                }
                Image image = RenderLayers(camera, layers, attrShader, channel, size, 0f);
                if (image == null) {
                    result["ok"] = false;
                    result["err"] = "shader not ready";
                    return result.ToJsonString();
                }
                m_captures++;
                result["ok"] = true;
                result["attrShader"] = attrShader;
                result["channel"] = channel;
                result["size"] = size;
                result["meshVersion"] = SkylineLod.MeshVersion;
                result["coarseIndices"] = SkylineLod.CoarseIndexCount;
                result["fineIndices"] = SkylineLod.FineIndexCount;
                result["nearIndices"] = SkylineLod.NearIndexCount;
                result["vertexStride"] = SkylineRuntime.LodVertexAttributes ? SkylineLodVertex.Stride : 20;
                result["stats"] = Stats(image, out int distinct);
                result["distinctColors"] = distinct;
                result["channelMeaning"] = "0=面明暗、1=法线可视化（去重颜色数应 ≥3）、2=材质 id 16 进制（可反解）、3=面因子灰度";
            }
            catch (Exception e) {
                m_lastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            finally {
                if (flip) {
                    SkylineRuntime.LodAttrShaderOn = savedShader;
                    SkylineLod.RebuildNow();
                }
            }
            return result.ToJsonString();
        }

        /// <summary>
        /// [v0.1.60] 逐像素比较两张同尺寸回读图（只在双方都不透明处比较）：
        /// 返回覆盖像素数、平均/最大通道差、>8 的像素数。用于"属性开/关应逐位一致"与
        /// "GPU 面明暗 == CPU 烘焙"这两条断言。
        /// </summary>
        public static JsonObject CompareImages(Image a, Image b) {
            JsonObject result = new();
            if (a == null || b == null) {
                result["ok"] = false;
                result["err"] = "image null";
                return result;
            }
            if (a.Width != b.Width || a.Height != b.Height) {
                result["ok"] = false;
                result["err"] = "size mismatch";
                return result;
            }
            long sum = 0, max = 0, over8 = 0, both = 0, onlyA = 0, onlyB = 0;
            for (int y = 0; y < a.Height; y++) {
                for (int x = 0; x < a.Width; x++) {
                    Color ca = a.GetPixel(x, y);
                    Color cb = b.GetPixel(x, y);
                    if (ca.A == 0 && cb.A == 0) {
                        continue;
                    }
                    if (ca.A == 0) {
                        onlyB++;
                        continue;
                    }
                    if (cb.A == 0) {
                        onlyA++;
                        continue;
                    }
                    both++;
                    int d = Math.Max(Math.Abs(ca.R - cb.R), Math.Max(Math.Abs(ca.G - cb.G), Math.Abs(ca.B - cb.B)));
                    sum += d;
                    max = Math.Max(max, d);
                    if (d > 8) {
                        over8++;
                    }
                }
            }
            result["ok"] = true;
            result["comparedPixels"] = both;
            result["onlyInA"] = onlyA;
            result["onlyInB"] = onlyB;
            result["meanAbsDiff"] = both > 0 ? Math.Round(sum / (double)both, 3) : 0.0;
            result["maxAbsDiff"] = max;
            result["diffPxGt8"] = over8;
            result["identical"] = both > 0 && max == 0 && onlyA == 0 && onlyB == 0;
            return result;
        }

        static JsonObject Stats(Image image, out int distinct) {
            int covered = 0;
            double luma = 0.0;
            HashSet<int> set = [];
            for (int y = 0; y < image.Height; y++) {
                for (int x = 0; x < image.Width; x++) {
                    Color c = image.GetPixel(x, y);
                    if (c.A == 0) {
                        continue;
                    }
                    covered++;
                    luma += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                    set.Add((c.R >> 3) << 10 | (c.G >> 3) << 5 | (c.B >> 3));
                }
            }
            distinct = set.Count;
            return new JsonObject {
                ["coveredPixels"] = covered,
                ["coverageRatio"] = Math.Round(covered / (double)(image.Width * image.Height), 4),
                ["meanLuma"] = Math.Round(covered > 0 ? luma / covered : 0.0, 2),
                ["distinct5bitColors"] = set.Count
            };
        }

        public static JsonObject Describe() {
            JsonObject result = new() {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["channel"] = Channel,
                ["size"] = Size,
                ["shaderReady"] = m_shader != null,
                ["draws"] = m_draws,
                ["captures"] = m_captures,
                ["lastError"] = m_lastError,
                ["light1"] = new JsonArray(LightingManager.DirectionToLight1.X, LightingManager.DirectionToLight1.Y, LightingManager.DirectionToLight1.Z),
                ["light2"] = new JsonArray(LightingManager.DirectionToLight2.X, LightingManager.DirectionToLight2.Y, LightingManager.DirectionToLight2.Z),
                ["topLight"] = Math.Round(SkylineFaceShading.TopFactor, 4),
                // [v0.1.78] 坡向明暗用的太阳方向（真太阳）——回归清单断言它 == TrackedLightDirection()
                ["sunDir"] = new JsonArray(SkylineLod.SlopeSunDirection().X, SkylineLod.SlopeSunDirection().Y,
                    SkylineLod.SlopeSunDirection().Z),
                ["sunSource"] = "SkylineLod.SlopeSunDirection() = SkylineRuntime.TrackedLightDirection()"
            };
            result["note"] = "体积着色器的光照常量直接取自 LightingManager（不是另编的一套）；"
                + "顶点属性见 skyline.LodVertexInfo()；A/B 证据用 skyline.LodVolumeCompare()";
            return result;
        }
    }

    /// <summary>桥：`skyline.LodVolume*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>开关"用体积着色器画演示层的体素壳"（默认关；要配合 skyline.CubeSurfaceVoxel(true)）。</summary>
        public static string LodVolume(bool enabled) {
            SkylineLodVolume.Enabled = enabled;
            JsonObject result = SkylineLodVolume.Describe();
            result["hint"] = "演示层还要 skyline.CubeSurfaceVoxel(true) + skyline.CubeSurfaceDraw(true)";
            return result.ToJsonString();
        }

        /// <summary>0=面明暗 1=法线可视化 2=材质 id 可视化 3=面因子灰度。</summary>
        public static string LodVolumeChannel(int channel) {
            SkylineLodVolume.Channel = Math.Clamp(channel, 0, 3);
            return SkylineLodVolume.Describe().ToJsonString();
        }

        public static string LodVolumeSize(int size) {
            SkylineLodVolume.Size = Math.Clamp(size, 64, 1024);
            return SkylineLodVolume.Describe().ToJsonString();
        }

        /// <summary>离屏画一张壳网格并回读统计（attributes=true 时要求属性格式 + 体积着色器）。</summary>
        public static string LodVolumeCapture(bool attributes, int channel, int size) =>
            SkylineLodVolume.Capture(attributes, channel, size);

        /// <summary>同一片壳：CPU 烘焙（Opaque）vs GPU 法线（体积着色器）逐像素比较。</summary>
        public static string LodVolumeCompare(int size) => SkylineLodVolume.CaptureCompare(size);

        public static string LodVolumeInfo() => SkylineLodVolume.Describe().ToJsonString();
    }
}
