using Engine;
using Engine.Graphics;
using System.Text.Json.Nodes;

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
    /// `GpuShadowSampleStrength`（0.45）、`GpuShadowSampleBias`（0.0002）、`GpuShadowFlipY`（后端 UV 方向；
    /// 先用 A/B 定，默认 false）。诊断：`skyline.GpuShadowSampleDescribe()`。
    /// [v0.1.35] 深度图改为 **16 bit 双通道**（R 高字节 / G 低字节）后，采样侧按**捕获时**的编码解码；
    /// 量化步长 4096 m 范围下 16.1 m → 0.0625 m，才撑得住"米级矮墙的投影"。
    /// [v0.1.38] **近/远两级级联**：片元先投影进近图（默认半径 128 m → 0.25 m/texel），
    /// 命中近盒就用近图（接触阴影更锐），否则回退远图（512 m → 1 m/texel）。
    /// </summary>
    public static partial class SkylineRuntime {
        public static bool GpuShadowSampleEnabled { get; set; }

        public static float GpuShadowSampleStrength { get; set; } = 0.45f;

        /// <summary>深度比较偏置（归一化深度单位）。实测：0.004（≈16 m）会把 16 m 高墙的投影整个抹掉，
        /// 取 0.0002（≈0.8 m）即可；更精细的自阴影要等 16 bit 深度（notes/105）。</summary>
        public static float GpuShadowSampleBias { get; set; } = 0.0002f;

        public static bool GpuShadowFlipY { get; set; }

        /// <summary>[v0.1.64] **软阴影（PCF）开关，默认开**。用户口径（里程碑 3.2）：
        /// "为了降低渲染开销，物体阴影应当为软阴影，而非 Dawnlight 目前的硬阴影"。
        /// 关掉 = 单次采样（v0.1.63 及以前的硬阴影），比较式与旧版**逐位一致**（用同一个已取到的 mapDepth）。
        /// </summary>
        public static bool GpuShadowSoftEnabled { get; set; } = true;

        /// <summary>[v0.1.64] PCF 核半径（texel 数，默认 1.5）。
        /// 偏移在 **UV 空间**累计（1 texel = 1/尺寸），所以这里按 texel 计；
        /// 换算成世界尺度：远图 2×512/1024 = 1.0 m/texel → 半影半径 ≈1.5 m；
        /// 近图 2×128/1024 = 0.25 m/texel → ≈0.375 m。**近场接触阴影天然更锐**（这正是级联想要的效果）。
        /// ⚠️ 第一版把"米"当 UV 传进去（1.0 UV = 整张贴图宽）→ 8 个抽样全落到边界外 → 阴影整片消失；
        /// 实测的软/硬 A/B 立刻暴露（见 notes/143）。
        ///
        /// **[v0.1.103] 默认 1.5 → 1.95**：默认核换成了 `ring16`（见 `GpuShadowKernel`），
        /// 而两个核在边缘法向上的**平均投影宽度**不同（八边形 2.55t vs 双环 1.95t，t = 本值 × texel），
        /// 所以半径要乘 2.55/1.95 ≈ 1.31 才能**保持平均半影宽度不变** —— 换核只改形状与量化，
        /// 不改"软不软"（milestone 3.2 的要求是软阴影）。换算：远图 ≈1.95 m、近图 ≈0.49 m。
        /// </summary>
        public static float GpuShadowSoftRadius { get; set; } = 1.95f;

        /// <summary>[v0.1.64] **坡度 bias 系数**（默认 1.0 = 按最坏情况补偿）。
        /// 为什么必须有这一项（实测踩到）：地面上相邻 texel 沿太阳方向的深度变化是
        /// `texel米数 / sinθ / depthMax`（θ = 太阳仰角；低太阳角下一个 texel 的**地面足迹**被拉长），
        /// 而 PCF 的抽样半径是 1.5 texel —— 只要这个变化超过固定 bias，"朝太阳那一侧"的抽样就会一律判成
        /// 遮挡，**大片受光地面被压暗**（实测 r=1.5 时 48.5% 像素变暗、亮度 −16.1）。
        /// </summary>
        public static float GpuShadowSoftSlopeBias { get; set; } = 1f;

        /// <summary>[v0.1.64] **relief（起伏）bias 的 texel 数**（默认 0）。
        /// 这一项补偿"地形起伏落在同一个 texel 内"：1 格雪阶会让相邻 texel 的深度差达到 1 m 量级。
        /// **实测结论（不要想当然开大）**：把坡度项改成带 `1/sinθ` 之后，斜坡项本身已经覆盖了起伏误差
        /// —— 扫描 relief = 0/1/2/3/4 时 **0 就已经没有条纹**（见 notes/143 的扫描表），更大的值只是让
        /// 亮度继续上漂（+1.1 → +3.1），也就是**把真阴影也擦掉**（peter-panning）。
        /// 所以默认 0；它同时保证"半径=0 时软路径与硬路径逐位相同"这条可证伪不变量继续成立。
        /// </summary>
        public static float GpuShadowSoftReliefTexels { get; set; }

        /// <summary>[v0.1.64] 太阳仰角下限（sinθ）。低于它按它算，避免日出日落时 bias 发散。</summary>
        public static float GpuShadowSoftSunYFloor { get; set; } = 0.15f;

        /// <summary>[v0.1.102 引入 / v0.1.103 改默认] **PCF 采样核选择**（里程碑 4：按 Dawnlight 的实现换核）。
        /// `0` = 八边形 8 抽样（v0.1.64 的原核）；`1` = Dawnlight 的 Poisson 盘 12 抽样；
        /// `2` = 双同心环 8+8（16 抽样）—— **v0.1.103 起默认**。
        ///
        /// 两个核的差别不是"随便换一组偏移"，有两条**可测量**的性质差异（见 `GpuShadowKernelSelfCheck()`）：
        ///   1. **支撑形状**：原核的 8 个偏移是 `(±1,0)/(0,±1)/(±1,±1)` ——
        ///      前 4 个半径 1、后 4 个半径 √2，全部落在 `max(|x|,|y|)=1` 的**正方形边界**上。
        ///      于是"半影沿边缘法向的宽度"随**边缘朝向**在 `2.00t ~ 2.83t` 之间变化（**×1.41**）——
        ///      同一个物体的影子，斜着看比正着看糊 41%；
        ///   2. **方向量化误差**：8 个方向均匀分布 ⇒ 半平面遮挡的真实覆盖率与核估计之间，
        ///      最坏偏差是 **1/8 = 12.5%** 半影；Dawnlight 的 12 抽样把这一项降到 **1/12 = 8.3%**。
        ///
        /// Dawnlight 一侧的出处：`lib/CalculateShadow.glsl` 的 `getWarpShadowPCF()`
        /// （`const vec2 poissonDisk[16]` + `int samples = 12; // 可选 8/12/16`，
        /// 半径 `1.0 * texelSize.x`）。我们只**换核**，不换它那套 warp/paraboloid 投影 ——
        /// 本分支的阴影是正交盒投影，没有 warp 空间，硬搬会把整个采样位置算错。
        ///
        /// **[v0.1.103] 默认由 `octagon8` 改为 `ring16`**，依据是同一套判据下的三条实测：
        ///   1. `GpuShadowKernelSelfCheck()`：量化误差 **12.5% → 6.25%**、支撑各向异性 **1.414 → 1.082**
        ///      （八边形的支撑是正方形，斜向半影宽 41%；双环是圆盘，太阳本来也是圆盘）；
        ///   2. **等宽**：半径同时由 1.5 → 1.95（见 `GpuShadowSoftRadius`），平均半影宽度不变，
        ///      A/B 出来的差别只剩形状/量化（1600 px 截图 4,323 px，噪声底 0 px）；
        ///   3. **代价**：配对交替测（0/2 各两轮、每轮 17~18 s）帧率差 **+1.18%**（在跑步动噪声内）、
        ///      显存/内存不变 ⇒ **没有可测代价**。
        /// 仍然如实记：**"哪个更自然"的放大目视对照还没做**（见 `notes/191 §5`）。
        /// </summary>
        public static int GpuShadowKernel { get; set; } = GpuShadowKernelRing16;

        /// <summary>[v0.1.102] 核代号：八边形 8 抽样（v0.1.64 原核）。</summary>
        public const int GpuShadowKernelOctagon = 0;
        /// <summary>[v0.1.102] 核代号：Dawnlight Poisson 盘 12 抽样。</summary>
        public const int GpuShadowKernelPoisson12 = 1;
        /// <summary>[v0.1.102] 核代号：双同心环 8+8（16 抽样，本分支按测量设计的核）。</summary>
        public const int GpuShadowKernelRing16 = 2;
        /// <summary>[v0.1.102] Poisson 核实际使用的抽样数（Dawnlight 的 `samples` 默认值）。</summary>
        public const int GpuShadowPoissonSamples = 12;
        /// <summary>[v0.1.102] 双环核实际使用的抽样数。</summary>
        public const int GpuShadowRingSamples = 16;

        /// <summary>[v0.1.103] **各核的真实最大采样半径**（单位 = `GpuShadowSoftRadius` × texel）。
        ///
        /// 为什么需要它：`u_shadowSoftSlopeBias` 那一项按"**抽样半径**覆盖的归一化深度差"补偿，
        /// 原式写死 `× 1.4142136`（= 八边形核的角点半径 √2）。换核之后这个系数就不再成立：
        ///   * `octagon8` 的 8 个偏移半径是 `1` 与 `√2` ⇒ **1.41421**；
        ///   * `poisson12` 的盘最大半径是 `1.23423`（`(0.97484398, 0.75648379)`）；
        ///   * `ring16` 的两条环半径是 `0.6` 与 `1.0` ⇒ **1.0**。
        /// 实测（v0.1.103 第一版）：只把半径从 1.5 抬到 1.95、这一项却仍按 1.4142 计 ⇒
        /// bias 额外多 30%（≈1.9 m 的归一化深度）⇒ "换核"的像素差被**放大到 77,619 px**。
        /// 按真实最大半径缩放后，bias 与旧默认基本持平，剩下的才是真正的形状/量化差别。
        /// </summary>
        public static float GpuShadowKernelSlopeScale(int kernel) => kernel switch {
            GpuShadowKernelPoisson12 => 1.23423f,
            GpuShadowKernelRing16 => 1f,
            _ => 1.41421f
        };

        /// <summary>[v0.1.34] 调试：0=正常阴影；1=把"采样到的阴影图深度"直接画到颜色（验证 UV/绑定是否正确）。</summary>
        public static int GpuShadowDebugMode { get; set; }

        // [v0.1.64] 捕获时的实际几何量：PCF 的核半径要按**真实 texel 尺寸**换算，不能用配置项想当然。
        static float m_gpuShadowRadiusAtCapture = 512f;
        static float m_gpuShadowNearRadiusAtCapture = 128f;
        static int m_gpuShadowSizeAtCapture = 1024;

        static string m_gpuShadowSampleError = "";
        static Shader m_gpuShadowOpaqueShader;
        static SamplerState m_gpuShadowSampler;
        // [v0.1.69] 只用体积雾时的 1×1 占位深度图（纹素参数必须绑真纹理；u_shadowEnable=0 时不采样）
        static RenderTarget2D m_gpuShadowDummyRt;
        static long m_gpuShadowSampleResolved;
        static long m_gpuShadowSampleFallbacks;
        static string m_gpuShadowSampleLastReason = "";

        /// <summary>[v0.1.64] 软阴影参数的**机器可读**快照（发版回归门禁的"默认值漂移门"用它）。
        /// 为什么单列一个方法：`...Describe()` 是给人读的字符串，门禁不该去解析它。</summary>
        public static string GpuShadowSoftInfo() {
            JsonObject o = new() {
                ["sampleEnabled"] = GpuShadowSampleEnabled,
                ["softEnabled"] = GpuShadowSoftEnabled,
                ["softRadius"] = (double)GpuShadowSoftRadius,
                ["reliefTexels"] = (double)GpuShadowSoftReliefTexels,
                ["slopeBias"] = (double)GpuShadowSoftSlopeBias,
                ["sunYFloor"] = (double)GpuShadowSoftSunYFloor,
                ["bias"] = (double)GpuShadowSampleBias,
                ["strength"] = (double)GpuShadowSampleStrength,
                ["sunRecaptureDeg"] = (double)GpuShadowSunRecaptureDegrees,
                ["kernel"] = GpuShadowKernel,
                ["kernelSamples"] = GpuShadowKernel switch {
                    GpuShadowKernelPoisson12 => GpuShadowPoissonSamples,
                    GpuShadowKernelRing16 => GpuShadowRingSamples,
                    _ => 8
                },
                ["kernelSlopeScale"] = System.Math.Round((double)GpuShadowKernelSlopeScale(GpuShadowKernel), 5)
            };
            return o.ToJsonString();
        }

        // [v0.1.102] 核表（自检用）—— **必须与 shader 里的字面量一致**，
        // `GpuShadowKernelSelfCheck()` 末尾有一条"锚点"检查防止两边漂移。
        static readonly float[] s_kernelPoissonXY = [
            -0.94201624f, -0.39906216f,
             0.94558609f, -0.76890725f,
            -0.094184101f, -0.92938870f,
             0.34495938f,  0.29387760f,
            -0.91588581f,  0.45771432f,
            -0.81544232f, -0.87912464f,
            -0.38277543f,  0.27676845f,
             0.97484398f,  0.75648379f,
             0.44323325f, -0.97511554f,
             0.53742981f, -0.47373420f,
            -0.26496911f, -0.41893023f,
             0.79197514f,  0.19090188f,
            -0.24188840f,  0.99706507f,
            -0.81409955f,  0.91437590f,
             0.19984126f,  0.78641367f,
             0.14383161f, -0.14100790f
        ];

        static readonly float[] s_kernelRingXY = [
             0.60000002f,  0.00000000f,
             0.42426407f,  0.42426407f,
             0.00000000f,  0.60000002f,
            -0.42426407f,  0.42426407f,
            -0.60000002f,  0.00000000f,
            -0.42426407f, -0.42426407f,
             0.00000000f, -0.60000002f,
             0.42426407f, -0.42426407f,
             0.92387956f,  0.38268343f,
             0.38268343f,  0.92387956f,
            -0.38268343f,  0.92387956f,
            -0.92387956f,  0.38268343f,
            -0.92387956f, -0.38268343f,
            -0.38268343f, -0.92387956f,
             0.38268343f, -0.92387956f,
             0.92387956f, -0.38268343f
        ];

        /// <summary>[v0.1.102] 取某个核的第 i 个偏移（**与 shader 里的表达式逐字对应**）。</summary>
        static void KernelOffset(int kernel, int i, float ca, float sa, out float x, out float y) {
            if (kernel == GpuShadowKernelPoisson12 || kernel == GpuShadowKernelRing16) {
                float[] table = kernel == GpuShadowKernelPoisson12 ? s_kernelPoissonXY : s_kernelRingXY;
                float px = table[i * 2], py = table[i * 2 + 1];
                x = px * ca - py * sa;
                y = px * sa + py * ca;
                return;
            }
            switch (i) {
                case 0: x = -ca + sa; y = -sa - ca; break;
                case 1: x = sa; y = -ca; break;
                case 2: x = ca + sa; y = sa - ca; break;
                case 3: x = -ca; y = -sa; break;
                case 4: x = ca; y = sa; break;
                case 5: x = -ca - sa; y = -sa + ca; break;
                case 6: x = -sa; y = ca; break;
                default: x = ca - sa; y = sa + ca; break;
            }
        }

        static int KernelSampleCount(int kernel) =>
            kernel == GpuShadowKernelPoisson12 ? GpuShadowPoissonSamples
            : kernel == GpuShadowKernelRing16 ? GpuShadowRingSamples
            : 8;

        static string KernelName(int kernel) => kernel switch {
            GpuShadowKernelPoisson12 => "poisson12",
            GpuShadowKernelRing16 => "ring16",
            _ => "octagon8"
        };

        /// <summary>
        /// [v0.1.102] **采样核自检**（`skyline.GpuShadowKernelSelfCheck`）：把"哪个核更软/更干净"
        /// 从口味题变成算术题 —— 用**半平面遮挡**（直边，建筑场景里最常见的那种）做解析对表。
        ///
        /// 口径（三条都能被数字证伪）：
        ///   1. **最坏方向量化误差** `maxErr`：边缘法向在 0..360° 扫一圈，核估计的"受光比例"与解析值
        ///      0.5 的最大偏差。它由方向数决定：8 抽样 → 12.5%，12 → 8.3%，16 → 6.25%（理论值就是 1/n）；
        ///   2. **支撑各向异性** `extRatio`：核在边缘法向上的投影宽度 max−min，随方向的最大/最小之比。
        ///      = 1 是完美圆盘；>1 说明"同一个影子斜着看更糊"；
        ///   3. **无偏**：扫一圈的均值必须贴近 0.5（偏了说明核本身有系统偏差，不是采样噪声）。
        ///
        /// 这条自检**不碰 GPU**、不吃帧率，且对三个核用同一套口径，所以可以直接对比。
        /// </summary>
        public static string GpuShadowKernelSelfCheck(int rotations = 256) {
            JsonObject result = new();
            try {
                rotations = System.Math.Clamp(rotations, 8, 4096);
                result["rotations"] = rotations;
                result["analyticLit"] = 0.5;
                JsonObject kernels = new();
                for (int kernel = 0; kernel <= 2; kernel++) {
                    int n = KernelSampleCount(kernel);
                    double sum = 0, sumSq = 0, sumExt = 0;
                    double minExt = double.MaxValue, maxExt = 0, maxErr = 0;
                    float maxRad = 0f;
                    // 估计值只可能是 k/n（n ≤ 16）⇒ 用桶统计"出现过几种离散层级"
                    bool[] seen = new bool[17];
                    for (int k = 0; k < rotations; k++) {
                        float ang = (float)(k * (2.0 * System.Math.PI) / rotations);
                        float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
                        int lit = 0;
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int i = 0; i < n; i++) {
                            KernelOffset(kernel, i, ca, sa, out float ox, out float oy);
                            maxRad = System.Math.Max(maxRad, MathF.Sqrt(ox * ox + oy * oy));
                            if (ox >= 0f) {
                                lit++;
                            }
                            if (ox < lo) {
                                lo = ox;
                            }
                            if (ox > hi) {
                                hi = ox;
                            }
                        }
                        double v = (double)lit / n;
                        sum += v;
                        sumSq += v * v;
                        maxErr = System.Math.Max(maxErr, System.Math.Abs(v - 0.5));
                        seen[lit] = true;
                        double ext = hi - lo;
                        sumExt += ext;
                        minExt = System.Math.Min(minExt, ext);
                        maxExt = System.Math.Max(maxExt, ext);
                    }
                    double mean = sum / rotations;
                    double std = System.Math.Sqrt(System.Math.Max(sumSq / rotations - mean * mean, 0));
                    int levels = 0;
                    for (int i = 0; i <= 16; i++) {
                        if (seen[i]) {
                            levels++;
                        }
                    }
                    kernels[KernelName(kernel)] = new JsonObject {
                        ["kernel"] = kernel,
                        ["samples"] = n,
                        ["mean"] = System.Math.Round(mean, 6),
                        ["std"] = System.Math.Round(std, 6),
                        ["maxErr"] = System.Math.Round(maxErr, 6),
                        ["maxErrTimesSamples"] = System.Math.Round(maxErr * n, 3),
                        ["extentMinT"] = System.Math.Round(minExt, 4),
                        ["extentMaxT"] = System.Math.Round(maxExt, 4),
                        ["extentMeanT"] = System.Math.Round(sumExt / rotations, 4),
                        ["extRatio"] = System.Math.Round(maxExt / System.Math.Max(minExt, 1e-9), 4),
                        ["distinctLitLevels"] = levels,
                        // [v0.1.103] 核的**真实最大采样半径**：bias 的"抽样半径"系数必须等于它
                        ["samplingRadius"] = System.Math.Round(maxRad, 5),
                        ["slopeScaleInUse"] = System.Math.Round(GpuShadowKernelSlopeScale(kernel), 5)
                    };
                }
                result["kernels"] = kernels;
                // 断言（都是"设计上必须成立"，不是把实测值抄回代码）：
                //   A. 无偏：三个核一圈的均值都在 0.5 ± 0.02 内；
                //   B1. **规则角分布**的两个核（八边形 8 / 双环 16）量化误差 = 1/n：maxErr × n 必须 = 1；
                //   B2. Dawnlight 的 blue-noise 盘在**直边**上打破这条：实测 maxErr × n = 3
                //       （12 个样本里有 3 个落到了边缘的另一侧）—— 这是它的性质，不是本自检的失败，
                //       所以它**单独记一条**、并且**追加**进 `poissonWorseThanOctagonOnStraightEdge`。
                //   C. 现核的支撑是正方形：extRatio ≥ 1.35（否则说明这条自检没有分辨力）；
                //   D. 双环核把支撑各向异性压到 1.15 以内，且最坏误差 ≤ 8 抽样核的 2/3；
                //   E. 反漂移：shader 源里真的含有这两张表和核选择 uniform。
                bool unbiased = true, quantOk = true;
                // [v0.1.103] F：bias 的"抽样半径"系数必须等于该核真的最大采样半径（防止换核后 bias 走偏）
                bool slopeScaleOk = true;
                for (int kernel = 0; kernel <= 2; kernel++) {
                    JsonObject k = kernels[KernelName(kernel)].AsObject();
                    if (System.Math.Abs(k["mean"].GetValue<double>() - 0.5) > 0.02) {
                        unbiased = false;
                    }
                    if (System.Math.Abs(k["samplingRadius"].GetValue<double>()
                                        - k["slopeScaleInUse"].GetValue<double>()) > 0.001) {
                        slopeScaleOk = false;
                    }
                }
                for (int kernel = 0; kernel <= 2; kernel += 2) {
                    if (System.Math.Abs(
                            kernels[KernelName(kernel)]["maxErrTimesSamples"].GetValue<double>() - 1.0) > 0.02) {
                        quantOk = false;
                    }
                }
                double octRatio = kernels["octagon8"]["extRatio"].GetValue<double>();
                double ringRatio = kernels["ring16"]["extRatio"].GetValue<double>();
                double ringErr = kernels["ring16"]["maxErr"].GetValue<double>();
                double octErr = kernels["octagon8"]["maxErr"].GetValue<double>();
                double poiErr = kernels["poisson12"]["maxErr"].GetValue<double>();
                bool squareDetected = octRatio >= 1.35;
                bool ringIsotropic = ringRatio <= 1.15;
                bool ringFiner = ringErr <= octErr * (2.0 / 3.0) + 1e-6;
                bool anchors = GpuShadowOpaquePsh.Contains("u_shadowKernel")
                    && GpuShadowOpaquePsh.Contains("rotKernelOffset")
                    && GpuShadowOpaquePsh.Contains("-0.94201624")
                    && GpuShadowOpaquePsh.Contains("0.92387956");
                result["checks"] = new JsonObject {
                    ["A_unbiased"] = unbiased,
                    ["B1_regularKernelsQuantizeAsOneOverN"] = quantOk,
                    ["C_legacySupportIsSquare"] = squareDetected,
                    ["D_ringIsotropic"] = ringIsotropic,
                    ["D_ringFinerThanOctagon"] = ringFiner,
                    ["E_shaderAnchorsPresent"] = anchors,
                    ["F_slopeScaleMatchesSamplingRadius"] = slopeScaleOk
                };
                // 如实标注：Dawnlight 的核在**直边**这一项上比现核更差（这是本轮的负结果，不是笔误）
                result["poissonWorseThanOctagonOnStraightEdge"] = poiErr > octErr;
                result["ok"] = unbiased && quantOk && squareDetected && ringIsotropic && ringFiner && anchors
                               && slopeScaleOk;
                result["activeKernel"] = GpuShadowKernel;
            }
            catch (System.Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }

        /// <summary>[v0.1.102] 切换 PCF 采样核（0=八边形 8 / 1=Dawnlight Poisson 12 / 2=双环 16）。</summary>
        public static string GpuShadowKernelSet(int kernel) {
            GpuShadowKernel = System.Math.Clamp(kernel, 0, 2);
            JsonObject result = (JsonObject)JsonNode.Parse(GpuShadowSoftInfo());
            result["ok"] = true;
            result["hint"] = "核只改片元的采样偏移，不需要重建深度图（下一帧即生效）；"
                + "取证请用 skyline.GpuShadowKernelSelfCheck() 与 skyline.GpuShadowSoftInfo()";
            return result.ToJsonString();
        }

        /// <summary>[v0.1.105] "阴影图+体积雾"这一套现在能不能用（**LOD 层**用它决定 `u_shadowEnable`）。</summary>
        public static bool ShadowSampleReady =>
            GpuShadowSampleEnabled && m_gpuShadowHasMap && m_gpuShadowRt != null;

        /// <summary>[v0.1.105] 把**阴影图 + 体积雾/神光**那一组 uniform 绑到**任意** shader 上
        /// （远景 LOD 层复用同一套；地形那条路由 `ResolveOpaqueShader` 自己绑）。
        ///
        /// ⚠️ **两边必须保持同一口径**：这里的每一项都要与 `ResolveOpaqueShader` 里的对应项一致，
        /// 否则"同一个影子/同一层雾在 LOD 与真地形上不一样"，交界处会露馅。
        /// 返回 `""` 表示成功，否则返回错误串（调用方自己决定是否记日志，**不抛**）。
        /// </summary>
        public static string BindShadowFogParams(Shader shader, bool shadows) {
            try {
                shader.GetParameter("u_shadowEnable", true).SetValue(shadows ? 1f : 0f);
                shader.GetParameter("u_shadowMap", true).SetValue(shadows ? m_gpuShadowRt : m_gpuShadowDummyRt);
                shader.GetParameter("u_shadowSampler", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjection", true).SetValue(m_gpuShadowViewProjection);
                shader.GetParameter("u_sunOrigin", true).SetValue(m_gpuShadowOrigin);
                shader.GetParameter("u_eye", true).SetValue(m_gpuShadowEye);
                shader.GetParameter("u_sunDir", true).SetValue(m_gpuShadowSun);
                shader.GetParameter("u_depthMax", true).SetValue(m_gpuShadowDepthMax);
                shader.GetParameter("u_shadowMapNear", true)
                    .SetValue(shadows ? (m_gpuShadowRtNear ?? m_gpuShadowRt) : m_gpuShadowDummyRt);
                shader.GetParameter("u_shadowSamplerNear", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjectionNear", true).SetValue(m_gpuShadowViewProjectionNear);
                shader.GetParameter("u_sunOriginNear", true).SetValue(m_gpuShadowOriginNear);
                shader.GetParameter("u_eyeNear", true).SetValue(m_gpuShadowEyeNear);
                shader.GetParameter("u_depthMaxNear", true).SetValue(m_gpuShadowDepthMaxNear);
                shader.GetParameter("u_nearCascade", true).SetValue(
                    (m_gpuShadowHasNearMap && m_gpuShadowRtNear != null) ? 1f : 0f);
                shader.GetParameter("u_shadowBias", true).SetValue(GpuShadowSampleBias);
                shader.GetParameter("u_shadowStrength", true).SetValue(GpuShadowSampleStrength);
                shader.GetParameter("u_shadowFlipY", true).SetValue(GpuShadowFlipY ? 1f : 0f);
                shader.GetParameter("u_shadowDepth16", true).SetValue(m_gpuShadowDepth16AtCapture ? 1f : 0f);
                shader.GetParameter("u_shadowDebug", true).SetValue(0f);
                float fogTime = (float)Time.RealTime;
                float fogBottom = Math.Min(FogBottomY, FogTopY - 1f);
                shader.GetParameter("u_vfEnable", true).SetValue(VolumetricFogEnabled ? 1f : 0f);
                shader.GetParameter("u_vfBottomY", true).SetValue(fogBottom);
                shader.GetParameter("u_vfTopY", true).SetValue(Math.Max(FogTopY, fogBottom + 1f));
                shader.GetParameter("u_vfDensity", true).SetValue(Math.Max(FogDensity, 0f));
                shader.GetParameter("u_vfScale", true).SetValue(Math.Max(FogScale, 1e-6f));
                shader.GetParameter("u_vfWind", true).SetValue(FogWind * fogTime);
                shader.GetParameter("u_vfThreshold", true).SetValue(Math.Clamp(FogThreshold, 0f, 0.99f));
                shader.GetParameter("u_vfStrength", true).SetValue(Math.Clamp(FogStrength, 0f, 1f));
                shader.GetParameter("u_vfColor", true).SetValue(FogColor);
                shader.GetParameter("u_vfSkyMix", true).SetValue(Math.Clamp(FogSkyMix, 0f, 1f));
                shader.GetParameter("u_vfMaxDistance", true).SetValue(Math.Max(FogMaxDistance, 10f));
                shader.GetParameter("u_vfShear", true).SetValue(Math.Max(FogHeightShear, 0f));
                shader.GetParameter("u_vfSunShaft", true).SetValue(Math.Max(VolumetricSunShaftStrength, 0f));
                shader.GetParameter("u_vfSunColor", true).SetValue(VolumetricSunShaftColor);
                shader.GetParameter("u_vfPhasePower", true)
                    .SetValue(Math.Clamp(VolumetricSunShaftPhasePower, 1f, 64f));
                shader.GetParameter("u_shadowSoft", true).SetValue(GpuShadowSoftEnabled ? 1f : 0f);
                shader.GetParameter("u_shadowSoftRadius", true).SetValue(GpuShadowSoftRadius);
                shader.GetParameter("u_shadowTexelFar", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                shader.GetParameter("u_shadowTexelNear", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                float sunY = System.MathF.Max(System.MathF.Abs(m_gpuShadowSun.Y), GpuShadowSoftSunYFloor);
                float farRelief = 2f * m_gpuShadowRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                  / System.Math.Max(m_gpuShadowDepthMax, 0.0001f) / sunY;
                float nearRelief = 2f * m_gpuShadowNearRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                   / System.Math.Max(m_gpuShadowDepthMaxNear, 0.0001f) / sunY;
                shader.GetParameter("u_shadowReliefFar", true).SetValue(farRelief);
                shader.GetParameter("u_shadowReliefNear", true).SetValue(nearRelief);
                shader.GetParameter("u_shadowSoftSlopeBias", true).SetValue(GpuShadowSoftSlopeBias);
                shader.GetParameter("u_shadowSoftReliefTexels", true).SetValue(GpuShadowSoftReliefTexels);
                shader.GetParameter("u_shadowKernel", true).SetValue((float)GpuShadowKernel);
                shader.GetParameter("u_shadowKernelSlopeScale", true).SetValue(GpuShadowKernelSlopeScale(GpuShadowKernel));
                return "";
            }
            catch (System.Exception e) {
                return e.Message;
            }
        }

        public static string GpuShadowSampleDescribe() =>
            $"gpuShadowSample enabled={GpuShadowSampleEnabled} strength={GpuShadowSampleStrength:0.##} "
            + $"bias={GpuShadowSampleBias:0.####} flipY={GpuShadowFlipY} hasMap={m_gpuShadowHasMap} "
            + $"soft={GpuShadowSoftEnabled}/{GpuShadowSoftRadius:0.##}texel "
            + $"penumbraFar={GpuShadowSoftRadius * 2f * m_gpuShadowRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1):0.###}m "
            + $"penumbraNear={GpuShadowSoftRadius * 2f * m_gpuShadowNearRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1):0.###}m "
            + $"relief={GpuShadowSoftReliefTexels:0.##}tx slopeBias={GpuShadowSoftSlopeBias:0.##} "
            + $"sunY={m_gpuShadowSun.Y:0.###} "
            + $"depth16={m_gpuShadowDepth16AtCapture} "
            + $"cascade={GpuShadowCascadeEnabled} hasNearMap={m_gpuShadowHasNearMap} "
            + $"sunRecaptureDeg={GpuShadowSunRecaptureDegrees:0.#} autoCaptures={m_gpuShadowAutoCaptures} "
            + $"resolved={m_gpuShadowSampleResolved} fallbacks={m_gpuShadowSampleFallbacks} "
            + $"lastReason='{m_gpuShadowSampleLastReason}' debug={GpuShadowDebugMode} err='{m_gpuShadowSampleError}'";

        /// <summary>[v0.1.34] 每帧由 `SkylineRuntime.Tick()` 调用：启用采样但还没深度图时自动补一次捕获。</summary>
        public static void GpuShadowTick() {
            if (!GpuShadowSampleEnabled) {
                return;
            }
            if (GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain == null) {
                return;
            }
            // 采样必须有深度图：捕获开关没开时随采样一起打开（否则 GpuShadowCapture 会直接拒绝）。
            if (!GpuShadowEnabled) {
                GpuShadowEnabled = true;
            }
            if (!m_gpuShadowHasMap) {
                GpuShadowCapture();
                return;
            }
            // [v0.1.65] 太阳追踪：光源转过阈值就自动重捕一次（轻量路径，不读回深度图）。
            // 没有这一条，"太阳追踪"只会在手动 `GpuShadowCapture()` 的那一刻成立 —— 影子不会跟着太阳转。
            if (GpuShadowSunRecaptureDegrees <= 0f) {
                return;
            }
            Vector3 now = TrackedLightDirection();
            float thresholdCos = System.MathF.Cos(GpuShadowSunRecaptureDegrees * System.MathF.PI / 180f);
            if (Vector3.Dot(now, m_gpuShadowSun) < thresholdCos) {
                m_gpuShadowSkipDiagnostics = true;
                try {
                    GpuShadowCapture();
                }
                finally {
                    m_gpuShadowSkipDiagnostics = false;
                }
            }
        }

        /// <summary>
        /// 解析不透明 pass 实际使用的 shader：启用采样且已有深度图时返回"地形+阴影采样"变体，
        /// 否则返回 fallback（游戏原本的 `m_opaqueShader`）→ 默认行为逐位不变。
        /// </summary>
        public static Shader ResolveOpaqueShader(Shader fallback) {
            // [v0.1.69] 变体条件解耦：**体积雾**与**阴影采样**都注入同一个不透明变体，
            // 所以"只想开体积雾"也必须能拿到变体（以前必须先有深度图，等于被阴影挡住）。
            bool shadows = GpuShadowSampleEnabled && m_gpuShadowHasMap && m_gpuShadowRt != null;
            bool fog = VolumetricFogEnabled;
            if (!shadows && !fog) {
                m_gpuShadowSampleFallbacks++;
                m_gpuShadowSampleLastReason = (!GpuShadowSampleEnabled ? "disabled"
                    : (!m_gpuShadowHasMap ? "noMap" : "noRt")) + "+fogOff";
                return fallback;
            }
            try {
                if (m_gpuShadowOpaqueShader == null) {
                    m_gpuShadowOpaqueShader = new Shader(GpuShadowOpaqueVsh, GpuShadowOpaquePsh);
                }
                if (!shadows && m_gpuShadowDummyRt == null) {
                    // 只用体积雾时的占位深度图（1×1）：纹素参数必须绑一个真的 Texture2D，
                    // 但 u_shadowEnable=0 会让片元根本不走采样分支。
                    m_gpuShadowDummyRt = new RenderTarget2D(1, 1, 1, ColorFormat.Rgba8888, DepthFormat.None);
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
                shader.GetParameter("u_shadowEnable", true).SetValue(shadows ? 1f : 0f);
                shader.GetParameter("u_shadowMap", true).SetValue(shadows ? m_gpuShadowRt : m_gpuShadowDummyRt);
                shader.GetParameter("u_shadowSampler", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjection", true).SetValue(m_gpuShadowViewProjection);
                shader.GetParameter("u_sunOrigin", true).SetValue(m_gpuShadowOrigin);
                shader.GetParameter("u_eye", true).SetValue(m_gpuShadowEye);
                shader.GetParameter("u_sunDir", true).SetValue(m_gpuShadowSun);
                shader.GetParameter("u_depthMax", true).SetValue(m_gpuShadowDepthMax);
                // [v0.1.38] 近图（级联）：没有近图时 u_nearCascade = 0，片元只走远图（行为同 v0.1.37）
                shader.GetParameter("u_shadowMapNear", true).SetValue(
                    shadows ? (m_gpuShadowRtNear ?? m_gpuShadowRt) : m_gpuShadowDummyRt);
                shader.GetParameter("u_shadowSamplerNear", true).SetValue(m_gpuShadowSampler);
                shader.GetParameter("u_sunViewProjectionNear", true).SetValue(m_gpuShadowViewProjectionNear);
                shader.GetParameter("u_sunOriginNear", true).SetValue(m_gpuShadowOriginNear);
                shader.GetParameter("u_eyeNear", true).SetValue(m_gpuShadowEyeNear);
                shader.GetParameter("u_depthMaxNear", true).SetValue(m_gpuShadowDepthMaxNear);
                shader.GetParameter("u_nearCascade", true).SetValue(
                    (m_gpuShadowHasNearMap && m_gpuShadowRtNear != null) ? 1f : 0f);
                shader.GetParameter("u_shadowBias", true).SetValue(GpuShadowSampleBias);
                shader.GetParameter("u_shadowStrength", true).SetValue(GpuShadowSampleStrength);
                shader.GetParameter("u_shadowFlipY", true).SetValue(GpuShadowFlipY ? 1f : 0f);
                shader.GetParameter("u_shadowDepth16", true).SetValue(m_gpuShadowDepth16AtCapture ? 1f : 0f);
                shader.GetParameter("u_shadowDebug", true).SetValue((float)GpuShadowDebugMode);
                // [v0.1.69] 自研体积雾：与阴影共用同一个不透明变体
                {
                    float fogTime = (float)Time.RealTime;
                    float fogBottom = Math.Min(FogBottomY, FogTopY - 1f);
                    shader.GetParameter("u_vfEnable", true).SetValue(VolumetricFogEnabled ? 1f : 0f);
                    shader.GetParameter("u_vfBottomY", true).SetValue(fogBottom);
                    shader.GetParameter("u_vfTopY", true).SetValue(Math.Max(FogTopY, fogBottom + 1f));
                    shader.GetParameter("u_vfDensity", true).SetValue(Math.Max(FogDensity, 0f));
                    shader.GetParameter("u_vfScale", true).SetValue(Math.Max(FogScale, 1e-6f));
                    shader.GetParameter("u_vfWind", true).SetValue(FogWind * fogTime);
                    shader.GetParameter("u_vfThreshold", true).SetValue(Math.Clamp(FogThreshold, 0f, 0.99f));
                    shader.GetParameter("u_vfStrength", true).SetValue(Math.Clamp(FogStrength, 0f, 1f));
                    shader.GetParameter("u_vfColor", true).SetValue(FogColor);
                    shader.GetParameter("u_vfSkyMix", true).SetValue(Math.Clamp(FogSkyMix, 0f, 1f));
                    shader.GetParameter("u_vfMaxDistance", true).SetValue(Math.Max(FogMaxDistance, 10f));
                    shader.GetParameter("u_vfShear", true).SetValue(Math.Max(FogHeightShear, 0f));
                    // [v0.1.104] 体积神光（Dawnlight ShaftLighting 的适配路线，见 SkylineVolumetricFog）
                    shader.GetParameter("u_vfSunShaft", true).SetValue(Math.Max(VolumetricSunShaftStrength, 0f));
                    shader.GetParameter("u_vfSunColor", true).SetValue(VolumetricSunShaftColor);
                    shader.GetParameter("u_vfPhasePower", true)
                        .SetValue(Math.Clamp(VolumetricSunShaftPhasePower, 1f, 64f));
                    m_volFogBound++;
                    m_volFogLastError = "";
                }
                // [v0.1.64] PCF：偏移在 **UV 空间**（1 texel = 1/尺寸）。传"米"会把整张贴图当偏移跨过去
                // （实测：8 个抽样全落到边界外 → 阴影整片消失）。级联的世界尺度差异由"覆盖范围不同"自然给出。
                shader.GetParameter("u_shadowSoft", true).SetValue(GpuShadowSoftEnabled ? 1f : 0f);
                shader.GetParameter("u_shadowSoftRadius", true).SetValue(GpuShadowSoftRadius);
                shader.GetParameter("u_shadowTexelFar", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                shader.GetParameter("u_shadowTexelNear", true).SetValue(
                    1f / System.Math.Max(m_gpuShadowSizeAtCapture, 1));
                // [v0.1.64] 坡度项：每个 texel 的**归一化深度变化**（最坏情况：受光面与太阳方向夹角 0°）
                //   每 texel 的归一化深度 = (2×半径/尺寸) 米 ÷ depthMax ÷ sinθ
                //   （÷sinθ 是因为正交盒沿太阳方向，太阳越低，一个 texel 对应的**地面足迹**越长）
                float sunY = System.MathF.Max(System.MathF.Abs(m_gpuShadowSun.Y), GpuShadowSoftSunYFloor);
                float farRelief = 2f * m_gpuShadowRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                  / System.Math.Max(m_gpuShadowDepthMax, 0.0001f) / sunY;
                float nearRelief = 2f * m_gpuShadowNearRadiusAtCapture / System.Math.Max(m_gpuShadowSizeAtCapture, 1)
                                   / System.Math.Max(m_gpuShadowDepthMaxNear, 0.0001f) / sunY;
                shader.GetParameter("u_shadowReliefFar", true).SetValue(farRelief);
                shader.GetParameter("u_shadowReliefNear", true).SetValue(nearRelief);
                shader.GetParameter("u_shadowSoftSlopeBias", true).SetValue(GpuShadowSoftSlopeBias);
                shader.GetParameter("u_shadowSoftReliefTexels", true).SetValue(GpuShadowSoftReliefTexels);
                // [v0.1.102] 核选择：0=八边形 8 抽样（v0.1.64 原核），1=Dawnlight Poisson 盘 12 抽样
                shader.GetParameter("u_shadowKernel", true).SetValue((float)GpuShadowKernel);
                // [v0.1.103] bias 的"抽样半径"系数必须按**这个核的真实最大半径**给（八边形 √2 / 双环 1.0）
                shader.GetParameter("u_shadowKernelSlopeScale", true).SetValue(GpuShadowKernelSlopeScale(GpuShadowKernel));
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
Texture2D u_shadowMapNear;
SamplerState u_shadowSamplerNear;
float4x4 u_sunViewProjection;
float4x4 u_sunViewProjectionNear;
float2 u_sunOrigin;
float2 u_sunOriginNear;
float3 u_eye;
float3 u_eyeNear;
float3 u_sunDir;
float u_depthMax;
float u_depthMaxNear;
float u_nearCascade;
float u_shadowBias;
float u_shadowStrength;
float u_shadowFlipY;
float u_shadowDepth16;
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
float u_shadowEnable;
float u_vfEnable;
float u_vfBottomY;
float u_vfTopY;
float u_vfDensity;
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
float3 u_viewPosition;
// [v0.1.69] 体积雾：确定性值噪声 + 沿视线 8 步积分（与云同一套噪声口径）
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
	return max(0.0, n - u_vfThreshold) * prof;
}

// [v0.1.35] R 高字节 / G 低字节：d16 = (R*255)*256 + G*255
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

// [v0.1.102] Dawnlight 的 Poisson 盘核（`lib/CalculateShadow.glsl` 的 `poissonDisk[16]` 逐字搬过来；
// 它自己的 `getWarpShadowPCF()` 取前 12 个，`int samples = 12;`）。只换核：本分支没有 warp 空间。
// ⚠️ **不要用 `const` 数组**：本引擎 Windows 侧跑的是 ANGLE/GL ES，实测第一版写成
// `static const float2 k[16] = {...}` 时整支像素着色器编译失败
// （`OpenGL does not allow constant arrays` / `array assignments require #version 120`），
// 阴影采样被自动关掉、A/B 变成「测空气」。所以这里改成「逐点字面量 + 一个旋转 helper」。
float2 rotKernelOffset(float2 o, float ca, float sa)
{
	return float2(o.x * ca - o.y * sa, o.x * sa + o.y * ca);
}

float pcfPoisson12Far(float2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.94201624, -0.39906216), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.94558609, -0.76890725), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.094184101, -0.92938870), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.34495938, 0.29387760), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.91588581, 0.45771432), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.81544232, -0.87912464), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.38277543, 0.27676845), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.97484398, 0.75648379), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.44323325, -0.97511554), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.53742981, -0.47373420), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.26496911, -0.41893023), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.79197514, 0.19090188), ca, sa) * t)) + sb);
	return acc * 0.0833333333;
}

