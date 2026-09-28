using System;
using Engine;

namespace Game {
    /// <summary>
    /// [v0.1.99] 里程碑 2.3：**云层阴影投到 LOD**（用户口径："LOD 的体素也要参与软阴影计算，
    /// 指的是 Iris 光影产生的太阳阴影、**云层阴影**、固定光源照射导致的亮度改变斑块等"）。
    ///
    /// 为什么这样做：体积云画在**天空穹顶**上（`SkylineVolumetricSky`），它自己不会给地面投任何影子；
    /// 而"云遮住太阳 → 地面变暗"这件事在视觉上是**低频**的（云的尺寸是几百米），
    /// 所以不必逐体素做昂贵的可见性计算：沿**太阳方向**在云带里积一次光学厚度就够了，
    /// 结果直接乘进 LOD 的顶点色（LOD 的坡向/自阴影也是这么烘的 ⇒ 两条渲染路径自动一致）。
    ///
    /// **必须与 shader 逐式同源**，否则"影子和看得见的云对不上"：
    /// 这里复制 `SkylineVolumetricSky` 里的 `hash12 / vnoise2 / volDensityAt` 与
    /// `od = Σ density · dt · CloudDensity`、`alpha = 1 - exp(-od)` 这套口径（注释里给出对应位置）。
    /// </summary>
    public static class SkylineLodCloudShadow {
        /// <summary>总开关（默认开）。关掉 = 逐位回到 v0.1.98（LOD 不参与云影）。</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>云影**最多压暗多少**（0..1，默认 0.55 = 最厚处压到 45% 亮度）。</summary>
        public static float Depth { get; set; } = 0.55f;
        /// <summary>沿太阳方向在云带里的采样步数（默认 6；云带是几百米厚，低频，不需要多）。</summary>
        public static int Steps { get; set; } = 6;
        /// <summary>光照低于这个量（夜里）不算云影 —— 夜里没有"太阳被云挡住"这回事。</summary>
        public static float MinSunAmount { get; set; } = 0.05f;

        /// <summary>
        /// [v0.1.99] **云影的刷新周期（秒，默认 2；0 = 不主动刷新）**。
        /// 为什么需要：云影是**烘进 LOD 顶点色**的（低频项，这样 CPU 烘焙/GPU 着色两条路径自动一致），
        /// 而云的相位随 `Time.RealTime` 走 —— 不主动重建的话，云飘走了影子却不跟，几分钟后就"影不对云"。
        /// 2 秒一次增量重建只重算"当前脏/超时"的那一档，实测重建在毫秒级（见 `notes/178`）。
        /// </summary>
        public static float RefreshSeconds { get; set; } = 2f;

        // ---- 诊断（判据从这里出） ----
        public static long SampledCells { get; private set; }
        public static long AffectedCells { get; private set; }
        public static float LastMinFactor { get; private set; } = 1f;
        public static float LastAvgFactor { get; private set; } = 1f;

        public static void ResetStats() {
            SampledCells = 0;
            AffectedCells = 0;
            LastMinFactor = 1f;
            LastAvgFactor = 1f;
        }

        /// <summary>记账（每次重建 / 每个格子调一次）：样本数、受影响数、最小/平均因子。</summary>
        public static void Note(float factor) {
            SampledCells++;
            if (factor < 0.999f) {
                AffectedCells++;
            }
            LastMinFactor = MathF.Min(LastMinFactor, factor);
            LastAvgFactor += (factor - LastAvgFactor) / SampledCells;
        }

        // ===== 与 SkylineVolumetricSky 的 shader 逐式同源的噪声/密度场 =====
        // 对应 GLSL：`hash12(float2)` / `vnoise2(float2)` / `volDensityAt(...)`。
        static float Hash12(float px, float py) {
            Vector3 p3 = new(
                Frac(px * 0.1031f),
                Frac(py * 0.1031f),
                Frac(px * 0.1031f));
            // GLSL: `p3 += dot(p3, p3.yzx + 33.33);` → dot 是**标量**，加在三分量上
            float d = Vector3.Dot(p3, new Vector3(p3.Y, p3.Z, p3.X) + new Vector3(33.33f));
            p3 = new Vector3(p3.X + d, p3.Y + d, p3.Z + d);
            return Frac((p3.X + p3.Y) * p3.Z);
        }

        static float Frac(float v) => v - MathF.Floor(v);

