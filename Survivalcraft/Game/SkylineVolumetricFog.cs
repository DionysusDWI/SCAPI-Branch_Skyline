using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.69：**自研体积雾（替换游戏原有 Fog）** —— 里程碑 3.3 的另一半。
    ///
    /// 用户口径（逐字）："**体积云=体积雾=需要替换游戏原有 Fog 的自研体积云雾**"。
    ///
    /// v0.1.47 起我们把原版雾**关掉**了（`FogDisabled` 把雾带密度与地平线霾密度置 0），
    /// 但"关掉"不等于"**替换**"：这一版补上替换品 —— 一个在**不透明片元**里沿视线步进的体积雾。
    ///
    /// ## 为什么放在不透明片元里（而不是像云那样放在天空穹顶）
    /// 雾是**贴着地形**的：必须逐像素知道"到这一点的距离"，而地形片元天然带世界坐标；
    /// 穹顶片元没有这个信息（它在 1800 m 处，拿不到被遮挡地形的距离）。
    /// 而且地形 pass 的片元已经被深度测试筛过 → 雾天然只作用在真正可见的地形上。
    ///
    /// ## 与阴影变体共用同一个 shader（这是有意的）
    /// 体积雾与阴影采样都注入**同一个**不透明变体（`SkylineGpuShadowSample`）。
    /// 本版顺手解耦了一个限制：**以前只有在"阴影采样已启用且有深度图"时才会用变体**，
    /// 于是"只想开体积雾"也会被挡住。现在变体条件是
    /// `(阴影采样且有图) 或 (体积雾启用)`；没有深度图时绑一张 1×1 占位纹理并把 `u_shadowEnable` 置 0。
    ///
    /// ## 硬口径
    ///   1. 关掉时必须**与关闭前逐位一致**（不介入任何 uniform 的语义变化）；
    ///   2. 不改世界数据、不改存档；
    ///   3. 不抛异常：绑定失败自动关掉自己并记 `lastError`。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>体积雾总开关。**默认开**（用户口径是"替换原有 Fog"）。</summary>
        public static bool VolumetricFogEnabled { get; set; } = true;

        /// <summary>雾带底高（米）。默认 0（贴地）。</summary>
        public static float FogBottomY { get; set; }

        /// <summary>雾带顶高（米）。默认 150 —— 一条**薄**的贴地雾，不是"整片天糊住"。</summary>
        public static float FogTopY { get; set; } = 150f;

        /// <summary>消光系数（每米）。实测扫描（雾开/关差分）：0.004 ≈ 看不出（雪原底色与雾色接近）、
        /// 0.03 = 0.53% 像素、**0.06 = 远处明显变淡且不过火**、0.10 = 40.97% 像素（太强）。
        /// ⇒ 取 **0.06**。</summary>
        public static float FogDensity { get; set; } = 0.06f;

        /// <summary>噪声频率（1/米）。0.004 ≈ 250 m 一个结构。</summary>
        public static float FogScale { get; set; } = 0.004f;

        /// <summary>覆盖率阈值：低于它就无雾（越大雾越少、越成团）。</summary>
        public static float FogThreshold { get; set; } = 0.42f;

        /// <summary>最终不透明度上限。</summary>
        public static float FogStrength { get; set; } = 0.90f;

        /// <summary>风向/风速（米/秒）。</summary>
        public static Vector2 FogWind { get; set; } = new(4f, 1.5f);

        /// <summary>雾色（默认取偏冷的浅灰蓝，和雪原/天空都能接上）。</summary>
        public static Vector3 FogColor { get; set; } = new(0.78f, 0.82f, 0.88f);

        /// <summary>步进最远距离（米）。雾是近程效果，不需要拉很远。</summary>
        public static float FogMaxDistance { get; set; } = 1200f;

        /// <summary>噪声随高度剪切的强度。</summary>
        public static float FogHeightShear { get; set; } = 0.8f;

        /// <summary>步数（**固定 8**，与云同一口径：手工展开）。</summary>
        public static int FogSteps { get; } = 8;

        static string m_volFogLastError = "";
        static long m_volFogBound;

        public static string VolumetricFogDescribe() {
            JsonObject o = new() {
                ["enabled"] = VolumetricFogEnabled,
                ["bottomY"] = (double)FogBottomY,
                ["topY"] = (double)FogTopY,
                ["density"] = (double)FogDensity,
                ["scale"] = (double)FogScale,
                ["threshold"] = (double)FogThreshold,
                ["strength"] = (double)FogStrength,
                ["wind"] = new JsonArray(FogWind.X, FogWind.Y),
                ["color"] = new JsonArray(FogColor.X, FogColor.Y, FogColor.Z),
                ["maxDistance"] = (double)FogMaxDistance,
                ["shear"] = (double)FogHeightShear,
                ["steps"] = FogSteps,
                ["boundFrames"] = m_volFogBound,
                ["lastError"] = m_volFogLastError
            };
            o["note"] = "在不透明片元里沿视线步进（替换被 FogDisabled 置 0 的原版雾）；与阴影采样共用同一个不透明变体";
            return o.ToJsonString();
        }
    }
}
