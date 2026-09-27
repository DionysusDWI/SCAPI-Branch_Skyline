using Engine;
using Engine.Graphics;
using System;
using System.Text.Json.Nodes;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.66：**显存预算表** —— 里程碑 3.5。
    ///
    /// 用户口径（逐字）："**光影渲染显存占用注意控制在 4 GB 以下**，因为 terrain-diffusion 需要大约 2 GB 显存容量，
    /// 游戏本体在 128-256 视觉距离需要 2 GB 做保险。"
    ///
    /// 本文件把"我们自己分配了什么"变成可读的表：每张 RT 的**实际**尺寸（不是配置项想当然）、
    /// 按构造时用的 `ColorFormat.Rgba8888`(4 B/px) + `DepthFormat.Depth24Stencil8`(4 B/px) 估算字节数，
    /// 以及设备信息（`Display.DeviceDescription` / `MaxTextureSize` / 后备缓冲尺寸）。
    ///
    /// **口径说明（重要，别把它读成"游戏总共只用了这么多"）**：这张表只覆盖**光影/离屏 pass 自己创建的 RT**。
    /// 场景本体的大头是游戏自己的地形图集与区块网格，另外还有窗口后备缓冲 —— 它们不在本表的统计范围内。
    /// 因此本表回答的是"我们这套光影加了多少"，而不是"整机用了多少"；
    /// 整机的真实占用用进程侧的 GPU 计数器测（见 `heightlab/vram-budget.py`）。
    /// </summary>
    public static partial class SkylineRuntime {
        /// <summary>RT 构造时用的字节/像素（颜色 4 + 深度 4）。改了格式就必须同步改这里，否则表会撒谎。</summary>
        public const int RtBytesPerPixel = 8;

        static JsonObject DescribeRt(string name, RenderTarget2D rt, string ownerNote) {
            JsonObject o = new();
            o["name"] = name;
            if (rt == null) {
                o["allocated"] = false;
                o["configuredSize"] = null;
                o["note"] = ownerNote;
                return o;
            }
            long bytes = (long)rt.Width * rt.Height * RtBytesPerPixel;
            o["allocated"] = true;
            o["width"] = rt.Width;
            o["height"] = rt.Height;
            o["bytes"] = bytes;
            o["mib"] = Math.Round(bytes / 1048576.0, 3);
            o["note"] = ownerNote;
            return o;
        }

        /// <summary>把"我们自己创建的离屏 RT"列成表，附设备信息与合计。</summary>
        public static string VramDescribe() {
            JsonObject result = new();
            JsonArray rts = [
                DescribeRt("gpuShadow.far", GpuShadowRtFar, "阴影深度图（远级联）"),
                DescribeRt("gpuShadow.near", GpuShadowRtNear, "阴影深度图（近级联）"),
                DescribeRt("gbuffer", SkylineGBuffer.GBufferRt, "离屏 G-buffer 采样 pass"),
                DescribeRt("lodVolume", SkylineLodVolume.LodVolumeRt, "LOD 体积感着色 demo pass"),
                DescribeRt("shadowPass", ShadowPassRt, "太阳视角深度 pass（v0.1.32 的实验 pass）")
            ];
            long total = 0;
            int allocated = 0;
            foreach (JsonNode node in rts) {
                if (node["allocated"]?.GetValue<bool>() == true) {
                    total += node["bytes"].GetValue<long>();
                    allocated++;
                }
            }
            result["renderTargets"] = rts;
            result["allocatedCount"] = allocated;
            result["totalBytes"] = total;
            result["totalMiB"] = Math.Round(total / 1048576.0, 3);
            result["bytesPerPixelAssumption"] = RtBytesPerPixel;
            result["device"] = Display.DeviceDescription;
            result["maxTextureSize"] = Display.MaxTextureSize;
            result["backbuffer"] = new JsonArray(Display.BackbufferSize.X, Display.BackbufferSize.Y);
            result["budgetMiB"] = 4096;
            result["budgetNote"] = "用户口径：光影显存 <4 GB；terrain-diffusion 约需 2 GB，游戏本体 128-256 视距再留 2 GB";
            result["scopeNote"] = "只统计**光影/离屏 pass 自己创建的 RT**；游戏地形图集、区块网格与窗口后备缓冲不在其中";
            return result.ToJsonString();
        }
    }
}
