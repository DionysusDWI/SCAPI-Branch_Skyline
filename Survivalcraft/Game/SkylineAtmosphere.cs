using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— **分层云雾 / 天空高度**（v0.0.5，游戏本体内置，非 mod）。
    ///
    /// 要解决的问题（v0.0.4 的已知限制）：世界竖直范围扩到 -1024..1023 之后，
    /// **云层高度仍是原版写死的绝对高度**（`SubsystemSky.DrawClouds` 里 `Lerp(600, 60, r²)`），
    /// 所以在 y=600 以上的高空建筑会"跑到云上面"——抬头没云、低头见云，天空失去了参照。
    ///
    /// 本模块提供两件事，**默认全部关闭（关闭时与原版逐位相同）**：
    ///   1. **云层高度**：把 4 层云的高度改成可配置的 `CloudBaseY`（最外圈/地平线那层）
    ///      ~ `CloudTopY`（头顶那层），并可用 `CloudAltitudeBlend` 让云层**跟随相机高度**——
    ///      跟随口径是"保持与原版一样的相对高度关系"：相机在 y 时，第 i 层在 `y + 原版高度`，
    ///      因此地面上的观感不变（blend=1、y=0 时与原版完全一致），高空则不再"在云上"。
    ///   2. **视图雾/雾带高度**：`FogAltitudeOffsetY`（绝对抬升）+ `FogAltitudeBlend`（跟随相机高度），
    ///      让高空/地下的远景雾仍然按"与你眼睛的相对高度"起效。
    ///
    /// 三条硬口径：
    ///   1. 关闭时**必须与原版逐位一致**：`CloudLayerY` 直接返回原公式，`ApplyFogBand` 直接返回；
    ///   2. 不改世界数据、不改存档、不碰渲染后端（只改渲染时用到的两个数）；
    ///   3. 所有成员不抛异常；状态可用 `Status()` / `Explain(viewY)` 读出，便于桥与自动化测试取证。
    ///
    /// 桥用法（AgentBridge 的反射根 `skylineatmosphere` / `atmosphere` → `Game.SkylineAtmosphere`）：
    ///   {"op":"invoke","target":"atmosphere","member":"Describe","action":"call"}
    ///   {"op":"invoke","target":"atmosphere.Enabled","value":true}
    ///   {"op":"invoke","target":"atmosphere.CloudAltitudeBlend","value":1.0}
    ///   {"op":"invoke","target":"atmosphere","member":"Status","action":"call"}
    ///   {"op":"invoke","target":"atmosphere","member":"Explain","action":"call","args":[1020.0]}
    /// </summary>
    public static class SkylineAtmosphere {
        // ============================================================================================
        // 常量与原版口径
        // ============================================================================================

        /// <summary>云层数（与 SubsystemSky 的 m_cloudsLayerRadii 长度一致）。</summary>
        public const int CloudLayers = 4;

        /// <summary>原版头顶那一层云的高度（半径 0）。</summary>
        public const float VanillaCloudTopY = 600f;

        /// <summary>原版最外圈那一层云的高度（半径 1，地平线附近）。</summary>
        public const float VanillaCloudBaseY = 60f;

        // ============================================================================================
        // 开关与参数（都是运行时状态，默认 = 原版行为）
        // ============================================================================================

        static bool m_enabled;
        static float m_cloudBaseY = VanillaCloudBaseY;
        static float m_cloudTopY = VanillaCloudTopY;
        static float m_cloudAltitudeBlend;
        static float m_fogAltitudeOffsetY;
        static float m_fogAltitudeBlend;
        static readonly float[] m_layerHeightsOverride = new float[CloudLayers];
        static bool m_layerHeightsOverrideActive;

        /// <summary>总开关。false（默认）= 本模块完全不介入渲染，云/雾按原版公式走。</summary>
        public static bool Enabled {
            get => m_enabled;
            set => m_enabled = value;
        }

        /// <summary>最外圈云层高度（原版 60）。只有 <see cref="Enabled"/> 为 true 时生效。</summary>
        public static float CloudBaseY {
            get => m_cloudBaseY;
            set => m_cloudBaseY = Math.Clamp(value, -2048f, 2047f);
        }

        /// <summary>头顶云层高度（原版 600）。只有 <see cref="Enabled"/> 为 true 时生效。</summary>
        public static float CloudTopY {
            get => m_cloudTopY;
            set => m_cloudTopY = Math.Clamp(value, -2048f, 2047f);
        }

        /// <summary>
        /// 云层跟随相机高度的比例：0 = 绝对高度（原版），1 = 完全跟随。
        /// 跟随口径 = "相机高度 + 原版高度"，所以地面观感不变、高空不再"在云上"。中间值线性插值。
        /// </summary>
        public static float CloudAltitudeBlend {
            get => m_cloudAltitudeBlend;
            set => m_cloudAltitudeBlend = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>视图雾带（原版 62~180）整体抬升/下沉的绝对高度偏移。</summary>
        public static float FogAltitudeOffsetY {
            get => m_fogAltitudeOffsetY;
            set => m_fogAltitudeOffsetY = Math.Clamp(value, -4096f, 4096f);
        }

        /// <summary>视图雾带跟随相机高度的比例（0 = 原版绝对高度，1 = 完全跟随）。</summary>
        public static float FogAltitudeBlend {
            get => m_fogAltitudeBlend;
            set => m_fogAltitudeBlend = Math.Clamp(value, 0f, 1f);
        }

        /// <summary>
        /// **逐层**云高（真正意义上的"分层"）：逗号分隔的 4 个数，依次是
        /// 第 0 层（头顶）…第 3 层（最外圈）的**绝对高度**；空串 = 取消逐层覆盖，回到
        /// <see cref="CloudBaseY"/>~<see cref="CloudTopY"/> 的插值口径。
        /// 例：`"300,320,340,360"` = 在 300~360 之间叠出一层厚云带。
        /// </summary>
        public static string LayerHeightsY {
            get => m_layerHeightsOverrideActive
                ? string.Join(",", Array.ConvertAll(m_layerHeightsOverride, v => v.ToString("0.###")))
                : "";
            set {
                if (string.IsNullOrWhiteSpace(value)) {
                    m_layerHeightsOverrideActive = false;
                    return;
                }
                string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != CloudLayers) {
                    m_lastError = $"LayerHeightsY needs {CloudLayers} comma-separated numbers, got {parts.Length}";
                    return;
                }
                float[] parsed = new float[CloudLayers];
                for (int i = 0; i < CloudLayers; i++) {
                    if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out parsed[i])) {
                        m_lastError = $"LayerHeightsY item '{parts[i]}' is not a number";
                        return;
                    }
                    parsed[i] = Math.Clamp(parsed[i], -2048f, 2047f);
                }
                Array.Copy(parsed, m_layerHeightsOverride, CloudLayers);
                m_layerHeightsOverrideActive = true;
                m_lastError = "";
            }
        }

        /// <summary>是否处于"逐层云高"覆盖状态。</summary>
        public static bool LayerHeightsOverrideActive => m_layerHeightsOverrideActive;

        // ============================================================================================
        // 运行观测（给"云到底被画在哪个高度"留证据）
        // ============================================================================================

        static readonly float[] m_lastCloudYs = new float[CloudLayers];
        static bool m_lastFrameSampled;
        static float m_lastViewY;
        static float m_lastFogBottom;
        static float m_lastFogTop;
        static long m_cloudCalls;
        static long m_cloudModified;
        static long m_fogCalls;
        static long m_fogApplied;
        static string m_lastError = "";

        /// <summary>本帧（最近一次绘制）的相机高度。</summary>
        public static float LastViewY => m_lastViewY;

        /// <summary>本帧 4 层云的实际世界高度（按 SubsystemSky 的 layer 下标 0..3）。</summary>
        public static float[] LastCloudYs => m_lastCloudYs;

        /// <summary>本帧实际使用的雾带上下界（原版值 + 本模块的偏移）。</summary>
        public static float LastFogBottom => m_lastFogBottom;

        public static float LastFogTop => m_lastFogTop;

        /// <summary>云层高度查询次数（累计，含关闭时的只读记录）。</summary>
        public static long CloudCalls => m_cloudCalls;

        /// <summary>云层高度**被本模块改写**的次数（累计；关闭或 blend=0 时为 0）。</summary>
        public static long CloudModified => m_cloudModified;

        public static long FogCalls => m_fogCalls;

        /// <summary>雾带**被本模块改写**的次数（累计）。</summary>
        public static long FogApplied => m_fogApplied;

        public static string LastError => m_lastError;

        // ============================================================================================
        // 渲染侧入口（SubsystemSky 调用）
        // ============================================================================================

        /// <summary>
        /// 云层实际绘制高度。**关闭时返回原版公式**；打开后返回配置高度（可跟随相机）。
        /// </summary>
        /// <param name="layer">层下标 0..3（0 = 头顶，3 = 最外圈）。</param>
        /// <param name="radius">SubsystemSky 的原版层半径（0 / 0.8 / 0.95 / 1）。</param>
        /// <param name="viewPosition">相机位置。</param>
        public static float CloudLayerY(int layer, float radius, Vector3 viewPosition) {
            float vanilla = MathUtils.Lerp(VanillaCloudTopY, VanillaCloudBaseY, radius * radius);
            try {
                // 关闭时也记录：这样"原版把云画在 600..60"本身就能被 Status() 取证
                if (!m_enabled) {
                    RecordCloud(layer, viewPosition.Y, vanilla);
                    return vanilla;
                }
                float y = LayerYAt(layer, radius, viewPosition.Y, vanilla);
                if (y != vanilla) {
                    m_cloudModified++;
                }
                RecordCloud(layer, viewPosition.Y, y);
                return y;
            }
            catch (Exception ex) {
                m_lastError = $"CloudLayerY failed (vanilla value used): {ex.GetType().Name}: {ex.Message}";
                RecordCloud(layer, viewPosition.Y, vanilla);
                return vanilla;
            }
        }

        /// <summary>
        /// 视图雾带上/下界的修正。**关闭时什么都不做**（`bottom`/`top` 保持原版）。
        /// </summary>
        public static void ApplyFogBand(ref float bottom, ref float top, Vector3 viewPosition) {
            try {
                if (m_enabled) {
                    float offset = m_fogAltitudeOffsetY;
                    if (m_fogAltitudeBlend > 0f) {
                        offset += viewPosition.Y * m_fogAltitudeBlend;
                    }
                    if (offset != 0f) {
                        bottom += offset;
                        top += offset;
                        m_fogApplied++;
                    }
                }
                // 关闭时也记录原版值，便于测试对比"介入前/后"
                m_fogCalls++;
                m_lastFogBottom = bottom;
                m_lastFogTop = top;
            }
            catch (Exception ex) {
                m_lastError = $"ApplyFogBand failed (vanilla value used): {ex.GetType().Name}: {ex.Message}";
            }
        }

        static void RecordCloud(int layer, float viewY, float y) {
            m_cloudCalls++;
            m_lastViewY = viewY;
            m_lastFrameSampled = true;
            if (layer >= 0 && layer < m_lastCloudYs.Length) {
                m_lastCloudYs[layer] = y;
            }
        }

        /// <summary>某一层在给定相机高度下的最终高度（CloudLayerY / Explain 共用同一套口径）。</summary>
        static float LayerYAt(int layer, float radius, float viewY, float vanilla) {
            float configured = m_layerHeightsOverrideActive && layer >= 0 && layer < CloudLayers
                ? m_layerHeightsOverride[layer]
                : MathUtils.Lerp(m_cloudTopY, m_cloudBaseY, radius * radius);
            if (m_cloudAltitudeBlend <= 0f) {
                return configured;
            }
            // 跟随口径：相机高度 + 原版高度（保持与原版相同的相对关系）
            float follow = viewY + vanilla;
            return MathUtils.Lerp(configured, follow, m_cloudAltitudeBlend);
        }

        // ============================================================================================
        // 便捷预设与复位（给脚本/桥用，避免一长串属性赋值）
        // ============================================================================================

        /// <summary>打开"云层/雾都跟随相机高度"的高空预设（blend 全开，基准高度 = 原版）。</summary>
        public static string HighAltitudePreset() {
            m_cloudBaseY = VanillaCloudBaseY;
            m_cloudTopY = VanillaCloudTopY;
            m_layerHeightsOverrideActive = false;
            m_cloudAltitudeBlend = 1f;
            m_fogAltitudeOffsetY = 0f;
            m_fogAltitudeBlend = 1f;
            m_enabled = true;
            return Describe();
        }

        /// <summary>恢复原版口径并关闭总开关。</summary>
        public static string Reset() {
            m_enabled = false;
            m_cloudBaseY = VanillaCloudBaseY;
            m_cloudTopY = VanillaCloudTopY;
            m_layerHeightsOverrideActive = false;
            m_cloudAltitudeBlend = 0f;
            m_fogAltitudeOffsetY = 0f;
            m_fogAltitudeBlend = 0f;
            // 观测一起归零：这样"关闭时不介入渲染"可以用 cloudModified==0 直接判据（见 AT1）
            m_cloudCalls = 0;
            m_cloudModified = 0;
            m_fogCalls = 0;
            m_fogApplied = 0;
            m_lastFrameSampled = false;
            Array.Clear(m_lastCloudYs);
            m_lastFogBottom = 0f;
            m_lastFogTop = 0f;
            m_lastError = "";
            return Describe();
        }

        // ============================================================================================
        // 自述 / 取证
        // ============================================================================================

        public static string Describe() =>
            $"SkylineAtmosphere v1 enabled={m_enabled} cloud={m_cloudBaseY}..{m_cloudTopY} "
            + $"cloudBlend={m_cloudAltitudeBlend:0.##} fogOffset={m_fogAltitudeOffsetY} fogBlend={m_fogAltitudeBlend:0.##} "
            + $"lastViewY={(m_lastFrameSampled ? m_lastViewY.ToString("0.#") : "n/a")} "
            + $"lastCloudYs=[{string.Join(",", Array.ConvertAll(m_lastCloudYs, v => v.ToString("0.#")))}] "
            + $"calls=cloud:{m_cloudCalls}(modified {m_cloudModified})/fog:{m_fogCalls}(applied {m_fogApplied}) "
            + $"lastError=\"{m_lastError}\"";

        /// <summary>完整状态（JSON 字符串）。</summary>
        public static string Status() => StatusObject().ToJsonString();

        /// <summary>
        /// 对指定相机高度**预演**云层高度（不改任何状态，纯粹算给测试/脚本看）。
        /// 返回 JSON：每层的 {layerIndex, radius, vanillaY, currentY}。
        /// </summary>
        public static string Explain(float viewY) {
            JsonObject root = StatusObject();
            float[] radii = [0f, 0.8f, 0.95f, 1f];
            JsonArray layers = [];
            for (int i = 0; i < radii.Length; i++) {
                float radius = radii[i];
                float vanilla = MathUtils.Lerp(VanillaCloudTopY, VanillaCloudBaseY, radius * radius);
                float current = m_enabled ? LayerYAt(i, radius, viewY, vanilla) : vanilla;
                layers.Add(new JsonObject {
                    ["layerIndex"] = i,
                    ["radius"] = radius,
                    ["vanillaY"] = Math.Round(vanilla, 3),
                    ["currentY"] = Math.Round(current, 3)
                });
            }
            root["viewY"] = viewY;
            root["layers"] = layers;
            return root.ToJsonString();
        }

        static JsonObject StatusObject() => new() {
            ["enabled"] = m_enabled,
            ["cloudBaseY"] = m_cloudBaseY,
            ["cloudTopY"] = m_cloudTopY,
            ["layerHeightsY"] = LayerHeightsY,
            ["layerHeightsOverrideActive"] = m_layerHeightsOverrideActive,
            ["cloudAltitudeBlend"] = m_cloudAltitudeBlend,
            ["fogAltitudeOffsetY"] = m_fogAltitudeOffsetY,
            ["fogAltitudeBlend"] = m_fogAltitudeBlend,
            ["vanillaCloudBaseY"] = VanillaCloudBaseY,
            ["vanillaCloudTopY"] = VanillaCloudTopY,
            ["sampled"] = m_lastFrameSampled,
            ["lastViewY"] = m_lastFrameSampled ? m_lastViewY : (float?)null,
            ["lastCloudYs"] = new JsonArray([.. Array.ConvertAll(m_lastCloudYs, v => (JsonNode)Math.Round(v, 3))]),
            ["lastFogBottom"] = m_lastFogBottom,
            ["lastFogTop"] = m_lastFogTop,
            ["cloudCalls"] = m_cloudCalls,
            ["cloudModified"] = m_cloudModified,
            ["fogCalls"] = m_fogCalls,
            ["fogApplied"] = m_fogApplied,
            ["lastError"] = m_lastError
        };
    }
}
