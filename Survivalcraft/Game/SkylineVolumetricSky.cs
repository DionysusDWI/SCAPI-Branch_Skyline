using Engine;
using Engine.Graphics;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.67：**自研体积云雾替换静态云层** —— 里程碑 3.3。
    ///
    /// 用户口径（逐字）："**体积云=体积雾=需要替换游戏原有 Fog 的自研体积云雾，原有的云层只是一层静态贴图
    /// 所以直接弃用，后续需要在不同高度上分布我们的体积云雾**"。
    ///
    /// ## 为什么画在"天空穹顶"上，而不是不透明 pass 里
    /// 把云雾加在不透明片元里是最省事的位置（我们已经有一个注入变体），但它有个**结构性缺陷**：
    /// 天空是**背景**，没有片元，所以云**永远不会出现在天上** —— 而云本来就是要在天上的。
    /// 游戏画天空穹顶用的 `SubsystemSky.m_shaderFlat`（`UnlitShader`）**开了深度读**
    /// （`DepthStencilState.DepthRead`），所以穹顶片元会被地形正确遮挡：
    /// 把云雾做进穹顶片元，**既在天上、又天然被地形遮住**，而且每像素沿视线步进就是**真体积**。
    ///
    /// ## 实现
    ///   * 变体 shader = `Unlit.vsh/psh` 的语义（`color + additiveColor`）+ 8 步**手工展开**的 raymarch；
    ///   * 密度 = **竖向剖面**（云带上下沿平滑淡出）× **双层二维值噪声**，并随 `WindSpeed` 平移 → 云会飘；
    ///   * 颜色 = 受光/背光两色按"沿视线的平均受光率"插值，受光率随高度上升（薄云更亮）；
    ///   * 步进做**逐像素抖动**（由视线方向的哈希给出，确定性、跨帧稳定）打破分层带纹；
    ///   * `ReplaceStaticClouds`（默认 true）让 `SubsystemSky.DrawClouds` 直接返回 —— **静态云层弃用**。
    ///
    /// ## 硬口径
    ///   1. `Enabled=false` 时**必须与原版逐位一致**：`ResolveSkyDomeShader` 直接返回原 `UnlitShader`，
    ///      `ReplaceStaticClouds` 也只在自己的开关下生效；
    ///   2. 不改世界数据、不改存档；
    ///   3. 不抛异常：shader 构造/绑定失败自动关掉自己并记 `lastError`。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>体积云雾总开关。**默认开**（用户口径是"弃用静态云层"）。</summary>
        public static bool VolumetricCloudsEnabled { get; set; } = true;

        /// <summary>是否**弃用**游戏原有静态云层（`SubsystemSky.DrawClouds` 直接返回）。默认开。</summary>
        public static bool ReplaceStaticClouds { get; set; } = true;

        /// <summary>云带底高（米）。默认 300 —— **高于地面**，否则贴地平视的近景也会被「云」糊住。</summary>
        public static float CloudBandBottomY { get; set; } = 300f;

        /// <summary>云带顶高（米）。默认 600（与原版头顶那层云同高）。</summary>
        public static float CloudBandTopY { get; set; } = 600f;

        /// <summary>密度尺度（每米消光）。**这个值要和阈值一起调**：太大整片天会糊成一层奶白。</summary>
        public static float CloudDensity { get; set; } = 0.05f;

        /// <summary>噪声频率（1/米）。0.0009 ≈ 1100 m 一个结构。</summary>
        public static float CloudScale { get; set; } = 0.0009f;

        /// <summary>覆盖率阈值：噪声低于它就当作无云（越大云越少）。</summary>
        public static float CloudThreshold { get; set; } = 0.50f;

        /// <summary>云被太阳照亮的程度（0=全背光，1=全受光）。</summary>
        public static float CloudSunLit { get; set; } = 0.85f;

        /// <summary>最终不透明度上限（取小于 1 的值可以保留一点天空透出来）。</summary>
        public static float CloudOpacity { get; set; } = 1f;

        /// <summary>风向/风速（米/秒），随真实时间平移噪声。</summary>
        public static Vector2 CloudWind { get; set; } = new(6f, 2.5f);

        /// <summary>受光色（默认接近白）。</summary>
        public static Vector3 CloudLitColor { get; set; } = new(1f, 1f, 1f);

        /// <summary>背光色。**必须与天空有明显明度差**，否则云"在里面但看不见"
        /// （实测：0.52/0.58/0.68 时整片天空看不出云；压到 0.45/0.50/0.60 才读得出来）。</summary>
        public static Vector3 CloudShadowColor { get; set; } = new(0.45f, 0.50f, 0.60f);

        /// <summary>raymarch 的最远距离（米）。云带本身有顶，所以这个值足够大即可。</summary>
        public static float CloudMaxDistance { get; set; } = 6000f;

        /// <summary>噪声随高度**剪切**的强度（0 = 每一层云用同一张图案 → 看起来像一层贴纸）。</summary>
        public static float CloudHeightShear { get; set; } = 0.6f;

        /// <summary>步数（**固定 8**：段是手工展开的，不是循环 —— 引擎的 shader 方言没有验证过循环）。</summary>
        public static int CloudSteps { get; } = 8;

        static Shader m_cloudSkyShader;
        static string m_cloudSkyLastError = "";
        static long m_cloudSkyResolved;
        static long m_cloudSkyFallbacks;
        static string m_cloudSkyLastReason = "";
        static int m_cloudSkyCompileGuard;

        public static string VolumetricCloudDescribe() {
            JsonObject o = new() {
                ["enabled"] = VolumetricCloudsEnabled,
                ["replaceStaticClouds"] = ReplaceStaticClouds,
                ["bandBottomY"] = (double)CloudBandBottomY,
                ["bandTopY"] = (double)CloudBandTopY,
                ["density"] = (double)CloudDensity,
                ["scale"] = (double)CloudScale,
                ["threshold"] = (double)CloudThreshold,
                ["sunLit"] = (double)CloudSunLit,
                ["opacity"] = (double)CloudOpacity,
                ["wind"] = new JsonArray(CloudWind.X, CloudWind.Y),
                ["steps"] = CloudSteps,
                ["skyShaderResolved"] = m_cloudSkyResolved,
                ["skyShaderFallbacks"] = m_cloudSkyFallbacks,
                ["skyShaderLastReason"] = m_cloudSkyLastReason,
                ["lastError"] = m_cloudSkyLastError
            };
            o["note"] = "云雾画在**天空穹顶**片元里（穹顶开深度读 → 天然被地形遮挡），不是全屏后处理";
            return o.ToJsonString();
        }

        /// <summary>
        /// 解析天空穹顶实际使用的 shader：启用体积云时返回自研变体，否则返回原 `UnlitShader`（逐位不变）。
        /// 见 `SkylineVolumetricSky` 的类注释：这个位置才能让云"出现在天上"且被地形遮挡。
        /// </summary>
        public static Shader ResolveSkyDomeShader(Shader fallback, Camera camera, Vector4 color, Vector4 additiveColor) {
            if (!VolumetricCloudsEnabled || camera == null) {
                m_cloudSkyFallbacks++;
                m_cloudSkyLastReason = !VolumetricCloudsEnabled ? "disabled" : "noCamera";
                return fallback;
            }
            try {
                if (m_cloudSkyShader == null) {
                    m_cloudSkyShader = new Shader(CloudSkyVsh, CloudSkyPsh);
                    m_cloudSkyCompileGuard++;
                }
                Shader shader = m_cloudSkyShader;
                shader.GetParameter("u_skyViewProjection", true).SetValue(camera.ViewProjectionMatrix);
                shader.GetParameter("u_skyCameraPos", true).SetValue(camera.ViewPosition);
                shader.GetParameter("u_color", true).SetValue(color);
                shader.GetParameter("u_additiveColor", true).SetValue(additiveColor);
                float time = (float)Time.RealTime;
                float bottom = Math.Min(CloudBandBottomY, CloudBandTopY - 1f);
                shader.GetParameter("u_volEnable", true).SetValue(1f);
                shader.GetParameter("u_volBottomY", true).SetValue(bottom);
                shader.GetParameter("u_volTopY", true).SetValue(Math.Max(CloudBandTopY, bottom + 1f));
                shader.GetParameter("u_volDensity", true).SetValue(Math.Max(CloudDensity, 0f));
                shader.GetParameter("u_volScale", true).SetValue(Math.Max(CloudScale, 1e-6f));
                shader.GetParameter("u_volWind", true).SetValue(CloudWind * time);
                shader.GetParameter("u_volThreshold", true).SetValue(Math.Clamp(CloudThreshold, 0f, 0.99f));
                shader.GetParameter("u_volSunLit", true).SetValue(Math.Clamp(CloudSunLit, 0f, 1f));
                shader.GetParameter("u_volOpacity", true).SetValue(Math.Clamp(CloudOpacity, 0f, 1f));
                shader.GetParameter("u_volLitColor", true).SetValue(CloudLitColor);
                shader.GetParameter("u_volShadowColor", true).SetValue(CloudShadowColor);
                shader.GetParameter("u_volMaxDistance", true).SetValue(Math.Max(CloudMaxDistance, 100f));
                shader.GetParameter("u_volShear", true).SetValue(Math.Max(CloudHeightShear, 0f));
                m_cloudSkyLastError = "";
                m_cloudSkyResolved++;
                m_cloudSkyLastReason = "resolved";
                return shader;
            }
            catch (Exception e) {
                m_cloudSkyLastError = e.Message;
                VolumetricCloudsEnabled = false;
                Log.Warning($"SkylineVolumetricSky: 云 shader 构造/绑定失败，已自动关闭：{e.Message}");
                return fallback;
            }
        }

        // ============================================================================================
        // 变体 shader：Unlit 的语义（color + additiveColor）+ 天空穹顶上的体积云 raymarch
        // ============================================================================================
        const string CloudSkyVsh = @"#ifdef HLSL

