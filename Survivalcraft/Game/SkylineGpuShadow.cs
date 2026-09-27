using System;
using System.IO;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.32：**GPU 阴影贴图第一步 —— 自编译深度 shader + 太阳视角深度图（含回读自检）**。
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

void main(
	in float v_depth : TEXCOORD0,
	out float4 svTarget : SV_TARGET
)
{
	svTarget = float4(v_depth, v_depth, v_depth, 1.0);
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision mediump float;
#endif

varying float v_depth;

void main()
{
	gl_FragColor = vec4(v_depth, v_depth, v_depth, 1.0);
}

#endif
";

        static Shader m_gpuShadowShader;
        static RenderTarget2D m_gpuShadowRt;
        static string m_gpuShadowLast = "(never captured)";
        static int m_gpuShadowCaptures;

        // [v0.1.34] 深度图相机参数（采样侧用；每次 Capture 刷新）
        static Matrix m_gpuShadowViewProjection;
        static Vector2 m_gpuShadowOrigin;
        static Vector3 m_gpuShadowEye;
        static Vector3 m_gpuShadowSun = Vector3.UnitY;
        static float m_gpuShadowDepthMax = 4096f;
        static bool m_gpuShadowHasMap;

        public static string GpuShadowDescribe() =>
            $"gpuShadow enabled={GpuShadowEnabled} size={GpuShadowSize} radius={GpuShadowRadius:0} "
            + $"captures={m_gpuShadowCaptures} last={m_gpuShadowLast}";

        static Shader EnsureGpuShadowShader() {
            if (m_gpuShadowShader == null) {
                m_gpuShadowShader = new Shader(GpuShadowVsh, GpuShadowPsh);
            }
            return m_gpuShadowShader;
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
            RenderTarget2D previousTarget = Display.RenderTarget;
            Viewport previousViewport = Display.Viewport;
            Rectangle previousScissor = Display.ScissorRectangle;
            double start = Time.RealTime;
            try {
                Shader shader = EnsureGpuShadowShader();
                if (m_gpuShadowRt == null || m_gpuShadowRt.Width != size) {
                    Utilities.Dispose(ref m_gpuShadowRt);
                    m_gpuShadowRt = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                Display.RenderTarget = m_gpuShadowRt;
                Display.Viewport = new Viewport(0, 0, size, size);
                Display.ScissorRectangle = new Rectangle(0, 0, size, size);
                Display.Clear(new Vector4(1f, 1f, 1f, 1f), 1f, 0);       // 背景 = 最远（depth 1）

                Vector3 center = camera.ViewPosition;
                Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
                float distance = MathF.Max(radius * 4f, 512f);
                float depthMax = distance * 2f;
                Vector3 eye = center + sun * distance;
                Matrix view = Matrix.CreateLookAt(eye, center, Vector3.UnitY);
                Matrix projection = Matrix.CreateOrthographic(radius * 2f, radius * 2f, 1f, distance * 4f);
                Vector3 origin3 = new(MathF.Floor(center.X), 0f, MathF.Floor(center.Z));
                Matrix viewShifted = Matrix.CreateTranslation(origin3) * view;
                Matrix viewProjectionShifted = viewShifted * projection;
                m_gpuShadowViewProjection = viewProjectionShifted;
                m_gpuShadowOrigin = new Vector2(origin3.X, origin3.Z);
                m_gpuShadowEye = eye;
                m_gpuShadowSun = sun;
                m_gpuShadowDepthMax = depthMax;
                m_gpuShadowHasMap = true;
                shader.GetParameter("u_origin", true).SetValue(new Vector2(origin3.X, origin3.Z));
                shader.GetParameter("u_viewProjectionMatrix", true).SetValue(viewProjectionShifted);
                shader.GetParameter("u_eye", true).SetValue(eye);
                shader.GetParameter("u_sunDir", true).SetValue(sun);
                shader.GetParameter("u_depthMax", true).SetValue(depthMax);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                SkylineLod.DrawWithShader(shader);
                // [v0.1.33] 真实区块几何：与 LOD 共用同一个深度 shader / 同一张深度图（不透明子集 0..4）。
                int chunksDrawn = 0;
                if (GpuShadowIncludeChunks) {
                    foreach (TerrainChunk chunk in subsystemTerrain.Terrain.AllocatedChunks) {
                        if (chunk == null || chunk.State != TerrainChunkState.Valid || chunk.Buffers.Count == 0) {
                            continue;
                        }
                        DrawChunkIntoDepth(shader, chunk);
                        chunksDrawn++;
                    }
                }

                Image image = m_gpuShadowRt.GetData(new Rectangle(0, 0, size, size));
                int covered = 0;
                double sumDepth = 0;
                int minDepth = 255, maxDepth = 0;
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        int d = image.GetPixel(x, y).R;
                        if (d < 254) {
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
                    int expected = (int)(MathUtils.Clamp(Vector3.Dot(eye - world, sun) / depthMax, 0f, 1f) * 255f);
                    int got = -1;
                    int bestErr = int.MaxValue;
                    // Y 轴方向（图像上下翻转）不确定 → 两个方向都搜；窗口 ±8 px 容忍投影/像素取整误差。
                    foreach (int flip in new[] { 0, 1 }) {
                        int cy = flip == 0 ? iy : size - 1 - iy;
                        for (int dy = -8; dy <= 8; dy++) {
                            for (int dx = -8; dx <= 8; dx++) {
                                int qx = Math.Clamp(ix + dx, 0, size - 1);
                                int qy = Math.Clamp(cy + dy, 0, size - 1);
                                int v = image.GetPixel(qx, qy).R;
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
                    int expected = (int)(MathUtils.Clamp(Vector3.Dot(eye - world, sun) / depthMax, 0f, 1f) * 255f);
                    int got = -1;
                    int bestErr = int.MaxValue;
                    foreach (int flip in new[] { 0, 1 }) {
                        int cy = flip == 0 ? iy : size - 1 - iy;
                        for (int dy = -8; dy <= 8; dy++) {
                            for (int dx = -8; dx <= 8; dx++) {
                                int qx = Math.Clamp(ix + dx, 0, size - 1);
                                int qy = Math.Clamp(cy + dy, 0, size - 1);
                                int v = image.GetPixel(qx, qy).R;
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
                    $"gpuShadow size={size} radius={radius:0} covered={covered} "
                    + $"({100.0 * covered / (size * size):0.##}%) depth=[{minDepth},{maxDepth}] "
                    + $"mean={(covered > 0 ? sumDepth / covered : 0):0.0} selfCheck={selfCheckOk} "
                    + $"captures={m_gpuShadowCaptures} ms={(Time.RealTime - start) * 1000.0:0.0}";
                result["ok"] = true;
                result["size"] = size;
                result["radius"] = radius;
                result["covered"] = covered;
                result["coverage"] = Math.Round((double)covered / (size * size), 5);
                result["chunksDrawn"] = chunksDrawn;
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
        /// [v0.1.33] 把一个区块的**不透明子集（0..4）**画进当前深度图。
        /// 逻辑与 `TerrainRenderer.DrawTerrainChunkGeometrySubsets(shader, chunk, 0x1F, false)` 一致：
        /// 相邻子集的索引区间合并成一次 DrawIndexed（减少 draw call）。
        /// </summary>
        static void DrawChunkIntoDepth(Shader shader, TerrainChunk chunk) {
            const int opaqueMask = 0x1F;                             // 子集 0..4 = 不透明
            foreach (TerrainChunkGeometry.Buffer buffer in chunk.Buffers) {
                int start = int.MaxValue;
                int end = 0;
                for (int i = 0; i < 8; i++) {
                    if (i < 7 && (opaqueMask & (1 << i)) != 0) {
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
