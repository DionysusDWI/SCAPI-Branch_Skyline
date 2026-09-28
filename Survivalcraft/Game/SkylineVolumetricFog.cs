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

        /// <summary>[v0.1.117] **霾 / 光轴解耦**（默认 true = v0.1.116 的行为）。
        /// 为什么要它：用户口径是"**可以把 Fog 关掉以便于测试光影的视觉效果**"，而本分支的
        /// **体积神光是长在体积雾的同一次 8 步步进里的** —— `vfAlpha`（= 密度积分 → 不透明度）
        /// 同时决定"雾色混白"与"光轴亮度"两件事 ⇒ 关雾就把最显眼的光影效果一起关掉了。
        /// 关掉这一项 = **保留光轴与其它一切**，只把"雾色混白"那一步的权重乘 0（着色器里的 `u_vfHaze`）；
        /// 于是"关雾之后的光影"不至于连光轴都没了。判据见 `heightlab/skyline-v0117-haze-godrays.py`。</summary>
        public static bool VolumetricHazeEnabled { get; set; } = true;

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

        /// <summary>[v0.1.118] **基础密度**（0..1，默认见下）。为什么必须有它（实测）：
        /// 旧式 `max(0, 噪声 − 阈值)` 是**纯云团掩膜** —— 噪声是 250 m 尺度的**平滑**场，
        /// 相机一旦落进"晴空"那一片，**几百米内密度恒为 0**：2026-09-28 实测该机位上
        /// 体积雾只覆盖 **0.19% 帧面积**（把雾色改成纯红、密度拉到 0.5 也一样），
        /// 而"用自研体积雾替换原有 Fog"这条里程碑的原版雾实测是 **32.4% 帧面积**。
        /// 加一个常数底之后，雾**始终存在**（像真实大气霾），云团噪声只调制浓淡。
        ///
        /// 口径：**0 = 逐位回 v0.1.117 的行为**（可 A/B）；着色器里是
        /// `max(u_vfBase, max(0, 噪声 − 阈值)) × 高度剖面`。
        ///
        /// **默认 0.10 是扫描出来的**（`heightlab/skyline-v0118-fog-visibility.py`，2026-09-28）：
        /// | base | 覆盖探针（纯红/密度0.5） | 产品默认档"雾开/关"像素差 | 平均亮度位移 |
        /// |---|---|---|---|
        /// | 0 | 0.27% | 0.07%~0.10% | +0.03 |
        /// | 0.02 | 21.7% | 0.30%~0.34% | +0.8~1.1 |
        /// | 0.05 | 58.6% | 2.9%~6.4% | +2.0~2.6 |
        /// | **0.10** | **70.0%** | **23.5%~26.2%** | **+4.0~5.0** |
        /// | 0.2 | 70.0% | 31.1%~31.6% | +7.5~9.6（偏白） |
        /// ⇒ 取 **0.10**：雾"始终存在"（原版雾的 32.4% 同一量级），亮度位移仍在 6/255 以内；
        /// 代价在噪声内（base 0 vs 0.2 配对交替 **−0.41%**，步进次数不变）。</summary>
        public static float FogBaseDensity { get; set; } = 0.10f;

        /// <summary>最终不透明度上限。</summary>
        public static float FogStrength { get; set; } = 0.90f;

        /// <summary>风向/风速（米/秒）。</summary>
        public static Vector2 FogWind { get; set; } = new(4f, 1.5f);

        /// <summary>雾色（默认取偏冷的浅灰蓝，和雪原/天空都能接上）。</summary>
        public static Vector3 FogColor { get; set; } = new(0.78f, 0.82f, 0.88f);

        /// <summary>[v0.1.70] 雾色与**游戏按天空/天气算出来的雾色**（`u_fogColor`）的混合比例。
        /// 0 = 只用上面的固定色；1 = 完全跟随天空/天气。默认 0.75 ——
        /// 这样下雨/下雪/黄昏时雾会跟着变色，而不是永远一个灰蓝。</summary>
        public static float FogSkyMix { get; set; } = 0.75f;

        /// <summary>步进最远距离（米）。雾是近程效果，不需要拉很远。</summary>
        public static float FogMaxDistance { get; set; } = 1200f;

        /// <summary>噪声随高度剪切的强度。</summary>
        public static float FogHeightShear { get; set; } = 0.8f;

        /// <summary>步数（**固定 8**，与云同一口径：手工展开）。</summary>
        public static int FogSteps { get; } = 8;

        /// <summary>[v0.1.104] **体积神光（体积雾里的单次散射）**强度。0 = 关（逐位回到 v0.1.103）。
        ///
        /// 口径来源：Dawnlight 的 `ShaftLighting.psh`（屏幕空间沿太阳方向的光轴 + Bayer 抖动）。
        /// **我们没有场景深度纹理** ⇒ 不能照搬屏幕空间那一路；这里走的是**适配路线**：
        /// 复用我们已经在做的**世界空间雾积分**（8 步），每一步问一次"这一小段烟有没有被太阳照到"
        /// （投影进太阳深度图做一次遮挡判定），再乘**前向散射相位**累加。
        /// 于是"从树影/建筑缝隙里漏进来的光柱"是**几何上真的**由阴影图算出来的，不是屏幕空间的假象。
        ///
        /// 代价：雾开启时每片元多 **8 次阴影图采样**（单抽，不做 PCF）。
        /// </summary>
        /// 默认值 **0.7 是量出来的**（可复算：`heightlab/skyline-v0104-sun-shafts.py`）：
        ///   * **0.35**：朝太阳增益 **+0.526 亮度**、背对 **+0.023**（方向比 23×），但只有 **0.13%** 像素过 8/255 ⇒ 偏弱；
        ///   * **0.7**：朝太阳 **+1.049 亮度 / 39,955 px（2.8%）**、背对 **+0.054 / 2,235 px**（方向比 19×）⇒ 看得见且仍只朝太阳方向；
        ///   * **代价**：配对交替测（0/0.7 各两轮、每轮 15 s、关垂直同步）**−0.60%**（在跑步动噪声内）。
        /// 归一化口径（第一版踩到并修）：不是 `散射积分 × 光深`（∝ 密度²，默认密度 0.06 下增量 &lt;8/255 完全看不见），
        /// 而是 **「雾里被照到的比例」× 雾的不透明度 × 前向相位** ⇒ 与雾的浓淡解耦。
        /// **如实记**：本机测试场地是 y≈310 的高台、视野里没有"挡住太阳的几何"，
        /// 所以现在这副效果表现为**朝太阳时雾里的前向散射辉光**；
        /// 经典的"光柱"（缝隙里漏下来的柱状光）需要雾 + 遮挡物同框的场景，**下一轮补**。
        public static float VolumetricSunShaftStrength { get; set; } = 0.7f;

        /// <summary>[v0.1.104] 神光的入射色（默认偏暖，和太阳直射一致）。</summary>
        public static Vector3 VolumetricSunShaftColor { get; set; } = new(1f, 0.93f, 0.80f);

        /// <summary>[v0.1.104] 前向散射相位指数（越大"朝太阳看才亮"越明显）。默认 8。</summary>
        public static float VolumetricSunShaftPhasePower { get; set; } = 8f;

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
                ["skyMix"] = (double)FogSkyMix,
                ["maxDistance"] = (double)FogMaxDistance,
                ["shear"] = (double)FogHeightShear,
                ["steps"] = FogSteps,
                ["sunShaft"] = (double)VolumetricSunShaftStrength,
                ["sunShaftColor"] = new JsonArray(VolumetricSunShaftColor.X, VolumetricSunShaftColor.Y,
                                                 VolumetricSunShaftColor.Z),
                ["sunShaftPhasePower"] = (double)VolumetricSunShaftPhasePower,
                ["boundFrames"] = m_volFogBound,
                ["lastError"] = m_volFogLastError
            };
            o["note"] = "在不透明片元里沿视线步进（替换被 FogDisabled 置 0 的原版雾）；与阴影采样共用同一个不透明变体";
            o["sunShaftNote"] = "体积神光 = 沿同一条雾积分射线每步做一次太阳遮挡判定（单抽）"
                + "× 前向散射相位；0 即逐位回 v0.1.103。Dawnlight 用屏幕空间光轴，我们没有场景深度纹理，走的是适配路线";
            return o.ToJsonString();
        }
    }
}