        static float Vnoise2(float x, float y) {
            float ix = MathF.Floor(x), iy = MathF.Floor(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash12(ix, iy);
            float b = Hash12(ix + 1f, iy);
            float c = Hash12(ix, iy + 1f);
            float d = Hash12(ix + 1f, iy + 1f);
            return MathUtils.Lerp(MathUtils.Lerp(a, b, fx), MathUtils.Lerp(c, d, fx), fy);
        }

        /// <summary>与 shader 的 `volDensityAt` 同式（同样的竖向剖面 + 高度剪切 + 双八度噪声）。</summary>
        static float DensityAt(float x, float y, float z, float bottomY, float topY,
                               float scale, Vector2 wind, float threshold, float shear) {
            float h = (y - bottomY) / MathF.Max(topY - bottomY, 0.001f);
            if (h < 0f || h > 1f) {
                return 0f;
            }
            float prof = MathUtils.SmoothStep(0f, 0.2f, h) * MathUtils.SmoothStep(1f, 0.75f, h);
            float qx = (x + y * shear * 1.7f) * scale + wind.X;
            float qz = (z + y * shear * 1.1f) * scale + wind.Y;
            float n = Vnoise2(qx, qz) * 0.65f + Vnoise2(qx * 2.7f + 11.3f, qz * 2.7f + 7.1f) * 0.35f;
            return MathF.Max(0f, n - threshold) * prof;
        }

        /// <summary>
        /// 某世界点受到的**云影因子**（1 = 没被云挡，越小越暗）。
        /// `sunDir` 是**指向太阳**的单位向量（与 LOD 其余光照口径一致）。
        /// </summary>
        public static float Factor(float x, float y, float z, Vector3 sunDir, float sunAmount) {
            if (!Enabled || !SkylineRuntime.VolumetricCloudsEnabled || Steps <= 0) {
                return 1f;
            }
            if (sunAmount < MinSunAmount) {
                return 1f;                                  // 夜里：没有"太阳被云挡"
            }
            if (sunDir.Y <= 0.02f) {
                return 1f;                                  // 太阳贴近/低于地平线：射线几乎平行地面
            }
            float bottom = MathF.Min(SkylineRuntime.CloudBandBottomY, SkylineRuntime.CloudBandTopY - 1f);
            float top = MathF.Max(SkylineRuntime.CloudBandTopY, bottom + 1f);
            float scale = MathF.Max(SkylineRuntime.CloudScale, 1e-6f);
            float threshold = Math.Clamp(SkylineRuntime.CloudThreshold, 0f, 0.99f);
            float shear = MathF.Max(SkylineRuntime.CloudHeightShear, 0f);
            float density = MathF.Max(SkylineRuntime.CloudDensity, 0f);
            // shader 里 `u_volWind = CloudWind * Time.RealTime`（BindBand）——必须同一相位，否则影子与云错位
            Vector2 wind = SkylineRuntime.CloudWind * (float)Time.RealTime;

            // 沿太阳方向穿过云带的 t 区间（y 从 bottom 到 top）
            float t0 = (bottom - y) / sunDir.Y;
            float t1 = (top - y) / sunDir.Y;
            if (t1 <= 0f) {
                return 1f;                                  // 整条云带在射线的"背后"（不可能，防御）
            }
            t0 = MathF.Max(t0, 0f);
            if (t1 <= t0) {
                return 1f;
            }
            float dt = (t1 - t0) / Steps;
            float od = 0f;
            for (int i = 0; i < Steps; i++) {
                float t = t0 + dt * (i + 0.5f);
                od += DensityAt(x + sunDir.X * t, y + sunDir.Y * t, z + sunDir.Z * t,
                    bottom, top, scale, wind, threshold, shear);
            }
            od *= dt * density;                             // 与 shader 的 `od *= dt * density` 同口径
            return MathF.Exp(-od);                          // shader 的 alpha = 1 - exp(-od)
        }

        /// <summary>烘进顶点色时的实际乘子：`1 - Depth·(1 - factor)`。</summary>
        public static float ShadeFactor(float factor) =>
            Enabled ? MathUtils.Clamp(1f - Depth * (1f - MathUtils.Clamp(factor, 0f, 1f)), 0f, 1f) : 1f;

        /// <summary>
        /// **确定性自检**（判据可证伪）：固定时刻/固定太阳，采样一条随风吹动的直线上的云影因子 ——
        /// ①必须落在 (0,1] 内；②沿风向移动后**必须发生变化**（否则说明密度场是常数/没接上）。
        /// </summary>
        public static string SelfCheck() {
            Vector3 sun = Vector3.Normalize(new Vector3(0.25f, 0.62f, 0.74f));
            float min = float.MaxValue, max = float.MinValue;
            float sum = 0f;
            const int n = 64;
            for (int i = 0; i < n; i++) {
                float x = 3000f + i * 137f;
                float z = 8000f + i * 211f;
                float f = Factor(x, 90f, z, sun, 1f);
                min = MathF.Min(min, f);
                max = MathF.Max(max, f);
                sum += f;
            }
            float avg = sum / n;
            bool inRange = min > 0f && max <= 1.0001f;
            bool varies = max - min > 0.02f;
            return $"{{\"ok\":{((inRange && varies) ? "true" : "false")},\"min\":{min:0.###}," +
                   $"\"max\":{max:0.###},\"avg\":{avg:0.###},\"depth\":{Depth:0.##}," +
                   $"\"steps\":{Steps},\"enabled\":{(Enabled ? "true" : "false")}," +
                   $"\"note\":\"沿风向移动 64 个采样点必须有变化（否则等于没接上云场）\"}}";
        }
    }
}
