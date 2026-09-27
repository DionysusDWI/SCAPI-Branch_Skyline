using System;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.58：**离屏 G-buffer 样板 pass**（光影接入面 v1 的"着色器 + 渲染引擎"那一半）。
    ///
    /// 它做三件事，都是光影包真接入时必须先具备的能力：
    ///   1. **自编译 shader**（`new Shader(vsh, psh)`，从 C# 字符串来 —— v0.1.32 证实可行），
    ///      把远景三层网格（粗 16 m / 细 8 m / 近环 4 m）渲染进**自建 RenderTarget**；
    ///   2. 片元输出 **albedo**（地形图集采样 × 顶点色）× 与 **覆盖率**（alpha=1 表示"这里画到了几何"）；
    ///   3. **回读自检**（`GetData`）+ **调试直显**（把这张 RT 贴到屏幕上做 A/B）。
    ///
    /// 如实记的边界（写在 `skyline.GBufferInfo()` 与 `SkylineShaderHook.Describe()` 里）：
    ///   * LOD 顶点格式目前只有 position/texcoord/color（v0.1.11 就公开了这个布局），**没有法线、没有材质 id**
    ///     —— 所以本版 G-buffer 只有 **albedo + 覆盖**；法线要等"给 LOD 网格加一个顶点属性"那一步，
    ///     材质 id 同理（这是 Iris 侧真正要改底层的地方）；
    ///   * 只画远景（LOD）几何，不含地形区块/实体/家具（后续 pass）。
    ///
    /// 默认**关**（不改变任何既有渲染）；打开后用 `skyline.GBufferCapture()` 取证。
    /// </summary>
    public static class SkylineGBuffer {
        public static bool Enabled { get; set; }
        public static int Size { get; set; } = 512;
        /// <summary>调试直显：把 G-buffer 画到屏幕（A/B 用；默认关）。</summary>
        public static bool DebugDraw { get; set; }
        /// <summary>
        /// [v0.1.61] **把 32³ 壳网格也画进 G-buffer**（里程碑 1.4）。
        /// 默认开；关掉 = 逐位回到 v0.1.60 的"G-buffer 只含三层远景 LOD"。
        /// 判定与主画面的壳层**完全同一套**（`SkylineCubeShellStore.CollectDrawableMeshes`），
        /// 并用同一个 `BandLift` 偏移，所以 G-buffer 里的壳与主画面里的壳是同一批几何、同一个位置。
        /// </summary>
        public static bool IncludeShells { get; set; } = true;
        /// <summary>
        /// [v0.1.61] 取证用的**分层模式**：`0` = 两层都画（正常）、`1` = 只画三层远景 LOD、`2` = **只画 32³ 壳层**。
        /// 为什么需要它：壳层与 LOD 在屏幕上大量重叠，而 G-buffer 的深度只由这个 pass 自己产生
        /// （主画面里 LOD 会在有壳的地方**让位**，离屏 pass 里做不到"按单元跳过"），
        /// 所以"两层一起画"的覆盖率**量不出壳层自己盖了多少** —— 模式 2 才是壳层的独立判据。
        /// </summary>
        public static int CaptureMode { get; set; }
        /// <summary>调试时叠加一个棋盘格（看清 uv/覆盖范围）。</summary>
        public static bool DebugChecker { get; set; } = true;

        /// <summary>[v0.1.66] 显存预算表用（只读）：G-buffer 离屏 RT，未分配时为 null。</summary>
        public static RenderTarget2D GBufferRt => m_rt;

        static RenderTarget2D m_rt;
        static Shader m_shader;
        static Shader m_debugShader;
        static SamplerState m_sampler;
        static long m_hookTouches;
        static string m_lastError = "";

        public static string LastError => m_lastError;
        public static long HookTouches => m_hookTouches;
        public static bool HasTarget => m_rt != null;

        /// <summary>阶段回调里调用（只累加计数，证明"外部处理器被调到了"）。</summary>
        public static void TouchFromHook() {
            m_hookTouches++;
        }

        const string GBufferVsh = @"#ifdef HLSL

float2 u_origin;
float4x4 u_viewProjectionMatrix;

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float4 v_color : COLOR,
	out float2 v_texcoord : TEXCOORD,
	out float4 sv_position: SV_POSITION
)
{
	v_color = a_color;
	v_texcoord = a_texcoord;
	sv_position = mul(float4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0), u_viewProjectionMatrix);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

