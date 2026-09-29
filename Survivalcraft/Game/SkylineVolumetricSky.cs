using Engine;
using Engine.Graphics;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.68：**自研体积云雾替换静态云层** —— 里程碑 3.3（两条云带）。
    ///
    /// 用户口径（逐字）："**体积云=体积雾=需要替换游戏原有 Fog 的自研体积云雾，原有的云层只是一层静态贴图
    /// 所以直接弃用，后续需要在不同高度上分布我们的体积云雾**"。
    ///
    /// ## 为什么画在「天空穹顶」上，而不是不透明 pass 或全屏后处理
    ///   * 不透明 pass：天空是**背景没有片元**，云**永远不会出现在天上** —— 而云就是要出现在天上的；
    ///   * 全屏后处理：需要**场景深度纹理**，引擎没有这条管线（`notes/57` 已标为最大不确定项）；
    ///   * **天空穹顶**（本版选用）：`SubsystemSky.Draw` 的穹顶 shader `m_shaderFlat` **开了深度读**，
    ///     穹顶片元会被地形正确遮挡；且穹顶顶点在**物体空间**就是「从相机出去的方向」。
    ///     ⇒ **既在天上、又天然被地形遮挡，而且每像素沿视线步进就是真体积。**
    ///
    /// ## 两条云带（v0.1.68 新增）
    /// 密度/光照/合成被抽成**带参数的函数**，同一条 8 步 raymarch 调用两次：
    ///   * **带 1「云海」**：默认 300..600 m，厚、密、受光对比强；
    ///   * **带 2「高层卷云」**：默认 850..1000 m，薄、淡、风更快、剪切更弱。
    /// 这就是用户要的「**在不同高度上分布我们的体积云雾**」；两条带**独立开关**，关掉带 2 即回到 v0.1.67 的单带。
    ///
    /// ## 硬口径
    ///   1. `Enabled=false` 时**必须与原版逐位一致**：`ResolveSkyDomeShader` 直接返回原 `UnlitShader`；
    ///   2. 不改世界数据、不改存档；
    ///   3. 不抛异常：shader 构造/绑定失败自动关掉自己并记 `lastError`。
    /// </summary>
    public static partial class SkylineRuntime {
        // ============================================================================================
        // 带 1「云海」
        // ============================================================================================

        /// <summary>体积云雾总开关。**默认开**（用户口径是「静态云层直接弃用」）。</summary>
        public static bool VolumetricCloudsEnabled { get; set; } = true;

        /// <summary>是否**弃用**游戏原有静态云层（`SubsystemSky.DrawClouds` 直接返回）。默认开。</summary>
        public static bool ReplaceStaticClouds { get; set; } = true;

        /// <summary>带 1 底高（米）。默认 300 —— **高于地面**，否则贴地平视的近景也会被「云」糊住。</summary>
        public static float CloudBandBottomY { get; set; } = 300f;

        /// <summary>带 1 顶高（米）。默认 600（与原版头顶那层云同高）。</summary>
        public static float CloudBandTopY { get; set; } = 600f;

        /// <summary>带 1 密度（每米消光）。**要和阈值一起调**：太大整片天会糊成一层奶白。</summary>
        public static float CloudDensity { get; set; } = 0.05f;

        /// <summary>带 1 噪声频率（1/米）。0.0009 ≈ 1100 m 一个结构。</summary>
        public static float CloudScale { get; set; } = 0.0009f;

        /// <summary>带 1 覆盖率阈值：噪声低于它就当作无云（越大云越少）。</summary>
        public static float CloudThreshold { get; set; } = 0.50f;

        /// <summary>带 1 受光程度（0=全背光，1=全受光）。</summary>
        public static float CloudSunLit { get; set; } = 0.85f;

        /// <summary>带 1 不透明度上限（取小于 1 的值可以保留一点天空透出来）。</summary>
        public static float CloudOpacity { get; set; } = 1f;

        /// <summary>
        /// 带 1 风向/风速（米/秒），随真实时间平移噪声。
        ///
        /// [v0.1.158 · 用户口径 4.4-2] 原来默认 **(6, 2.5)**，用户反馈"默认的云流速度太快了"；
        /// 且原来**越高越快**（带 2 卷云 12 m/s &gt; 带 1 云海 6 m/s）与"越高越慢"相反。
        /// 现在三层统一按 **越高越慢** 排：带 1 **2.6** &gt; 带 2 **1.6** &gt; 带 3 **0.8**（米/秒，X 分量）。
        /// </summary>
        public static Vector2 CloudWind { get; set; } = new(2.6f, 1.1f);

        /// <summary>带 1 受光色。</summary>
        public static Vector3 CloudLitColor { get; set; } = new(1f, 1f, 1f);

        /// <summary>带 1 背光色。**必须与天空有明显明度差**，否则云「在里面但看不见」
        /// （实测：0.52/0.58/0.68 时整片天空看不出云；压到 0.45/0.50/0.60 才读得出来）。</summary>
        public static Vector3 CloudShadowColor { get; set; } = new(0.45f, 0.50f, 0.60f);

        /// <summary>
        /// raymarch 的最远距离（米）。[v0.1.158 · 用户口径 4.4-3] 用户要求"云层渲染范围过小，
        /// 应当增大到视觉上与地平线接近"。原来 6000 m：站在 y≈65、云底 300 m 时，
        /// 6000 m 对应的仰角约 **2.2°** ⇒ 云在离地平线约 2° 的地方就被切出一个硬边。
        /// 现在 **24000 m**（对应约 **0.55°**，视觉上贴着地平线）。
        ///
        /// ⚠️ 单纯拉远会**把地平线糊成一片白**（8 步固定、dt 随距离线性变大 ⇒ 掠射光线
        /// 的光学厚度 `od` 直接饱和 ⇒ alpha→1 且 `cloud` 取受光色）。所以这次同时加了
        /// **空气透视混融** `CloudFadeDistance`（见 `volMarch` 里的 `aerial`）：
        /// 中段距离越远，云色越向**天空底色**混合 —— 这既是物理上该有的（远处大气散射）、
        /// 也正是用户看到的"白色化遮罩"的根因修法。
        /// </summary>
        public static float CloudMaxDistance { get; set; } = 24000f;

        /// <summary>[v0.1.158] 带 1 的**空气透视混融距离**（米，默认 2200）：
        /// 射线中段距离 t 处，云色按 `1 - exp(-t / fade)` 向天空底色混合。
        /// 越小 = 远处云越快淡入天空（越不容易出现白墙）；0 = 关闭混融（回到 v0.1.157 的硬边观感）。</summary>
        public static float CloudFadeDistance { get; set; } = 2200f;

        /// <summary>带 1 噪声随高度**剪切**的强度（0 = 每层同一张图案 → 像一层贴纸）。</summary>
        public static float CloudHeightShear { get; set; } = 0.6f;

        // ============================================================================================
        // 带 2「高层卷云」（v0.1.68）—— 「在不同高度上分布我们的体积云雾」
        // ============================================================================================

        /// <summary>带 2 开关。默认开；关掉即回到 v0.1.67 的单带画面。</summary>
        public static bool Cloud2Enabled { get; set; } = true;

        public static float Cloud2BottomY { get; set; } = 850f;

        public static float Cloud2TopY { get; set; } = 1000f;

        /// <summary>带 2 密度：比带 1 淡得多（卷云是薄的）。</summary>
        public static float Cloud2Density { get; set; } = 0.012f;

        /// <summary>带 2 噪声频率：比带 1 细（0.0020 ≈ 500 m）。</summary>
        public static float Cloud2Scale { get; set; } = 0.0020f;

        public static float Cloud2Threshold { get; set; } = 0.55f;

        public static float Cloud2SunLit { get; set; } = 0.90f;

        public static float Cloud2Opacity { get; set; } = 0.85f;

        /// <summary>
        /// 带 2 风。[v0.1.158] 原来 **(12, 5)** —— 比带 1 快一倍（"高层风大"），
        /// 但用户口径明确要求 **越高越慢**，故改为 **(1.6, 0.7)**：带 1 (2.6) &gt; 带 2 (1.6) &gt; 带 3 (0.8)。
        /// 两层仍会**相对移动**（观感来源保留），只是方向反过来。
        /// </summary>
        public static Vector2 Cloud2Wind { get; set; } = new(1.6f, 0.7f);

        public static Vector3 Cloud2ShadowColor { get; set; } = new(0.62f, 0.66f, 0.74f);

        /// <summary>带 2 剪切：卷云更「平」，所以比带 1 小。</summary>
        public static float Cloud2HeightShear { get; set; } = 0.25f;

        /// <summary>[v0.1.158] 带 2 的空气透视混融距离（米，默认 3000）。</summary>
        public static float Cloud2FadeDistance { get; set; } = 3000f;

        // ============================================================================================
        // 带 3「高空薄云」（v0.1.158 · 用户口径 4.4-2「现有的两层体积云应该改为三层」）
        // ============================================================================================

        /// <summary>带 3 开关（默认开）。关掉即回到"两层"画面（带 1 + 带 2）。</summary>
        public static bool Cloud3Enabled { get; set; } = true;

        /// <summary>带 3 底高（米）：比卷云更高的薄云层。</summary>
        public static float Cloud3BottomY { get; set; } = 1300f;

        public static float Cloud3TopY { get; set; } = 1700f;

        /// <summary>带 3 密度：最淡的一层（很薄的高空云）。</summary>
        public static float Cloud3Density { get; set; } = 0.008f;

        /// <summary>带 3 噪声频率：最细（0.0032 ≈ 310 m 一个结构）。</summary>
        public static float Cloud3Scale { get; set; } = 0.0032f;

        public static float Cloud3Threshold { get; set; } = 0.52f;

        public static float Cloud3SunLit { get; set; } = 0.95f;

        public static float Cloud3Opacity { get; set; } = 0.70f;

        /// <summary>带 3 风：**三层里最慢**（用户口径 4.4-2「越高越慢」）。</summary>
        public static Vector2 Cloud3Wind { get; set; } = new(0.8f, 0.35f);

        public static Vector3 Cloud3ShadowColor { get; set; } = new(0.72f, 0.76f, 0.84f);

        public static float Cloud3HeightShear { get; set; } = 0.15f;

        /// <summary>[v0.1.158] 带 3 的空气透视混融距离（米，默认 3600；最高最远的一层最该淡入天空）。</summary>
        public static float Cloud3FadeDistance { get; set; } = 3600f;

        /// <summary>步数（**固定 8**：段是手工展开的，不是循环 —— 引擎的 shader 方言没有验证过循环）。</summary>
        public static int CloudSteps { get; } = 8;

        static Shader m_cloudSkyShader;
        static string m_cloudSkyLastError = "";
        static long m_cloudSkyResolved;
        static long m_cloudSkyFallbacks;
        static string m_cloudSkyLastReason = "";

        public static string VolumetricCloudDescribe() {
            JsonObject band1 = new() {
                ["name"] = "云海",
                ["bottomY"] = (double)CloudBandBottomY,
                ["topY"] = (double)CloudBandTopY,
                ["density"] = (double)CloudDensity,
                ["scale"] = (double)CloudScale,
                ["threshold"] = (double)CloudThreshold,
                ["sunLit"] = (double)CloudSunLit,
                ["opacity"] = (double)CloudOpacity,
                ["wind"] = new JsonArray(CloudWind.X, CloudWind.Y),
                ["shear"] = (double)CloudHeightShear
            };
            JsonObject band2 = new() {
                ["name"] = "高层卷云",
                ["enabled"] = Cloud2Enabled,
                ["bottomY"] = (double)Cloud2BottomY,
                ["topY"] = (double)Cloud2TopY,
                ["density"] = (double)Cloud2Density,
                ["scale"] = (double)Cloud2Scale,
                ["threshold"] = (double)Cloud2Threshold,
                ["sunLit"] = (double)Cloud2SunLit,
                ["opacity"] = (double)Cloud2Opacity,
                ["wind"] = new JsonArray(Cloud2Wind.X, Cloud2Wind.Y),
                ["shear"] = (double)Cloud2HeightShear
            };
            JsonObject band3 = new() {
                ["name"] = "高空薄云",
                ["enabled"] = Cloud3Enabled,
                ["bottomY"] = (double)Cloud3BottomY,
                ["topY"] = (double)Cloud3TopY,
                ["density"] = (double)Cloud3Density,
                ["scale"] = (double)Cloud3Scale,
                ["threshold"] = (double)Cloud3Threshold,
                ["sunLit"] = (double)Cloud3SunLit,
                ["opacity"] = (double)Cloud3Opacity,
                ["wind"] = new JsonArray(Cloud3Wind.X, Cloud3Wind.Y),
                ["shear"] = (double)Cloud3HeightShear
            };
            JsonObject o = new() {
                ["enabled"] = VolumetricCloudsEnabled,
                ["replaceStaticClouds"] = ReplaceStaticClouds,
                // 打平一份带 1 的关键字段：发版门禁的「默认值漂移门」直接读这些键
                ["bandBottomY"] = (double)CloudBandBottomY,
                ["bandTopY"] = (double)CloudBandTopY,
                ["density"] = (double)CloudDensity,
                ["scale"] = (double)CloudScale,
                ["threshold"] = (double)CloudThreshold,
                ["sunLit"] = (double)CloudSunLit,
                ["opacity"] = (double)CloudOpacity,
                ["wind"] = new JsonArray(CloudWind.X, CloudWind.Y),
                ["band1"] = band1,
                ["band2"] = band2,
                ["band3"] = band3,
                // [v0.1.158 · 用户口径 4.4-2/-3] 两条**可证伪**的读数：
                //   `windSpeedOrderOk`：越高越慢（带 1 > 带 2 > 带 3）——用户明确要求的那条；
                //   `bandsEnabled`：三层是否都开着（"现有的两层体积云应该改为三层"）。
                ["windSpeedOrderOk"] =
                    CloudWind.Length() > Cloud2Wind.Length() + 1e-3f
                    && Cloud2Wind.Length() > Cloud3Wind.Length() + 1e-3f,
                ["windSpeeds"] = new JsonArray(CloudWind.Length(), Cloud2Wind.Length(), Cloud3Wind.Length()),
                ["bandsEnabled"] = 1 + (Cloud2Enabled ? 1 : 0) + (Cloud3Enabled ? 1 : 0),
                ["maxDistanceMetres"] = (double)CloudMaxDistance,
                ["fadeDistanceMetres"] = new JsonArray(CloudFadeDistance, Cloud2FadeDistance, Cloud3FadeDistance),
                ["steps"] = CloudSteps,
                ["skyShaderResolved"] = m_cloudSkyResolved,
                ["skyShaderFallbacks"] = m_cloudSkyFallbacks,
                ["skyShaderLastReason"] = m_cloudSkyLastReason,
                ["lastError"] = m_cloudSkyLastError
            };
            o["note"] = "云雾画在**天空穹顶**片元里（穹顶开深度读 → 天然被地形遮挡），不是全屏后处理；"
                        + "三层带在不同高度（v0.1.158 起：云海/卷云/高空薄云），风**越高越慢**；"
                        + "近地平线靠 maxDistance + 空气透视混融（fadeDistance）覆盖，不会糊成白墙";
            return o.ToJsonString();
        }

        static void BindBand(Shader shader, string prefix, bool enabled, float bottom, float top, float density,
                             float scale, Vector2 wind, float threshold, float sunLit, float opacity,
                             Vector3 litColor, Vector3 shadowColor, float shear, float fade, float time) {
            float b = Math.Min(bottom, top - 1f);
            float t = Math.Max(top, b + 1f);
            shader.GetParameter($"{prefix}Enable", true).SetValue(enabled ? 1f : 0f);
            shader.GetParameter($"{prefix}BottomY", true).SetValue(b);
            shader.GetParameter($"{prefix}TopY", true).SetValue(t);
            shader.GetParameter($"{prefix}Density", true).SetValue(Math.Max(density, 0f));
            shader.GetParameter($"{prefix}Scale", true).SetValue(Math.Max(scale, 1e-6f));
            shader.GetParameter($"{prefix}Wind", true).SetValue(wind * time);
            shader.GetParameter($"{prefix}Threshold", true).SetValue(Math.Clamp(threshold, 0f, 0.99f));
            shader.GetParameter($"{prefix}SunLit", true).SetValue(Math.Clamp(sunLit, 0f, 1f));
            shader.GetParameter($"{prefix}Opacity", true).SetValue(Math.Clamp(opacity, 0f, 1f));
            shader.GetParameter($"{prefix}LitColor", true).SetValue(litColor);
            shader.GetParameter($"{prefix}ShadowColor", true).SetValue(shadowColor);
            shader.GetParameter($"{prefix}Shear", true).SetValue(Math.Max(shear, 0f));
            shader.GetParameter($"{prefix}Fade", true).SetValue(Math.Max(fade, 0f));
        }

        /// <summary>
        /// 解析天空穹顶实际使用的 shader：启用体积云时返回自研变体，否则返回原 `UnlitShader`（逐位不变）。
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
                }
                Shader shader = m_cloudSkyShader;
                float time = (float)Time.RealTime;
                shader.GetParameter("u_skyViewProjection", true).SetValue(camera.ViewProjectionMatrix);
                shader.GetParameter("u_skyCameraPos", true).SetValue(camera.ViewPosition);
                shader.GetParameter("u_color", true).SetValue(color);
                shader.GetParameter("u_additiveColor", true).SetValue(additiveColor);
                shader.GetParameter("u_volMaxDistance", true).SetValue(Math.Max(CloudMaxDistance, 100f));
                BindBand(shader, "u_vol", true, CloudBandBottomY, CloudBandTopY, CloudDensity, CloudScale,
                    CloudWind, CloudThreshold, CloudSunLit, CloudOpacity, CloudLitColor, CloudShadowColor,
                    CloudHeightShear, CloudFadeDistance, time);
                BindBand(shader, "u_vol2", Cloud2Enabled, Cloud2BottomY, Cloud2TopY, Cloud2Density, Cloud2Scale,
                    Cloud2Wind, Cloud2Threshold, Cloud2SunLit, Cloud2Opacity, CloudLitColor, Cloud2ShadowColor,
                    Cloud2HeightShear, Cloud2FadeDistance, time);
                BindBand(shader, "u_vol3", Cloud3Enabled, Cloud3BottomY, Cloud3TopY, Cloud3Density, Cloud3Scale,
                    Cloud3Wind, Cloud3Threshold, Cloud3SunLit, Cloud3Opacity, CloudLitColor, Cloud3ShadowColor,
                    Cloud3HeightShear, Cloud3FadeDistance, time);
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
        // 变体 shader：Unlit 的语义（color × vertexColor + additiveColor）+ 穹顶上的体积云 raymarch
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
	// 所以这里必须乘上 a_color；第一版漏了它 → 整片天空变成均匀白。
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
float u_volMaxDistance;
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
float u_volShear;
float u_volFade;
float u_vol2Enable;
float u_vol2BottomY;
float u_vol2TopY;
float u_vol2Density;
float u_vol2Scale;
float2 u_vol2Wind;
float u_vol2Threshold;
float u_vol2SunLit;
float u_vol2Opacity;
float3 u_vol2ShadowColor;
float u_vol2Shear;
float u_vol2Fade;
float u_vol3Enable;
float u_vol3BottomY;
float u_vol3TopY;
float u_vol3Density;
float u_vol3Scale;
float2 u_vol3Wind;
float u_vol3Threshold;
float u_vol3SunLit;
float u_vol3Opacity;
float3 u_vol3ShadowColor;
float u_vol3Shear;
float u_vol3Fade;

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

