using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— **NVIDIA 深化（NVAPI 直连）**（v0.0.5，游戏本体内置，非 mod）。
    ///
    /// 目标（用户给定）：
    ///   * 为将来接入 DLSS / RT（光追）等 NVIDIA 特性做准备；
    ///   * **必须有单独开关**（默认关掉高级特性；信息读取也允许整体关闭）；
    ///   * **绝不能损伤"基础模式"**——集显/核显/APU/没有 NVIDIA 卡的机器上必须完全无副作用。
    ///
    /// 实现口径：
    ///   1. 运行时 `NativeLibrary.TryLoad("nvapi64.dll")` + `nvapi_QueryInterface(id)` 动态取函数指针
    ///      （**不静态 DllImport**，所以没装 NVIDIA 驱动 / 非 NVIDIA 机器上不会抛异常）；
    ///   2. 函数 ID 与结构体布局全部对照官方 `nvapi.h` / `nvapi_interface.h`（NVIDIA/nvapi，MIT）；
    ///   3. 只做"读"（驱动版本 / 卡名 / 显存 / 核心数 / 温度 / 占用 / 架构 / PCI ID），
    ///      写操作（NVIDIA 驱动配置、DLSS/RTO 开关）本版一律不做；
    ///   4. `AllowDlss` / `AllowRayTracing` 是**单独开关**，默认 `false`；且只有"当前渲染器就是 NVIDIA"
    ///      时才允许打开——本版渲染器是 OpenGL ES(原生/ANGLE)，两个特性实际都不可用，
    ///      模块会如实报告 `hardware=yes / renderer=no`，为后续换成 D3D12/Vulkan 后端铺路。
    /// </summary>
    public static class SkylineNvidia {
        // ============================================================================================
        // 状态
        // ============================================================================================

        public const string ConfigFileName = "SkylineNvidia.cfg";

        public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, ConfigFileName);

        /// <summary>
        /// 总开关（**默认打开，但只读**）：模块只做"读"（驱动/型号/显存/温度/占用/架构），不碰渲染管线。
        /// 关闭后本模块**完全不加载 nvapi64.dll、不调用任何 NVAPI 导出**，对集显/核显/APU 零副作用
        /// （没有 NVIDIA 卡的机器上，即使打开也只是"一次 TryLoad 失败即返回"，实测 `state=no-dll`、无异常）。
        /// 关闭方式：启动参数 `-nvapi off`、配置文件 `enabled=0`、或桥调用 `SetSwitches(false,…)`；
        /// 重新打开用 `-nvapi on` / `enabled=1`。**DLSS / 光追这两个特性开关默认关闭**，见 AllowDlss / AllowRayTracing。
        /// </summary>
        public static bool Enabled = true;

        /// <summary>nvapi64.dll 存在、初始化成功、且至少枚举到一块 NVIDIA GPU。</summary>
        public static bool Supported;

        /// <summary>当前**渲染器**是不是 NVIDIA（用引擎的 DeviceDescription 判定，而不是 NVAPI 枚举）。</summary>
        public static bool RendererIsNvidia;

        /// <summary>idle / disabled / no-dll / init-failed / ok / error。</summary>
        public static string State = "idle";

        public static string DriverVersion = "";
        /// <summary>NVAPI 原始的驱动版本 DWORD（十六进制），版本字符串格式不确定时用它兜底。</summary>
        public static string DriverVersionRaw = "";
        /// <summary>从渲染器字符串里读到的 NVIDIA 驱动版本（如 `610.88`），最接近玩家认知。</summary>
        public static string GlDriverVersion = "";
        public static string GpuName = "";
        public static int VramMb;
        public static int CoreCount;
        public static uint DeviceId;
        public static string Architecture = "";
        public static int TemperatureC = int.MinValue;
        public static int UtilizationPercent = -1;
        public static string LastAction = "";

        /// <summary>硬件层面是否具备光追单元（Turing 及以后）。</summary>
        public static bool HardwareSupportsRayTracing;

        /// <summary>硬件层面是否具备 DLSS 所需张量单元（Turing 及以后）。</summary>
        public static bool HardwareSupportsDlss;

        /// <summary>【单独开关】允许 DLSS —— 本版渲染器不支持，打开也只会记录意图。</summary>
        public static bool AllowDlss;

        /// <summary>【单独开关】允许光追 —— 本版渲染器不支持，打开也只会记录意图。</summary>
        public static bool AllowRayTracing;

        static double m_nextPollTime;
        static bool m_loggedSummary;

        // ============================================================================================
        // 启动接入
        // ============================================================================================

        /// <summary>在创建窗口/图形上下文**之前**调用（Program.EntryPoint 内）。只做加载与初始化，不碰渲染。</summary>
        public static void Prepare() {
            try {
                Dictionary<string, string> cfg = LoadConfig();
                LoadSwitches(cfg);
                if (Program.StartupParameters.TryGetValue("nvapi", out string parameter)) {
                    if (string.Equals(parameter, "off", StringComparison.OrdinalIgnoreCase)) {
                        Enabled = false;
                        State = "disabled";
                        LastAction = "startup parameter -nvapi off";
                        Log.Information("Skyline NVIDIA: disabled by the startup parameter (-nvapi off)");
                        SaveConfig(cfg, "");
                        return;
                    }
                    if (string.Equals(parameter, "on", StringComparison.OrdinalIgnoreCase)) {
                        // 显式打开（默认即打开；此参数用于覆盖配置文件里的 enabled=0 记录）
                        Enabled = true;
                        Log.Information("Skyline NVIDIA: enabled by the startup parameter (-nvapi on)");
                    }
                }
                if (!Enabled) {
                    State = "disabled";
                    LastAction = "disabled by config";
                    Log.Information("Skyline NVIDIA: disabled by SkylineNvidia.cfg");
                    return;
                }
#if WINDOWS
                PrepareWindows();
#else
                State = "unsupported";
                LastAction = "NVAPI is Windows only";
#endif
                SaveConfig(cfg, "");
            }
            catch (Exception ex) {
                State = "error";
                LastAction = ex.Message;
                // 任何失败都只记录，绝不影响游戏启动（尤其不能影响集显/核显/APU 机器）
                Log.Error($"Skyline NVIDIA: prepare failed (ignored). {ex}");
            }
        }

        /// <summary>图形上下文建立后调用一次：判定"当前渲染器是不是 NVIDIA"，再决定特性开关是否可用。</summary>
        public static void OnGameInitialized() {
            try {
                string renderer = Display.DeviceDescription ?? "";
                RendererIsNvidia = renderer.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0;
                GlDriverVersion = ParseNvidiaDriverFromRenderer(renderer);
                if (Supported) {
                    Poll(true);
                }
                if (!m_loggedSummary) {
                    m_loggedSummary = true;
                    Log.Information($"Skyline NVIDIA: {Describe()}");
                }
                Dictionary<string, string> cfg = LoadConfig();
                SaveConfig(cfg, renderer);
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: post-init failed (ignored). {ex}");
            }
        }

        /// <summary>每帧调用（内部节流到 1 秒）：刷新温度/占用，供外部查询。</summary>
        public static void Tick() {
            if (!Supported || !Enabled) {
                return;
            }
            if (Time.RealTime < m_nextPollTime) {
                return;
            }
            m_nextPollTime = Time.RealTime + 1.0;
            try {
                Poll(false);
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: poll failed (ignored). {ex}");
                ResetProbe();
                State = "error";
                LastAction = ex.Message;
            }
        }

        // ============================================================================================
        // 对外查询（含桥）
        // ============================================================================================

        public static string Describe() {
            var sb = new StringBuilder();
            sb.Append($"state={State} enabled={Enabled} supported={Supported} rendererIsNvidia={RendererIsNvidia}");
            if (Supported) {
                sb.Append($" driver=\"{DriverVersion}\" glDriver=\"{GlDriverVersion}\" gpu=\"{GpuName}\" vram={VramMb}MB cores={CoreCount}");
                sb.Append($" arch={Architecture} temp={(TemperatureC == int.MinValue ? "n/a" : TemperatureC + "C")}");
                sb.Append($" util={(UtilizationPercent < 0 ? "n/a" : UtilizationPercent + "%")}");
            }
            sb.Append($" dlss(hw={HardwareSupportsDlss},allow={AllowDlss}) rt(hw={HardwareSupportsRayTracing},allow={AllowRayTracing})");
            return sb.ToString();
        }

        /// <summary>能力清单（JSON 风格一行，给外部/桥用）。</summary>
        public static string Capabilities() {
            bool rendererCanUseAdvanced = RendererIsNvidia && Supported;
            return "{"
                + $"\"nvidia\":{Json(Supported)},"
                + $"\"driver\":\"{DriverVersion}\","
                + $"\"gpu\":\"{GpuName}\","
                + $"\"vramMb\":{VramMb},"
                + $"\"cores\":{CoreCount},"
                + $"\"arch\":\"{Architecture}\","
                + $"\"rendererIsNvidia\":{Json(RendererIsNvidia)},"
                + $"\"hardwareRayTracing\":{Json(HardwareSupportsRayTracing)},"
                + $"\"hardwareDlss\":{Json(HardwareSupportsDlss)},"
                + $"\"allowDlss\":{Json(AllowDlss)},"
                + $"\"allowRayTracing\":{Json(AllowRayTracing)},"
                + $"\"rendererSupportsDlss\":{Json(false)},"
                + $"\"rendererSupportsRayTracing\":{Json(false)},"
                + $"\"futureReady\":{Json(rendererCanUseAdvanced)}"
                + "}";
        }

        static string Json(bool value) => value ? "true" : "false";

        /// <summary>从 `... Renderer=NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2, Version=OpenGL ES 3.2 NVIDIA 610.88 ...` 里抠出 `610.88`。</summary>
        static string ParseNvidiaDriverFromRenderer(string renderer) {
            try {
                int index = renderer.LastIndexOf("NVIDIA ", StringComparison.OrdinalIgnoreCase);
                if (index < 0) {
                    return "";
                }
                string tail = renderer[(index + 7)..].Trim();
                int end = 0;
                while (end < tail.Length && (char.IsDigit(tail[end]) || tail[end] == '.')) {
                    end++;
                }
                return end > 0 ? tail[..end].TrimEnd('.') : "";
            }
            catch {
                return "";
            }
        }

        /// <summary>开关（外部/桥可以直接调）：总开关 + 两个独立特性开关。</summary>
        public static string SetSwitches(bool enabled, bool allowDlss, bool allowRayTracing) {
            bool wasEnabled = Enabled;
            Enabled = enabled;
            AllowDlss = allowDlss && RendererIsNvidia && Supported;
            AllowRayTracing = allowRayTracing && RendererIsNvidia && Supported;
            if (!Enabled) {
                // 关掉时如实报 disabled（读数保留但不再刷新）
                State = "disabled";
            }
            Dictionary<string, string> cfg = LoadConfig();
            SaveConfig(cfg, Display.DeviceDescription ?? "");
            LastAction = $"set enabled={Enabled} dlss={AllowDlss} rt={AllowRayTracing}";
            Log.Information($"Skyline NVIDIA: {LastAction}");
            if (Enabled && !wasEnabled) {
                // 从关闭状态重新打开：重跑一次探测，避免残留 "disabled" 或过期读数
                Rescan();
            }
            return Describe();
        }

        public static string Rescan() {
            try {
#if WINDOWS
                if (Enabled) {
                    PrepareWindows();
                }
#endif
                OnGameInitialized();
            }
            catch (Exception ex) {
                LastAction = ex.Message;
            }
            return Describe();
        }

        // ============================================================================================
        // Windows / NVAPI 实现
        // ============================================================================================