float4x4 u_skyViewProjection;
float3 u_skyCameraPos;
float4 u_color;

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	out float4 v_color : COLOR,
	out float3 v_local : TEXCOORD0,
	out float4 sv_position: SV_POSITION
)
{
	// 天空渐变色写在**顶点色**里（SubsystemSky.FillSkyVertexBuffer 把 CalculateSkyColor 写进 Color），
	// 所以这里必须乘上 a_color；第一版漏了它 → 整片天空变成均匀白（v_color 恒为 u_color）。
	v_color = u_color * a_color;
	v_local = a_position;
	sv_position = mul(float4(a_position + u_skyCameraPos, 1.0), u_skyViewProjection);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />

uniform mat4 u_skyViewProjection;
uniform vec3 u_skyCameraPos;
uniform vec4 u_color;

attribute vec3 a_position;
attribute vec4 a_color;

varying vec4 v_color;
varying vec3 v_local;

void main()
{
	v_color = u_color * a_color;
	v_local = a_position;
	gl_Position = u_skyViewProjection * vec4(a_position + u_skyCameraPos, 1.0);
	OPENGL_POSITION_FIX;
}

#endif
";

        const string CloudSkyPsh = @"#ifdef HLSL

float4 u_additiveColor;
float3 u_skyCameraPos;
float u_volEnable;
float u_volBottomY;
float u_volTopY;
float u_volDensity;
float u_volScale;
float2 u_volWind;
float u_volThreshold;
float u_volSunLit;
float u_volOpacity;
float3 u_volLitColor;
float3 u_volShadowColor;
float u_volMaxDistance;
float u_volShear;

// 值噪声（确定性，无纹理依赖 —— 引擎里没有现成的 3D 噪声资源）
float hash12(float2 p)
{
	float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, float3(p3.y, p3.z, p3.x) + 33.33);
	return frac((p3.x + p3.y) * p3.z);
}