float volHeightAt(float3 p, float bottomY, float topY)
{
	return (p.y - bottomY) / max(topY - bottomY, 0.001);
}

float volDensityAt(float3 p, float bottomY, float topY, float scale, float2 wind, float threshold, float shear)
{
	float h = volHeightAt(p, bottomY, topY);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	// 竖向剖面：上下沿平滑淡出，中间鼓
	float prof = smoothstep(0.0, 0.2, h) * smoothstep(1.0, 0.75, h);
	// 噪声随高度剪切：每个高度看到的图案错开 → 出团块，而不是「一张贴纸横着拉」
	float2 q = (p.xz + p.y * shear * float2(1.7, 1.1)) * scale + wind;
	float n = vnoise2(q) * 0.65 + vnoise2(q * 2.7 + float2(11.3, 7.1)) * 0.35;
	return max(0.0, n - threshold) * prof;
}

// 受光率：越靠上（越薄）越亮；sunLit 是太阳整体强度
float volLitAt(float3 p, float bottomY, float topY, float sunLit)
{
	float h = clamp(volHeightAt(p, bottomY, topY), 0.0, 1.0);
	return sunLit * (0.30 + 0.70 * h);
}

// 返回 (密度, 受光加权密度)
float2 volSampleAt(float3 ro, float3 rd, float t, float bottomY, float topY, float scale, float2 wind,
                   float threshold, float shear, float sunLit)
{
	float3 p = ro + rd * t;
	float d = volDensityAt(p, bottomY, topY, scale, wind, threshold, shear);
	return float2(d, volLitAt(p, bottomY, topY, sunLit) * d);
}

