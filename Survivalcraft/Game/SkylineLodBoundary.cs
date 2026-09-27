using System;
using Engine;

namespace Game {
    /// <summary>v0.1.43：交接带审计的桥入口（相机为中心）。实现见 `SkylineLodAudit.cs`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>[v0.1.44] LOD 顶点基色是否用"采集时光照"（默认 true；false = 常数 220，A/B 用）。</summary>
        public static bool LodLightFromSamples { get; set; } = true;

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
