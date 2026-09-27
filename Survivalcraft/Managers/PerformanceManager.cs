using System.Text;
using Engine;
using Engine.Graphics;
using Engine.Media;

namespace Game {
    public static class PerformanceManager {
        public struct FrameData {
            public float CpuTime;

            public float TotalTime;
        }

        public static PrimitivesRenderer2D m_primitivesRenderer;

        public static RunningAverage m_averageFrameTime;

        public static RunningAverage m_averageCpuFrameTime;

        public static float? m_longTermAverageFrameTime;

        public static long m_totalMemoryUsed;

        public static long m_totalGpuMemoryUsed;

        public static long m_totalGraphicResourcesCount;

        public static StateMachine m_stateMachine;

        public static double m_totalGameTime;

        public static double m_totalFrameTime;

        public static double m_totalCpuFrameTime;

        public static int m_frameCount;

        public static string m_statsString;

        public static readonly List<string> m_extraStats = [];

        // ===== Skyline v0.1.1：性能 HUD 的 CPU/GPU 真实占用（用户反馈：原来只有单核口径）=====
        // CPU：主线程占帧比（原有）+ 进程占全机 CPU 比（本组）+ 系统 CPU 总占用；
        // GPU：NVAPI 利用率（SkylineNvidia.UtilizationPercent，-1=不可用）。
        static float m_processCpuPercent = -1f;
        static float m_systemCpuPercent = -1f;
        static double m_lastCpuSampleWall = -1.0;
        static System.TimeSpan m_lastProcessCpuTime;
        static long m_lastSysIdle;
        static long m_lastSysKernel;
        static long m_lastSysUser;

        public static float ProcessCpuPercent => m_processCpuPercent;
        public static float SystemCpuPercent => m_systemCpuPercent;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

        static void SampleCpuLoad() {
            try {
                double wall = Time.RealTime;
                System.TimeSpan procCpu = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                if (m_lastCpuSampleWall > 0.0 && wall - m_lastCpuSampleWall > 0.2) {
                    double dCpu = (procCpu - m_lastProcessCpuTime).TotalSeconds;
                    double dWall = wall - m_lastCpuSampleWall;
                    m_processCpuPercent = (float)(dCpu / dWall / System.Environment.ProcessorCount * 100.0);
                }
                m_lastCpuSampleWall = wall;
                m_lastProcessCpuTime = procCpu;
            }
            catch {
                m_processCpuPercent = -1f;
            }
            try {
                if (GetSystemTimes(out long idle, out long kernel, out long user)) {
                    if (m_lastSysKernel != 0 || m_lastSysUser != 0) {
                        long dIdle = idle - m_lastSysIdle;
                        long dTotal = (kernel - m_lastSysKernel) + (user - m_lastSysUser);
                        if (dTotal > 0) {
                            m_systemCpuPercent = (float)((dTotal - dIdle) * 100.0 / dTotal);
                        }
                    }
                    m_lastSysIdle = idle;
                    m_lastSysKernel = kernel;
                    m_lastSysUser = user;
                }
            }
            catch {
                m_systemCpuPercent = -1f;
            }
        }

        public static FrameData[] m_frameData;

        public static int m_frameDataIndex;

        public static FontBatch2D m_fontBatch;

        public static float? LongTermAverageFrameTime => m_longTermAverageFrameTime;

        public static float AverageFrameTime => m_averageFrameTime.Value;

        public static float AverageCpuFrameTime => m_averageCpuFrameTime.Value;

        public static long TotalMemoryUsed => m_totalMemoryUsed;

        public static long TotalGpuMemoryUsed => m_totalGpuMemoryUsed;

        public static long TotalGraphicResourcesCount => m_totalGraphicResourcesCount;