precision highp float;

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec4 v_color;
varying vec2 v_texcoord;

void main()
{
	v_color = a_color;
	v_texcoord = a_texcoord;
	gl_Position = u_viewProjectionMatrix * vec4(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y, 1.0);
	OPENGL_POSITION_FIX;
}

#endif";

        const string GBufferPsh = @"#ifdef HLSL

Texture2D u_texture;
SamplerState u_samplerState;

void main(
	in float4 v_color : COLOR,
	in float2 v_texcoord: TEXCOORD,
	out float4 svTarget: SV_TARGET
)
{
	float4 albedo = v_color * u_texture.Sample(u_samplerState, v_texcoord);
	svTarget = float4(albedo.rgb, 1.0);      // alpha=1 = 这里画到了几何（覆盖率判据）
}

#endif
#ifdef GLSL

// <Sampler Name='u_samplerState' Texture='u_texture' />

precision highp float;

uniform sampler2D u_texture;

varying vec4 v_color;
varying vec2 v_texcoord;

void main()
{
	vec4 albedo = v_color * texture2D(u_texture, v_texcoord);
	gl_FragColor = vec4(albedo.rgb, 1.0);
}

#endif";

        /// <summary>调试直显用的 shader（只采样那张 RT + 可选棋盘格）。</summary>
        const string DebugVsh = @"#ifdef HLSL

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

#endif";

        const string DebugPsh = @"#ifdef HLSL

Texture2D u_texture;
SamplerState u_samplerState;
float u_checker;

void main(
	in float2 v_texcoord: TEXCOORD,
	out float4 svTarget: SV_TARGET
)
{
	float4 c = u_texture.Sample(u_samplerState, v_texcoord);
	if (u_checker > 0.5)
	{
		float2 g = floor(v_texcoord * 16.0);
		float k = frac((g.x + g.y) * 0.5) * 0.25 + 0.75;
		c.rgb *= k;
	}
	svTarget = float4(c.rgb, 1.0);
}

#endif
#ifdef GLSL

// <Sampler Name='u_samplerState' Texture='u_texture' />

precision highp float;

uniform sampler2D u_texture;
uniform float u_checker;

varying vec2 v_texcoord;

void main()
{
	vec4 c = texture2D(u_texture, v_texcoord);
	if (u_checker > 0.5)
	{
		vec2 g = floor(v_texcoord * 16.0);
		float k = fract((g.x + g.y) * 0.5) * 0.25 + 0.75;
		c.rgb *= k;
	}
	gl_FragColor = vec4(c.rgb, 1.0);
}

