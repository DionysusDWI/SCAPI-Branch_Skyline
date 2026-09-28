using System;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.113：**相机空间深度预通道**（里程碑 4 的"场景深度纹理"）。
    ///
    /// 为什么先做它：Iris 光影包里三件最显眼的东西 —— **屏幕空间体积光**（`volumetricLight.glsl`）、
    /// **SSAO**、**TAA** —— 全部以"场景深度纹理"为前置，而本分支到现在为止只有**太阳视角**的深度图
    /// （`SkylineGpuShadow`）与一张只有 albedo/覆盖的 G-buffer（`SkylineGBuffer`），**没有相机视角的深度**。
    /// 这一层就是那个缺口：每帧在**不透明地形之前**多跑一个只写深度的 pass。
    ///
    /// 设计（与既有 pass 保持同一套口径，便于复用与对照）：
    ///   * **RT**：`BackbufferSize / ScreenDepthDivisor`（默认 2 ⇒ 半分辨率），RGBA8 + Depth24Stencil8；
    ///     **R/G = 16 bit 窗口深度**（与 `SkylineGpuShadow` 的编码/解码逐字同式）、B = 8 bit 预览、A = 覆盖；
    ///   * **深度 = 线性视距**（`distance(u_eye, worldPos) / (maxMetres × 1.5)`，见 `ScreenDepthScaleMetres`）；
    ///     片元侧用 `inverse(viewProjection)` 求出该像素的视线方向，再 `world = eye + dir × dist` 重建；
    ///   * **只画真地形区块**（不透明子集 `0x1F`，与太阳深度图同口径），并且**按距离粗裁**：
    ///     AO/接触阴影只在近处有意义，`ScreenDepthMaxMetres`（默认 96 m）之外不画 ⇒ 预通道的代价有界；
    ///   * **默认关**（不改任何既有渲染）；打开后 `skyline.ScreenDepthCapture()` 回读取证，
    ///     `skyline.ScreenDepthDebugDraw=true` 把深度图直显在**左上角 1/4 画中画**（A/B 用）。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>总开关（默认关；打开后每帧多一个深度 pass）。</summary>
        public static bool ScreenDepthEnabled { get; set; }

        /// <summary>分辨率除数（1..4，默认 2 = 半分辨率）。</summary>
        public static int ScreenDepthDivisor { get; set; } = 2;

        /// <summary>只画这个半径内的地形区块（米，默认 96）。</summary>
        public static float ScreenDepthMaxMetres { get; set; } = 96f;

        /// <summary>16 bit 窗口深度（默认开；关掉逐位退回 8 bit，用于验证"量化够不够"）。</summary>
        public static bool ScreenDepthDepth16 { get; set; } = true;

        /// <summary>调试直显（左上角 1/4 画中画）。</summary>
        public static bool ScreenDepthDebugDraw { get; set; }

        /// <summary>
        /// **诊断用输出模式**（默认 0）：
        /// `0` = 正常（线性视距）、`1` = 常量红（**只验"几何 + 矩阵 + 状态"通不通**）、
        /// `2` = 顶点色（看属性绑定）。
        /// 为什么需要它：v0.1.113 第一版回读全是"背景"（`coveredPixels=0`），
        /// 分不清是"几何没画进去"还是"深度取值不对" —— 有了这个开关就能一次部署把两者分开。
        /// </summary>
        public static int ScreenDepthDebugMode { get; set; }

        /// <summary>[诊断] 把 LOD 层也画进预通道（默认关）。
        /// 为什么需要：如果"画 LOD 有像素、画区块没有像素"，问题就在区块那条路；反之在 RT/状态那条路。</summary>
        public static bool ScreenDepthDrawLod { get; set; }

        /// <summary>[诊断] 矩阵变体：`0` = 相机透视（正常）；`1` = **太阳深度 pass 那套已验证的正交矩阵**
        /// （从相机方向看出去）。用途：把"矩阵算错"与"绘制路径本身不通"分开 —— 变体 1 能出像素就说明
        /// RT/状态/绘制这条链是好的。</summary>
        public static int ScreenDepthMatrixVariant { get; set; }

        /// <summary>[诊断] 分阶段回读：清屏之后 / 画完之后各读一小块，看"内容到底在哪一步丢的"。</summary>
        public static bool ScreenDepthProbeStages { get; set; }

        /// <summary>离屏 RT（未分配时为 null）。</summary>
        public static RenderTarget2D ScreenDepthRt => m_sdRt;

        /// <summary>片元侧重建世界坐标用：`ndc → 世界（未减 origin）`。</summary>
        public static Matrix ScreenDepthInvViewProjection => m_sdInvViewProjection;

        /// <summary>片元侧重建世界坐标用：重建结果是"浮动原点"坐标，加回这个 XZ 才是世界坐标。</summary>
        public static Vector2 ScreenDepthOrigin => m_sdOrigin;

        /// <summary>
        /// **[v0.1.113] 深度值的世界尺度**：存储值 `1.0` 对应这个米数（= `ScreenDepthMaxMetres × 1.5`）。
        /// 片元侧用它把深度解码成"到相机的直线距离"（米）：`dist = d * ScreenDepthScaleMetres`。
        ///
        /// 为什么存**线性视距**而不是窗口深度：实测（2026-09-28）这台机器上相机远平面很大（数千），
        /// DX 风格窗口深度 `clip.z/clip.w` 把 **2 m 与 96 m 都压到 0.996+**（debug 直显整片品红），
        /// 16 bit 也救不回来 ⇒ 线性视距在 1.5×96 m 上的量化步长只有 **2.2 mm**。
        /// </summary>
        public static float ScreenDepthScaleMetres => MathF.Max(ScreenDepthMaxMetres, 16f) * 1.5f;

        /// <summary>本帧的预通道是否可用（片元侧要用它决定要不要做 AO）。</summary>
        public static bool ScreenDepthReady => ScreenDepthEnabled && m_sdRt != null && m_sdFrames > 0;

        static RenderTarget2D m_sdRt;
        static Shader m_sdShader;
        static Shader m_sdDebugShader;
        static SamplerState m_sdSampler;
        static Matrix m_sdInvViewProjection = Matrix.Identity;
        static Vector2 m_sdOrigin;
        static int m_sdChunksDrawn;
        static long m_sdFrames;
        static long m_sdLastMs;
        static string m_sdLastError = "";
        static JsonArray m_sdNdcProbe = [];
        static int m_sdChunksTested;
        static int m_sdChunksWithCornerInside;
        static string m_sdStages = "";

        /// <summary>[诊断] 从 RT 中心读一小块，数"非背景（R<0.999）"的像素。</summary>
        static string ProbeStages(RenderTarget2D rt) {
            try {
                int w = Math.Min(64, rt.Width), h = Math.Min(64, rt.Height);
                int x0 = (rt.Width - w) / 2, y0 = (rt.Height - h) / 2;
                Image img = rt.GetData(new Rectangle(x0, y0, w, h));
                int nonBg = 0;
                float minR = 1f, maxR = 0f;
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        Color c = img.GetPixel(x, y);
                        minR = MathF.Min(minR, c.R);
                        maxR = MathF.Max(maxR, c.R);
                        if (c.R < 0.999f) {
                            nonBg++;
                        }
                    }
                }
                return $"{nonBg}/{w * h}(R {minR:0.###}..{maxR:0.###})";
            }
            catch (Exception e) {
                return $"err {e.Message}";
            }
        }
        static VertexBuffer m_sdQuad;
        static IndexBuffer m_sdQuadIndices;
        static int m_sdQuadIndicesCount;

        public static string ScreenDepthLastError => m_sdLastError;
        public static long ScreenDepthFrames => m_sdFrames;
        public static int ScreenDepthChunksDrawn => m_sdChunksDrawn;

        /// <summary>
        /// 每帧由 `SubsystemTerrain.Draw` 在**不透明地形之前**调用（与 `Run("shadow")` 同一位置）。
        /// 任何异常都只记日志并自动关掉本 pass，**绝不打断帧**（与 G-buffer/阴影同一纪律）。
        /// </summary>
        public static void ScreenDepthPass(Camera camera) {
            if (!ScreenDepthEnabled || camera == null) {
                return;
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            if (subsystemTerrain?.Terrain == null) {
                return;
            }
            try {
                int divisor = Math.Clamp(ScreenDepthDivisor, 1, 4);
                Point2 backbuffer = Display.BackbufferSize;
                int width = Math.Max(16, backbuffer.X / divisor);
                int height = Math.Max(16, backbuffer.Y / divisor);
                if (m_sdRt == null || m_sdRt.Width != width || m_sdRt.Height != height) {
                    Utilities.Dispose(ref m_sdRt);
                    m_sdRt = new RenderTarget2D(width, height, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                RenderTarget2D previousTarget = Display.RenderTarget;
                Viewport previousViewport = Display.Viewport;
                Rectangle previousScissor = Display.ScissorRectangle;
                double start = Time.RealTime;
                try {
                    Display.RenderTarget = m_sdRt;
                    Display.Viewport = new Viewport(0, 0, width, height);
                    Display.ScissorRectangle = new Rectangle(0, 0, width, height);
                    Display.Clear(new Vector4(1f, 1f, 1f, 1f), 1f, 0);       // 背景 = 最远
                    if (ScreenDepthProbeStages) {
                        m_sdStages = "afterClear=" + ProbeStages(m_sdRt);
                    }

                    Vector3 eye = camera.ViewPosition;
                    Vector3 origin3 = new(MathF.Floor(eye.X), 0f, MathF.Floor(eye.Z));
                    m_sdOrigin = new Vector2(origin3.X, origin3.Z);
                    Matrix viewProjection;
                    if (ScreenDepthMatrixVariant == 1) {
                        // [诊断] 照抄 `SkylineGpuShadow.RenderDepthMap` 的正交矩阵（那一条是已验证能出像素的），
                        //   只是把"从太阳看"换成"从相机看"。
                        float radius = MathF.Max(ScreenDepthMaxMetres, 16f) + 16f;
                        Vector3 dir = Vector3.Normalize(camera.ViewDirection);
                        Vector3 orthoEye = eye + dir * MathF.Max(radius * 4f, 512f);
                        Matrix view = Matrix.CreateLookAt(orthoEye, eye, Vector3.UnitY);
                        Matrix ortho = Matrix.CreateOrthographic(radius * 2f, radius * 2f, 1f, radius * 8f);
                        viewProjection = Matrix.CreateTranslation(origin3) * view * ortho;
                    }
                    else {
                        viewProjection =
                            Matrix.CreateTranslation(origin3 - eye)
                            * camera.ViewMatrix.OrientationMatrix
                            * camera.ProjectionMatrix;
                    }
                    m_sdInvViewProjection = Matrix.Invert(viewProjection);

                    Shader shader = EnsureScreenDepthShader();
                    shader.GetParameter("u_origin", true).SetValue(m_sdOrigin);
                    shader.GetParameter("u_viewProjectionMatrix", true).SetValue(viewProjection);
                    shader.GetParameter("u_eye", true).SetValue(eye);
                    shader.GetParameter("u_depthScale", true).SetValue(1f / ScreenDepthScaleMetres);
                    shader.GetParameter("u_depth16", true).SetValue(ScreenDepthDepth16 ? 1f : 0f);
                    shader.GetParameter("u_debugMode", true).SetValue((float)ScreenDepthDebugMode);
                    if (ScreenDepthMatrixVariant == 2) {
                        // [诊断] 用**太阳深度 pass 自己的、已验证能出像素的 shader**
                        //   （它只认 u_origin/u_viewProjectionMatrix/u_eye/u_sunDir/u_depthMax/u_depth16）。
                        shader = EnsureGpuShadowShader();
                        float radius = MathF.Max(ScreenDepthMaxMetres, 16f) + 16f;
                        Vector3 dir = Vector3.Normalize(camera.ViewDirection);
                        Vector3 orthoEye = eye + dir * MathF.Max(radius * 4f, 512f);
                        Matrix view = Matrix.CreateLookAt(orthoEye, eye, Vector3.UnitY);
                        Matrix ortho = Matrix.CreateOrthographic(radius * 2f, radius * 2f, 1f, radius * 8f);
                        shader.GetParameter("u_origin", true).SetValue(m_sdOrigin);
                        shader.GetParameter("u_viewProjectionMatrix", true).SetValue(
                            Matrix.CreateTranslation(new Vector3(m_sdOrigin.X, 0f, m_sdOrigin.Y)) * view * ortho);
                        shader.GetParameter("u_eye", true).SetValue(orthoEye);
                        shader.GetParameter("u_sunDir", true).SetValue(dir);
                        shader.GetParameter("u_depthMax", true).SetValue(radius * 8f);
                        shader.GetParameter("u_depth16", true).SetValue(ScreenDepthDepth16 ? 1f : 0f);
                    }
                    Display.BlendState = BlendState.Opaque;
                    Display.DepthStencilState = DepthStencilState.Default;
                    Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;

                    float maxDistance = MathF.Max(ScreenDepthMaxMetres, 16f) + 16f;
                    int drawn = 0;
                    JsonArray ndcProbe = [];
                    int cornersInside = 0;
                    int chunksTested = 0;
                    foreach (TerrainChunk chunk in subsystemTerrain.Terrain.AllocatedChunks) {
                        if (chunk == null || chunk.State != TerrainChunkState.Valid || chunk.Buffers.Count == 0) {
                            continue;
                        }
                        if (Vector3.Distance(chunk.BoundingBox.Center(), eye) > maxDistance) {
                            continue;                                          // 距离粗裁（AO 只在近处有意义）
                        }
                        if (ndcProbe.Count < 4) {
                            // [诊断] 用**同一套矩阵**在 CPU 上把这个区块中心投到 NDC：矩阵写错时这里一眼可见。
                            //   只收"在相机前方"（w>0）的区块 —— 第一版探针全落在背后的区块上（w 负），
                            //   什么也说明不了（迭代顺序导致）。
                            Vector3 c = chunk.BoundingBox.Center();
                            Vector4 clip = Vector4.Transform(
                                new Vector4(c.X - origin3.X, c.Y, c.Z - origin3.Z, 1f), viewProjection);
                            if (clip.W <= 0.01f) {
                                continue;
                            }
                            double w = Math.Abs(clip.W) < 1e-9 ? 1e-9 : clip.W;
                            ndcProbe.Add(new JsonObject {
                                ["center"] = new JsonArray(Math.Round(c.X, 1), Math.Round(c.Y, 1), Math.Round(c.Z, 1)),
                                ["ndc"] = new JsonArray(Math.Round(clip.X / w, 4), Math.Round(clip.Y / w, 4),
                                                       Math.Round(clip.Z / w, 4)),
                                ["w"] = Math.Round((double)clip.W, 3),
                                ["dist"] = Math.Round((double)Vector3.Distance(c, eye), 1)
                            });
                        }
                        // [诊断] 包围盒 8 个角点：有几个落在裁剪体里（|x|<=w, |y|<=w, 0<=z<=w）
                        chunksTested++;
                        BoundingBox box = chunk.BoundingBox;
                        for (int corner = 0; corner < 8; corner++) {
                            Vector3 p = new(
                                (corner & 1) == 0 ? box.Min.X : box.Max.X,
                                (corner & 2) == 0 ? box.Min.Y : box.Max.Y,
                                (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
                            Vector4 clip = Vector4.Transform(
                                new Vector4(p.X - origin3.X, p.Y, p.Z - origin3.Z, 1f), viewProjection);
                            if (clip.W > 0.01f
                                && MathF.Abs(clip.X) <= clip.W
                                && MathF.Abs(clip.Y) <= clip.W
                                && clip.Z >= 0f && clip.Z <= clip.W) {
                                cornersInside++;
                                break;
                            }
                        }
                        DrawChunkSubsets(shader, chunk, 0x1F);
                        drawn++;
                    }
                    m_sdChunksDrawn = drawn;
                    m_sdNdcProbe = ndcProbe;
                    m_sdChunksTested = chunksTested;
                    m_sdChunksWithCornerInside = cornersInside;
                    if (ScreenDepthDrawLod) {
                        SkylineLod.DrawWithShader(shader);
                    }
                    if (ScreenDepthProbeStages) {
                        m_sdStages += " afterDraws=" + ProbeStages(m_sdRt);
                    }
                }
                finally {
                    Display.RenderTarget = previousTarget;
                    Display.Viewport = previousViewport;
                    Display.ScissorRectangle = previousScissor;
                }
                m_sdFrames++;
                m_sdLastMs = (long)((Time.RealTime - start) * 1000.0);
            }
            catch (Exception e) {
                m_sdLastError = $"{e.GetType().Name}: {e.Message}";
                ScreenDepthEnabled = false;
                Log.Warning($"SkylineScreenDepth: {m_sdLastError}（已自动关闭）");
            }
        }

        /// <summary>立即跑一次预通道并**回读**统计（取证用；回读本身慢，不要在正常帧里调）。</summary>
        public static string ScreenDepthCapture() {
            JsonObject result = new();
            Camera camera = GetCamera();
            if (camera == null) {
                result["ok"] = false;
                result["err"] = "no camera";
                return result.ToJsonString();
            }
            bool saved = ScreenDepthEnabled;
            ScreenDepthEnabled = true;
            try {
                ScreenDepthPass(camera);
                if (m_sdRt == null) {
                    result["ok"] = false;
                    result["err"] = m_sdLastError.Length > 0 ? m_sdLastError : "no render target";
                    return result.ToJsonString();
                }
                int width = m_sdRt.Width, height = m_sdRt.Height;
                Image image = m_sdRt.GetData(new Rectangle(0, 0, width, height));
                int covered = 0;
                double sum = 0.0;
                float minD = 1f, maxD = 0f;
                // [口径修正] "量化台阶"要数**不同的深度取值**，不是"相邻像素跳变的次数"——
                //   第一版数的是跳变次数（16 bit 与 8 bit 都 ~19k），那个数根本区分不出两种编码。
                HashSet<int> distinctValues = [];
                int nonBackdropRaw = 0;      // R 通道不等于清屏值(255)的像素数（不分 16/8 bit）
                bool depth16 = ScreenDepthDepth16;
                for (int y = 0; y < height; y++) {
                    for (int x = 0; x < width; x++) {
                        Color pixel = image.GetPixel(x, y);
                        // ⚠️ `Engine.Media.Color` 的 R/G/B 是 **0..255 的字节**（不是 0..1 浮点）——
                        //   第一版按 0..1 解码 ⇒ 阈值 `R < 0.999` 几乎永远为假 ⇒ `coveredPixels` 恒为 0，
                        //   于是把"能画的 pass"误判成"什么都没画"（踩了整整三轮部署）。
                        if (pixel.R < 254) {
                            nonBackdropRaw++;
                        }
                        float d = depth16
                            ? (pixel.R * 256f + pixel.G) / 65535f
                            : pixel.R / 255f;
                        if (depth16 && pixel.R * 256 + pixel.G >= 65278) {      // 背景（清屏 = 65535）
                            continue;
                        }
                        if (!depth16 && pixel.R >= 254) {
                            continue;                                        // 背景（没有几何）
                        }
                        covered++;
                        sum += d;
                        minD = MathF.Min(minD, d);
                        maxD = MathF.Max(maxD, d);
                        distinctValues.Add((int)MathF.Round(d * 65535f));
                    }
                }
                int total = width * height;
                result["ok"] = true;
                result["size"] = new JsonArray(width, height);
                result["divisor"] = ScreenDepthDivisor;
                result["maxMetres"] = ScreenDepthMaxMetres;
                result["depth16"] = depth16;
                result["scaleMetres"] = Math.Round((double)ScreenDepthScaleMetres, 3);
                result["chunksDrawn"] = m_sdChunksDrawn;
                result["coveredPixels"] = covered;
                result["coveredRatio"] = Math.Round((double)covered / Math.Max(total, 1), 4);
                result["meanDepth"] = covered > 0 ? Math.Round(sum / covered, 5) : (double?)null;
                result["minDepth"] = covered > 0 ? Math.Round((double)minD, 5) : (double?)null;
                result["maxDepth"] = covered > 0 ? Math.Round((double)maxD, 5) : (double?)null;
                result["distinctDepthValues"] = distinctValues.Count;   // 不同深度取值数（16 bit 应远多于 8 bit）
                result["nonBackdropRaw"] = nonBackdropRaw;       // 诊断：清屏值之外还有多少像素
                result["debugMode"] = ScreenDepthDebugMode;
                result["ndcProbe"] = m_sdNdcProbe;              // 诊断：CPU 侧把区块中心投到 NDC 的结果
                result["chunksTested"] = m_sdChunksTested;
                result["chunksWithCornerInside"] = m_sdChunksWithCornerInside;
                result["drawLod"] = ScreenDepthDrawLod;
                result["matrixVariant"] = ScreenDepthMatrixVariant;
                result["probeStages"] = ScreenDepthProbeStages ? m_sdStages : "";
                {
                    // 相机投影的近平/远平（从矩阵反推，与 SkylineGBuffer.ExtendFarPlane 同一套推导）
                    Matrix p = camera.ProjectionMatrix;
                    double near = 0, far = 0;
                    if (Math.Abs(p.M33) > 1e-6 && Math.Abs(1 + p.M33) > 1e-6) {
                        near = p.M43 / p.M33;
                        far = p.M43 / (1 + p.M33);
                    }
                    result["cameraNearFar"] = new JsonArray(Math.Round(near, 3), Math.Round(far, 3));
                    result["cameraPos"] = new JsonArray(Math.Round(camera.ViewPosition.X, 1),
                                                        Math.Round(camera.ViewPosition.Y, 1),
                                                        Math.Round(camera.ViewPosition.Z, 1));
                    result["cameraViewDir"] = new JsonArray(Math.Round(camera.ViewDirection.X, 3),
                                                            Math.Round(camera.ViewDirection.Y, 3),
                                                            Math.Round(camera.ViewDirection.Z, 3));
                }
                result["captureMs"] = m_sdLastMs;
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = $"{e.GetType().Name}: {e.Message}";
            }
            finally {
                ScreenDepthEnabled = saved;
            }
            return result.ToJsonString();
        }

        public static string ScreenDepthDescribe() {
            JsonObject o = new() {
                ["enabled"] = ScreenDepthEnabled,
                ["ready"] = ScreenDepthReady,
                ["divisor"] = ScreenDepthDivisor,
                ["size"] = m_sdRt == null ? null : new JsonArray(m_sdRt.Width, m_sdRt.Height),
                ["maxMetres"] = ScreenDepthMaxMetres,
                ["depth16"] = ScreenDepthDepth16,
                ["frames"] = m_sdFrames,
                ["chunksDrawn"] = m_sdChunksDrawn,
                ["lastPassMs"] = m_sdLastMs,
                ["debugDraw"] = ScreenDepthDebugDraw,
                ["lastError"] = m_sdLastError,
                ["note"] = "相机视角深度预通道；片元侧用 ScreenDepthInvViewProjection + ndc.z 重建世界坐标"
            };
            return o.ToJsonString();
        }

        /// <summary>[v0.1.113] 调试直显（左上角 1/4 画中画）：R 通道 = 16 bit 深度的高字节。</summary>
        public static void ScreenDepthDebugDrawIfEnabled(Camera camera) {
            if (!ScreenDepthDebugDraw || m_sdRt == null || camera == null) {
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
                Shader shader = EnsureScreenDepthDebugShader();
                shader.GetParameter("u_texture", true).SetValue(m_sdRt);
                shader.GetParameter("u_samplerState", true).SetValue(EnsureScreenDepthSampler());
                shader.GetParameter("u_checker", true).SetValue(0f);
                if (m_sdQuad == null) {
                    CreateScreenDepthQuad();
                }
                Display.DrawIndexed(PrimitiveType.TriangleList, shader, m_sdQuad, m_sdQuadIndices, 0, m_sdQuadIndicesCount);
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
            catch (Exception e) {
                m_sdLastError = e.Message;
                Log.Warning($"SkylineScreenDepth.DebugDraw: {e.Message}");
            }
        }

        static Shader EnsureScreenDepthShader() {
            m_sdShader ??= new Shader(ScreenDepthVsh, ScreenDepthPsh);
            return m_sdShader;
        }

        static Shader EnsureScreenDepthDebugShader() {
            m_sdDebugShader ??= new Shader(ScreenDepthDebugVsh, ScreenDepthDebugPsh);
            return m_sdDebugShader;
        }

        static SamplerState EnsureScreenDepthSampler() {
            m_sdSampler ??= new SamplerState {
                AddressModeU = TextureAddressMode.Clamp,
                AddressModeV = TextureAddressMode.Clamp,
                FilterMode = TextureFilterMode.Point,
                MaxLod = 0f
            };
            return m_sdSampler;
        }

        static void CreateScreenDepthQuad() {
            TerrainVertex[] vertices = new TerrainVertex[4];
            BlockGeometryGenerator.SetupVertex(-1f, 0f, 0f, Color.White, 0f, 1f, ref vertices[0]);
            BlockGeometryGenerator.SetupVertex(0f, 0f, 0f, Color.White, 1f, 1f, ref vertices[1]);
            BlockGeometryGenerator.SetupVertex(0f, 1f, 0f, Color.White, 1f, 0f, ref vertices[2]);
            BlockGeometryGenerator.SetupVertex(-1f, 1f, 0f, Color.White, 0f, 0f, ref vertices[3]);
            short[] indices = [0, 1, 2, 0, 2, 3];
            m_sdQuad = new VertexBuffer(TerrainVertex.VertexDeclaration, 4);
            m_sdQuad.SetData(vertices, 0, 4);
            m_sdQuadIndices = new IndexBuffer(IndexFormat.SixteenBits, indices.Length);
            m_sdQuadIndices.SetData(indices, 0, indices.Length);
            m_sdQuadIndicesCount = indices.Length;
        }

        // ============================================================================================
        // 着色器：只写窗口深度（R/G = 16 bit，B = 8 bit 预览，A = 覆盖）
        // ============================================================================================

const string ScreenDepthVsh = @"#ifdef HLSL

float2 u_origin;
float4x4 u_viewProjectionMatrix;
float3 u_eye;
float u_depthScale;
float u_debugMode;

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float v_depth : TEXCOORD0,
	out float4 v_color : COLOR,
	out float4 sv_position: SV_POSITION
)
{
	float3 shifted = float3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	float4 clip = mul(float4(shifted, 1.0), u_viewProjectionMatrix);
	// **线性视距**（不是窗口深度）：窗口深度在大远平面下几乎饱和（见类注释里的实测）。
	v_depth = saturate(distance(u_eye, a_position) * u_depthScale);
	v_color = a_color;
	sv_position = clip;
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

uniform vec2 u_origin;
uniform mat4 u_viewProjectionMatrix;
uniform vec3 u_eye;
uniform float u_depthScale;
uniform float u_debugMode;

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying float v_depth;
varying vec4 v_color;

void main()
{
	vec3 shifted = vec3(a_position.x - u_origin.x, a_position.y, a_position.z - u_origin.y);
	vec4 clip = u_viewProjectionMatrix * vec4(shifted, 1.0);
	v_depth = clamp(distance(u_eye, a_position) * u_depthScale, 0.0, 1.0);
	v_color = a_color;
	gl_Position = clip;
	OPENGL_POSITION_FIX;
}

#endif
";

        const string ScreenDepthPsh = @"#ifdef HLSL

float u_depth16;
float u_debugMode;

void main(
	in float v_depth : TEXCOORD0,
	in float4 v_color : COLOR,
	out float4 svTarget : SV_TARGET
)
{
	if (u_debugMode > 1.5) { svTarget = float4(v_color.rgb, 1.0); return; }
	if (u_debugMode > 0.5) { svTarget = float4(1.0, 0.0, 0.0, 1.0); return; }
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
		svTarget = float4(d, 0.0, 0.0, 1.0);
	}
}

#endif
#ifdef GLSL

precision highp float;

uniform float u_depth16;
uniform float u_debugMode;

varying float v_depth;
varying vec4 v_color;

void main()
{
	if (u_debugMode > 1.5) { gl_FragColor = vec4(v_color.rgb, 1.0); return; }
	if (u_debugMode > 0.5) { gl_FragColor = vec4(1.0, 0.0, 0.0, 1.0); return; }
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
		gl_FragColor = vec4(d, 0.0, 0.0, 1.0);
	}
}

#endif
";

        const string ScreenDepthDebugVsh = @"#ifdef HLSL

void main(
	in float3 a_position: POSITION,
	in float4 a_color: COLOR,
	in float2 a_texcoord: TEXCOORD,
	out float2 v_texcoord : TEXCOORD0,
	out float4 sv_position: SV_POSITION
)
{
	v_texcoord = a_texcoord;
	sv_position = float4(a_position.xy, 0.0, 1.0);
}

#endif
#ifdef GLSL

// <Semantic Name='POSITION' Attribute='a_position' />
// <Semantic Name='COLOR' Attribute='a_color' />
// <Semantic Name='TEXCOORD' Attribute='a_texcoord' />

attribute vec3 a_position;
attribute vec4 a_color;
attribute vec2 a_texcoord;

varying vec2 v_texcoord;

void main()
{
	v_texcoord = a_texcoord;
	gl_Position = vec4(a_position.x, a_position.y, 0.0, 1.0);
}

#endif
";

        const string ScreenDepthDebugPsh = @"#ifdef HLSL

sampler2D u_texture;

void main(
	in float2 v_texcoord : TEXCOORD0,
	out float4 svTarget : SV_TARGET
)
{
	float4 c = tex2D(u_texture, v_texcoord);
	svTarget = float4(c.r, c.g, c.b, 1.0);
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

#endif
";
    }
}
