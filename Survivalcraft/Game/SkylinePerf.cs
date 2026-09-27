using Engine;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.68：**性能测量的前置条件**。
    ///
    /// 为什么需要它：本轮给天空加了每像素 8 步 raymarch 之后要测"它到底花了多少帧时间"，
    /// 但实测帧率**恒在 ~30**（垂直同步把帧锁在刷新率的一半），开关体积云的两次采样是
    /// `29.78` vs `29.68` —— **锁帧下这个差什么都说明不了**。
    /// 所以把 `Window.PresentationInterval`（0 = 关垂直同步）暴露成动词，
    /// 让"测性能"这件事有个可重复的前提，而不是在锁帧下猜。
    ///
    /// 用法（测试完请还原）：
    ///   {"op":"invoke","target":"skyline","member":"PresentationInterval","action":"set","value":0}
    ///   {"op":"invoke","target":"skyline","member":"PerfDescribe","action":"call"}
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>呈现间隔（0 = 关垂直同步，1 = 同步刷新率）。**测试用**，会改变现场手感。</summary>
        public static int PresentationInterval {
            get => Window.PresentationInterval;
            set => Window.PresentationInterval = value;
        }

        public static string PerfDescribe() {
            JsonObject o = new() {
                ["presentationInterval"] = Window.PresentationInterval,
                ["note"] = "0 = 关垂直同步（测帧时间用）；非 0 = 与刷新率同步"
            };
            return o.ToJsonString();
        }
    }
}