        static PerformanceManager() {
            m_primitivesRenderer = new PrimitivesRenderer2D();
            m_fontBatch = m_primitivesRenderer.FontBatch(BitmapFont.DebugFont, 0, null, null, null, SamplerState.PointClamp);
            m_averageFrameTime = new RunningAverage(1f);
            m_averageCpuFrameTime = new RunningAverage(1f);
            m_stateMachine = new StateMachine();
            m_statsString = string.Empty;
            m_stateMachine.AddState(
                "PreMeasure",
                delegate { m_totalGameTime = 0.0; },
                delegate {
                    if (GameManager.Project != null) {
                        m_totalGameTime += Time.FrameDuration;
                        if (m_totalGameTime > 60.0) {
                            m_stateMachine.TransitionTo("Measuring");
                        }
                    }
                },
                null
            );
            m_stateMachine.AddState(
                "Measuring",
                delegate {
                    m_totalFrameTime = 0.0;
                    m_totalCpuFrameTime = 0.0;
                    m_frameCount = 0;
                },
                delegate {
                    if (GameManager.Project != null) {
                        if (ScreensManager.CurrentScreen != null
                            && ScreensManager.CurrentScreen.GetType() == typeof(GameScreen)) {
                            float lastFrameTime = Program.LastFrameTime;
                            float lastCpuFrameTime = Program.LastCpuFrameTime;
                            if (lastFrameTime > 0f
                                && lastFrameTime < 1f
                                && lastCpuFrameTime > 0f
                                && lastCpuFrameTime < 1f) {
                                m_totalFrameTime += lastFrameTime;
                                m_totalCpuFrameTime += lastCpuFrameTime;
                                m_frameCount++;
                            }
                            if (m_totalFrameTime > 180.0) {
                                m_stateMachine.TransitionTo("PostMeasure");
                            }
                        }
                    }
                    else {
                        m_stateMachine.TransitionTo("PreMeasure");
                    }
                },
                null
            );
            m_stateMachine.AddState(
                "PostMeasure",
                delegate {
                    if (m_frameCount > 0) {
                        m_longTermAverageFrameTime = (float)(m_totalFrameTime / m_frameCount);
                        float num = (int)Math.Round(
                            Math.Round(m_totalFrameTime / m_frameCount / 0.004999999888241291) * 0.004999999888241291 * 1000.0
                        );
                        float num2 = (int)Math.Round(
                            Math.Round(m_totalCpuFrameTime / m_frameCount / 0.004999999888241291) * 0.004999999888241291 * 1000.0
                        );
                        Log.Information($"PerformanceManager Measurement: frames={m_frameCount}, avgFrameTime={num}ms, avgFrameCpuTime={num2}ms");
                    }
                },
                delegate {
                    if (GameManager.Project == null) {
                        m_stateMachine.TransitionTo("PreMeasure");
                    }
                },
                null
            );
            m_stateMachine.TransitionTo("PreMeasure");
        }

        public static void Update() {
            m_averageFrameTime.AddSample(Program.LastFrameTime);
            m_averageCpuFrameTime.AddSample(Program.LastCpuFrameTime);
            if (Time.PeriodicEvent(1.0, 0.0)) {
                m_totalMemoryUsed = GC.GetTotalMemory(false);
                m_totalGpuMemoryUsed = Display.GetGpuMemoryUsage();
                m_totalGraphicResourcesCount = GraphicsResource.m_resources.Count;
                SampleCpuLoad();
            }
            m_stateMachine.Update();
        }

