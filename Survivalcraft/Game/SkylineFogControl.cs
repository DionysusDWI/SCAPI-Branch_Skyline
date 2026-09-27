using System;
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
        /// <summary>[v0.1.35] 关雾开关（纯视觉测试用，默认关）。</summary>
        public static bool FogDisabled { get; set; }

        /// <summary>雾带 (bottom, top, density)：关雾时把密度置 0 → 片元里 fogFactor 恒为 0。</summary>
        public static Vector3 FogBand(Vector3 bottomTopDensity) =>
            FogDisabled ? new Vector3(bottomTopDensity.X, bottomTopDensity.Y, 0f) : bottomTopDensity;

        /// <summary>地平线霾 (start, 1/(end-start))：关雾时把密度置 0（start 置 0 更干净）。</summary>
        public static Vector2 HazeStartDensity(Vector2 startDensity) =>
            FogDisabled ? new Vector2(0f, 0f) : startDensity;

        public static string FogDescribe() =>
            $"fog disabled={FogDisabled} (density=0; 关掉开关即恢复)";

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
