using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.35：**测试用显示控制**。
    ///
    ///   * `FogDisabled`（默认 false）——把"视距雾/地平线霾"的密度清零，让远景与光影实验看得清。
    ///     用户口径（2026-09-27）："可以将 Fog 关掉以便于测试光影的视觉效果"。
    ///     实现只改**雾的密度参数**，不动地形/光照数据，关掉开关即逐位恢复。
    ///   * `CloseDialogs()` —— 强制关掉当前所有对话框（自动化测试用）。原版 `DialogsManager.HideAllDialogs()`
    ///     会在某些对话框上抛 NullReference，这里直接从管理器列表摘除 + 从父控件移除，绕开动画队列。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>
        /// [v0.1.35] 关雾开关；**[v0.1.47] 默认改为 true** —— 用户口径：
        /// "可以移除游戏原有内置雾气效果和视距边缘渐变效果，后续如有需要则配合光影系统独立开发"。
        /// 置 false 即回到游戏原版雾（含视距边缘霾带）。
        ///
        /// **[v0.1.111] 覆盖范围补全**：原来有 4 个 pass（粒子 / 移动方块 / 挖掘裂纹 / 方块选中框）
        /// 把 `SubsystemSky.View*` **直接**塞进 uniform，因此"关雾"关不干净；天空穹顶的地平线雾
        /// （`CalculateSkyFog`）、天空颜色的霾灰度（`CalculateHazeFactor`）、以及掉落物/抛射物/发光精灵
        /// 的远处褪色（CPU 侧 `CalculateFog`）同样没被这条开关管到。现在这些一律走本文末尾的三个
        /// 变换函数（`FogBand` / `HazeStartDensity` / `CpuFog`），并用 `FogStatus()` 逐 pass 记账。
        /// </summary>
        public static bool FogDisabled { get; set; } = true;

        /// <summary>雾带 (bottom, top, density)：关雾时把密度置 0 → 片元里 fogFactor 恒为 0。</summary>
        /// <param name="bottomTopDensity">原版雾带参数 (bottom, top, density)。</param>
        /// <param name="pass">调用方标识（写进 `FogStatus()` 的逐 pass 账本；空 = 不记账）。</param>
        public static Vector3 FogBand(Vector3 bottomTopDensity, string pass = null) {
            bool off = FogDisabled;
            NoteFogPass(pass, off);
            return off ? new Vector3(bottomTopDensity.X, bottomTopDensity.Y, 0f) : bottomTopDensity;
        }

        /// <summary>地平线霾 (start, 1/(end-start))：关雾时把密度置 0（start 置 0 更干净）。</summary>
        public static Vector2 HazeStartDensity(Vector2 startDensity, string pass = null) {
            bool off = FogDisabled;
            NoteFogPass(pass, off);
            return off ? new Vector2(0f, 0f) : startDensity;
        }

        /// <summary>
        /// **[v0.1.111] CPU 侧雾量**：给不经过 shader uniform 的那几处用 ——
        /// 天空穹顶的地平线雾（`CalculateFogNoHaze` → `CalculateSkyFog`，还管日月与云的颜色）、
        /// 天空颜色的霾灰度（`CalculateHazeFactor`）、掉落物/抛射物/发光精灵/投影的远处褪色
        /// （`CalculateFog`，调用点形如 `1f - CalculateFog(...)`）。
        /// 关雾时返回 0，语义与片元侧"密度置 0"完全一致。
        /// </summary>
        public static float CpuFog(float value, string pass = null) {
            bool off = FogDisabled;
            NoteFogPass(pass, off);
            return off ? 0f : value;
        }

        /// <summary>
        /// **[v0.1.111] 一条调用关/开"所有雾层"**（测试光影用）：
        /// `FogAll(true)` = 原版几何雾 + 天空地平线雾 + 实体褪色 + 方块选中框/粒子/移动方块/挖掘裂纹
        /// 的雾（全部由 `FogDisabled` 管）+ 自研体积雾/体积神光/远景 LOD 雾/彩光雾（`VolumetricFogEnabled`）。
        /// 返回 `FogStatus()` 的 JSON，调用方一眼能看到"还有哪层没关"。
        /// </summary>
        public static string FogAll(bool disabled) {
            FogDisabled = disabled;
            VolumetricFogEnabled = !disabled;
            return FogStatus();
        }

        // ============================================================================================
        // [v0.1.111] 逐 pass 记账 —— "关雾到底关干净了没有"的可证伪证据
        // ============================================================================================
        // 口径：**关雾时，每一个出现过的 pass 都必须 `zeroed == calls`**；否则就是"漏网 pass"。
        // 2026-09-28 复核发现 4 个漏网 pass 就是靠这张表抓出来的（粒子/移动方块/挖掘裂纹/选中框）。
        static readonly ConcurrentDictionary<string, long> m_fogPassCalls = new();
        static readonly ConcurrentDictionary<string, long> m_fogPassZeroed = new();

        static void NoteFogPass(string pass, bool zeroed) {
            if (string.IsNullOrEmpty(pass)) {
                return;
            }
            m_fogPassCalls.AddOrUpdate(pass, 1L, (_, n) => n + 1);
            if (zeroed) {
                m_fogPassZeroed.AddOrUpdate(pass, 1L, (_, n) => n + 1);
            }
        }

        /// <summary>清空逐 pass 账本（下一次 `FogStatus()` 只看这之后的一段时间）。</summary>
        public static string FogPassesReset() {
            m_fogPassCalls.Clear();
            m_fogPassZeroed.Clear();
            return FogStatus();
        }

        /// <summary>逐 pass 账本（JSON）：calls / zeroed / zeroedAll。</summary>
        public static string FogStatus() {
            JsonObject root = new();
            root["fogDisabled"] = FogDisabled;
            root["volumetricFogEnabled"] = VolumetricFogEnabled;
            JsonObject layers = new() {
                ["vanillaGeometryFog"] = !FogDisabled,
                ["vanillaSkyHorizonFog"] = !FogDisabled,
                ["vanillaEntityFade"] = !FogDisabled,
                ["volumetricFog"] = VolumetricFogEnabled,
            ["volumetricGodRays"] = VolumetricFogEnabled && VolumetricSunShaftStrength > 0f,
            ["volumetricHaze"] = VolumetricFogEnabled && VolumetricHazeEnabled,
            // [v0.1.118] 基础密度（决定"雾是不是一直存在"）；脚本收尾要能读它把运行时改回去
            ["fogBaseDensity"] = (double)FogBaseDensity,
            ["lodFog"] = VolumetricFogEnabled,
                ["coloredLightFog"] = VolumetricFogEnabled && SkylinePointLights.Enabled
            };
            root["layers"] = layers;
            JsonObject passes = new();
            long callsTotal = 0, zeroedTotal = 0;
            bool covered = true;
            foreach (KeyValuePair<string, long> kv in m_fogPassCalls) {
                long z = m_fogPassZeroed.TryGetValue(kv.Key, out long zz) ? zz : 0;
                passes[kv.Key] = new JsonObject { ["calls"] = kv.Value, ["zeroed"] = z };
                callsTotal += kv.Value;
                zeroedTotal += z;
                if (FogDisabled && z != kv.Value) {
                    covered = false;
                }
            }
            root["passes"] = passes;
            root["passCalls"] = callsTotal;
            root["passZeroed"] = zeroedTotal;
            root["passesAllZeroed"] = covered;
            root["note"] = "关雾时要求每个 pass 的 zeroed == calls；passesAllZeroed=false 即存在漏网 pass";
            return root.ToJsonString();
        }

        /// <summary>
        /// [v0.1.56] 壳采集时**把家具塌缩成设计主材质**（默认开）。家具的"长什么样"由 data 里的设计索引决定，
        /// 而壳每格只有 14 位（contents+light）、存不下 data；塌缩成主材质后，LOD 里家具就画成
        /// "该材质的占位方盒"，与 3.3 的非完整方块口径一致，且**壳仍是 16 KiB**。
        /// </summary>
        public static bool ShellFurnitureCollapse { get; set; } = true;

        /// <summary>
        /// [v0.1.59] 壳采集时**把材质替换成 LOD 结构材质**（默认开）：草方块→泥土、雪层/雪→雪方块。
        /// 用户口径（1.6）："雪层算一个雪方块，而草方块则算作泥土"。
        /// </summary>
        public static bool ShellLodMaterialSubstitute { get; set; } = true;

        public static string FogDescribe() {
            long leaks = 0;
            foreach (KeyValuePair<string, long> kv in m_fogPassCalls) {
                long z = m_fogPassZeroed.TryGetValue(kv.Key, out long zz) ? zz : 0;
                if (z != kv.Value) {
                    leaks++;
                }
            }
            int passCount = m_fogPassCalls.Count;
            return $"fog disabled={FogDisabled} (density=0; 关掉开关即恢复) "
                   + $"volumetric={VolumetricFogEnabled} passes={passCount} "
                   + $"passesWithFogOn={leaks}";
        }

        /// <summary>强制关闭所有对话框；返回 JSON { ok, closed, remaining:[类型名] }。</summary>
        public static string CloseDialogs() {
            JsonObject result = new();
            int closed = 0;
            List<string> errors = [];
            try {
                Dialog[] snapshot = DialogsManager.m_dialogs.ToArray();
                foreach (Dialog dialog in snapshot) {
                    try {
                        if (dialog.ParentWidget != null) {
                            dialog.ParentWidget.Children.Remove(dialog);
                        }
                        DialogsManager.m_dialogs.Remove(dialog);
                        DialogsManager.m_animationData.Remove(dialog);
                        closed++;
                    }
                    catch (Exception e) {
                        errors.Add($"{dialog?.GetType().Name}: {e.Message}");
                    }
                }
            }
            catch (Exception e) {
                errors.Add(e.Message);
            }
            JsonArray remaining = [];
            try {
                foreach (Dialog dialog in DialogsManager.m_dialogs) {
                    remaining.Add(dialog.GetType().Name);
                }
            }
            catch {
                // ignored
            }
            result["ok"] = errors.Count == 0;
            result["closed"] = closed;
            result["remaining"] = remaining;
            if (errors.Count > 0) {
                JsonArray errorList = [];
                foreach (string error in errors) {
                    errorList.Add(error);
                }
                result["errors"] = errorList;
            }
            return result.ToJsonString();
        }
    }
}