float vnoise2(float2 p)
{
	float2 i = floor(p);
	float2 f = frac(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash12(i);
	float b = hash12(i + float2(1.0, 0.0));
	float c = hash12(i + float2(0.0, 1.0));
	float d = hash12(i + float2(1.0, 1.0));
	return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float volHeight(float3 p)
{
	return (p.y - u_volBottomY) / max(u_volTopY - u_volBottomY, 0.001);
}

float volDensity(float3 p)
{
	float h = volHeight(p);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	// 竖向剖面：上下沿平滑淡出，中间鼓
	float prof = smoothstep(0.0, 0.2, h) * smoothstep(1.0, 0.75, h);
	// 噪声随高度剪切：每一层高度看到的图案错开 → 出团块，而不是「一张贴纸横着拉」
	float2 q = (p.xz + p.y * u_volShear * float2(1.7, 1.1)) * u_volScale + u_volWind;
	float n = vnoise2(q) * 0.65 + vnoise2(q * 2.7 + float2(11.3, 7.1)) * 0.35;
	return max(0.0, n - u_volThreshold) * prof;
}

// 受光率：越靠上（越薄）越亮；u_volSunLit 是太阳整体强度
float volLit(float3 p)
{
	float h = clamp(volHeight(p), 0.0, 1.0);
	return u_volSunLit * (0.30 + 0.70 * h);
}

// 返回 (密度, 受光加权密度)
float2 volSample(float3 ro, float3 rd, float t)
{
	float3 p = ro + rd * t;
	float d = volDensity(p);
	return float2(d, volLit(p) * d);
}

void main(
	in float4 v_color : COLOR,
	in float3 v_local : TEXCOORD0,
	out float4 svTarget: SV_TARGET
)
{
	float4 sky = v_color + u_additiveColor;
	if (u_volEnable > 0.5)
	{
		// 穹顶顶点在**物体空间**就是「从相机出去的方向」（World 只是平移相机 + 投影）
		float3 rd = normalize(v_local);
		float3 ro = u_skyCameraPos;
		float t0 = 0.0;
		float t1 = u_volMaxDistance;
		if (abs(rd.y) > 1e-5)
		{
			float ta = (u_volBottomY - ro.y) / rd.y;
			float tb = (u_volTopY - ro.y) / rd.y;
			t0 = max(0.0, min(ta, tb));
			t1 = min(u_volMaxDistance, max(ta, tb));
		}
		else if (ro.y < u_volBottomY || ro.y > u_volTopY)
		{
			t1 = 0.0;
		}
		if (t1 > t0)
		{
			float dt = (t1 - t0) * 0.125;
			// 逐像素抖动：由**视线方向**的哈希给出 → 确定性、跨帧稳定，纯粹是打破步进分层
			float jitter = hash12(v_local.xz * 37.0) * dt;
			float od = 0.0;
			float lum = 0.0;
			float2 s;
			s = volSample(ro, rd, t0 + jitter + dt * 0.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 1.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 2.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 3.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 4.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 5.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 6.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 7.5); od += s.x; lum += s.y;
			od *= dt * u_volDensity;
			float alpha = saturate((1.0 - exp(-od)) * u_volOpacity);
			float rawOd = od / max(dt * u_volDensity, 1e-5);
			float lit = saturate(lum / max(rawOd, 1e-5));
			float3 cloud = lerp(u_volShadowColor, u_volLitColor, lit);
			sky.rgb = lerp(sky.rgb, cloud, alpha);
		}
	}
	svTarget = float4(sky.rgb, 1.0);
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;
#endif

uniform vec4 u_additiveColor;
uniform vec3 u_skyCameraPos;
uniform float u_volEnable;
uniform float u_volBottomY;
uniform float u_volTopY;
uniform float u_volDensity;
uniform float u_volScale;
uniform vec2 u_volWind;
uniform float u_volThreshold;
uniform float u_volSunLit;
uniform float u_volOpacity;
uniform vec3 u_volLitColor;
uniform vec3 u_volShadowColor;
uniform float u_volMaxDistance;
uniform float u_volShear;

varying vec4 v_color;
varying vec3 v_local;

float hash12(vec2 p)
{
	vec3 p3 = fract(vec3(p.x, p.y, p.x) * 0.1031);
	p3 += dot(p3, vec3(p3.y, p3.z, p3.x) + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float vnoise2(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = hash12(i);
	float b = hash12(i + vec2(1.0, 0.0));
	float c = hash12(i + vec2(0.0, 1.0));
	float d = hash12(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float volHeight(vec3 p)
{
	return (p.y - u_volBottomY) / max(u_volTopY - u_volBottomY, 0.001);
}

float volDensity(vec3 p)
{
	float h = volHeight(p);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.2, h) * smoothstep(1.0, 0.75, h);
	// 噪声随高度剪切（与 HLSL 段同一算法）
	vec2 q = (p.xz + p.y * u_volShear * vec2(1.7, 1.1)) * u_volScale + u_volWind;
	float n = vnoise2(q) * 0.65 + vnoise2(q * 2.7 + vec2(11.3, 7.1)) * 0.35;
	return max(0.0, n - u_volThreshold) * prof;
}

float volLit(vec3 p)
{
	float h = clamp(volHeight(p), 0.0, 1.0);
	return u_volSunLit * (0.30 + 0.70 * h);
}

vec2 volSample(vec3 ro, vec3 rd, float t)
{
	vec3 p = ro + rd * t;
	float d = volDensity(p);
	return vec2(d, volLit(p) * d);
}

void main()
{
	vec4 sky = v_color + u_additiveColor;
	if (u_volEnable > 0.5)
	{
		vec3 rd = normalize(v_local);
		vec3 ro = u_skyCameraPos;
		float t0 = 0.0;
		float t1 = u_volMaxDistance;
		if (abs(rd.y) > 1e-5)
		{
			float ta = (u_volBottomY - ro.y) / rd.y;
			float tb = (u_volTopY - ro.y) / rd.y;
			t0 = max(0.0, min(ta, tb));
			t1 = min(u_volMaxDistance, max(ta, tb));
		}
		else if (ro.y < u_volBottomY || ro.y > u_volTopY)
		{
			t1 = 0.0;
		}
		if (t1 > t0)
		{
			float dt = (t1 - t0) * 0.125;
			float jitter = hash12(v_local.xz * 37.0) * dt;
			float od = 0.0;
			float lum = 0.0;
			vec2 s;
			s = volSample(ro, rd, t0 + jitter + dt * 0.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 1.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 2.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 3.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 4.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 5.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 6.5); od += s.x; lum += s.y;
			s = volSample(ro, rd, t0 + jitter + dt * 7.5); od += s.x; lum += s.y;
			od *= dt * u_volDensity;
			float alpha = clamp((1.0 - exp(-od)) * u_volOpacity, 0.0, 1.0);
			float rawOd = od / max(dt * u_volDensity, 1e-5);
			float lit = clamp(lum / max(rawOd, 1e-5), 0.0, 1.0);
			vec3 cloud = mix(u_volShadowColor, u_volLitColor, lit);
			sky.rgb = mix(sky.rgb, cloud, alpha);
		}
	}
	gl_FragColor = vec4(sky.rgb, 1.0);
}

#endif
";
    }
}