// 一条云带的 8 步积分 + 合成（**手工展开**，不用循环）
float3 volMarch(float3 skyIn, float3 ro, float3 rd, float bottomY, float topY, float density, float scale,
                float2 wind, float threshold, float sunLit, float opacity, float3 litColor, float3 shadowColor,
                float shear, float fadeDist, float jitter)
{
	float t0 = 0.0;
	float t1 = u_volMaxDistance;
	if (abs(rd.y) > 1e-5)
	{
		float ta = (bottomY - ro.y) / rd.y;
		float tb = (topY - ro.y) / rd.y;
		t0 = max(0.0, min(ta, tb));
		t1 = min(u_volMaxDistance, max(ta, tb));
	}
	else if (ro.y < bottomY || ro.y > topY)
	{
		t1 = 0.0;
	}
	if (t1 <= t0)
	{
		return skyIn;
	}
	float dt = (t1 - t0) * 0.125;
	float od = 0.0;
	float lum = 0.0;
	float2 s;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 0.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 1.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 2.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 3.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 4.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 5.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 6.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 7.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	od *= dt * density;
	float rawOd = od / max(dt * density, 1e-5);
	float lit = saturate(lum / max(rawOd, 1e-5));
	float alpha = saturate((1.0 - exp(-od)) * opacity);
	float3 cloud = lerp(shadowColor, litColor, lit);
	// [v0.1.158 · 用户口径 4.4-1/-3] **空气透视混融**：中段距离 tMid 越远，云色越向天空底色靠。
	// 为什么必须有它：8 步固定步长下，掠射视图的 dt 随 (t1-t0) 线性增大 ⇒ `od` 直接饱和 ⇒
	// alpha→1 且 cloud 取受光色 ⇒ 地平线糊成**一片白**（用户报的『白色化遮罩』就是它）。⚠️ 注释里别写半角引号（会截断 C# 逐字字符串）
	// 混融既是物理上该有的（远处大气散射），也让 maxDistance 拉到 24 km 时不会出现硬边。
	if (fadeDist > 0.0)
	{
		float tMid = (t0 + t1) * 0.5;
		float aerial = saturate(1.0 - exp(-max(tMid, 0.0) / fadeDist));
		cloud = lerp(cloud, skyIn.rgb, aerial);
	}
	return lerp(skyIn, cloud, alpha);
}