#endif";

        static Shader EnsureShader() {
            m_shader ??= new Shader(GBufferVsh, GBufferPsh);
            return m_shader;
        }

        static Shader EnsureDebugShader() {
            m_debugShader ??= new Shader(DebugVsh, DebugPsh);
            return m_debugShader;
        }

        static SamplerState EnsureSampler() {
            m_sampler ??= new SamplerState {
                AddressModeU = TextureAddressMode.Clamp,
                AddressModeV = TextureAddressMode.Clamp,
                FilterMode = TextureFilterMode.Point,
                MaxLod = 0f
            };
            return m_sampler;
        }

        /// <summary>
        /// [v0.1.61] **给离屏 pass 一个够远的远平面**（只换远平面，近平/FOV/宽高比全不动）。
        ///
        /// 为什么必须：CPU 侧 NDC 探针（`shells.ndcProbe`）实测壳体在 x/y 上**是在视锥里**的
        /// （NDC ≈ `-0.01 / -0.64`），但 **z 恒等于 1.0** —— 落在远平面处/之外，被裁掉了。
        /// 相机投影的远平面是给**近景**设的，而壳体带最远到 `视距 + BandMetres`（默认 896 m）、
        /// LOD 到 `RadiusMetres`（1024 m），于是离屏 pass 里这些几何**一个像素都画不出来**
        /// （实测：只画壳层时覆盖率 0，提交了 165 个网格 / 93,594 个索引）。
        ///
        /// 推导：透视矩阵 `M33 = f/(n−f)`、`M43 = n·f/(n−f)` → `n = M43/M33`、`f = M43/(1+M33)`。
        /// 非标准透视（正交等）直接原样返回，不硬改。
        /// </summary>
        static Matrix ExtendFarPlane(Matrix projection, float farPlane, out float derivedNear, out float derivedFar) {
            float a = projection.M33, b = projection.M43;
            if (MathF.Abs(a) < 1e-6f || MathF.Abs(1f + a) < 1e-6f) {
                derivedNear = derivedFar = 0f;
                return projection;
            }
            float near = b / a;
            float currentFar = b / (1f + a);
            derivedNear = near;
            derivedFar = currentFar;
            if (near <= 0f || farPlane <= currentFar) {
                return projection;                       // 远平面已经够远 → 不动
            }
            projection.M33 = farPlane / (near - farPlane);
            projection.M43 = near * farPlane / (near - farPlane);
            return projection;
        }

        static void DrawLodLayers(Shader shader) {
            if (SkylineLod.CoarseVertexBuffer != null && SkylineLod.CoarseIndexCount > 0) {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, SkylineLod.CoarseVertexBuffer,
                    SkylineLod.CoarseIndexBuffer, 0, SkylineLod.CoarseIndexCount);
            }
            if (SkylineLod.FineVertexBuffer != null && SkylineLod.FineIndexCount > 0) {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, SkylineLod.FineVertexBuffer,
                    SkylineLod.FineIndexBuffer, 0, SkylineLod.FineIndexCount);
            }
            if (SkylineLod.NearVertexBuffer != null && SkylineLod.NearIndexCount > 0) {
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, SkylineLod.NearVertexBuffer,
                    SkylineLod.NearIndexBuffer, 0, SkylineLod.NearIndexCount);
            }
        }

        /// <summary>
        /// [v0.1.61] **诊断用**：把三层远景 LOD 的缓冲当成"壳层列表"返回（`CaptureMode == 5`）。
        /// 用途：壳层在离屏 pass 里不出像素时，用它把"**这条路画不出来**"和"**壳的 buffer 有问题**"分开。
        /// </summary>
        static List<(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int IndexCount, bool IsVoxel,
                     Vector3 Center, Vector3 FirstVertex)>
            LodBuffersAsList() {
            List<(VertexBuffer, IndexBuffer, int, bool, Vector3, Vector3)> list = [];
            if (SkylineLod.CoarseVertexBuffer != null && SkylineLod.CoarseIndexCount > 0) {
                list.Add((SkylineLod.CoarseVertexBuffer, SkylineLod.CoarseIndexBuffer,
                    SkylineLod.CoarseIndexCount, false, Vector3.Zero, Vector3.Zero));
            }
            if (SkylineLod.FineVertexBuffer != null && SkylineLod.FineIndexCount > 0) {
                list.Add((SkylineLod.FineVertexBuffer, SkylineLod.FineIndexBuffer,
                    SkylineLod.FineIndexCount, false, Vector3.Zero, Vector3.Zero));
            }
            if (SkylineLod.NearVertexBuffer != null && SkylineLod.NearIndexCount > 0) {
                list.Add((SkylineLod.NearVertexBuffer, SkylineLod.NearIndexBuffer,
                    SkylineLod.NearIndexCount, false, Vector3.Zero, Vector3.Zero));
            }
            return list;
        }

        /// <summary>
        /// 渲染一次 G-buffer 并回读自检。返回 JSON：
        /// { ok, size, coveragePixels, coverageRatio, meanLuma, distinctColors, ms, png? }
        /// </summary>
        public static string Capture() {
            JsonObject result = new();
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            Camera camera = SkylineLod.ActiveCamera;
            if (subsystemTerrain?.Terrain == null || camera == null) {
                result["ok"] = false;
                result["err"] = "no terrain/camera";
                return result.ToJsonString();
            }
            if (TerrainRenderer.m_opaqueShader == null) {
                result["ok"] = false;
                result["err"] = "no opaque shader";
                return result.ToJsonString();
            }
            int size = Math.Clamp(Size, 64, Math.Min(Display.MaxTextureSize, 2048));
            RenderTarget2D previousTarget = Display.RenderTarget;
            Viewport previousViewport = Display.Viewport;
            Rectangle previousScissor = Display.ScissorRectangle;
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try {
                if (m_rt == null || m_rt.Width != size) {
                    Utilities.Dispose(ref m_rt);
                    m_rt = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                Display.RenderTarget = m_rt;
                // 注意：**必须显式清深度**（与 SkylineGpuShadow.RenderDepthMap 同一坑）——
                // 只清颜色、不清深度时，新 RenderTarget 的深度缓冲是未定义值（实测全 0）→ 所有片元都过不了深度测试
                // → 回读覆盖率 0（第一次实现就是这么翻车的）。
                Display.Clear(new Vector4(0f, 0f, 0f, 0f), 1f, 0);
                Display.Viewport = new Viewport(0, 0, size, size);
                Display.ScissorRectangle = new Rectangle(0, 0, size, size);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;

                Vector3 viewPosition = camera.InvertedViewMatrix.Translation;
                Vector3 v = new(MathF.Floor(viewPosition.X), 0f, MathF.Floor(viewPosition.Z));
                // [v0.1.61] 远平面要盖住"壳带 + LOD 半径"，否则离屏 pass 里远景几何全被裁掉（见 ExtendFarPlane 注释）
                float wantFar = MathF.Max(SkylineLod.RadiusMetres, SkylineCubeShellStore.BandMetres) + 256f;
                Matrix projection = ExtendFarPlane(camera.ProjectionMatrix, wantFar,
                    out float derivedNear, out float derivedFar);
                Matrix matrix = Matrix.CreateTranslation(v - viewPosition)
                    * camera.ViewMatrix.OrientationMatrix * projection;
                Shader shader = EnsureShader();
                shader.GetParameter("u_origin", true).SetValue(new Vector2(v.X, v.Z));
                shader.GetParameter("u_viewProjectionMatrix", true).SetValue(matrix);
                shader.GetParameter("u_texture", true)
                    .SetValue(subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                shader.GetParameter("u_samplerState", true).SetValue(EnsureSampler());
                bool drawLod = CaptureMode != 2;
                bool drawShells = IncludeShells && (CaptureMode == 0 || CaptureMode >= 2);
                bool shellsViaLodBuffers = CaptureMode == 5;     // 诊断：把 LOD 的 VB 走"壳层那条路"画一遍
                if (shellsViaLodBuffers) {
                    drawLod = false;
                }
                if (drawLod) {
                    DrawLodLayers(shader);
                }
                // [v0.1.61] 里程碑 1.4：把 32³ 壳网格也画进来（几何与主画面壳层同一批）
                int shellMeshes = 0, shellIndices = 0, shellVoxelMeshes = 0;
                int drawErrors = 0;
                JsonArray ndcProbe = [];
                if (drawShells) {
                    List<(VertexBuffer VertexBuffer, IndexBuffer IndexBuffer, int IndexCount, bool IsVoxel,
                          Vector3 Center, Vector3 FirstVertex)> shells =
                        shellsViaLodBuffers ? LodBuffersAsList() : SkylineCubeShellStore.CollectDrawableMeshes(camera);
                    if (CaptureMode == 4 && shells.Count > 1) {
                        shells = shells.GetRange(0, 1);          // 诊断：只画第一个壳（排除"数量太多被丢"）
                    }
                    // 缓冲元数据：壳与 LOD 的顶点步长/元素数/索引格式都不一样的话，混在一个 pass 里可能就是问题
                    VertexBuffer firstVb = shells.Count > 0 ? shells[0].VertexBuffer : null;
                    IndexBuffer firstIb = shells.Count > 0 ? shells[0].IndexBuffer : null;
                    result["buffers"] = new JsonObject {
                        ["shellStride"] = firstVb?.VertexDeclaration?.VertexStride,
                        ["shellElements"] = firstVb?.VertexDeclaration == null
                            ? (int?)null : firstVb.VertexDeclaration.VertexElements.Count,
                        ["shellVerts"] = firstVb?.VerticesCount,
                        ["shellIndexFormat"] = firstIb?.IndexFormat.ToString(),
                        ["lodStride"] = SkylineLod.CoarseVertexBuffer?.VertexDeclaration?.VertexStride,
                        ["lodElements"] = SkylineLod.CoarseVertexBuffer?.VertexDeclaration == null
                            ? (int?)null : SkylineLod.CoarseVertexBuffer.VertexDeclaration.VertexElements.Count,
                        ["lodIndexFormat"] = SkylineLod.CoarseIndexBuffer?.IndexFormat.ToString()
                    };
                    // [v0.1.61] **CPU 侧 NDC 探针**：把前几个壳的中心用同一个矩阵投一遍。
                    // 为什么需要：壳层在离屏 pass 里"提交了 165 个网格却一个像素都没有"，
                    // 只有把"投到哪去了"量出来，才能分清是"不在视锥内"还是"被深度挡住"。
                    // NDC 判据：|x|≤1 且 |y|≤1 且 0<z≤1 才算真的在视锥里。
                    foreach ((VertexBuffer _, IndexBuffer _, int _, bool _, Vector3 center, Vector3 first) in shells) {
                        if (ndcProbe.Count >= 4) {
                            break;
                        }
                        Vector4 clip = Vector4.Transform(
                            new Vector4(center.X - v.X, center.Y, center.Z - v.Z, 1f), matrix);
                        Vector4 clipV = Vector4.Transform(
                            new Vector4(first.X - v.X, first.Y, first.Z - v.Z, 1f), matrix);
                        ndcProbe.Add(new JsonObject {
                            ["center"] = new JsonArray(Math.Round(center.X, 1), Math.Round(center.Y, 1), Math.Round(center.Z, 1)),
                            ["vertex0"] = new JsonArray(Math.Round(first.X, 1), Math.Round(first.Y, 1), Math.Round(first.Z, 1)),
                            ["w"] = Math.Round(clip.W, 2),
                            ["ndc"] = new JsonArray(Math.Round(clip.X / clip.W, 3), Math.Round(clip.Y / clip.W, 3),
                                Math.Round(clip.Z / clip.W, 3)),
                            ["wV"] = Math.Round(clipV.W, 2),
                            ["ndcV"] = new JsonArray(Math.Round(clipV.X / clipV.W, 3), Math.Round(clipV.Y / clipV.W, 3),
                                Math.Round(clipV.Z / clipV.W, 3))
                        });
                    }
                    if (shells.Count > 0) {
                        // **用主画面同一个 shader 与同一个矩阵**（`PrepareTerrainShader` 已经把 `BandLift` 包进矩阵里）：
                        // 这样 G-buffer 里的壳就是玩家在主画面里真正看到的那批几何、那个位置，逐像素对拍才有意义。
                        //
                        // 为什么不用上面那个自编译的 G-buffer shader 画壳：实测（v0.1.61）那样**一个像素都不光栅化**
                        // （把壳整体抬 ±40 m 画面也毫无变化）—— 同一个 VB/IB 在主画面里画得好好的，
                        // 说明问题出在"自编译 shader + 壳顶点声明"这个组合上，具体根因未定位，如实记在 notes/140。
                        Shader shellShader = SkylineCubeSurfaceDemo.PrepareTerrainShader(
                            camera, SkylineCubeShellStore.BandLift);
                        if (shellShader != null) {
                            foreach ((VertexBuffer vb, IndexBuffer ib, int count, bool isVoxel, Vector3 center,
                                      Vector3 first) in shells) {
                                try {
                                    // 模式 3 = 用**自编译的 G-buffer shader** 画壳（第一次尝试的做法，用于同一二进制内对照）
                                    Shader use = CaptureMode == 3 ? shader : shellShader;
                                    Display.DrawIndexed(PrimitiveType.TriangleList, use, vb, ib, 0, count);
                                }
                                catch (Exception ex) {
                                    drawErrors++;
                                    m_lastError = $"{vb?.VerticesCount}v/{count}i: {ex.Message}";
                                }
                                shellMeshes++;
                                shellIndices += count;
                                if (isVoxel) {
                                    shellVoxelMeshes++;
                                }
                            }
                        }
                    }
                }

                Engine.Media.Image image = m_rt.GetData(new Rectangle(0, 0, size, size));
                int covered = 0;
                double lumaSum = 0.0;
                System.Collections.Generic.HashSet<int> distinct = [];
                for (int y = 0; y < size; y++) {
                    for (int x = 0; x < size; x++) {
                        Color c = image.GetPixel(x, y);
                        if (c.A > 0) {
                            covered++;
                            lumaSum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                            distinct.Add((c.R >> 4) << 8 | (c.G >> 4) << 4 | (c.B >> 4));
                        }
                    }
                }
                watch.Stop();
                result["ok"] = true;
                result["size"] = size;
                result["coveragePixels"] = covered;
                result["coverageRatio"] = Math.Round(covered / (double)(size * size), 4);
                result["meanLuma"] = Math.Round(covered > 0 ? lumaSum / covered : 0.0, 2);
                result["distinctColors"] = distinct.Count;
                result["ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 2);
                result["layers"] = new JsonObject {
                    ["coarseIndices"] = SkylineLod.CoarseIndexCount,
                    ["fineIndices"] = SkylineLod.FineIndexCount,
                    ["nearIndices"] = SkylineLod.NearIndexCount
                };
                result["shells"] = new JsonObject {
                    ["includeShells"] = IncludeShells,
                    ["captureMode"] = CaptureMode,
                    ["drewLod"] = drawLod,
                    ["drewShells"] = drawShells,
                    ["meshes"] = shellMeshes,
                    ["indices"] = shellIndices,
                    ["voxelMeshes"] = shellVoxelMeshes,
                    ["bandLift"] = SkylineCubeShellStore.BandLift,
                    ["drawErrors"] = drawErrors,
                    ["derivedNear"] = Math.Round(derivedNear, 3),
                    ["derivedFar"] = Math.Round(derivedFar, 1),
                    ["appliedFar"] = Math.Round(wantFar, 1),
                    ["ndcProbe"] = ndcProbe
                };
                result["note"] = "G-buffer：RGB=albedo（图集采样×顶点色），A=1 表示画到了几何；"
                    + "几何源 = 三层远景 LOD" + (IncludeShells ? " + 32³ 壳网格（里程碑 1.4）" : "（壳层已关）");
            }
            catch (Exception e) {
                m_lastError = e.Message;
                result["ok"] = false;
                result["err"] = e.Message;
            }
            finally {
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
            return result.ToJsonString();
        }

        /// <summary>由 `SubsystemTerrain.Draw` 的 final 阶段调用：调试直显这张 G-buffer。</summary>
        public static void DebugDrawIfEnabled(Camera camera) {
            if (!DebugDraw || m_rt == null || camera == null) {
                return;
            }
            try {
                RenderTarget2D previousTarget = Display.RenderTarget;
                Viewport previousViewport = Display.Viewport;
                Rectangle previousScissor = Display.ScissorRectangle;
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.None;
                Display.RasterizerState = RasterizerState.CullNoneScissor;
                Display.Viewport = new Viewport(0, 0, previousTarget.Width, previousTarget.Height);
                Display.ScissorRectangle = new Rectangle(0, 0, previousTarget.Width, previousTarget.Height);
                Shader shader = EnsureDebugShader();
                shader.GetParameter("u_texture", true).SetValue(m_rt);
                shader.GetParameter("u_samplerState", true).SetValue(EnsureSampler());
                shader.GetParameter("u_checker", true).SetValue(DebugChecker ? 1f : 0f);
                // 一个覆盖全屏的四边形（NDC 坐标在顶点着色器里直接用）
                m_quad ??= CreateFullScreenQuad();
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_quad, m_quadIndices, 0, m_quadIndicesCount);
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
            catch (Exception e) {
                m_lastError = e.Message;
                Log.Warning($"SkylineGBuffer.DebugDraw: {e.Message}");
            }
        }

        static VertexBuffer m_quad;

        static VertexBuffer CreateFullScreenQuad() {
            TerrainVertex[] vertices = new TerrainVertex[4];
            // **左上角 1/4 画中画**（NDC 里 y=+1 是屏幕上方）：这样 A/B 截图能同时看到"正常画面"和"G-buffer 内容"，
            // 比全屏覆盖直观得多（全屏覆盖会把整个画面替换成 G-buffer，除了黑背景什么也看不出来）。
            BlockGeometryGenerator.SetupVertex(-1f, 0f, 0f, Color.White, 0f, 1f, ref vertices[0]);
            BlockGeometryGenerator.SetupVertex(0f, 0f, 0f, Color.White, 1f, 1f, ref vertices[1]);
            BlockGeometryGenerator.SetupVertex(0f, 1f, 0f, Color.White, 1f, 0f, ref vertices[2]);
            BlockGeometryGenerator.SetupVertex(-1f, 1f, 0f, Color.White, 0f, 0f, ref vertices[3]);
            short[] indices = [0, 1, 2, 0, 2, 3];
            VertexBuffer vb = new(TerrainVertex.VertexDeclaration, 4);
            vb.SetData(vertices, 0, 4);
            // 这里把索引也塞进同一个 VertexBuffer 的“兄弟”里不方便，改为直接返回带索引的对象
            m_quadIndices = new IndexBuffer(IndexFormat.SixteenBits, indices.Length);
            m_quadIndices.SetData(indices, 0, indices.Length);
            m_quadIndicesCount = indices.Length;
            return vb;
        }

        static IndexBuffer m_quadIndices;
        static int m_quadIndicesCount;

        public static string Info() {
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["size"] = Size,
                ["debugDraw"] = DebugDraw,
                ["includeShells"] = IncludeShells,
                ["captureMode"] = CaptureMode,
                ["hasTarget"] = HasTarget,
                ["hookTouches"] = m_hookTouches,
                ["layers"] = new JsonObject {
                    ["coarseIndices"] = SkylineLod.CoarseIndexCount,
                    ["fineIndices"] = SkylineLod.FineIndexCount,
                    ["nearIndices"] = SkylineLod.NearIndexCount
                },
                ["note"] = "G-buffer v1 = albedo + 覆盖；法线/材质 id 需要先给 LOD 顶点格式加属性（下一步）",
                ["lastError"] = m_lastError
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.GBuffer*`。</summary>
    public static partial class SkylineRuntime {
        public static string GBufferEnable(bool enabled) {
            SkylineGBuffer.Enabled = enabled;
            return SkylineGBuffer.Info();
        }

        public static string GBufferDebug(bool debugDraw, bool checker) {
            SkylineGBuffer.DebugDraw = debugDraw;
            SkylineGBuffer.DebugChecker = checker;
            return SkylineGBuffer.Info();
        }

        public static string GBufferCapture() => SkylineGBuffer.Capture();

        /// <summary>
        /// [v0.1.61] **壳网格进 G-buffer 的开关**（里程碑 1.4）：关掉 = G-buffer 只含三层远景 LOD（v0.1.60 行为）。
        /// 用来做 A/B：开着 `GBufferCapture` 的覆盖率/几何数应当**不低于**关着的时候（壳层只会往里加几何）。
        /// </summary>
        public static string GBufferShells(bool enabled) {
            SkylineGBuffer.IncludeShells = enabled;
            return SkylineGBuffer.Info();
        }

        /// <summary>
        /// [v0.1.61] G-buffer 分层取证模式：`0`=两层都画、`1`=只画 LOD、**`2`=只画 32³ 壳层**。
        /// 壳层的独立覆盖率只看模式 2（两层一起画时壳被 LOD 压住，量不出自己的贡献）。
        /// </summary>
        public static string GBufferMode(int mode) {
            SkylineGBuffer.CaptureMode = Math.Clamp(mode, 0, 5);
            return SkylineGBuffer.Info();
        }

        public static string GBufferInfo() => SkylineGBuffer.Info();
    }
}
