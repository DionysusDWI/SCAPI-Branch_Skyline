using Engine;
using Engine.Graphics;
using System.Text;
#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
#endif

// Windows 实现要用注册表（HKCU 显卡偏好）与 COM（DXGI 枚举）；非 Windows 目标走同一个类的空实现分支，
// 平台隔离已经由类内的 #if WINDOWS 完成，这里只是把 CA1416 平台分析告警关掉。
#pragma warning disable CA1416

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— "自动跑到计算能力最强的那张显卡上"（**新挂载的模块**，非 mod；Windows 专有，其他平台空实现）。
    ///
    /// 需求口径（用户给定）：
    ///   * 换显卡必须重启游戏：第一次运行先用**默认显示适配器**跑起来 → 扫描全部适配器 → 选出最优 →
    ///     **提示重启**；重启之后才真正跑在那张卡上。
    ///   * 后续优先提升对 NVIDIA 显卡的适配。
    ///
    /// 实现口径（v0.0.4）：
    ///   1. 用 DXGI（`IDXGIFactory1`/`IDXGIAdapter1`）枚举适配器，得到名称/厂商/显存/LUID/是否软件适配器；
    ///   2. 评分挑最优：独显 > 核显，NVIDIA 在同等条件下优先（用户要求），显存大者优先；
    ///   3. 状态写 `SkylineGpu.cfg`（游戏目录，删掉即重新扫描）；
    ///   4. **切换手段 = ANGLE 按 LUID 精确选卡**（实测可用，确定性，不动系统设置）：
    ///      预检 `Egl.TryCreatePlatformDisplay()` 成功后交给 <see cref="GLWrapper"/> 复用，
    ///      游戏即以 D3D11 后端跑在目标卡上；
    ///   5. 兜底：ANGLE 不可用（缺 libEGL.dll / 预检失败）时改写 Windows"本应用显卡偏好"
    ///      （HKCU\Software\Microsoft\DirectX\UserGpuPreferences = GpuPreference=2;）并提示重启；
    ///   6. 启动后用 `Display.DeviceDescription` 复核"到底跑在哪张卡"，把结论写回配置与日志。
    ///
    /// 外部（含 AgentBridge）可直接调用：
    ///   skylinegpu.Describe()      —— 状态/适配器/当前渲染器全览
    ///   skylinegpu.ClearTarget()   —— 清掉目标卡（下次启动重新扫描）
    ///   skylinegpu.Rescan()        —— 立刻重新枚举并给出建议（不改变本次运行）
    /// </summary>
    public static class SkylineGpu {
        public const string ConfigFileName = "SkylineGpu.cfg";

        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        /// <summary>总开关（可用启动参数 `-skylinegpu off` 关闭）。</summary>
        public static bool Enabled = true;

        /// <summary>当前状态：idle / disabled / already_best / angle_prepared / pending_restart / applied / failed / unsupported。</summary>
        public static string State = "idle";

        public static AdapterInfo DefaultAdapter;
        public static AdapterInfo BestAdapter;
        public static AdapterInfo TargetAdapter;

        /// <summary>本次运行是否已强制把 ANGLE 切到目标适配器。</summary>
        public static bool AngleForcedThisRun;

        /// <summary>本次运行实际使用的渲染器描述（Engine.Graphics.Display.DeviceDescription）。</summary>
        public static string LastRenderer = "";

        /// <summary>最近一次动作说明（写日志/排查用）。</summary>
        public static string LastAction = "";

        static bool m_promptArmed;
        static bool m_promptShown;
        static bool m_recorded;
        /// <summary>本次启动才写下 pending_restart（系统偏好要"下一次启动"才生效，不能在同一轮就判定失败）。</summary>
        static bool m_pendingWrittenThisRun;
        static string m_promptTitle = "";
        static string m_promptText = "";
        static string m_promptButton1 = "";
        static string m_promptButton2 = "";

        // ================================================================================================
        // 适配器模型
        // ================================================================================================
        public sealed class AdapterInfo {
            public uint Index;
            public string Name = "";
            public uint VendorId;
            public uint DeviceId;
            public ulong DedicatedVideoMemory;
            public uint LuidHigh;
            public uint LuidLow;
            public uint Flags;

            public bool IsSoftware => (Flags & 2u) != 0u;

            /// <summary>核显判定：Intel，或显存小于 1GB 的 AMD（如 Radeon 780M 报 417MB）。</summary>
            public bool IsIntegrated =>
                VendorId == 0x8086u
                || ((VendorId == 0x1002u || VendorId == 0x1022u) && DedicatedVideoMemory < 1024UL * 1024UL * 1024UL);

            public string VendorName => VendorId switch {
                0x10DEu => "NVIDIA",
                0x1002u or 0x1022u => "AMD",
                0x8086u => "Intel",
                0x1414u => "Microsoft",
                _ => $"Vendor0x{VendorId:X4}"
            };

            public ulong VramMb => DedicatedVideoMemory / (1024UL * 1024UL);

            public string LuidText => $"{LuidHigh:X8}-{LuidLow:X8}";

            /// <summary>越大越强。软件适配器直接排除；独显 > 核显；NVIDIA 优先；显存大者优先。</summary>
            public long Score {
                get {
                    if (IsSoftware) {
                        return long.MinValue;
                    }
                    long score = IsIntegrated ? 1_000_000L : 5_000_000L;
                    if (VendorId == 0x10DEu) {
                        score += 1_000_000L;
                    }
                    score += (long)Math.Min(VramMb, 65536UL);
                    return score;
                }
            }

            public override string ToString() =>
                $"[{Index}] {Name} ({VendorName}, {VramMb}MB, luid={LuidText}{(IsSoftware ? ", software" : IsIntegrated ? ", integrated" : ", discrete")})";
        }

        // ================================================================================================
        // 启动接入
        // ================================================================================================

        /// <summary>必须在创建窗口/图形上下文之前调用（Program.EntryPoint 内、Window.Run 之前）。</summary>
        public static void Prepare() {
#if WINDOWS
            try {
                PrepareWindows();
            }
            catch (Exception ex) {
                State = "error";
                LastAction = ex.Message;
                Log.Error($"Skyline GPU: prepare failed. {ex}");
            }
#else
            State = "unsupported";
            LastAction = "only implemented on Windows";
#endif
        }

        /// <summary>图形上下文建立后调用一次（Program.Initialize 末尾），复核"实际跑在哪张卡"并写回配置。</summary>
        public static void OnGameInitialized() {
            if (m_recorded) {
                return;
            }
            m_recorded = true;
            try {
                LastRenderer = Display.DeviceDescription ?? "";
                Dictionary<string, string> cfg = LoadConfig();
                cfg["last_run_renderer"] = LastRenderer;
                cfg["last_run_angle_forced"] = AngleForcedThisRun ? "1" : "0";
                // [v0.0.5] ANGLE 路径下 EGL 的 swap interval 需要在上下文建立后再显式设一次，
                // 否则启动时读到的"帧率上限"（SettingsManager.PresentationInterval）不生效（实测会跑成不限帧）。
                if (AngleForcedThisRun) {
                    try {
                        Window.PresentationInterval = SettingsManager.PresentationInterval;
                        Log.Information($"Skyline GPU: re-applied the frame limit for the ANGLE path (PresentationInterval={SettingsManager.PresentationInterval})");
                    }
                    catch (Exception ex) {
                        Log.Error($"Skyline GPU: re-applying the frame limit failed (ignored). {ex}");
                    }
                }
                string previousState = cfg.GetValueOrDefault("state", "");
                if (TargetAdapter != null
                    && !string.IsNullOrEmpty(TargetAdapter.Name)) {
                    bool onTarget = LastRenderer.Contains(TargetAdapter.Name, StringComparison.OrdinalIgnoreCase);
                    if (onTarget) {
                        State = "applied";
                        cfg["state"] = "applied";
                        LastAction = $"running on the target adapter \"{TargetAdapter.Name}\"";
                        Log.Information($"Skyline GPU: OK — running on the selected adapter \"{TargetAdapter.Name}\". {LastRenderer}");
                    }
                    else if (AngleForcedThisRun) {
                        int n = int.TryParse(cfg.GetValueOrDefault("angle_attempts", "0"), out int parsed) ? parsed : 0;
                        n++;
                        cfg["angle_attempts"] = n.ToString();
                        State = n >= 2 ? "failed" : "escalated";
                        cfg["state"] = State;
                        LastAction = $"ANGLE was forced to \"{TargetAdapter.Name}\" but the renderer is \"{LastRenderer}\" (attempt {n})";
                        Log.Warning($"Skyline GPU: {LastAction}");
                    }
                    else if (!m_pendingWrittenThisRun
                        && string.Equals(previousState, "pending_restart", StringComparison.OrdinalIgnoreCase)) {
                        // 系统显卡偏好在这次启动里没有生效 → 下次启动改用 ANGLE(D3D11) 兼容模式（需再重启一次）
                        State = "escalated";
                        cfg["state"] = "escalated";
                        LastAction = "the Windows GPU preference did not take effect; the next launch will force the ANGLE (D3D11) path";
                        Log.Warning($"Skyline GPU: {LastAction}. renderer=\"{LastRenderer}\", target=\"{TargetAdapter.Name}\".");
                        ArmRestartPrompt(TargetAdapter, DefaultAdapter,
                            "系统显卡偏好未生效，下次启动将改用 ANGLE(D3D11) 兼容模式跑在目标显卡上，请再重启一次。\nThe Windows GPU preference did not take effect; the next launch will use the ANGLE (D3D11) compatibility path instead. One more restart is required.");
                    }
                    else {
                        LastAction = $"not on the target adapter yet; renderer is \"{LastRenderer}\", target is \"{TargetAdapter.Name}\"";
                        Log.Information($"Skyline GPU: {LastAction}");
                    }
                }
                else {
                    LastAction = $"no switch needed; renderer is \"{LastRenderer}\"";
                }
                cfg["last_run_state"] = State;
                SaveConfig(cfg);
            }
            catch (Exception ex) {
                Log.Error($"Skyline GPU: post-start verification failed. {ex}");
            }
        }

        /// <summary>每帧调用（Program.Run 内）：到主菜单后弹一次"检测到更强显卡，重启后生效"提示。</summary>
        public static void Tick() {
            if (!m_promptArmed || m_promptShown) {
                return;
            }
            if (ScreensManager.CurrentScreen == null) {
                return;
            }
            if (!ScreensManager.m_screens.TryGetValue("MainMenu", out Screen mainMenu)
                || ScreensManager.CurrentScreen != mainMenu) {
                return;
            }
            m_promptShown = true;
            m_promptArmed = false;
            try {
                DialogsManager.ShowDialog(
                    mainMenu,
                    new MessageDialog(
                        m_promptTitle,
                        m_promptText,
                        m_promptButton1,
                        m_promptButton2,
                        button => {
                            if (button == MessageDialogButton.Button1) {
                                Log.Information("Skyline GPU: user chose to restart the game now");
                                Window.Restart();
                            }
                            else {
                                Log.Information("Skyline GPU: user postponed the restart; it will be applied automatically on the next launch");
                            }
                        }
                    )
                );
            }
            catch (Exception ex) {
                Log.Error($"Skyline GPU: failed to show the restart prompt. {ex}");
            }
        }

        // ================================================================================================
        // 对外查询（含桥）
        // ================================================================================================

        /// <summary>状态总览（AgentBridge：`skylinegpu.Describe()`）。</summary>
        public static string Describe() {
            var sb = new StringBuilder();
            sb.AppendLine($"Skyline GPU state={State} enabled={Enabled} angleForced={AngleForcedThisRun}");
            sb.AppendLine($"  adapters:");
            foreach (AdapterInfo adapter in EnumerateAdapters()) {
                sb.AppendLine($"    {adapter}");
            }
            sb.AppendLine($"  default={DefaultAdapter?.ToString() ?? "<none>"}");
            sb.AppendLine($"  best={BestAdapter?.ToString() ?? "<none>"}");
            sb.AppendLine($"  target={TargetAdapter?.ToString() ?? "<none>"}");
            sb.AppendLine($"  renderer={LastRenderer}");
            sb.AppendLine($"  config={ConfigPath}");
            foreach (KeyValuePair<string, string> kv in LoadConfig()) {
                sb.AppendLine($"    {kv.Key}={kv.Value}");
            }
            sb.AppendLine($"  lastAction={LastAction}");
            return sb.ToString().TrimEnd();
        }

        /// <summary>清掉目标卡与状态（下次启动重新扫描）。</summary>
        public static string ClearTarget() {
            try {
                if (File.Exists(ConfigPath)) {
                    File.Delete(ConfigPath);
                }
                TargetAdapter = null;
                State = "cleared";
                LastAction = "target cleared; delete done, a new scan happens on the next launch";
                Log.Information("Skyline GPU: target cleared (config deleted)");
                return LastAction;
            }
            catch (Exception ex) {
                Log.Error($"Skyline GPU: failed to clear target. {ex}");
                return $"failed: {ex.Message}";
            }
        }

        /// <summary>立刻重新枚举（只读；不改变本次运行）。</summary>
        public static string Rescan() {
            List<AdapterInfo> adapters = EnumerateAdapters();
            DefaultAdapter = FindDefault(adapters);
            BestAdapter = FindBest(adapters);
            LastAction = $"rescanned: {adapters.Count} adapters, best={BestAdapter?.Name ?? "<none>"}";
            Log.Information($"Skyline GPU: {LastAction}");
            return Describe();
        }

        // ================================================================================================
        // Windows 实现
        // ================================================================================================
#if WINDOWS
        static void PrepareWindows() {
            string startupParameter = null;
            if (Program.StartupParameters.TryGetValue("skylinegpu", out string parameter)) {
                startupParameter = parameter;
            }
            if (string.Equals(startupParameter, "off", StringComparison.OrdinalIgnoreCase)) {
                Enabled = false;
                State = "disabled";
                LastAction = "startup parameter -skylinegpu off";
                Log.Information("Skyline GPU: disabled by the startup parameter (-skylinegpu off)");
                return;
            }

            List<AdapterInfo> adapters = EnumerateAdapters();
            if (adapters.Count == 0) {
                State = "no-adapters";
                LastAction = "DXGI reported no adapters";
                Log.Warning("Skyline GPU: DXGI reported no display adapters");
                return;
            }
            DefaultAdapter = FindDefault(adapters);
            BestAdapter = FindBest(adapters);
            Log.Information($"Skyline GPU: {adapters.Count} adapters, default=\"{DefaultAdapter?.Name ?? "<none>"}\", best=\"{BestAdapter?.Name ?? "<none>"}\"");
            foreach (AdapterInfo adapter in adapters) {
                Log.Information($"Skyline GPU:   {adapter}");
            }

            Dictionary<string, string> cfg = LoadConfig();
            // [v0.0.5] `-skylinegpu angle`：显式强制走 ANGLE 直选（即使目标卡已经是系统默认）。
            // 之前只有配置文件里写 strategy=angle 才能强制，文档与命令行不一致。
            if (string.Equals(startupParameter, "angle", StringComparison.OrdinalIgnoreCase)) {
                cfg["strategy"] = "angle";
                Log.Information("Skyline GPU: forced ANGLE path by the startup parameter (-skylinegpu angle)");
            }
            // [v0.0.5] UsingAngle 标记的生命周期：如果是上一次 Skyline 的 ANGLE 兜底写下的标记，
            // 而这次要走"系统偏好 + 原生驱动"，就把它删掉（只删我们自己写的；用户手工建的不动）。
            // 不删的话游戏会永远停在 ANGLE 兼容模式，回不到原生 GL。
            if (!string.Equals(cfg.GetValueOrDefault("strategy", ""), "angle", StringComparison.OrdinalIgnoreCase)
                && string.Equals(cfg.GetValueOrDefault("angle_marker_ours", "0"), "1", StringComparison.Ordinal)) {
                try {
                    string markerPath = Path.Combine(AppContext.BaseDirectory, "UsingAngle");
                    if (File.Exists(markerPath)) {
                        File.Delete(markerPath);
                        Log.Information("Skyline GPU: removed the UsingAngle marker written by a previous Skyline ANGLE run; native GL will be used on this start");
                    }
                    cfg["angle_marker_ours"] = "0";
                    SaveConfig(cfg);
                }
                catch (Exception ex) {
                    Log.Error($"Skyline GPU: removing the UsingAngle marker failed (ignored). {ex}");
                }
            }
            string strategy = cfg.GetValueOrDefault("strategy", "auto");
            AdapterInfo target = null;
            if (uint.TryParse(cfg.GetValueOrDefault("target_luid_high", ""), out uint high)
                && uint.TryParse(cfg.GetValueOrDefault("target_luid_low", ""), out uint low)) {
                target = adapters.FirstOrDefault(a => a.LuidHigh == high && a.LuidLow == low);
                if (target == null) {
                    Log.Warning($"Skyline GPU: the previously selected adapter (luid {high:X8}-{low:X8}) is no longer present, re-scanning");
                }
            }

            // 预检失败过就不再重试 ANGLE（删掉 SkylineGpu.cfg 即可重置，避免每次启动反复重试）
            bool angleBlocked = cfg.ContainsKey("angle_missing")
                || cfg.ContainsKey("angle_load_failed")
                || cfg.ContainsKey("angle_preflight_failed");
            string state = cfg.GetValueOrDefault("state", "");

            if (target == null) {
                // 第一次运行（或旧目标已不存在）：本次仍跑默认卡 → 写"本应用显卡偏好" → 提示重启
                if (BestAdapter == null
                    || (DefaultAdapter != null && BestAdapter.LuidHigh == DefaultAdapter.LuidHigh && BestAdapter.LuidLow == DefaultAdapter.LuidLow)) {
                    State = "already_best";
                    LastAction = $"already on the best adapter \"{DefaultAdapter?.Name ?? "<none>"}\"";
                    Log.Information($"Skyline GPU: {LastAction}");
                    return;
                }
                cfg["target_luid_high"] = BestAdapter.LuidHigh.ToString();
                cfg["target_luid_low"] = BestAdapter.LuidLow.ToString();
                cfg["target_name"] = BestAdapter.Name;
                cfg["target_vendor"] = BestAdapter.VendorName;
                TargetAdapter = BestAdapter;
                bool preferOk = TryWriteOsGpuPreference();
                bool angleOk = false;
                if (!preferOk) {
                    // 写不了系统偏好时直接用 ANGLE（Prepare 在图形上下文之前，本次启动即可生效）
                    angleOk = !angleBlocked && TryPrepareAngle(BestAdapter, cfg);
                }
                if (preferOk) {
                    cfg["strategy"] = "os-preference";
                    cfg["state"] = "pending_restart";
                    m_pendingWrittenThisRun = true;
                    State = "pending_restart";
                    LastAction = $"selected \"{BestAdapter.Name}\", the Windows GPU preference was written, a restart is required";
                }
                else if (angleOk) {
                    cfg["strategy"] = "angle";
                    cfg["state"] = "escalated";
                    State = "angle_prepared";
                    LastAction = $"selected \"{BestAdapter.Name}\", ANGLE was forced (the Windows GPU preference is unavailable)";
                }
                else {
                    cfg["strategy"] = "none";
                    cfg["state"] = "failed";
                    State = "failed";
                    LastAction = $"could not switch to \"{BestAdapter.Name}\" (neither ANGLE nor the Windows GPU preference is available)";
                }
                SaveConfig(cfg);
                if (preferOk) {
                    ArmRestartPrompt(BestAdapter, DefaultAdapter,
                        "第一次运行使用默认显卡完成扫描；重启后游戏将改用上面选出的显卡运行。\nThe first run used the default adapter; restart the game to run on the selected adapter.");
                }
                else if (!angleOk) {
                    ArmRestartPrompt(BestAdapter, DefaultAdapter,
                        "无法自动切换显卡（ANGLE 与系统偏好都不可用），详见日志 Skyline GPU 行。\nCould not switch adapters automatically (neither ANGLE nor the Windows GPU preference is available). See the Skyline GPU lines in the log.");
                }
                Log.Information($"Skyline GPU: {LastAction} (current default \"{DefaultAdapter?.Name ?? "<none>"}\")");
                return;
            }

            // 有目标卡
            TargetAdapter = target;
            bool targetIsDefault = DefaultAdapter != null
                && target.LuidHigh == DefaultAdapter.LuidHigh
                && target.LuidLow == DefaultAdapter.LuidLow;
            // 阶段 2：ANGLE 直选（用户显式指定 strategy=angle / 升级流程 state=escalated）——本次启动即可生效。
            // 注意要放在"目标就是默认适配器"的短路之前：strategy=angle 是显式覆盖，即使目标卡已经是系统默认也要强制走 ANGLE。
            bool anglePhase = !angleBlocked
                && (string.Equals(strategy, "angle", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "escalated", StringComparison.OrdinalIgnoreCase));
            if (anglePhase
                && TryPrepareAngle(target, cfg)) {
                State = "angle_prepared";
                LastAction = $"ANGLE forced to \"{target.Name}\" (luid {target.LuidText})";
                Log.Information($"Skyline GPU: {LastAction}");
                return;
            }

            if (targetIsDefault) {
                State = "already_best";
                LastAction = $"target \"{target.Name}\" is already the default adapter";
                Log.Information($"Skyline GPU: {LastAction}");
                return;
            }

            // 阶段 1：让"本应用显卡偏好"在这一轮启动里生效；启动后由 OnGameInitialized 复核
            if (string.Equals(state, "applied", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "pending_restart", StringComparison.OrdinalIgnoreCase)) {
                State = state;
                LastAction = $"letting the Windows GPU preference take effect (state={state}, target=\"{target.Name}\")";
                Log.Information($"Skyline GPU: {LastAction}");
                return;
            }

            // 其它情况（无状态/失败过）：重新写系统偏好并提示重启
            bool repreferOk = TryWriteOsGpuPreference();
            cfg["strategy"] = repreferOk ? "os-preference" : "none";
            cfg["state"] = repreferOk ? "pending_restart" : "failed";
            cfg["angle_attempts"] = "0";
            m_pendingWrittenThisRun = repreferOk;
            SaveConfig(cfg);
            State = repreferOk ? "pending_restart" : "failed";
            LastAction = repreferOk
                ? $"re-wrote the Windows GPU preference for \"{target.Name}\"; a restart is required"
                : $"could not switch to \"{target.Name}\"";
            ArmRestartPrompt(target, DefaultAdapter,
                repreferOk
                    ? "已重新写入 Windows“本应用显卡偏好 = 高性能”，重启后生效。\nThe Windows GPU preference was written again; restart to apply it."
                    : "无法自动切换到目标显卡，详见日志 Skyline GPU 行。\nCould not switch to the selected adapter automatically (see the Skyline GPU lines in the log).");
            Log.Warning($"Skyline GPU: {LastAction}");
        }

        /// <summary>预检：加载游戏目录的 ANGLE，按 LUID 建一个 D3D11 平台显示并初始化；成功后交给 GLWrapper 复用。</summary>
        static bool TryPrepareAngle(AdapterInfo target, Dictionary<string, string> cfg) {
            string angleDir = AppContext.BaseDirectory;
            string pathGles = Path.Combine(angleDir, "libGLESv2.dll");
            string pathEgl = Path.Combine(angleDir, "libEGL.dll");
            if (!File.Exists(pathGles)
                || !File.Exists(pathEgl)) {
                Log.Warning($"Skyline GPU: ANGLE libraries are missing in \"{angleDir}\" (libEGL.dll / libGLESv2.dll), falling back to the OS GPU preference");
                cfg["angle_missing"] = "1";
                return false;
            }
            if (!NativeLibrary.TryLoad(pathGles, out _)
                || !NativeLibrary.TryLoad(pathEgl, out _)) {
                Log.Warning("Skyline GPU: failed to preload the ANGLE libraries, falling back to the OS GPU preference");
                cfg["angle_load_failed"] = "1";
                return false;
            }
            IntPtr display = Egl.TryCreatePlatformDisplay(target.LuidHigh, target.LuidLow, out int major, out int minor);
            if (display == IntPtr.Zero) {
                Log.Warning($"Skyline GPU: eglGetPlatformDisplayEXT/eglInitialize failed for luid {target.LuidText}, falling back to the OS GPU preference");
                cfg["angle_preflight_failed"] = "1";
                return false;
            }
            GLWrapper.UsingAngle = true;
            GLWrapper.AngleAdapterForced = true;
            GLWrapper.AngleAdapterLuidHigh = target.LuidHigh;
            GLWrapper.AngleAdapterLuidLow = target.LuidLow;
            GLWrapper.PrecreatedEglDisplay = display;
            AngleForcedThisRun = true;
            cfg["strategy"] = "angle";
            // 记下"这个 UsingAngle 标记是 Skyline 自己写出来的"，下次回到系统偏好路径时才能安全清掉
            cfg["angle_marker_ours"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "UsingAngle")) ? "0" : "1";
            cfg["angle_egl_version"] = $"{major}.{minor}";
            cfg.Remove("angle_missing");
            cfg.Remove("angle_load_failed");
            cfg.Remove("angle_preflight_failed");
            SaveConfig(cfg);
            return true;
        }

        /// <summary>兜底手段：写 HKCU 的"本应用显卡偏好 = 高性能"（等价于 Windows 设置 → 显示 → 图形 里的那一项）。</summary>
        static bool TryWriteOsGpuPreference() {
            try {
                string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) {
                    return false;
                }
                using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\DirectX\UserGpuPreferences", true);
                if (key == null) {
                    return false;
                }
                key.SetValue(exePath, "GpuPreference=2;", Microsoft.Win32.RegistryValueKind.String);
                Log.Information($"Skyline GPU: wrote HKCU GPU preference GpuPreference=2 for \"{exePath}\"");
                return true;
            }
            catch (Exception ex) {
                Log.Warning($"Skyline GPU: failed to write the Windows GPU preference. {ex.Message}");
                return false;
            }
        }

        static void ArmRestartPrompt(AdapterInfo target, AdapterInfo current, string extra) {
            m_promptTitle = "Skyline 显卡选择 / GPU selection";
            m_promptText =
                $"检测到更强显卡：{target.Name}\n"
                + $"当前使用：{current?.Name ?? "(unknown)"}\n"
                + $"{extra}\n\n"
                + $"A more powerful GPU is available: {target.Name}\n"
                + $"Currently running on: {current?.Name ?? "(unknown)"}";
            m_promptButton1 = "立即重启 / Restart now";
            m_promptButton2 = "稍后 / Later";
            m_promptArmed = true;
        }

        static AdapterInfo FindDefault(List<AdapterInfo> adapters) =>
            adapters.FirstOrDefault(a => !a.IsSoftware);

        static AdapterInfo FindBest(List<AdapterInfo> adapters) =>
            adapters.Where(a => !a.IsSoftware).OrderByDescending(a => a.Score).ThenBy(a => a.Index).FirstOrDefault();

        // ---------------------------------------------------------------- DXGI 枚举
        [StructLayout(LayoutKind.Sequential)]
        struct Luid {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DxgiAdapterDesc1 {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public Luid AdapterLuid;
            public uint Flags;
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDxgiAdapter1 {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint output, out IntPtr outputPtr);
            [PreserveSig] int GetDesc(out IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
            [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
        }

        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDxgiFactory1 {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr adapterPtr);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapterPtr);
            [PreserveSig] int EnumAdapters1(uint adapter, out IDxgiAdapter1 adapter1);
            [PreserveSig] int IsCurrent();
        }

        [DllImport("dxgi.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDxgiFactory1 factory);
#endif

        // ================================================================================================
        // 适配器枚举（跨平台空实现 + Windows 真实实现）
        // ================================================================================================
        public static List<AdapterInfo> EnumerateAdapters() {
            var result = new List<AdapterInfo>();
#if WINDOWS
            try {
                var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                int hr = CreateDXGIFactory1(ref iid, out IDxgiFactory1 factory);
                if (hr < 0
                    || factory == null) {
                    Log.Warning($"Skyline GPU: CreateDXGIFactory1 failed hr=0x{hr:X8}");
                    return result;
                }
                try {
                    for (uint i = 0; i < 16; i++) {
                        hr = factory.EnumAdapters1(i, out IDxgiAdapter1 adapter);
                        if (hr < 0
                            || adapter == null) {
                            break;
                        }
                        try {
                            if (adapter.GetDesc1(out DxgiAdapterDesc1 desc) >= 0) {
                                result.Add(new AdapterInfo {
                                    Index = i,
                                    Name = desc.Description ?? "",
                                    VendorId = desc.VendorId,
                                    DeviceId = desc.DeviceId,
                                    DedicatedVideoMemory = desc.DedicatedVideoMemory.ToUInt64(),
                                    LuidHigh = unchecked((uint)desc.AdapterLuid.HighPart),
                                    LuidLow = desc.AdapterLuid.LowPart,
                                    Flags = desc.Flags
                                });
                            }
                        }
                        finally {
                            Marshal.ReleaseComObject(adapter);
                        }
                    }
                }
                finally {
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch (Exception ex) {
                Log.Warning($"Skyline GPU: DXGI enumeration failed. {ex.Message}");
            }
#endif
            return result;
        }

        // ================================================================================================
        // 配置读写
        // ================================================================================================
        static Dictionary<string, string> LoadConfig() {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (File.Exists(ConfigPath)) {
                    foreach (string rawLine in File.ReadAllLines(ConfigPath)) {
                        string line = rawLine.Trim();
                        if (line.Length == 0
                            || line.StartsWith('#')) {
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq <= 0) {
                            continue;
                        }
                        cfg[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception ex) {
                Log.Warning($"Skyline GPU: failed to read {ConfigFileName}. {ex.Message}");
            }
            return cfg;
        }

        static void SaveConfig(Dictionary<string, string> cfg) {
            try {
                cfg["version"] = "1";
                cfg["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var sb = new StringBuilder();
                sb.AppendLine("# SCAPI Skyline Project - GPU selection state (auto-generated; delete this file to force a re-scan)");
                foreach (KeyValuePair<string, string> kv in cfg.OrderBy(k => k.Key, StringComparer.Ordinal)) {
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                }
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch (Exception ex) {
                Log.Warning($"Skyline GPU: failed to write {ConfigFileName}. {ex.Message}");
            }
        }
    }
}

#pragma warning restore CA1416
