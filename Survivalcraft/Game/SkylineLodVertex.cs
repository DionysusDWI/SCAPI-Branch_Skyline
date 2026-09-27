using System;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Engine;
using Engine.Graphics;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.60：**LOD 顶点格式加属性**（用户里程碑 1.3 的"改底层"那一步）。
    ///
    /// 原来的 LOD/壳顶点是游戏的 `TerrainVertex`（position + texcoord + color，20 B）。
    /// 三个渲染层（远景 16/8/4 m、32³ 壳网格、表面体素壳）都只能靠**顶点色里烘焙好的光**，
    /// 于是：着色器不知道"这个片元朝哪"，也不知道"它是什么材质"——
    /// 这正是"LOD 太平、材质没有体积感"以及"Iris 侧接不进来"的结构性原因。
    ///
    /// 本格式在原有三个属性的**后面**追加两个属性（原属性偏移与语义**一字不动**，
    /// 所以用 `TerrainVertex` 的着色器（`Opaque`）照样能画这个 buffer，多出来的属性只是被忽略）：
    /// | 偏移 | 格式 | 语义 | 内容 |
    /// |---|---|---|---|
    /// | 0  | Vector3           | POSITION   | 世界坐标（与 TerrainVertex 同） |
    /// | 12 | NormalizedShort2  | TEXCOORD   | 图集瓦片坐标（与 TerrainVertex 同） |
    /// | 16 | NormalizedByte4   | COLOR      | 烘焙光照（与 TerrainVertex 同） |
    /// | 20 | NormalizedByte4   | NORMAL     | **xyz = 面法线**，按 `0.5+0.5*n` 编成无符号归一化（GL 后端 `NormalizedByte4` = UnsignedByte+normalize）；w 保留 = 255 |
    /// | 24 | Single            | TEXCOORD1  | **材质 id = 方块值**（contents+light；壳路径没有 data 位，家具已由 v0.1.56 规则塌缩） |
    /// 步长 20 B → **28 B**（+40%）。属性顺序刻意让老着色器可复用（这就是"加属性"而不是"换格式"）。
    ///
    /// 与 Iris 的关系：G-buffer 的两条缺失通道（法线、材质 id）现在有数据源了，
    /// 见 `SkylineGBuffer` / `SkylineLodVolume`。
    /// </summary>
    public struct SkylineLodVertex {
        public float X;
        public float Y;
        public float Z;
        public short Tx;
        public short Ty;
        public Color Color;
        /// <summary>xyz 是面法线（无符号归一化编码），w 保留为 255。</summary>
        public Color Normal;
        /// <summary>方块值（contents + light；data 位在 32³ 壳路径里不存在）。</summary>
        public float MaterialId;

        public static readonly VertexDeclaration VertexDeclaration = new(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementSemantic.Position),
            new VertexElement(12, VertexElementFormat.NormalizedShort2, VertexElementSemantic.TextureCoordinate),
            new VertexElement(16, VertexElementFormat.NormalizedByte4, VertexElementSemantic.Color),
            new VertexElement(20, VertexElementFormat.NormalizedByte4, VertexElementSemantic.Normal),
            new VertexElement(24, VertexElementFormat.Single, VertexElementSemantic.TextureCoordinate1)
        );

        /// <summary>顶点步长（字节）。着色器侧与内存布局必须一致，`Info()` 会断言这一点。</summary>
        public const int Stride = 28;

        /// <summary>把单位法线编成 `NormalizedByte4` 的 xyz（0..255）+ w=255。</summary>
        public static Color EncodeNormal(Vector3 normal) {
            byte enc(float v) => (byte)MathUtils.Clamp(MathF.Round((v * 0.5f + 0.5f) * 255f), 0f, 255f);
            return new Color(enc(normal.X), enc(normal.Y), enc(normal.Z), (byte)255);
        }

        /// <summary>
        /// 与 `BlockGeometryGenerator.SetupVertex` 同口径（texcoord 用 32767 定点），
        /// 追加法线与材质 id。`materialId` 传方块值本身（不是 contents）。
        /// </summary>
        public static void Setup(float x, float y, float z, Color color, float tx, float ty,
                                 Vector3 normal, float materialId, ref SkylineLodVertex vertex) {
            vertex.X = x;
            vertex.Y = y;
            vertex.Z = z;
            vertex.Tx = (short)(tx * 32767f);
            vertex.Ty = (short)(ty * 32767f);
            vertex.Color = color;
            vertex.Normal = EncodeNormal(normal);
            vertex.MaterialId = materialId;
        }

        /// <summary>六面法线（索引沿用 <see cref="SkylineFaceShading.FaceNames"/>：0=+Z、1=+X、2=-Z、3=-X、4=+Y、5=-Y）。</summary>
        public static Vector3 FaceNormal(int face) => CellFace.FaceToVector3(face < 0 || face > 5 ? 4 : face);

        public static JsonObject Describe() {
            int stride = VertexDeclaration.VertexStride;
            int marshal = Marshal.SizeOf<SkylineLodVertex>();
            JsonObject result = new() {
                ["ok"] = stride == Stride && marshal == Stride,
                ["stride"] = stride,
                ["marshalSize"] = marshal,
                ["declaredStride"] = Stride,
                ["previousStride"] = 20,
                ["bytesPerVertexDelta"] = $"+{(Stride - 20) / 20.0:P0}",
                ["attributes"] = new JsonArray(
                    new JsonObject { ["offset"] = 0, ["format"] = "Vector3", ["semantic"] = "POSITION", ["what"] = "世界坐标" },
                    new JsonObject { ["offset"] = 12, ["format"] = "NormalizedShort2", ["semantic"] = "TEXCOORD", ["what"] = "图集瓦片坐标" },
                    new JsonObject { ["offset"] = 16, ["format"] = "NormalizedByte4", ["semantic"] = "COLOR", ["what"] = "烘焙光照" },
                    new JsonObject { ["offset"] = 20, ["format"] = "NormalizedByte4", ["semantic"] = "NORMAL", ["what"] = "面法线（0.5+0.5*n）" },
                    new JsonObject { ["offset"] = 24, ["format"] = "Single", ["semantic"] = "TEXCOORD1", ["what"] = "材质 id = 方块值" }
                )
            };
            result["oldShaderStillWorks"] = true;
            result["note"] = "前 20 B 与 TerrainVertex 逐位兼容 → 游戏 Opaque 着色器可直接画这个 buffer（多出的属性被忽略）；"
                + "新增两个属性由 SkylineLodVolume 消费（法线→面明暗、材质 id→材质通道）。"
                + "材质 id 是**方块值**（contents+light），32³ 壳路径没有 data 位（家具已按 v0.1.56 规则塌缩成主材质）。";
            return result;
        }
    }

    /// <summary>桥：`skyline.LodVertexInfo()`。</summary>
    public static partial class SkylineRuntime {
        /// <summary>LOD 顶点格式（步长/属性表/兼容性），供回归清单断言。</summary>
        public static string LodVertexInfo() => SkylineLodVertex.Describe().ToJsonString();
    }
}
