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
	out float v_fog : FOG,
	out float4 sv_position: SV_POSITION
)
{
	v_color = a_color;
	v_texcoord = a_texcoord;
	v_normal = a_normal.xyz * 2.0 - 1.0;
	v_matid = a_matid;
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
float u_topLight;
float u_channel;
float3 u_fogColor;

void main(
	in float4 v_color : COLOR,
	in float2 v_texcoord: TEXCOORD,
	in float3 v_normal : TEXCOORD2,
	in float v_matid : TEXCOORD3,
	in float v_fog: FOG,
	out float4 svTarget: SV_TARGET
)
{
	float4 albedo = v_color * u_texture.Sample(u_samplerState, v_texcoord);
	float3 n = normalize(v_normal);
	float lit = 0.5 + max(dot(n, u_light1), 0.0) + max(dot(n, u_light2), 0.0);
	lit = saturate(lit / max(u_topLight, 0.0001));
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
	svTarget = float4(rgb, albedo.a);
}

#endif
#ifdef GLSL

// <Sampler Name='u_samplerState' Texture='u_texture' />

precision highp float;

uniform sampler2D u_texture;
uniform vec3 u_light1;
uniform vec3 u_light2;
uniform float u_topLight;
uniform float u_channel;
uniform vec3 u_fogColor;

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_normal;
varying float v_matid;
varying float v_fog;

void main()
{
	vec4 albedo = v_color * texture2D(u_texture, v_texcoord);
	vec3 n = normalize(v_normal);
	float lit = 0.5 + max(dot(n, u_light1), 0.0) + max(dot(n, u_light2), 0.0);
	lit = clamp(lit / max(u_topLight, 0.0001), 0.0, 1.0);
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
	gl_FragColor = vec4(mix(albedo.rgb * lit, u_fogColor * v_color.a, v_fog), albedo.a);
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
        static Shader PrepareVolumeShader(Camera camera, float yOffset, int channel) {
            try {
                SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
                SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
                if (subsystemTerrain == null || sky == null || camera == null) {
                    return null;
                }
                SkylineFaceShading.Refresh();     // 与 CPU 侧同源：每次画之前按同一公式取一遍因子
                Shader shader = EnsureShader();
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
                shader.GetParameter("u_topLight", true).SetValue(SkylineFaceShading.TopFactor);
                shader.GetParameter("u_channel", true).SetValue((float)channel);
                shader.GetParameter("u_fogYMultiplier", true).SetValue(sky.VisibilityRangeYMultiplier);
                shader.GetParameter("u_fogColor", true).SetValue(new Vector3(sky.ViewFogColor));
                shader.GetParameter("u_fogBottomTopDensity", true)
                    .SetValue(SkylineRuntime.FogBand(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, sky.ViewFogDensity)));
                shader.GetParameter("u_hazeStartDensity", true)
                    .SetValue(SkylineRuntime.HazeStartDensity(new Vector2(sky.ViewHazeStart, sky.ViewHazeDensity)));
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
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, mesh.VertexBuffer, mesh.IndexBuffer,
                    0, mesh.IndexCount);
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
                ["topLight"] = Math.Round(SkylineFaceShading.TopFactor, 4)
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