void main(
	in float4 v_color : COLOR,
	in float3 v_local : TEXCOORD0,
	out float4 svTarget: SV_TARGET
)
{
	float4 sky = v_color + u_additiveColor;
	// 穹顶顶点在**物体空间**就是「从相机出去的方向」（World 只是平移相机 + 投影）
	float3 rd = normalize(v_local);
	float3 ro = u_skyCameraPos;
	// 逐像素抖动：由**视线方向**的哈希给出 → 确定性、跨帧稳定，纯粹是打破步进分层
	float jitter = hash12(v_local.xz * 37.0);
	if (u_volEnable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_volBottomY, u_volTopY, u_volDensity, u_volScale, u_volWind,
			u_volThreshold, u_volSunLit, u_volOpacity, u_volLitColor, u_volShadowColor, u_volShear, u_volFade, jitter);
	}
	if (u_vol2Enable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_vol2BottomY, u_vol2TopY, u_vol2Density, u_vol2Scale, u_vol2Wind,
			u_vol2Threshold, u_vol2SunLit, u_vol2Opacity, u_volLitColor, u_vol2ShadowColor, u_vol2Shear, u_vol2Fade, jitter);
	}
	if (u_vol3Enable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_vol3BottomY, u_vol3TopY, u_vol3Density, u_vol3Scale, u_vol3Wind,
			u_vol3Threshold, u_vol3SunLit, u_vol3Opacity, u_volLitColor, u_vol3ShadowColor, u_vol3Shear, u_vol3Fade, jitter);
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
uniform float u_volMaxDistance;
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
uniform float u_volShear;
uniform float u_volFade;
uniform float u_vol2Enable;
uniform float u_vol2BottomY;
uniform float u_vol2TopY;
uniform float u_vol2Density;
uniform float u_vol2Scale;
uniform vec2 u_vol2Wind;
uniform float u_vol2Threshold;
uniform float u_vol2SunLit;
uniform float u_vol2Opacity;
uniform vec3 u_vol2ShadowColor;
uniform float u_vol2Shear;
uniform float u_vol2Fade;
uniform float u_vol3Enable;
uniform float u_vol3BottomY;
uniform float u_vol3TopY;
uniform float u_vol3Density;
uniform float u_vol3Scale;
uniform vec2 u_vol3Wind;
uniform float u_vol3Threshold;
uniform float u_vol3SunLit;
uniform float u_vol3Opacity;
uniform vec3 u_vol3ShadowColor;
uniform float u_vol3Shear;
uniform float u_vol3Fade;

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

