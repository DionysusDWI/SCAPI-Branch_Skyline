using Engine;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.65：**太阳追踪（统一光源方向）** —— 里程碑 3.4。
    ///
    /// 用户口径（逐字）："**太阳追踪功能很重要，不要让光源点偏离太阳**"。
    ///
    /// 先说清一个容易误判的事实：**游戏本体并不追踪太阳**。
    /// `LightingManager.DirectionToLight1/2` 是 `readonly` 的**常量**（(0.12,0.25,0.34) 与 (-0.12,0.25,-0.34)），
    /// 昼夜只体现在天空色/雾/太阳贴图上 —— 所以任何"拿 DirectionToLight1 当太阳"的实现
    /// （包括本项目 v0.1.32~v0.1.64 的阴影）**阴影方向永远是固定的**，太阳转过一圈也不会动。
    ///
    /// 本文件给出**唯一的太阳真值** `SunDirectionToSky()`：公式与 `SubsystemSky.DrawSunAndMoon`
    /// 画天上那个太阳时用的**完全是同一套**（天球角 + 季节倾角），所以"光源点"与"看见的太阳"不可能偏离。
    /// 阴影捕获（`SkylineGpuShadow`）与地形顶点阴影烘焙（`SkylineTerrainShadow`）都改为取它。
    ///
    /// 夜里太阳在地平线下，此时按**月亮**（太阳的正对面）取方向 —— 与游戏同时画月亮的做法一致。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>[v0.1.65] **光源方向跟着太阳走**（默认开）。关掉 = 回到 v0.1.64 的固定光
        /// （`LightingManager.DirectionToLight1`），用于 A/B 与回退。</summary>
        public static bool SunTracking { get; set; } = true;

        /// <summary>[v0.1.65] 太阳落到地平线下时改用**月亮**方向（= 太阳的正对面）。默认开。</summary>
        public static bool SunUseMoonAtNight { get; set; } = true;

        static SubsystemSky FindSkySubsystem() => GameManager.Project?.FindSubsystem<SubsystemSky>(true);

        static SubsystemTimeOfDay FindTimeOfDaySubsystem() =>
            GameManager.Project?.FindSubsystem<SubsystemTimeOfDay>(true);

        /// <summary>
        /// 指向**太阳**的单位向量（可以在地平线以下 —— 夜里就是）。
        /// 取不到子系统时退回固定光方向，绝不返回 NaN。
        /// </summary>
        public static Vector3 SunDirectionToSky() {
            SubsystemSky sky = FindSkySubsystem();
            SubsystemTimeOfDay timeOfDay = FindTimeOfDaySubsystem();
            if (sky == null || timeOfDay == null) {
                Vector3 fallback = LightingManager.DirectionToLight1;
                return fallback.LengthSquared() > 1e-8f ? Vector3.Normalize(fallback) : Vector3.UnitY;
            }
            // 与 SubsystemSky.DrawSunAndMoon 逐行同式。**注意别把太阳的角度和月亮的搞混**：
            //   num   = 2π(TimeOfDay − Midday)   ← 太阳用这个（QueueCelestialBody(batch2, …) 传的是 num）
            //   angle = num + π                 ← 月亮用这个（batch3 传的是 angle）
            // 第一版我用了 angle（月亮），结果正午的太阳在地平线下 64° —— 实测一眼就露馅。
            // 太阳 = UnitY 先绕 Z 转 −num、再绕 X 转季节角。
            float num = (float)Math.PI * 2f * (timeOfDay.TimeOfDay - timeOfDay.Midday);
            Matrix m = Matrix.CreateRotationZ(0f - num) * Matrix.CreateRotationX(sky.CalculateSeasonAngle());
            Vector3 v = Vector3.TransformNormal(Vector3.UnitY, m);
            return v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.UnitY;
        }

        /// <summary>
        /// 阴影 / 光照系统应当使用的**光源方向**（单位向量，**指向光源**；与 v0.1.32 起
        /// `u_sunDir` 的约定一致：深度 `d = dot(eye − world, sunDir)`，更靠近光源 = 深度更小）。
        /// </summary>
        public static Vector3 TrackedLightDirection() {
            if (!SunTracking) {
                Vector3 fixedLight = LightingManager.DirectionToLight1;
                return fixedLight.LengthSquared() > 1e-8f ? Vector3.Normalize(fixedLight) : Vector3.UnitY;
            }
            Vector3 toSun = SunDirectionToSky();
            if (toSun.Y < 0f && SunUseMoonAtNight) {
                toSun = -toSun;
            }
            return toSun;
        }

        /// <summary>太阳/月亮是否在地平线上（`TrackedLightDirection().Y > 0` 恒成立，所以看这个）。</summary>
        public static bool SunIsUp => SunDirectionToSky().Y > 0f;

        static float AzimuthDegrees(Vector3 v) {
            float deg = MathF.Atan2(v.Z, v.X) * 180f / (float)Math.PI;
            return deg < 0f ? deg + 360f : deg;
        }

        public static string SunDescribe() {
            JsonObject result = new();
            SubsystemSky sky = FindSkySubsystem();
            SubsystemTimeOfDay timeOfDay = FindTimeOfDaySubsystem();
            Vector3 toSun = SunDirectionToSky();
            Vector3 light = TrackedLightDirection();
            result["tracking"] = SunTracking;
            result["useMoonAtNight"] = SunUseMoonAtNight;
            result["timeOfDay"] = timeOfDay == null ? null : (double)timeOfDay.TimeOfDay;
            result["midday"] = timeOfDay == null ? null : (double)timeOfDay.Midday;
            result["sunIsUp"] = SunIsUp;
            result["seasonAngleDeg"] = sky == null ? null : (double)(sky.CalculateSeasonAngle() * 180f / (float)Math.PI);
            result["toSun"] = new JsonArray(toSun.X, toSun.Y, toSun.Z);
            result["toSunElevationDeg"] = MathF.Asin(Math.Clamp(toSun.Y, -1f, 1f)) * 180f / (float)Math.PI;
            result["toSunAzimuthDeg"] = AzimuthDegrees(toSun);
            result["lightDir"] = new JsonArray(light.X, light.Y, light.Z);
            result["lightElevationDeg"] = MathF.Asin(Math.Clamp(light.Y, -1f, 1f)) * 180f / (float)Math.PI;
            result["fixedLight1"] = new JsonArray(LightingManager.DirectionToLight1.X,
                LightingManager.DirectionToLight1.Y, LightingManager.DirectionToLight1.Z);
            result["dotWithFixedLight"] = (double)Vector3.Dot(light, Vector3.Normalize(LightingManager.DirectionToLight1));
            result["source"] = "SubsystemSky.DrawSunAndMoon 同式（天球角 + 季节倾角）";
            return result.ToJsonString();
        }

        /// <summary>
        /// **[测试用]** 把一天中的时刻拨到 `timeOfDay`（0..1），返回拨动前后的偏移量以便**原样还原**。
        /// 实现上改的是 `SubsystemTimeOfDay.TimeOfDayOffset`（世界存档里的值），不是"另编一套时间"。
        /// **必须同时把 `TimeOfDayMode` 切成 `Changing`**，否则 `TimeOfDay` 恒等于 `Midday`（固定白天世界的取值），
        /// 拨了等于没拨 —— 第一版就踩了这个（实测返回 `timeMode=Day`、`nowTimeOfDay` 一直是 Midday）。
        /// 返回里带 `previousMode`，测试完请用 `SunSetTimeOfDayMode(previousMode)` 还原。
        /// </summary>
        public static string SunSetTimeOfDay(double timeOfDay) {
            JsonObject result = new();
            SubsystemTimeOfDay time = FindTimeOfDaySubsystem();
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (time == null || info == null) {
                result["ok"] = false;
                result["err"] = "no timeOfDay/gameInfo subsystem";
                return result.ToJsonString();
            }
            double previous = time.TimeOfDayOffset;
            string previousMode = info.WorldSettings.TimeOfDayMode.ToString();
            double duration = Math.Max(time.DayDuration, 1f);
            double u = info.TotalElapsedGameTime / duration;
            u -= Math.Floor(u);
            if (info.WorldSettings.TimeOfDayMode != TimeOfDayMode.Changing) {
                info.WorldSettings.TimeOfDayMode = TimeOfDayMode.Changing;
            }
            time.TimeOfDayOffset = timeOfDay - time.DayStart - u;
            result["ok"] = true;
            result["requested"] = timeOfDay;
            result["nowTimeOfDay"] = (double)time.TimeOfDay;
            result["previousOffset"] = previous;
            result["newOffset"] = time.TimeOfDayOffset;
            result["previousMode"] = previousMode;
            result["timeMode"] = info.WorldSettings.TimeOfDayMode.ToString();
            return result.ToJsonString();
        }

        /// <summary>**[测试用]** 还原 `TimeOfDayMode`（配合 `SunSetTimeOfDay` 的 `previousMode`）。</summary>
        public static string SunSetTimeOfDayMode(string mode) {
            JsonObject result = new();
            SubsystemGameInfo info = GameManager.Project?.FindSubsystem<SubsystemGameInfo>(true);
            if (info == null) {
                result["ok"] = false;
                result["err"] = "no gameInfo subsystem";
                return result.ToJsonString();
            }
            string previous = info.WorldSettings.TimeOfDayMode.ToString();
            if (!Enum.TryParse(mode, ignoreCase: true, out TimeOfDayMode parsed)) {
                result["ok"] = false;
                result["err"] = $"unknown TimeOfDayMode '{mode}'";
                return result.ToJsonString();
            }
            info.WorldSettings.TimeOfDayMode = parsed;
            result["ok"] = true;
            result["previous"] = previous;
            result["now"] = info.WorldSettings.TimeOfDayMode.ToString();
            return result.ToJsonString();
        }
    }
}
