using System.Text;
using Engine;

namespace Game {
    /// <summary>
    /// SCAPI Skyline v0.1.31：**家具"透明但有碰撞"诊断**（`notes/97 §4`）。
    ///
    /// `FurnitureBlock.GenerateTerrainVertices` 有两个早退分支：
    ///   ① `generator.SubsystemFurnitureBlockBehavior == null`；
    ///   ② `behavior.GetDesign(designIndex) == null`（设计索引在当前世界里不存在）。
    /// 命中任一时家具**不生成任何几何** —— 数据可挖、有碰撞、可站，但**看不见**。
    /// 跨世界粘贴、或只写 contents 不写 data 的旧命令通道（`notes/35`）都会踩这个。
    ///
    /// 桥：`skyline.FurnitureDiagnose(x,y,z)` —— 直接给出判定与原因（见 `notes/101`）。
    /// </summary>
    public static partial class SkylineRuntime {
        public static string FurnitureDiagnose(int x, int y, int z) {
            Terrain terrain = GameManager.Project?.FindSubsystem<SubsystemTerrain>(true)?.Terrain;
            if (terrain == null) {
                return "furnitureDiagnose: no terrain";
            }
            int value = terrain.GetCellValue(x, y, z);
            int contents = Terrain.ExtractContents(value);
            int data = Terrain.ExtractData(value);
            StringBuilder sb = new();
            sb.Append($"cell=({x},{y},{z}) contents={contents} data={data} ");
            if (contents != FurnitureBlock.Index) {
                string name = contents >= 0 && contents < BlocksManager.Blocks.Length && BlocksManager.Blocks[contents] != null
                    ? BlocksManager.Blocks[contents].GetType().Name
                    : "?";
                sb.Append($"notFurniture(block={name})");
                return sb.ToString();
            }
            int designIndex = FurnitureBlock.GetDesignIndex(data);
            int rotation = FurnitureBlock.GetRotation(data);
            sb.Append($"designIndex={designIndex} rotation={rotation} ");
            SubsystemFurnitureBlockBehavior behavior =
                GameManager.Project?.FindSubsystem<SubsystemFurnitureBlockBehavior>(true);
            if (behavior == null) {
                sb.Append("behavior=MISSING -> FurnitureBlock 早退：**不生成几何（透明/有碰撞）**");
                return sb.ToString();
            }
            FurnitureDesign design = behavior.GetDesign(designIndex);
            if (design == null) {
                sb.Append("design=NULL -> **不生成几何（透明但有碰撞）**："
                    + "跨世界粘贴 / 只写 contents 的旧通道的典型症状");
                return sb.ToString();
            }
            int vertices = SkylineFurniture.CountDesignVertices(design);
            sb.Append($"design=OK resolution={design.Resolution} vertices={vertices} ");
            sb.Append(vertices > 0 ? "-> 正常渲染路径" : "-> design 顶点数为 0（异常）");
            return sb.ToString();
        }

        /// <summary>
        /// 列出当前世界**真实存在**的家具设计（索引/分辨率/顶点数）——写家具做正向对照时用它取合法索引：
        /// 合法 `data = designIndex &lt;&lt; 2 | rotation`（见 `FurnitureBlock.GetDesignIndex`）。
        /// </summary>
        public static string FurnitureDesigns(int limit = 32) {
            SubsystemFurnitureBlockBehavior behavior =
                GameManager.Project?.FindSubsystem<SubsystemFurnitureBlockBehavior>(true);
            if (behavior?.m_furnitureDesigns == null) {
                return "furnitureDesigns: no behavior";
            }
            int total = 0;
            int listed = 0;
            int max = Math.Max(1, limit);
            StringBuilder sb = new();
            for (int i = 0; i < behavior.m_furnitureDesigns.Length; i++) {
                FurnitureDesign design = behavior.m_furnitureDesigns[i];
                if (design == null) {
                    continue;
                }
                total++;
                if (listed < max) {
                    listed++;
                    sb.Append($"[{i}] res={design.Resolution} verts={SkylineFurniture.CountDesignVertices(design)}; ");
                }
            }
            return $"furnitureDesigns total={total} listed={listed}/{max}: {sb}";
        }
    }
}