float volHeightAt(vec3 p, float bottomY, float topY)
{
	return (p.y - bottomY) / max(topY - bottomY, 0.001);
}

float volDensityAt(vec3 p, float bottomY, float topY, float scale, vec2 wind, float threshold, float shear)
{
	float h = volHeightAt(p, bottomY, topY);
	if (h < 0.0 || h > 1.0)
	{
		return 0.0;
	}
	float prof = smoothstep(0.0, 0.2, h) * smoothstep(1.0, 0.75, h);
	vec2 q = (p.xz + p.y * shear * vec2(1.7, 1.1)) * scale + wind;
	float n = vnoise2(q) * 0.65 + vnoise2(q * 2.7 + vec2(11.3, 7.1)) * 0.35;
	return max(0.0, n - threshold) * prof;
}

float volLitAt(vec3 p, float bottomY, float topY, float sunLit)
{
	float h = clamp(volHeightAt(p, bottomY, topY), 0.0, 1.0);
	return sunLit * (0.30 + 0.70 * h);
}

vec2 volSampleAt(vec3 ro, vec3 rd, float t, float bottomY, float topY, float scale, vec2 wind,
                 float threshold, float shear, float sunLit)
{
	vec3 p = ro + rd * t;
	float d = volDensityAt(p, bottomY, topY, scale, wind, threshold, shear);
	return vec2(d, volLitAt(p, bottomY, topY, sunLit) * d);
}

