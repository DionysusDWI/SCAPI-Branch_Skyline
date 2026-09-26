using Engine;
using GameEntitySystem;

namespace Game {
    public class SubsystemBlockEntities : Subsystem {
        public Dictionary<Point3, ComponentBlockEntity> m_blockEntities = [];

        public Dictionary<MovingBlock, ComponentBlockEntity> m_movingBlockEntities = [];

        public ComponentBlockEntity GetBlockEntity(int x, int y, int z) {
            m_blockEntities.TryGetValue(new Point3(x, y, z), out ComponentBlockEntity value);
            return value;
        }

        public ComponentBlockEntity GetBlockEntity(Point3 coordinates) {
            m_blockEntities.TryGetValue(coordinates, out ComponentBlockEntity value);
            return value;
        }

        public ComponentBlockEntity GetBlockEntity(MovingBlock movingBlock) {
            m_movingBlockEntities.TryGetValue(movingBlock, out ComponentBlockEntity value);
            return value;
        }

        public override void OnEntityAdded(Entity entity) {
            ComponentBlockEntity componentBlockEntity = entity.FindComponent<ComponentBlockEntity>();
            if (componentBlockEntity != null) {
                if (!MovingBlock.IsNullOrStopped(componentBlockEntity.MovingBlock)) {
                    // [Skyline v0.0.4] 容忍重复键：宁可少登记一个，也不能在加载存档时抛异常
                    if (m_movingBlockEntities.ContainsKey(componentBlockEntity.MovingBlock)) {
                        Log.Warning($"Duplicated moving-block entity at {componentBlockEntity.MovingBlock.Position}; keeping the first one.");
                    }
                    else {
                        m_movingBlockEntities.Add(componentBlockEntity.MovingBlock, componentBlockEntity);
                    }
                }
                // [Skyline v0.0.4] 原来写死 0：y<0 的箱子/熔炉等方块实体不会被登记 → 玩家打不开
                else if (componentBlockEntity.Coordinates.Y >= TerrainChunk.MinHeight) {
                    // [Skyline v0.0.4] 旧版在 y<0 不登记方块实体 → 同一坐标被反复创建并一起存进存档；
                    // 修好登记范围后，这些重复项会让 Dictionary.Add 抛 "An item with the same key has already been added"，
                    // 直接导致整个存档加载失败。这里改为容忍重复：保留先登记的那个，其余只记警告。
                    if (m_blockEntities.TryGetValue(componentBlockEntity.Coordinates, out ComponentBlockEntity existing)) {
                        if (existing != componentBlockEntity) {
                            Log.Warning($"Duplicated block entity at {componentBlockEntity.Coordinates}; keeping the first one.");
                        }
                    }
                    else {
                        m_blockEntities.Add(componentBlockEntity.Coordinates, componentBlockEntity);
                    }
                }
            }
        }

        public override void OnEntityRemoved(Entity entity) {
            ComponentBlockEntity componentBlockEntity = entity.FindComponent<ComponentBlockEntity>();
            if (componentBlockEntity != null) {
                m_blockEntities.Remove(componentBlockEntity.Coordinates);
                if (componentBlockEntity.MovingBlock != null) {
                    m_movingBlockEntities.Remove(componentBlockEntity.MovingBlock);
                }
            }
        }
    }
}