float pcfPoisson12Near(float2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.94201624, -0.39906216), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.94558609, -0.76890725), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.094184101, -0.92938870), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.34495938, 0.29387760), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.91588581, 0.45771432), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.81544232, -0.87912464), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.38277543, 0.27676845), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.97484398, 0.75648379), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.44323325, -0.97511554), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.53742981, -0.47373420), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.26496911, -0.41893023), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.79197514, 0.19090188), ca, sa) * t)) + sb);
	return acc * 0.0833333333;
}

// [v0.1.102] 本分支自己设计的核（**依据是对上面两个核的测量**，不是口味）：
// 双同心环 8+8，半径 0.6t / 1.0t，环间角偏移 22.5°。
// 出处：`GpuShadowKernelSelfCheck()` 量出「八边形核的支撑是**正方形**」（角点半径 √2 ⇒ 斜向半影宽 41%）、
// 且只有 8 个方向（半平面最坏误差 12.5%）；Dawnlight 的 blue-noise 盘虽然把支撑变成圆盘，
// 但方向不规则 ⇒ 半平面最坏误差反而涨到 **25%**。把「圆盘支撑 + 规则角分布 + 16 抽样」合起来
// 就同时拿到两个好处：最坏误差 6.25% × 支撑各向异性 ≈1.02。
float pcfRing16Far(float2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.00000000, 0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.00000000, -0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.92387956, -0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(-0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uv + rotKernelOffset(float2(0.92387956, -0.38268343), ca, sa) * t)) + sb);
	return acc * 0.0625;
}

