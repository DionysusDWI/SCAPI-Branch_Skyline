using System;
using System.Text.Json.Nodes;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**面明暗（体积感）** —— 远景 LOD 与 32³ 壳网格一直"太平"的根，
    /// 不是缺阴影，而是**侧面/底面根本没有明暗差**：
    ///   * `SkylineLod` 的侧壁（山体立面）用常数 `(220,220,220)` 画；顶面用"单元光照 × 坡向 × 自阴影"。
    ///   * `CubeSurfaceMesh32`（生产壳）与 `SurfaceVoxelMesh`（表面体素壳）四个侧面用**同一条** `BaseColor`。
    /// 而**游戏自己的真方块**是有面差的：`LightingManager.CalculateLighting(faceNormal)`
    ///   = `LightAmbient(0.5) + max(dot(n, L1), 0) + max(dot(n, L2), 0)`，
    ///   L1 = (0.12, 0.25, 0.34)、L2 = (-0.12, 0.25, -0.34)（见 `Managers/LightingManager.cs:13` 与 `:34`）。
    ///   由此得到**六面因子**：+Y = 1.00、±Z = 0.84、±X = 0.62、-Y = 0.50。
    ///
    /// 本类就是这一条规则，**不做任何新光照模型**：直接调用游戏的 `CalculateLighting`，
    /// 并以 **+Y 因子归一化**（`CalculateLighting(UnitY)` 恰好 = 1.0）。
    /// 归一化带来的可验证性质：**顶面因子恒为 1.0 → 顶面颜色逐位不变**，
    /// 所以开关的 A/B 像素差**全部**来自侧面/底面（这让"A/B 差异到底是哪来的"可被断言，而不是看起来对）。
    ///
    /// 开关：`skyline.FaceShading(false)` 逐位回到旧观感（默认 true；已建好的网格要用各自的重建入口刷新）。
    /// </summary>
    public static class SkylineFaceShading {
        /// <summary>默认开。关掉 = 所有面因子恒为 1（回到 v0.1.59 的观感）。</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Face 编号沿用引擎 `CellFace`：0=+Z、1=+X、2=-Z、3=-X、4=+Y、5=-Y。</summary>
        public static readonly string[] FaceNames = ["+Z", "+X", "-Z", "-X", "+Y", "-Y"];

        static readonly float[] m_factorByFace = new float[6];
        static float m_topFactor = 1f;
        static bool m_ready;

        static void Ensure() {
            if (!m_ready) {
                Refresh();
            }
        }

        /// <summary>重新从游戏光照模型取六面因子（`LightingManager` 的亮度设置变化时调用）。</summary>
        public static void Refresh() {
            float top = LightingManager.CalculateLighting(Vector3.UnitY);
            m_topFactor = top > 0.0001f ? top : 1f;
            for (int f = 0; f < 6; f++) {
                m_factorByFace[f] = FactorRaw(CellFace.FaceToVector3(f));
            }
            m_ready = true;
        }

        static float FactorRaw(Vector3 normal) =>
            MathUtils.Clamp(LightingManager.CalculateLighting(normal) / m_topFactor, 0f, 1f);

        /// <summary>归一化基准（= 顶面的 `CalculateLighting`，理论上 1.0）。着色器用同一个值当分母。</summary>
        public static float TopFactor {
            get {
                Ensure();
                return m_topFactor;
            }
        }

        /// <summary>某个面的明暗因子（0..1）。`Enabled=false` 时恒为 1。</summary>
        public static float Factor(int face) {
            Ensure();
            if (!Enabled) {
                return 1f;
            }
            return m_factorByFace[face < 0 || face > 5 ? 4 : face];
        }

        /// <summary>任意法线的明暗因子（着色器侧同一公式；CPU 侧用于对角面/坡面）。</summary>
        public static float FactorForNormal(Vector3 normal) {
            Ensure();
            return Enabled ? FactorRaw(normal) : 1f;
        }

        /// <summary>把一个顶点色按面因子变暗（<c>Enabled=false</c> 时原样返回）。</summary>
        public static Color Apply(Color color, int face) {
            float k = Factor(face);
            if (k >= 0.9999f) {
                return color;
            }
            return new Color(
                (byte)MathUtils.Clamp(color.R * k, 0f, 255f),
                (byte)MathUtils.Clamp(color.G * k, 0f, 255f),
                (byte)MathUtils.Clamp(color.B * k, 0f, 255f),
                color.A
            );
        }

        public static JsonObject Describe() {
            Ensure();
            JsonObject result = new() {
                ["ok"] = true,
                ["enabled"] = Enabled,
                ["topFactor"] = Math.Round(m_topFactor, 4),
                ["source"] = "LightingManager.CalculateLighting(faceNormal) / CalculateLighting(+Y)",
                ["light1"] = new JsonArray(0.12, 0.25, 0.34),
                ["light2"] = new JsonArray(-0.12, 0.25, -0.34),
                ["ambient"] = LightingManager.LightAmbient
            };
            JsonObject factors = [];
            for (int f = 0; f < 6; f++) {
                factors[FaceNames[f]] = Math.Round(m_factorByFace[f], 4);
            }
            result["factors"] = factors;
            result["note"] = "顶面因子恒为 1.0 → 打开本规则时顶面颜色逐位不变，A/B 差异全部来自侧面/底面；"
                + "已建好的网格需重建：远景 LOD 用 skyline.LodFaceShading(true) 立刻重建，"
                + "壳网格用 skyline.CubeSurfaceHarvest(...) 重采、生产壳用 skyline.CubeShell* 触发。";
            return result;
        }

        /// <summary>
        /// 自检：①六面因子符合游戏光照模型；②顶面因子 = 1（这就是"顶面不变"的断言）；
        /// ③`Apply` 对一个 light=15 的顶面颜色是逐位不变的。
        /// </summary>
        public static string SelfCheck() {
            Ensure();
            JsonObject result = new();
            try {
                int fails = 0;
                // 期望值直接用同一条公式独立算一遍（不走 m_factorByFace 缓存）
                for (int f = 0; f < 6; f++) {
                    float expect = FactorRaw(CellFace.FaceToVector3(f));
                    if (MathF.Abs(expect - m_factorByFace[f]) > 0.0005f) {
                        fails++;
                    }
                }
                bool topIsOne = MathF.Abs(m_factorByFace[4] - 1f) < 0.0005f;
                Color sample = new(255, 255, 255, 255);
                bool topUnchanged = Apply(sample, 4).PackedValue == sample.PackedValue;
                Color side = Apply(sample, 1);
                result["ok"] = fails == 0 && topIsOne && topUnchanged;
                result["factorMismatches"] = fails;
                result["topFactorIsOne"] = topIsOne;
                result["topColorUnchanged"] = topUnchanged;
                result["sideSample"] = new JsonArray(side.R, side.G, side.B);
                result["factors"] = new JsonObject {
                    ["+Y"] = Math.Round(m_factorByFace[4], 4),
                    ["-Y"] = Math.Round(m_factorByFace[5], 4),
                    ["+X"] = Math.Round(m_factorByFace[1], 4),
                    ["-X"] = Math.Round(m_factorByFace[3], 4),
                    ["+Z"] = Math.Round(m_factorByFace[0], 4),
                    ["-Z"] = Math.Round(m_factorByFace[2], 4)
                };
            }
            catch (Exception e) {
                result["ok"] = false;
                result["err"] = e.Message;
            }
            return result.ToJsonString();
        }
    }

    /// <summary>桥：`skyline.FaceShading*`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>开关面明暗（默认开）。关掉 = 回到 v0.1.59 观感（已建好的网格要重建才看得到）。</summary>
        public static string FaceShading(bool enabled) {
            SkylineFaceShading.Enabled = enabled;
            JsonObject result = SkylineFaceShading.Describe();
            result["hint"] = "远景 LOD 立刻重建请调 skyline.LodFaceShading(" + (enabled ? "true" : "false") + ")";
            return result.ToJsonString();
        }

        /// <summary>当前面因子与开关状态。</summary>
        public static string FaceShadingInfo() => SkylineFaceShading.Describe().ToJsonString();

        /// <summary>面明暗自检（六面因子 + "顶面逐位不变"断言）。</summary>
        public static string FaceShadingSelfCheck() => SkylineFaceShading.SelfCheck();

        /// <summary>开关面明暗**并立刻重建远景 LOD 网格**（用于 A/B 取证）。</summary>
        public static string LodFaceShading(bool enabled) {
            SkylineFaceShading.Enabled = enabled;
            SkylineLod.RequestRebuild();
            JsonObject result = SkylineFaceShading.Describe();
            result["rebuildRequested"] = true;
            return result.ToJsonString();
        }
    }
}
