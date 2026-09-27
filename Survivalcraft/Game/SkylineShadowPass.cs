using System;
using System.IO;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;
using Engine.Media;
using GameEntitySystem;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.18：**阴影阶段的第一步 —— 太阳视角 pass + 回读验证**
    /// （里程碑 5 路线图：只读网格访问器 ✅v0.1.11 → **阴影** → G-buffer/法线/材质 id）。
    ///
    /// 为什么先做"能回读的 pass"：真阴影贴图需要在管线里加一个"从太阳方向渲染深度"的阶段，
    /// 而 SC 自带 `SubsystemShadows` 只是对象贴片阴影（没有阴影贴图），Dawnlight 也没给外部接口。
    /// 所以先自建这一层，并**用回读把结果变成可看、可量化的证据**（PNG + 统计），再谈把它接进着色。
    ///
    /// 本步只做：把 LOD 网格用**太阳正交相机**画进自建 RenderTarget，然后回读像素（图 + 统计）。
    /// 默认关闭（`skyline.ShadowPassEnabled`），不参与任何正常渲染路径。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>太阳视角 pass 总开关（默认关；开启只影响 `CaptureSunView/SaveSunView` 这两个显式调用）。</summary>
        public static bool ShadowPassEnabled { get; set; }

        /// <summary>渲染目标边长（像素）。</summary>
        public static int ShadowPassSize { get; set; } = 1024;

        /// <summary>正交半径（米）：太阳视角覆盖以玩家为中心的这个范围。</summary>
        public static float ShadowPassRadius { get; set; } = 256f;

        /// <summary>清屏色（诊断用：设成醒目色可单独验证"渲染目标 + 回读"这条通路）。</summary>
        public static Vector3 ShadowPassClearColor { get; set; } = new(0f, 0f, 0f);

        /// <summary>诊断开关：true = 用**游戏相机自己的矩阵**渲染（应与正常画面一致），
        /// 用来把"矩阵/相机算错"和"绘制本身有问题"分开。</summary>
        public static bool ShadowPassUseCameraView { get; set; }

        static RenderTarget2D m_shadowRt;
        static int m_shadowCaptures;
        static string m_shadowLastStats = "(never captured)";

        /// <summary>诊断：最近一次太阳视角 pass 的统计。</summary>
        public static string ShadowPassStats => m_shadowLastStats;

        public static int ShadowPassCaptures => m_shadowCaptures;

        /// <summary>
        /// 渲染一次"太阳视角"并回读统计。返回 JSON：
        ///   { ok, size, radius, visiblePixels, visibleRatio, meanRGB, nonEmptyRows, ms }
        /// 说明：用游戏自己的**地形不透明 shader** 画 LOD 网格（粗+细），所以图像就是"从太阳看地形"的样子；
        /// 真阴影贴图要的是同一批几何的深度，几何/矩阵通了，深度只是换个输出目标。
        /// </summary>
        public static string CaptureSunView() {
            JsonObject result = new();
            if (!ShadowPassEnabled) {
                result["ok"] = false;
                result["err"] = "ShadowPassEnabled = false（默认关；先打开再做实验）";
                return result.ToJsonString();
            }
            SubsystemTerrain subsystemTerrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true);
            SubsystemSky sky = GameManager.Project?.FindSubsystem<SubsystemSky>(true);
            Camera camera = GetCamera();
            if (subsystemTerrain == null || sky == null || camera == null || TerrainRenderer.m_opaqueShader == null) {
                result["ok"] = false;
                result["err"] = "no terrain/sky/camera/shader";
                return result.ToJsonString();
            }
            int size = Math.Clamp(ShadowPassSize, 64, Math.Min(Display.MaxTextureSize, 4096));
            float radius = Math.Clamp(ShadowPassRadius, 32f, 2048f);
            RenderTarget2D previousTarget = Display.RenderTarget;
            Viewport previousViewport = Display.Viewport;
            Rectangle previousScissor = Display.ScissorRectangle;
            double start = Time.RealTime;
            try {
                if (m_shadowRt == null || m_shadowRt.Width != size) {
                    Utilities.Dispose(ref m_shadowRt);
                    m_shadowRt = new RenderTarget2D(size, size, 1, ColorFormat.Rgba8888, DepthFormat.Depth24Stencil8);
                }
                Display.RenderTarget = m_shadowRt;
                Display.Viewport = new Viewport(0, 0, size, size);
                Display.ScissorRectangle = new Rectangle(0, 0, size, size);
                Display.Clear(new Vector4(ShadowPassClearColor.X, ShadowPassClearColor.Y, ShadowPassClearColor.Z, 1f), 1f, 0);

                Vector3 center = camera.ViewPosition;
                Vector3 sun = Vector3.Normalize(LightingManager.DirectionToLight1);
                float distance = MathF.Max(radius * 4f, 512f);
                Vector3 eye = center + sun * distance;
                Matrix view = Matrix.CreateLookAt(eye, center, Vector3.UnitY);
                Matrix projection = Matrix.CreateOrthographic(radius * 2f, radius * 2f, 1f, distance * 4f);
                Shader shader = TerrainRenderer.m_opaqueShader;
                // 地形 shader 用**浮动原点**：顶点先减去 u_origin(XZ) 再乘矩阵。所以矩阵要先把原点平移补回来
                // （与 SkylineLod.Draw 的 CreateTranslation(v - viewPosition) * view * projection 同一写法）。
                Vector3 origin3 = new(MathF.Floor(center.X), 0f, MathF.Floor(center.Z));
                if (ShadowPassUseCameraView) {
                    shader.GetParameter("u_origin", true).SetValue(new Vector2(origin3.X, origin3.Z));
                    shader.GetParameter("u_viewProjectionMatrix", true)
                        .SetValue(Matrix.CreateTranslation(origin3 - center) * camera.ViewMatrix.OrientationMatrix
                                  * camera.ProjectionMatrix);
                }
                else {
                    shader.GetParameter("u_origin", true).SetValue(new Vector2(origin3.X, origin3.Z));
                    // 注意：`Matrix.CreateLookAt` **自带平移**（把世界坐标变到视线空间），所以这里只需把
                    // shader 减掉的 u_origin 补回来，**不能**再减一次 eye——否则平移被扣两次，画面全空
                    // （v0.1.18 实测：黑屏 → 修正后 13% 像素可见）。
                    shader.GetParameter("u_viewProjectionMatrix", true)
                        .SetValue(Matrix.CreateTranslation(origin3) * view * projection);
                }
                shader.GetParameter("u_viewPosition", true).SetValue(eye);
                shader.GetParameter("u_samplerState", true).SetValue(new SamplerState {
                    AddressModeU = TextureAddressMode.Clamp,
                    AddressModeV = TextureAddressMode.Clamp,
                    FilterMode = TextureFilterMode.Point,
                    MaxLod = 0f
                });
                shader.GetParameter("u_fogYMultiplier", true).SetValue(sky.VisibilityRangeYMultiplier);
                shader.GetParameter("u_fogColor", true).SetValue(new Vector3(sky.ViewFogColor));
                shader.GetParameter("u_fogBottomTopDensity", true)
                    .SetValue(new Vector3(sky.ViewFogBottom, sky.ViewFogTop, 0f));      // 测试图不要雾
                shader.GetParameter("u_hazeStartDensity", true).SetValue(new Vector2(1e9f, 0f));
                shader.GetParameter("u_texture", true).SetValue(
                    subsystemTerrain.SubsystemAnimatedTextures.AnimatedBlocksTexture);
                Display.BlendState = BlendState.Opaque;
                Display.DepthStencilState = DepthStencilState.Default;
                Display.RasterizerState = RasterizerState.CullCounterClockwiseScissor;
                SkylineLod.DrawWithShader(shader);

                Image image = m_shadowRt.GetData(new Rectangle(0, 0, size, size));
                (int visible, double meanR, double meanG, double meanB, int nonEmptyRows) = ImageStats(image);
                m_shadowCaptures++;
                m_shadowLastStats =
                    $"sunView size={size} radius={radius:0} visible={visible} "
                    + $"({100.0 * visible / (size * size):0.##}%) meanRGB=({meanR:0},{meanG:0},{meanB:0}) "
                    + $"rows={nonEmptyRows} captures={m_shadowCaptures} ms={(Time.RealTime - start) * 1000.0:0.0}";
                result["ok"] = true;
                result["size"] = size;
                result["radius"] = radius;
                result["visiblePixels"] = visible;
                result["visibleRatio"] = Math.Round((double)visible / (size * size), 5);
                result["meanR"] = Math.Round(meanR, 1);
                result["meanG"] = Math.Round(meanG, 1);
                result["meanB"] = Math.Round(meanB, 1);
                result["nonEmptyRows"] = nonEmptyRows;
                result["captures"] = m_shadowCaptures;
                result["ms"] = Math.Round((Time.RealTime - start) * 1000.0, 2);
                result["note"] = "从太阳方向看的 LOD 网格（地形不透明 shader，雾关闭）；阴影贴图要的是同一批几何的深度";
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
                m_shadowLastStats = "sunView failed: " + e.Message;
            }
            finally {
                Display.RenderTarget = previousTarget;
                Display.Viewport = previousViewport;
                Display.ScissorRectangle = previousScissor;
            }
            return result.ToJsonString();
        }

        /// <summary>
        /// 渲染一次太阳视角并把 PNG 存到游戏目录的 `ScreenCapture/` 下（与桥截屏同目录，便于查看）。
        /// 返回 JSON：{ ok, file, bytes, stats }。
        /// </summary>
        public static string SaveSunView() {
            JsonObject result = new();
            if (!ShadowPassEnabled) {
                result["ok"] = false;
                result["err"] = "ShadowPassEnabled = false";
                return result.ToJsonString();
            }
            JsonObject capture = JsonNode.Parse(CaptureSunView()) as JsonObject;
            if (capture == null || capture["ok"]?.GetValue<bool>() != true) {
                return capture?.ToJsonString() ?? "{\"ok\":false,\"err\":\"capture failed\"}";
            }
            try {
                string dir = Path.Combine(Storage.GetSystemPath("app:/"), "ScreenCapture");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, $"skyline-sunview-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                using Stream stream = Storage.OpenFile(file, OpenFileMode.Create);
                Image image = m_shadowRt.GetData(new Rectangle(0, 0, m_shadowRt.Width, m_shadowRt.Height));
                Image.Save(image, stream, ImageFileFormat.Png, false);
                long bytes = new FileInfo(file).Length;
                result["ok"] = true;
                result["file"] = file;
                result["bytes"] = bytes;
                result["stats"] = m_shadowLastStats;
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        static (int visible, double meanR, double meanG, double meanB, int nonEmptyRows) ImageStats(Image image) {
            int size = image.Width;
            int visible = 0;
            long sumR = 0, sumG = 0, sumB = 0;
            int nonEmptyRows = 0;
            for (int y = 0; y < size; y++) {
                bool rowHasPixel = false;
                for (int x = 0; x < size; x++) {
                    Color c = image.GetPixel(x, y);
                    if (c.R > 8 || c.G > 8 || c.B > 8) {
                        visible++;
                        sumR += c.R;
                        sumG += c.G;
                        sumB += c.B;
                        rowHasPixel = true;
                    }
                }
                if (rowHasPixel) {
                    nonEmptyRows++;
                }
            }
            double d = Math.Max(visible, 1);
            return (visible, sumR / d, sumG / d, sumB / d, nonEmptyRows);
        }

        static Camera GetCamera() {
            SubsystemGameWidgets widgets = GameManager.Project?.FindSubsystem<SubsystemGameWidgets>(true);
            if (widgets == null) {
                return null;
            }
            foreach (GameWidget widget in widgets.GameWidgets) {
                if (widget?.ActiveCamera != null) {
                    return widget.ActiveCamera;
                }
            }
            return null;
        }
    }
}