float pcfRing16Near(float2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.00000000, 0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.00000000, -0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.92387956, -0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(-0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uv + rotKernelOffset(float2(0.92387956, -0.38268343), ca, sa) * t)) + sb);
	return acc * 0.0625;
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

// [v0.1.104] 体积神光用：**世界空间某一点有没有被太阳照到**（单抽，不做 PCF —— 每帧要多 8 次采样）。
// 与主阴影同一套投影/级联/bias 口径；超出阴影图范围或没开阴影采样都按「照到」处理。
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
	in float3 v_world: TEXCOORD1,
	in float v_fog: FOG,
	out float4 svTarget: SV_TARGET
)
{
	float4 result = v_color;
	result *= u_texture.Sample(u_samplerState, v_texcoord);
	float3 sunShiftedFar = float3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	float2 uvFar = shadowUv(mul(float4(sunShiftedFar, 1.0), u_sunViewProjection), u_shadowFlipY);
	float3 sunShiftedNear = float3(v_world.x - u_sunOriginNear.x, v_world.y, v_world.z - u_sunOriginNear.y);
	float2 uvNear = shadowUv(mul(float4(sunShiftedNear, 1.0), u_sunViewProjectionNear), u_shadowFlipY);
	float mapDepth = 1.0;
	bool insideFar = uvFar.x >= 0.0 && uvFar.x <= 1.0 && uvFar.y >= 0.0 && uvFar.y <= 1.0;
	bool insideNear = u_nearCascade > 0.5 && uvNear.x >= 0.0 && uvNear.x <= 1.0 && uvNear.y >= 0.0 && uvNear.y <= 1.0;
	bool inside = insideFar || insideNear;
	// [v0.1.38] 近图优先：近图 texel 更细（128 m/1024² = 0.25 m），接触阴影更锐
	if (insideNear)
	{
		mapDepth = decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear));
	}
	else if (insideFar)
	{
		mapDepth = decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar));
	}
	if (u_shadowDebug > 0.5)
	{
		if (u_shadowDebug > 1.5)
		{
			// debug=2：蓝=用近图（级联），红=只用远图，绿=两张都不在范围
			result.rgb = insideNear ? float3(0.1, 0.2, 1.0)
				: insideFar ? float3(1.0, 0.15, 0.15)
				: float3(0.1, 1.0, 0.2);
		}
		else
		{
			result.rgb = float3(mapDepth, mapDepth, mapDepth);
		}
	}
	else if (inside && u_shadowEnable > 0.5)
	{
		// 近图的深度是按「近图自己的 eye + depthMax」归一化的（两张图的太阳距离不同），
		// 所以比较时必须换成对应的 eye/depthMax —— 用错会整片判成阴影（实测踩到）。
		float depthMax = insideNear ? u_depthMaxNear : u_depthMax;
		float3 eye = insideNear ? u_eyeNear : u_eye;
		float fragDepth = saturate(dot(eye - v_world, u_sunDir) / max(depthMax, 0.0001));
		float lit;
		if (u_shadowSoft > 0.5)
		{
			// [v0.1.64] 软阴影 = 8 抽样八边形核 PCF。要点：**平均的是「遮挡判定」而不是深度**
			// （平均深度只会让明暗过渡跟着深度走，不会产生半影）。
			// 逐像素旋转来自世界坐标哈希 → 确定性、跨帧稳定（截图 A/B 可复现），同时打破八边形的规则性。
			// 只对**自身级联**取样：近图 0.25 m/texel、远图 1 m/texel，半影半径因此天然分层。
			float t = (insideNear ? u_shadowTexelNear : u_shadowTexelFar) * max(u_shadowSoftRadius, 0.0);
			float ang = frac(sin(dot(v_world.xz, float2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
			// bias 补偿：(relief texel 数 + 抽样半径×1.4142) × 每 texel 归一化深度（已含 1/sinθ）。
			// 没有坡度项 → 大片受光地面被压暗（实测 48.5%）；没有 relief 项 → 残留斜向条纹 acne。
			float relief = insideNear ? u_shadowReliefNear : u_shadowReliefFar;
			float sb = u_shadowBias + (u_shadowSoftReliefTexels
				+ u_shadowSoftSlopeBias * u_shadowSoftRadius * u_shadowKernelSlopeScale) * relief;
			float ca = cos(ang);
			float sa = sin(ang);
			// [v0.1.102] 核选择：1 = Dawnlight 的 Poisson 盘 12 抽样（`lib/CalculateShadow.glsl`），
			// 2 = 本分支的双环 8+8（16 抽样），0 = v0.1.64 的八边形 8 抽样（逐位不变）。
			// 三个核共用同一 t / 同一 bias 补偿，所以 A/B 只换了**核**。
			if (u_shadowKernel > 1.5)
			{
				lit = insideNear
					? pcfRing16Near(uvNear, t, fragDepth, sb, ca, sa)
					: pcfRing16Far(uvFar, t, fragDepth, sb, ca, sa);
			}
			else if (u_shadowKernel > 0.5)
			{
				lit = insideNear
					? pcfPoisson12Near(uvNear, t, fragDepth, sb, ca, sa)
					: pcfPoisson12Far(uvFar, t, fragDepth, sb, ca, sa);
			}
			else
			{
				float acc = 0.0;
				if (insideNear)
				{
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((-ca + sa) * t, (-sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(sa * t, -ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((ca + sa) * t, (sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(-ca * t, -sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(ca * t, sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((-ca - sa) * t, (-sa + ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2(-sa * t, ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMapNear.Sample(u_shadowSamplerNear, uvNear + float2((ca - sa) * t, (sa + ca) * t))) + sb);
				}
				else
				{
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((-ca + sa) * t, (-sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(sa * t, -ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((ca + sa) * t, (sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(-ca * t, -sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(ca * t, sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((-ca - sa) * t, (-sa + ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2(-sa * t, ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(u_shadowMap.Sample(u_shadowSampler, uvFar + float2((ca - sa) * t, (sa + ca) * t))) + sb);
				}
				lit = acc * 0.125;
			}
		}
		else
		{
			// 关掉软阴影：用已取到的 mapDepth 判（与 v0.1.63 逐位一致）
			lit = step(fragDepth, mapDepth + u_shadowBias);
		}
		result.rgb *= (1.0 - u_shadowStrength * (1.0 - lit));
	}
	result.rgb = lerp(result.rgb, u_fogColor * v_color.a, v_fog);
	// [v0.1.69] 自研体积雾（替换被 FogDisabled 置 0 的原版雾）：沿视线 8 步积分
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
				// [v0.1.104] 每个雾步同时问一次「这一段烟有没有被太阳照到」（体积神光，见 sunShaftVisibility）
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
				// [v0.1.70] 雾色与游戏按天空/天气算的 u_fogColor 混合：下雨/黄昏时雾会跟着变色
				float3 vfCol = lerp(u_vfColor, max(u_fogColor, float3(0.02, 0.02, 0.02)), u_vfSkyMix);
				result.rgb = lerp(result.rgb, vfCol, vfAlpha);
				if (u_vfSunShaft > 0.0)
				{
					float vfPhase = 0.25 + 0.75 * pow(saturate(dot(vfRd, u_sunDir)), u_vfPhasePower);
					// 归一化口径（v0.1.104 实测修正）：**「雾里有多大比例被太阳照到」 × 雾的不透明度 × 相位**。
					// 第一版写成 `vfScat × vfOdScale`（∝ 密度²）⇒ 默认密度 0.06 下增量 <8/255 完全看不见
					// （实测：0.06 时 0.0% 像素、0.30 时 7.85% 像素）。现在与密度解耦。
					float vfLit = saturate(vfScat / vfOdRaw);
					result.rgb += u_vfSunColor * (vfLit * vfAlpha * vfPhase * u_vfSunShaft);
				}
			}
		}
	}
	svTarget = result;
}

#endif
#ifdef GLSL

#ifdef GL_ES
precision highp float;   // [v0.1.35] 16 bit 深度解码需要 fp32（mediump 尾数只有 ~10 bit）
#endif

// <Sampler Name='u_samplerState' Texture='u_texture' />
// <Sampler Name='u_shadowSampler' Texture='u_shadowMap' />
// <Sampler Name='u_shadowSamplerNear' Texture='u_shadowMapNear' />

uniform sampler2D u_texture;
uniform sampler2D u_shadowMap;
uniform sampler2D u_shadowMapNear;
uniform vec3 u_fogColor;
uniform mat4 u_sunViewProjection;
uniform mat4 u_sunViewProjectionNear;
uniform vec2 u_sunOrigin;
uniform vec2 u_sunOriginNear;
uniform vec3 u_eye;
uniform vec3 u_eyeNear;
uniform vec3 u_sunDir;
uniform float u_depthMax;
uniform float u_depthMaxNear;
uniform float u_nearCascade;
uniform float u_shadowBias;
uniform float u_shadowStrength;
uniform float u_shadowFlipY;
uniform float u_shadowDepth16;
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
uniform float u_shadowEnable;
uniform float u_vfEnable;
uniform float u_vfBottomY;
uniform float u_vfTopY;
uniform float u_vfDensity;
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
uniform vec3 u_viewPosition;

// [v0.1.69] 体积雾（与 HLSL 段同一算法）
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
	return max(0.0, n - u_vfThreshold) * prof;
}

varying vec4 v_color;
varying vec2 v_texcoord;
varying vec3 v_world;
varying float v_fog;

// [v0.1.35] R 高字节 / G 低字节：d16 = (R*255)*256 + G*255
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

// [v0.1.102] Dawnlight 的 Poisson 盘核（与 HLSL 段同一张表，逐字来自 `lib/CalculateShadow.glsl`）。
// ⚠️ **不用 const 数组**：实测本引擎的 GL ES 路径会直接报
// `OpenGL does not allow constant arrays` ⇒ 整支像素着色器编译失败。改成逐点字面量 + 旋转 helper。
vec2 rotKernelOffset(vec2 o, float ca, float sa)
{
	return vec2(o.x * ca - o.y * sa, o.x * sa + o.y * ca);
}

float pcfPoisson12Far(vec2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.94201624, -0.39906216), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.94558609, -0.76890725), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.094184101, -0.92938870), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.34495938, 0.29387760), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.91588581, 0.45771432), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.81544232, -0.87912464), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.38277543, 0.27676845), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.97484398, 0.75648379), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.44323325, -0.97511554), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.53742981, -0.47373420), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.26496911, -0.41893023), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.79197514, 0.19090188), ca, sa) * t)) + sb);
	return acc * 0.0833333333;
}