        public static void Draw() {
            Vector2 scale = new(MathF.Round(Math.Clamp(ScreensManager.RootWidget.GlobalScale, 1.0f, 2.0f)));
            Viewport viewport = Display.Viewport;
            if (SettingsManager.DisplayFpsCounter) {
                if (Time.PeriodicEvent(1.0, 0.0)
                    && ScreensManager.CurrentScreen != null) {
                    string sysPart = m_systemCpuPercent >= 0f ? $"{m_systemCpuPercent:0}%" : "n/a";
                    string procPart = m_processCpuPercent >= 0f
                        ? $"{m_processCpuPercent:0.0}%" : "n/a";
                    string gpuPart = SkylineNvidia.UtilizationPercent >= 0
                        ? $"GPU {SkylineNvidia.UtilizationPercent}%"
                        : "GPU n/a";
                    m_statsString =
                        $"CPUMEM {TotalMemoryUsed / 1024f / 1024f:0}MB, GPUMEM {TotalGpuMemoryUsed / 1024f / 1024f:0}MB({TotalGraphicResourcesCount}), "
                        + $"CPU {AverageCpuFrameTime / AverageFrameTime * 100f:0}% (main thread), "
                        + $"SYS {sysPart}, PROC {procPart}/{System.Environment.ProcessorCount} cores, {gpuPart}, "
                        + $"{1f / AverageFrameTime:0.0} FPS";
#if DEBUG
                    string wname = ScreensManager.RootWidget.Input.MousePosition.HasValue
                        ? ScreensManager.RootWidget.HitTestGlobal(ScreensManager.RootWidget.Input.MousePosition.Value)?.GetType().Name
                        : string.Empty;
                    m_statsString += "\nScreen:[" + ScreensManager.CurrentScreen.GetType().Name + "]  [" + wname + "]";
#endif
                }
                StringBuilder stringBuilder = new();
                stringBuilder.AppendLine(m_statsString);
                if (m_extraStats.Count > 0) {
                    stringBuilder.AppendJoin('\n', m_extraStats);
                }
                m_fontBatch.QueueText(
                    stringBuilder.ToString(),
                    Vector2.Transform(Vector2.Zero, ScreensManager.RootWidget.GlobalTransform) + Window.DisplayCutoutInsets.XY,
                    0f,
                    Color.White,
                    TextAnchor.Default,
                    scale,
                    Vector2.Zero
                );
                m_extraStats.Clear();
            }
            if (SettingsManager.DisplayFpsRibbon) {
                float num = viewport.Width / scale.X > 480f ? scale.X * 2f : scale.X;
                float num2 = viewport.Height / -0.1f;
                float num3 = viewport.Height - 1;
                float s = 0.5f;
                int num4 = MathUtils.Max((int)(viewport.Width / num), 1);
                if (m_frameData == null
                    || m_frameData.Length != num4) {
                    m_frameData = new FrameData[num4];
                    m_frameDataIndex = 0;
                }
                m_frameData[m_frameDataIndex] = new FrameData { CpuTime = Program.LastCpuFrameTime, TotalTime = Program.LastFrameTime };
                m_frameDataIndex = (m_frameDataIndex + 1) % m_frameData.Length;
                FlatBatch2D flatBatch2D = m_primitivesRenderer.FlatBatch();
                Color color = Color.Orange * s;
                Color color2 = Color.Red * s;
                for (int num5 = m_frameData.Length - 1; num5 >= 0; num5--) {
                    int num6 = (num5 - m_frameData.Length + 1 + m_frameDataIndex + m_frameData.Length) % m_frameData.Length;
                    FrameData frameData = m_frameData[num6];
                    float x = num5 * num;
                    float x2 = (num5 + 1) * num;
                    flatBatch2D.QueueQuad(new Vector2(x, num3), new Vector2(x2, num3 + frameData.CpuTime * num2), 0f, color);
                    flatBatch2D.QueueQuad(
                        new Vector2(x, num3 + frameData.CpuTime * num2),
                        new Vector2(x2, num3 + frameData.TotalTime * num2),
                        0f,
                        color2
                    );
                }
                float num7 = num3 + num2 / Window.ScreenRefreshRate;
                flatBatch2D.QueueLine(
                    new Vector2(0f, num7),
                    new Vector2(viewport.Width, num7),
                    0f,
                    Color.Green
                );
            }
            else {
                m_frameData = null;
            }
            // [v0.1.60] 手动生成 LOD 的 HUD 进度条（跑的时候 + 跑完 3 s；与 DH 类似）
            SkylineLodManualBuild.DrawHud(m_primitivesRenderer, viewport, scale);
            m_primitivesRenderer.Flush();
        }

        /// <summary>
        ///     在性能信息下方添加一行信息，需每帧添加，不支持中文
        /// </summary>
        public static void AddExtraStat(string stat) {
            m_extraStats.Add(stat);
        }
    }
}