#if WINDOWS
        const string NvapiDll = "nvapi64.dll";

        // --- 官方函数 ID（来源：NVIDIA/nvapi `nvapi_interface.h`，MIT）---
        const uint ID_Initialize = 0x0150E828;
        const uint ID_Unload = 0xD22BDD7E;
        const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
        const uint ID_GetDriverAndBranchVersion = 0x2926AAAD;   // NvAPI_SYS_GetDriverAndBranchVersion（官方推荐，替代已废弃的 GetDisplayDriverVersion）
        const uint ID_GPU_GetFullName = 0xCEEE8E9F;
        const uint ID_GPU_GetPhysicalFrameBufferSize = 0x46FBEB03;
        const uint ID_GPU_GetGpuCoreCount = 0xC7026A87;
        const uint ID_GPU_GetPCIIdentifiers = 0x2DDFB66E;
        const uint ID_GPU_GetThermalSettings = 0xE3640A56;
        const uint ID_GPU_GetDynamicPstatesInfoEx = 0x60DED2ED;
        const uint ID_GPU_GetArchInfo = 0xD8265D24;

        const int MAX_PHYSICAL_GPUS = 64;
        const int MAX_THERMAL_SENSORS = 3;
        const int MAX_UTILIZATIONS = 8;

        const int NVAPI_OK = 0;

        static IntPtr m_nvapi;
        static IntPtr m_gpu;
        static QueryInterfaceDelegate m_queryInterface;

        delegate IntPtr QueryInterfaceDelegate(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int InitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int UnloadDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out uint count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetDriverAndBranchVersionDelegate(out uint driverVersion, byte[] buildBranchString);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetFullNameDelegate(IntPtr gpu, byte[] name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetUintDelegate(IntPtr gpu, out uint value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetPciIdentifiersDelegate(IntPtr gpu, out uint deviceId, out uint subSystemId,
            out uint revisionId, out uint extDeviceId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetThermalSettingsDelegate(IntPtr gpu, uint sensorIndex, ref NV_GPU_THERMAL_SETTINGS_V1 settings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetDynamicPstatesDelegate(IntPtr gpu, ref NV_GPU_DYNAMIC_PSTATES_INFO_EX info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GpuGetArchInfoDelegate(IntPtr gpu, ref NV_GPU_ARCH_INFO_V1 info);

        [StructLayout(LayoutKind.Sequential)]
        struct NV_GPU_THERMAL_SENSOR {
            public int controller;
            public int defaultMinTemp;
            public int defaultMaxTemp;
            public int currentTemp;
            public int target;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NV_GPU_THERMAL_SETTINGS_V1 {
            public uint version;
            public uint count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_THERMAL_SENSORS)]
            public NV_GPU_THERMAL_SENSOR[] sensor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NV_GPU_UTILIZATION_DOMAIN {
            public uint bIsPresent;   // C 里是 1 位位域，存储单元仍是 4 字节
            public uint percentage;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NV_GPU_DYNAMIC_PSTATES_INFO_EX {
            public uint version;
            public uint flags;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_UTILIZATIONS)]
            public NV_GPU_UTILIZATION_DOMAIN[] utilization;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct NV_GPU_ARCH_INFO_V1 {
            public uint version;
            public uint architecture;
            public uint implementation;
            public uint revision;
        }

        static uint MakeVersion(int size, int ver) => (uint)(size | (ver << 16));

        /// <summary>探测失败/被关掉时清空上一次的读数，避免外部看到"过期的能力值"。</summary>
        static void ResetProbe() {
            Supported = false;
            GpuName = "";
            DriverVersion = "";
            DriverVersionRaw = "";
            VramMb = 0;
            CoreCount = 0;
            DeviceId = 0;
            Architecture = "";
            TemperatureC = int.MinValue;
            UtilizationPercent = -1;
            HardwareSupportsRayTracing = false;
            HardwareSupportsDlss = false;
            AllowDlss = false;
            AllowRayTracing = false;
        }

        static void PrepareWindows() {
            // 允许用配置覆盖 DLL 名（诊断/自测用：指到一个不存在的名字就能验证"没有 NVAPI 时完全无副作用"）
            string dllName = LoadConfig().GetValueOrDefault("dll", NvapiDll);
            if (!NativeLibrary.TryLoad(dllName, out m_nvapi)) {
                ResetProbe();
                State = "no-dll";
                LastAction = $"{dllName} not present (non-NVIDIA machine) — feature silently disabled";
                Log.Information($"Skyline NVIDIA: {LastAction}");
                return;
            }
            if (!NativeLibrary.TryGetExport(m_nvapi, "nvapi_QueryInterface", out IntPtr queryPtr)) {
                ResetProbe();
                State = "no-entry";
                LastAction = "nvapi_QueryInterface not exported";
                Log.Warning($"Skyline NVIDIA: {LastAction}");
                return;
            }
            m_queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryPtr);
            var initialize = GetFunction<InitializeDelegate>(ID_Initialize);
            if (initialize == null) {
                ResetProbe();
                State = "no-init";
                LastAction = "NvAPI_Initialize not available";
                Log.Warning($"Skyline NVIDIA: {LastAction}");
                return;
            }
            int status = initialize();
            if (status != NVAPI_OK) {
                ResetProbe();
                State = "init-failed";
                LastAction = $"NvAPI_Initialize returned 0x{status:X8}";
                Log.Warning($"Skyline NVIDIA: {LastAction}");
                return;
            }

            // 枚举物理 GPU，取第一块（笔记本上就是那块独显）
            var enumGpus = GetFunction<EnumPhysicalGpusDelegate>(ID_EnumPhysicalGPUs);
            if (enumGpus == null) {
                ResetProbe();
                State = "no-enum";
                LastAction = "NvAPI_EnumPhysicalGPUs not available";
                return;
            }
            IntPtr[] handles = new IntPtr[MAX_PHYSICAL_GPUS];
            if (enumGpus(handles, out uint count) != NVAPI_OK || count == 0) {
                ResetProbe();
                State = "no-gpu";
                LastAction = "no NVIDIA physical GPU reported";
                Log.Information($"Skyline NVIDIA: {LastAction}");
                return;
            }
            m_gpu = handles[0];
            Supported = true;
            State = "ok";
            ReadStaticInfo();
            Poll(true);
            LastAction = $"NVAPI ready ({GpuName}, driver {DriverVersion})";
            Log.Information($"Skyline NVIDIA: {LastAction}");
        }

        static T GetFunction<T>(uint id) where T : Delegate {
            IntPtr ptr = m_queryInterface(id);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        static void ReadStaticInfo() {
            try {
                var driver = GetFunction<GetDriverAndBranchVersionDelegate>(ID_GetDriverAndBranchVersion);
                if (driver != null) {
                    byte[] branch = new byte[64];
                    if (driver(out uint driverVersion, branch) == NVAPI_OK) {
                        DriverVersionRaw = $"0x{driverVersion:X8}";
                        string branchString = Encoding.ASCII.GetString(branch).TrimEnd('\0', ' ');
                        // 打包格式在不同驱动版本间有差异：这里以"分支字符串 + 原始值"留档，
                        // 更贴近玩家认知的 `610.88` 由渲染器字符串解析（OnGameInitialized → GlDriverVersion）。
                        DriverVersion = string.IsNullOrEmpty(branchString)
                            ? DriverVersionRaw
                            : $"{branchString} ({DriverVersionRaw})";
                    }
                }
                var fullName = GetFunction<GpuGetFullNameDelegate>(ID_GPU_GetFullName);
                if (fullName != null) {
                    byte[] name = new byte[64];
                    if (fullName(m_gpu, name) == NVAPI_OK) {
                        GpuName = Encoding.ASCII.GetString(name).TrimEnd('\0', ' ');
                    }
                }
                var vram = GetFunction<GpuGetUintDelegate>(ID_GPU_GetPhysicalFrameBufferSize);
                if (vram != null && vram(m_gpu, out uint sizeKb) == NVAPI_OK) {
                    VramMb = (int)(sizeKb / 1024);
                }
                var cores = GetFunction<GpuGetUintDelegate>(ID_GPU_GetGpuCoreCount);
                if (cores != null && cores(m_gpu, out uint coreCount) == NVAPI_OK) {
                    CoreCount = (int)coreCount;
                }
                var pci = GetFunction<GpuGetPciIdentifiersDelegate>(ID_GPU_GetPCIIdentifiers);
                if (pci != null
                    && pci(m_gpu, out uint deviceId, out _, out _, out _) == NVAPI_OK) {
                    DeviceId = deviceId;
                }
                var arch = GetFunction<GpuGetArchInfoDelegate>(ID_GPU_GetArchInfo);
                if (arch != null) {
                    var info = new NV_GPU_ARCH_INFO_V1 {
                        version = MakeVersion(Marshal.SizeOf<NV_GPU_ARCH_INFO_V1>(), 1)
                    };
                    if (arch(m_gpu, ref info) == NVAPI_OK) {
                        Architecture = DescribeArchitecture(info.architecture);
                        bool turingOrLater = info.architecture >= 0x00000160 && info.architecture < 0xE0000000;
                        HardwareSupportsRayTracing = turingOrLater;
                        HardwareSupportsDlss = turingOrLater;
                    }
                }
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: read static info failed (ignored). {ex}");
            }
        }

        static string DescribeArchitecture(uint architecture) {
            switch (architecture) {
                case 0x00000160: return "Turing (TU100)";
                case 0x00000170: return "Ampere (GA100)";
                case 0x00000190: return "Ada (AD100)";
                case 0x000001B0: return "Blackwell (GB200)";
                default:
                    if (architecture is >= 0x00000161 and < 0x00000170) return $"Turing (0x{architecture:X})";
                    if (architecture is >= 0x00000171 and < 0x00000190) return $"Ampere (0x{architecture:X})";
                    if (architecture is >= 0x00000191 and < 0x000001B0) return $"Ada (0x{architecture:X})";
                    if (architecture is >= 0x000001B1 and < 0x00000200) return $"Blackwell or newer (0x{architecture:X})";
                    return $"0x{architecture:X}";
            }
        }

        static void Poll(bool first) {
            try {
                var thermal = GetFunction<GpuGetThermalSettingsDelegate>(ID_GPU_GetThermalSettings);
                if (thermal != null) {
                    var settings = new NV_GPU_THERMAL_SETTINGS_V1 {
                        version = MakeVersion(Marshal.SizeOf<NV_GPU_THERMAL_SETTINGS_V1>(), 1),
                        sensor = new NV_GPU_THERMAL_SENSOR[MAX_THERMAL_SENSORS]
                    };
                    if (thermal(m_gpu, 0, ref settings) == NVAPI_OK && settings.count > 0) {
                        TemperatureC = settings.sensor[0].currentTemp;
                    }
                }
                var pstates = GetFunction<GpuGetDynamicPstatesDelegate>(ID_GPU_GetDynamicPstatesInfoEx);
                if (pstates != null) {
                    var info = new NV_GPU_DYNAMIC_PSTATES_INFO_EX {
                        version = MakeVersion(Marshal.SizeOf<NV_GPU_DYNAMIC_PSTATES_INFO_EX>(), 1),
                        utilization = new NV_GPU_UTILIZATION_DOMAIN[MAX_UTILIZATIONS]
                    };
                    if (pstates(m_gpu, ref info) == NVAPI_OK) {
                        int max = -1;
                        for (int i = 0; i < MAX_UTILIZATIONS; i++) {
                            if ((info.utilization[i].bIsPresent & 1u) != 0u) {
                                max = Math.Max(max, (int)info.utilization[i].percentage);
                            }
                        }
                        UtilizationPercent = max;
                    }
                }
                if (first) {
                    LastAction = $"first poll: {Describe()}";
                }
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: poll failed (ignored). {ex}");
            }
        }

        /// <summary>测试/诊断用：强制走一遍完整的加载流程（即使 Enabled=false）。</summary>
        public static string ForceProbe() {
            bool saved = Enabled;
            try {
                Enabled = true;
                NativeLibrary.Free(m_nvapi);
                m_nvapi = IntPtr.Zero;
                m_queryInterface = null;
                m_gpu = IntPtr.Zero;
                PrepareWindows();
                return Describe();
            }
            catch (Exception ex) {
                LastAction = ex.Message;
                return Describe();
            }
            finally {
                Enabled = saved;
            }
        }
#else
        public static string ForceProbe() => Describe();
#endif

        // ============================================================================================
        // 配置
        // ============================================================================================

        static Dictionary<string, string> LoadConfig() {
            var cfg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try {
                if (File.Exists(ConfigPath)) {
                    foreach (string raw in File.ReadAllLines(ConfigPath)) {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) {
                            continue;
                        }
                        int index = line.IndexOf('=');
                        if (index > 0) {
                            cfg[line[..index].Trim()] = line[(index + 1)..].Trim();
                        }
                    }
                }
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: reading config failed (ignored). {ex}");
            }
            return cfg;
        }

        static void LoadSwitches(Dictionary<string, string> cfg) {
            if (cfg.TryGetValue("enabled", out string enabled)) {
                Enabled = enabled != "0";
            }
            if (cfg.TryGetValue("allow_dlss", out string dlss)) {
                AllowDlss = dlss == "1";
            }
            if (cfg.TryGetValue("allow_ray_tracing", out string rt)) {
                AllowRayTracing = rt == "1";
            }
        }

        static void SaveConfig(Dictionary<string, string> cfg, string renderer) {
            try {
                cfg["enabled"] = Enabled ? "1" : "0";
                cfg["allow_dlss"] = AllowDlss ? "1" : "0";
                cfg["allow_ray_tracing"] = AllowRayTracing ? "1" : "0";
                cfg["last_state"] = State;
                cfg["last_supported"] = Supported ? "1" : "0";
                cfg["last_renderer_is_nvidia"] = RendererIsNvidia ? "1" : "0";
                if (!string.IsNullOrEmpty(renderer)) {
                    cfg["last_renderer"] = renderer.Replace("\r", " ").Replace("\n", " ");
                }
                if (Supported) {
                    cfg["last_gpu"] = GpuName;
                    cfg["last_driver"] = DriverVersion;
                    cfg["last_vram_mb"] = VramMb.ToString();
                    cfg["last_arch"] = Architecture;
                    cfg["last_temp_c"] = TemperatureC == int.MinValue ? "" : TemperatureC.ToString();
                    cfg["last_util_percent"] = UtilizationPercent < 0 ? "" : UtilizationPercent.ToString();
                }
                cfg["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                var sb = new StringBuilder();
                sb.AppendLine("# SCAPI Skyline Project - NVIDIA (NVAPI) state (auto-generated; delete to reset)");
                foreach (KeyValuePair<string, string> kv in cfg) {
                    sb.AppendLine($"{kv.Key}={kv.Value}");
                }
                File.WriteAllText(ConfigPath, sb.ToString());
            }
            catch (Exception ex) {
                Log.Error($"Skyline NVIDIA: writing config failed (ignored). {ex}");
            }
        }
    }
}