float pcfPoisson12Near(vec2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.94201624, -0.39906216), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.94558609, -0.76890725), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.094184101, -0.92938870), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.34495938, 0.29387760), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.91588581, 0.45771432), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.81544232, -0.87912464), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.38277543, 0.27676845), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.97484398, 0.75648379), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.44323325, -0.97511554), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.53742981, -0.47373420), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.26496911, -0.41893023), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.79197514, 0.19090188), ca, sa) * t)) + sb);
	return acc * 0.0833333333;
}

// [v0.1.102] 双同心环 8+8（与 HLSL 段同一张表）：圆盘支撑 + 规则角分布 + 16 抽样

float pcfRing16Far(vec2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.00000000, 0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.00000000, -0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.92387956, -0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(-0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uv + rotKernelOffset(vec2(0.92387956, -0.38268343), ca, sa) * t)) + sb);
	return acc * 0.0625;
}

float pcfRing16Near(vec2 uv, float t, float fragDepth, float sb, float ca, float sa)
{
	float acc = 0.0;
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.00000000, 0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.42426407, 0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.60000002, 0.00000000), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.00000000, -0.60000002), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.42426407, -0.42426407), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.38268343, 0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.92387956, 0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.92387956, -0.38268343), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(-0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.38268343, -0.92387956), ca, sa) * t)) + sb);
	acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uv + rotKernelOffset(vec2(0.92387956, -0.38268343), ca, sa) * t)) + sb);
	return acc * 0.0625;
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