vec3 volMarch(vec3 skyIn, vec3 ro, vec3 rd, float bottomY, float topY, float density, float scale,
              vec2 wind, float threshold, float sunLit, float opacity, vec3 litColor, vec3 shadowColor,
              float shear, float fadeDist, float jitter)
{
	float t0 = 0.0;
	float t1 = u_volMaxDistance;
	if (abs(rd.y) > 1e-5)
	{
		float ta = (bottomY - ro.y) / rd.y;
		float tb = (topY - ro.y) / rd.y;
		t0 = max(0.0, min(ta, tb));
		t1 = min(u_volMaxDistance, max(ta, tb));
	}
	else if (ro.y < bottomY || ro.y > topY)
	{
		t1 = 0.0;
	}
	if (t1 <= t0)
	{
		return skyIn;
	}
	float dt = (t1 - t0) * 0.125;
	float od = 0.0;
	float lum = 0.0;
	vec2 s;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 0.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 1.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 2.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 3.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 4.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 5.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 6.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	s = volSampleAt(ro, rd, t0 + jitter + dt * 7.5, bottomY, topY, scale, wind, threshold, shear, sunLit);
	od += s.x; lum += s.y;
	od *= dt * density;
	float rawOd = od / max(dt * density, 1e-5);
	float lit = clamp(lum / max(rawOd, 1e-5), 0.0, 1.0);
	float alpha = clamp((1.0 - exp(-od)) * opacity, 0.0, 1.0);
	vec3 cloud = mix(shadowColor, litColor, lit);
	// [v0.1.158] 空气透视混融：远处云色向天空底色靠（否则掠射光线 od 饱和 ⇒ 地平线一片白）
	if (fadeDist > 0.0)
	{
		float tMid = (t0 + t1) * 0.5;
		float aerial = clamp(1.0 - exp(-max(tMid, 0.0) / fadeDist), 0.0, 1.0);
		cloud = mix(cloud, skyIn.rgb, aerial);
	}
	return mix(skyIn, cloud, alpha);
}

