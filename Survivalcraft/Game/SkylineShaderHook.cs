using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.58：**光影接入面 v1**（里程碑 4「Iris 真接入」的底座）。
    ///
    /// 目标不是"做出一个光影包"，而是把**一个外部光影包真正需要的那几件事**先摆好、并说清楚哪些还没做：
    ///
    ///   1. **阶段（stage）注册表**：外部包把回调挂到固定的几个阶段上，而不是去改 `SubsystemTerrain.Draw`
    ///      —— 阶段：`shadow`（太阳深度图之前/之后）、`opaque`（不透明地形之后）、`lod`（远景层与 32³ 壳之后）、
    ///      `composite`（不透明 pass 全部结束）、`final`（帧末）；
    ///   2. **同一条绘制链上的接管点**：远景 LOD 已经能整层交给外部画（`SkylineLod.ExternalShaderHooked` +
    ///      `CustomDraw`，v0.1.5），网格本体也能只读拿到（`CoarseVertexBuffer` 等，v0.1.11）；
    ///   3. **离屏 pass 的样板**：`SkylineGBuffer` 用**自编译 shader**把远景几何渲染进自建 RenderTarget，
    ///      回读自检、并能把结果直接画到屏幕上做 A/B —— 这就是"着色器 + 渲染引擎可推进"的那一半；
    ///   4. **窗口/相机信息**：阶段回调里能拿到 `Camera`，另外 `Describe()` 会给出当前可用的几何源清单
    ///      （地形区块 / 三层 LOD / 壳立方体数量）与已注册的处理器，方便光影包"握手"。
    ///
    /// **纪律**（与 v0.1.5 的 `CustomDraw` 一致）：任何外部处理器抛异常都**只记日志**，绝不让它打断帧；
    /// 默认没有任何处理器时，本类**完全不改变渲染**（逐位同 v0.1.57）。
    /// </summary>
    public static class SkylineShaderHook {
        /// <summary>阶段名（固定这几个；外部包按名字注册）。</summary>
        public static readonly string[] Stages = ["shadow", "opaque", "lod", "composite", "final"];

        public sealed class Entry {
            public string Stage;
            public string Name;
            public int Priority;                 // 数字小的先跑
            public Action<Camera> Handler;
        }

        static readonly List<Entry> m_entries = [];
        static readonly Dictionary<string, long> m_calls = [];
        static readonly Dictionary<string, long> m_errors = [];

        /// <summary>总开关（默认 true；没有任何处理器时是彻底的空操作）。</summary>
        public static bool Enabled { get; set; } = true;
        /// <summary>外部包声明的名字（只用于展示/日志）。</summary>
        public static string PackName { get; set; } = "";

        public static long TotalCalls { get; private set; }
        public static long TotalErrors { get; private set; }
        public static string LastError { get; private set; } = "";

        public static int HandlerCount => m_entries.Count;

        public static bool Register(string stage, string name, int priority, Action<Camera> handler) {
            if (handler == null || string.IsNullOrEmpty(stage)) {
                return false;
            }
            if (Array.IndexOf(Stages, stage) < 0) {
                LastError = $"unknown stage '{stage}'";
                return false;
            }
            for (int i = 0; i < m_entries.Count; i++) {
                if (m_entries[i].Stage == stage && m_entries[i].Name == name) {
                    m_entries[i] = new Entry { Stage = stage, Name = name, Priority = priority, Handler = handler };
                    m_entries.Sort((a, b) => a.Priority.CompareTo(b.Priority));
                    return true;
                }
            }
            m_entries.Add(new Entry { Stage = stage, Name = name, Priority = priority, Handler = handler });
            m_entries.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            return true;
        }

        public static void Unregister(string name) {
            m_entries.RemoveAll(e => e.Name == name);
        }

        public static void Clear() {
            m_entries.Clear();
            m_calls.Clear();
            m_errors.Clear();
        }

        /// <summary>跑一个阶段（由 `SubsystemTerrain.Draw` 在固定位置调用）。异常只记不改帧。</summary>
        public static void Run(string stage, Camera camera) {
            if (!Enabled || m_entries.Count == 0 || camera == null) {
                return;
            }
            for (int i = 0; i < m_entries.Count; i++) {
                Entry entry = m_entries[i];
                if (entry.Stage != stage) {
                    continue;
                }
                try {
                    entry.Handler(camera);
                    TotalCalls++;
                    m_calls.TryGetValue(stage, out long n);
                    m_calls[stage] = n + 1;
                }
                catch (Exception e) {
                    TotalErrors++;
                    m_errors.TryGetValue(stage, out long n);
                    m_errors[stage] = n + 1;
                    LastError = $"{entry.Name}@{stage}: {e.Message}";
                    Log.Warning($"SkylineShaderHook[{stage}/{entry.Name}]: {e.Message}");
                }
            }
        }

        /// <summary>几何源清单：光影包"握手"时能看到现在有哪些东西可画。</summary>
        static JsonObject GeometrySources() {
            return new JsonObject {
                ["terrainChunks"] = SkylineLod.LoadedChunks,
                ["lodCoarse"] = new JsonObject {
                    ["cellMetres"] = SkylineLod.CellSize,
                    ["indexCount"] = SkylineLod.CoarseIndexCount,
                    ["meshVersion"] = SkylineLod.MeshVersion
                },
                ["lodFine"] = new JsonObject {
                    ["cellMetres"] = SkylineLod.FineSize,
                    ["indexCount"] = SkylineLod.FineIndexCount
                },
                ["lodNear"] = new JsonObject {
                    ["cellMetres"] = SkylineLod.NearSize,
                    ["indexCount"] = SkylineLod.NearIndexCount
                },
                ["shellCubes"] = SkylineCubeShellStore.CubeCount,
                ["shellMeshes"] = SkylineCubeShellStore.MeshResident,
                ["vertexLayout"] = SkylineLod.VertexLayout
            };
        }

        public static string Describe() {
            JsonArray handlers = [];
            foreach (Entry entry in m_entries) {
                handlers.Add(new JsonObject {
                    ["stage"] = entry.Stage,
                    ["name"] = entry.Name,
                    ["priority"] = entry.Priority
                });
            }
            JsonObject stageInfo = new();
            foreach (string stage in Stages) {
                m_calls.TryGetValue(stage, out long calls);
                m_errors.TryGetValue(stage, out long errors);
                stageInfo[stage] = new JsonObject { ["calls"] = calls, ["errors"] = errors };
            }
            return new JsonObject {
                ["ok"] = true,
                ["api"] = "SkylineShaderHook/1",
                ["enabled"] = Enabled,
                ["packName"] = PackName,
                ["stages"] = new JsonArray([.. Stages]),
                ["handlers"] = handlers,
                ["handlerCount"] = m_entries.Count,
                ["totalCalls"] = TotalCalls,
                ["totalErrors"] = TotalErrors,
                ["perStage"] = stageInfo,
                ["takeover"] = new JsonObject {
                    ["lodExternalHooked"] = SkylineLod.ExternalShaderHooked,
                    ["lodCustomDrawSet"] = SkylineLod.CustomDraw != null,
                    ["entry"] = "SkylineLod.ExternalShaderHooked=true 且 SkylineLod.CustomDraw 非空 → 远景层整层交给外部画"
                },
                ["geometrySources"] = GeometrySources(),
                ["offscreen"] = new JsonObject {
                    ["gBuffer"] = "SkylineGBuffer（自编译 shader → 自建 RenderTarget → 回读自检 / 调试直显）",
                    ["shadowDepth"] = "SkylineGpuShadow（v0.1.32~v0.1.38 的太阳深度图 + 级联近图）"
                },
                ["notYet"] = new JsonArray(
                    "法线通道（LOD 顶点格式目前只有 position/texcoord/color，没有 normal）",
                    "材质 id 通道（同上，缺一个逐顶点材质属性）",
                    "天空盒替代 / 体积云 / 后处理链 / 水面反射（按用户口径暂缓）",
                    "实体与家具的独立 pass"),
                ["lastError"] = LastError
            }.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.ShaderHook*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>注册一个**内置测试处理器**（用来验证阶段接线；外部光影包应注册自己的）。</summary>
        public static string ShaderHookTest(string stage, string name, int priority) {
            bool ok = SkylineShaderHook.Register(stage, name, priority, camera => {
                // 什么都不画：只证明阶段回调真的被调到了（计数在 Describe() 里）
                SkylineGBuffer.TouchFromHook();
            });
            return new JsonObject {
                ["ok"] = ok,
                ["stage"] = stage,
                ["name"] = name,
                ["describe"] = SkylineShaderHook.Describe()
            }.ToJsonString();
        }

        public static string ShaderHookClear() {
            SkylineShaderHook.Clear();
            return SkylineShaderHook.Describe();
        }

        public static string ShaderHookInfo() => SkylineShaderHook.Describe();

        /// <summary>外部包声明自己的名字（只用于展示）。</summary>
        public static string ShaderHookPack(string name) {
            SkylineShaderHook.PackName = name ?? "";
            return SkylineShaderHook.Describe();
        }
    }
}