// [v0.1.104] 体积神光用（与 HLSL 段同一算法）
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
	vec4 result = v_color;
	result *= texture2D(u_texture, v_texcoord);
	vec3 sunShiftedFar = vec3(v_world.x - u_sunOrigin.x, v_world.y, v_world.z - u_sunOrigin.y);
	vec2 uvFar = shadowUv(u_sunViewProjection * vec4(sunShiftedFar, 1.0), u_shadowFlipY);
	vec3 sunShiftedNear = vec3(v_world.x - u_sunOriginNear.x, v_world.y, v_world.z - u_sunOriginNear.y);
	vec2 uvNear = shadowUv(u_sunViewProjectionNear * vec4(sunShiftedNear, 1.0), u_shadowFlipY);
	float mapDepth = 1.0;
	bool insideFar = uvFar.x >= 0.0 && uvFar.x <= 1.0 && uvFar.y >= 0.0 && uvFar.y <= 1.0;
	bool insideNear = u_nearCascade > 0.5 && uvNear.x >= 0.0 && uvNear.x <= 1.0 && uvNear.y >= 0.0 && uvNear.y <= 1.0;
	bool inside = insideFar || insideNear;
	// [v0.1.38] 近图优先：近图 texel 更细（128 m/1024² = 0.25 m），接触阴影更锐
	if (insideNear)
	{
		mapDepth = decodeShadowDepth(texture2D(u_shadowMapNear, uvNear));
	}
	else if (insideFar)
	{
		mapDepth = decodeShadowDepth(texture2D(u_shadowMap, uvFar));
	}
	if (u_shadowDebug > 0.5)
	{
		if (u_shadowDebug > 1.5)
		{
			// debug=2：蓝=用近图（级联），红=只用远图，绿=两张都不在范围
			result.rgb = insideNear ? vec3(0.1, 0.2, 1.0)
				: insideFar ? vec3(1.0, 0.15, 0.15)
				: vec3(0.1, 1.0, 0.2);
		}
		else
		{
			result.rgb = vec3(mapDepth, mapDepth, mapDepth);
		}
	}
	else if (inside && u_shadowEnable > 0.5)
	{
		// 近图深度按「近图自己的 eye + depthMax」归一化（两张图太阳距离不同）→ 比较时必须一起换。
		float depthMax = insideNear ? u_depthMaxNear : u_depthMax;
		vec3 eye = insideNear ? u_eyeNear : u_eye;
		float fragDepth = clamp(dot(eye - v_world, u_sunDir) / max(depthMax, 0.0001), 0.0, 1.0);
		float lit;
		if (u_shadowSoft > 0.5)
		{
			// [v0.1.64] 软阴影 = 8 抽样八边形核 PCF（与 HLSL 段同一算法：比较「遮挡判定」、逐像素旋转）
			float t = (insideNear ? u_shadowTexelNear : u_shadowTexelFar) * max(u_shadowSoftRadius, 0.0);
			float ang = fract(sin(dot(v_world.xz, vec2(12.9898, 78.233))) * 43758.5453) * 6.2831853;
			// bias 补偿（与 HLSL 段同一算法）：relief texel + 抽样半径×1.4142，再乘每 texel 归一化深度
			float relief = insideNear ? u_shadowReliefNear : u_shadowReliefFar;
			float sb = u_shadowBias + (u_shadowSoftReliefTexels
				+ u_shadowSoftSlopeBias * u_shadowSoftRadius * u_shadowKernelSlopeScale) * relief;
			float ca = cos(ang);
			float sa = sin(ang);
			// [v0.1.102] 核选择（与 HLSL 段同一算法）
			if (u_shadowKernel > 1.5)
			{
				lit = insideNear
					? pcfRing16Near(uvNear, t, fragDepth, sb, ca, sa)
					: pcfRing16Far(uvFar, t, fragDepth, sb, ca, sa);
			}
			else if (u_shadowKernel > 0.5)
			{
				lit = insideNear
					? pcfPoisson12Near(uvNear, t, fragDepth, sb, ca, sa)
					: pcfPoisson12Far(uvFar, t, fragDepth, sb, ca, sa);
			}
			else
			{
				float acc = 0.0;
				if (insideNear)
				{
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((-ca + sa) * t, (-sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(sa * t, -ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((ca + sa) * t, (sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(-ca * t, -sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(ca * t, sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((-ca - sa) * t, (-sa + ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2(-sa * t, ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMapNear, uvNear + vec2((ca - sa) * t, (sa + ca) * t))) + sb);
				}
				else
				{
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((-ca + sa) * t, (-sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(sa * t, -ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((ca + sa) * t, (sa - ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(-ca * t, -sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(ca * t, sa * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((-ca - sa) * t, (-sa + ca) * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2(-sa * t, ca * t))) + sb);
					acc += step(fragDepth, decodeShadowDepth(texture2D(u_shadowMap, uvFar + vec2((ca - sa) * t, (sa + ca) * t))) + sb);
				}
				lit = acc * 0.125;
			}
		}
		else
		{
			// 关掉软阴影：用已取到的 mapDepth 判（与 v0.1.63 逐位一致）
			lit = step(fragDepth, mapDepth + u_shadowBias);
		}
		result.rgb *= (1.0 - u_shadowStrength * (1.0 - lit));
	}
	result.rgb = mix(result.rgb, u_fogColor * v_color.a, v_fog);
	// [v0.1.69] 自研体积雾（与 HLSL 段同一算法）
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
				// [v0.1.104] 体积神光（与 HLSL 段同一算法）
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
				// [v0.1.70] 雾色与 u_fogColor 混合（与 HLSL 段同一算法）
				vec3 vfCol = mix(u_vfColor, max(u_fogColor, vec3(0.02, 0.02, 0.02)), u_vfSkyMix);
				result.rgb = mix(result.rgb, vfCol, vfAlpha);
				if (u_vfSunShaft > 0.0)
				{
					float vfPhase = 0.25 + 0.75 * pow(clamp(dot(vfRd, u_sunDir), 0.0, 1.0), u_vfPhasePower);
					float vfLit = clamp(vfScat / vfOdRaw, 0.0, 1.0);
					result.rgb += u_vfSunColor * (vfLit * vfAlpha * vfPhase * u_vfSunShaft);
				}
			}
		}
	}
	gl_FragColor = result;
}

#endif
";
    }
}
