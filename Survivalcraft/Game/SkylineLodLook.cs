using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.109：**按 Distant Horizons 的规格实装 LOD 的"观感"两项**（里程碑 2 的新口径：
    /// "全面学习 DH，源码可复用与迁移，尽可能做到同等规格"）。两条都来自 DH 的着色器（**本机已克隆源码**，
    /// 见 `.resource/refs/distant-horizons/coreSubProjects/core/src/main/resources/shaders/`）：
    ///
    ///   1. **`ditherDhFade`（抖动淡出）** —— `flat_shaded.frag`：
    ///      `worldNoise = bayerMatrix4x4(gl_FragCoord.xy) + 0.001;`
    ///      `fadeStep = smoothstep(uClipDistance, uClipDistance*1.5, viewDist);`
    ///      `if (fadeStep &lt;= worldNoise) discard;`
    ///      效果：**靠近时按屏幕空间抖动概率把 LOD 片元丢掉**，于是 LOD 与真地形的交界是"渐隐"而不是"硬切"。
    ///      我们**不用 4×4 Bayer 常量数组**（本引擎的 GLES 路径不接受 const 数组，v0.1.102 踩过），
    ///      改用 **Iris 光影包 Complementary 的 IGN**（`fract(52.9829189*fract(0.06711056x+0.00583715y))`）
    ///      —— 同类"屏幕空间稳定有序抖动"，且出自本轮的主要参考对象。
    ///
    ///   2. **噪声补细节**（`applyNoise`，DH 配置项 `noiseSteps=4 / noiseIntensity=5 / noiseDropoff=1024`）：
    ///      片元里按**量化过的世界坐标**取随机数，把颜色往白/黑推一点：
    ///      `amp = intensity*0.01 * (1-(2*lum-1)^2) * alpha`（暗/亮两端减弱），
    ///      `newCol = c + (1-c) * (rand(qState)*2*amp - amp)`，
    ///      再按 `mix(newCol, c, min(dist/noiseDropoff,1))` **随距离淡出**。
    ///      效果：粗单元不再是一整块平色，近处"看起来有细节"。
    ///
    /// 硬口径：`Enabled=false` 时两个 uniform 都不参与（逐位回 v0.1.108）；不抛异常；参数可从桥读写。
    /// </summary>
    public static class SkylineLodLook {
        /// <summary>总开关（默认 **true**：这就是"与 DH 同等规格"的默认；关掉逐位回 v0.1.108）。</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>抖动淡出（DH `ditherDhFade` 默认 **true**）。</summary>
        public static bool DitherFade { get; set; } = true;

        /// <summary>抖动淡出的起点距离（米）。DH 用的是 `overdrawPrevention × 原版视距`；
        /// 我们默认取"原版视距"（即 LOD 的内边界），到它 ×1.5 处完全淡出。</summary>
        public static float DitherFadeStartMetres { get; set; }

        /// <summary>噪声补细节（DH `noiseIntensity &gt; 0` 即开，默认 5）。</summary>
        public static bool NoiseEnabled { get; set; } = true;

        /// <summary>DH `noiseSteps`（默认 4）：噪声在**世界坐标的 1/4 格**上量化 —— 越大越细碎。</summary>
        public static float NoiseSteps { get; set; } = 4f;

        /// <summary>DH `noiseIntensity`（默认 5，即 5%）。</summary>
        public static float NoiseIntensity { get; set; } = 5f;

        /// <summary>DH `noiseDropoff`（默认 1024 m）：超过它噪声完全消失；0 = 不淡出。</summary>
        public static float NoiseDropoff { get; set; } = 1024f;

        public static string Describe() {
            return new JsonObject {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["ditherFade"] = DitherFade,
                ["ditherFadeStartMetres"] = (double)DitherFadeStartMetres,
                ["noiseEnabled"] = NoiseEnabled,
                ["noiseSteps"] = (double)NoiseSteps,
                ["noiseIntensity"] = (double)NoiseIntensity,
                ["noiseDropoff"] = (double)NoiseDropoff,
                ["source"] = "Distant Horizons 源码（本机克隆）：shaders/flat_shaded.frag 的 ditherDhFade/applyNoise；"
                    + "抖动函数改用 Iris 光影包 Complementary 的 IGN（避开本引擎 GLES 不接受 const 数组的限制）",
                ["dhDefaults"] = new JsonObject {
                    ["ditherDhFade"] = true, ["noiseSteps"] = 4, ["noiseIntensity"] = 5, ["noiseDropoff"] = 1024
                }
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.LodLook*`。</summary>
    public static partial class SkylineRuntime {
        public static string LodLook(bool enabled) {
            SkylineLodLook.Enabled = enabled;
            return SkylineLodLook.Describe();
        }

        public static string LodLookInfo() => SkylineLodLook.Describe();
    }
}
