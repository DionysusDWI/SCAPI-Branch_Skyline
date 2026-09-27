using System;
using Engine;

namespace Game {
    /// <summary>v0.1.43：交接带审计的桥入口（相机为中心）。实现见 `SkylineLodAudit.cs`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>[v0.1.44] LOD 顶点基色是否用"采集时光照"（默认 true；false = 常数 220，A/B 用）。</summary>
        public static bool LodLightFromSamples { get; set; } = true;

        /// <summary>[v0.1.45] 交接带 4 m 近环细层（默认 true；false = 回到 8 m 细层，A/B 用）。
        /// 关掉后 `LodReset()` 会清掉近环数据。</summary>
        public static bool LodNearLayerEnabled { get; set; } = true;

        /// <summary>[v0.1.46] 手动把交接带标脏（立刻重采近环层）；返回标记的单元数。</summary>
        public static string LodNearBandMark() {
            int marked = SkylineLod.MarkNearBandDirty();
            return $"{{\"ok\":true,\"marked\":{marked},\"totalMarked\":{SkylineLod.NearMarkedCells}}}";
        }

        /// <summary>
        /// [v0.1.62] **LOD 也采样"所有裸露在外的方块"**（里程碑 1.6 的另一半）：开 = 除了列顶，
        /// 还采集并绘制"第二层表面"（树冠之下的地面、檐下的墙顶等裸露但非列顶的表面）；
        /// 关 = 逐位回到 v0.1.61 的"每格只有一个中位高度"。切换后立刻重建网格。
        /// 判据在 `skyline.LodSurvey()` 的 `cellsWithSecond / secondQuads / secondWallQuads`。
        /// </summary>
        public static string LodSecondSurface(bool enabled) {
            SkylineLod.SecondSurfaceEnabled = enabled;
            SkylineLod.RequestRebuild();
            return SkylineLod.Survey();
        }

        /// <summary>[v0.1.62] 第二层表面的参数：最小落差 / 需要的空气间隔 / 向下搜索深度。</summary>
        public static string LodSecondSurfaceParams(int minDrop, int gap, int searchDepth) {
            SkylineLod.SecondMinDrop = Math.Clamp(minDrop, 1, 32);
            SkylineLod.SecondGap = Math.Clamp(gap, 1, 8);
            SkylineLod.SecondSearchDepth = Math.Clamp(searchDepth, 4, 120);
            SkylineLod.RequestRebuild();
            return SkylineLod.Survey();
        }

        /// <summary>[v0.1.48] 4.4：LOD 材质按"完整方块取顶面 / 非完整方块取侧面"（默认 true；
        /// false = 回到 v0.1.47 的"一律顶面"）。改后需 `LodReset()` 或等下一次网格重建生效。</summary>
        public static bool LodMaterialAware { get; set; } = true;

        /// <summary>[v0.1.48] 材质选择诊断：`skyline.LodMaterialProbe(contents)`。</summary>
        public static string LodMaterialProbe(int contents) => SkylineLod.MaterialProbe(contents);

        /// <summary>[v0.1.48] 4.4 全方块材质审计：`skyline.LodMaterialAudit()`。</summary>
        public static string LodMaterialAudit() => SkylineLod.MaterialAudit();

        /// <summary>`skyline.LodBoundaryAudit(inner, outer, samples)` —— 默认环带 128~384 m、4000 样本。</summary>
        public static string LodBoundaryAudit(int inner = 128, int outer = 384, int samples = 4000) {
            Camera camera = GetCamera();
            if (camera == null) {
                return "lodBoundaryAudit: no camera";
            }
            Vector3 position = camera.ViewPosition;
            return SkylineLod.BoundaryAudit((int)position.X, (int)position.Z, inner, outer, samples);
        }

        /// <summary>`skyline.LodRectAudit(x1,z1,x2,z2,samples)` —— 矩形区审计（配 EnsureRegionLoaded 用）。</summary>
        public static string LodRectAudit(int x1, int z1, int x2, int z2, int samples = 4000) {
            return SkylineLod.RectAudit(x1, z1, x2, z2, samples);
        }
    }
}