void main()
{
	vec4 sky = v_color + u_additiveColor;
	vec3 rd = normalize(v_local);
	vec3 ro = u_skyCameraPos;
	float jitter = hash12(v_local.xz * 37.0);
	if (u_volEnable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_volBottomY, u_volTopY, u_volDensity, u_volScale, u_volWind,
			u_volThreshold, u_volSunLit, u_volOpacity, u_volLitColor, u_volShadowColor, u_volShear, u_volFade, jitter);
	}
	if (u_vol2Enable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_vol2BottomY, u_vol2TopY, u_vol2Density, u_vol2Scale, u_vol2Wind,
			u_vol2Threshold, u_vol2SunLit, u_vol2Opacity, u_volLitColor, u_vol2ShadowColor, u_vol2Shear, u_vol2Fade, jitter);
	}
	if (u_vol3Enable > 0.5)
	{
		sky.rgb = volMarch(sky.rgb, ro, rd, u_vol3BottomY, u_vol3TopY, u_vol3Density, u_vol3Scale, u_vol3Wind,
			u_vol3Threshold, u_vol3SunLit, u_vol3Opacity, u_volLitColor, u_vol3ShadowColor, u_vol3Shear, u_vol3Fade, jitter);
	}
	gl_FragColor = vec4(sky.rgb, 1.0);
}

#endif
";
    }
}
