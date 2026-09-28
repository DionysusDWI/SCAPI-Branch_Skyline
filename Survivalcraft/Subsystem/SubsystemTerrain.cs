using Engine;
using GameEntitySystem;
using TemplatesDatabase;

namespace Game {
    public class SubsystemTerrain : Subsystem, IDrawable, IUpdateable {
        public static bool TerrainRenderingEnabled = true;
        public static bool TerrainUpdaterEnabled = true;

        public Dictionary<Point3, bool> m_modifiedCells = [];

        public DynamicArray<Point3> m_modifiedList = [];

        public static Point3[] m_neighborOffsets = [
            new(0, 0, 0),
            new(-1, 0, 0),
            new(1, 0, 0),
            new(0, -1, 0),
            new(0, 1, 0),
            new(0, 0, -1),
            new(0, 0, 1)
        ];

        public SubsystemSky m_subsystemsky;

        public SubsystemTime m_subsystemTime;

        public SubsystemTimeOfDay m_subsystemTimeOfDay;

        public SubsystemGameWidgets m_subsystemViews;

        public SubsystemParticles m_subsystemParticles;

        public SubsystemPickables m_subsystemPickables;

        public SubsystemBlockBehaviors m_subsystemBlockBehaviors;

        public List<BlockDropValue> m_dropValues = [];

        public static int[] m_drawOrders = [0, 100];

        public virtual SubsystemGameInfo SubsystemGameInfo { get; set; }

        public virtual SubsystemAnimatedTextures SubsystemAnimatedTextures { get; set; }

        public virtual SubsystemFurnitureBlockBehavior SubsystemFurnitureBlockBehavior { get; set; }

        public virtual SubsystemPalette SubsystemPalette { get; set; }

        public virtual Terrain Terrain { get; set; }

        public virtual TerrainUpdater TerrainUpdater { get; set; }

        public virtual TerrainRenderer TerrainRenderer { get; set; }

        public virtual TerrainSerializer23 TerrainSerializer { get; set; }

        public virtual ITerrainContentsGenerator TerrainContentsGenerator { get; set; }

        public virtual BlockGeometryGenerator BlockGeometryGenerator { get; set; }

        public int[] DrawOrders => m_drawOrders;

        public UpdateOrder UpdateOrder => UpdateOrder.Terrain;

        public virtual void ProcessModifiedCells() {
            m_modifiedList.Clear();
            foreach (Point3 key in m_modifiedCells.Keys) {
                m_modifiedList.Add(key);
            }
            m_modifiedCells.Clear();
            for (int i = 0; i < m_modifiedList.Count; i++) {
                Point3 point = m_modifiedList.Array[i];
                for (int j = 0; j < m_neighborOffsets.Length; j++) {
                    Point3 point2 = m_neighborOffsets[j];
                    int cellValue = Terrain.GetCellValue(point.X + point2.X, point.Y + point2.Y, point.Z + point2.Z);
                    SubsystemBlockBehavior[] blockBehaviors = m_subsystemBlockBehaviors.GetBlockBehaviors(Terrain.ExtractContents(cellValue));
                    for (int k = 0; k < blockBehaviors.Length; k++) {
                        blockBehaviors[k]
                            .OnNeighborBlockChanged(point.X + point2.X, point.Y + point2.Y, point.Z + point2.Z, point.X, point.Y, point.Z);
                    }
                }
            }
        }

        public virtual TerrainRaycastResult? Raycast(Vector3 start,
            Vector3 end,
            bool useInteractionBoxes,
            bool skipAirBlocks,
            Func<int, float, bool> action) {
            float num = Vector3.Distance(start, end);
            if (num > 1000f) {
                end = start + 1000f * Vector3.Normalize(end - start);
            }
            Ray3 ray = new(start, Vector3.Normalize(end - start));
            float x = start.X;
            float y = start.Y;
            float z = start.Z;
            float x2 = end.X;
            float y2 = end.Y;
            float z2 = end.Z;
            int num2 = Terrain.ToCell(x);
            int num3 = Terrain.ToCell(y);
            int num4 = Terrain.ToCell(z);
            int num5 = Terrain.ToCell(x2);
            int num6 = Terrain.ToCell(y2);
            int num7 = Terrain.ToCell(z2);
            int num8 = x < x2 ? 1 :
                x > x2 ? -1 : 0;
            int num9 = y < y2 ? 1 :
                y > y2 ? -1 : 0;
            int num10 = z < z2 ? 1 :
                z > z2 ? -1 : 0;
            float num11 = MathF.Floor(x);
            float num12 = num11 + 1f;
            float num13 = (x > x2 ? x - num11 : num12 - x) / Math.Abs(x2 - x);
            float num14 = MathF.Floor(y);
            float num15 = num14 + 1f;
            float num16 = (y > y2 ? y - num14 : num15 - y) / Math.Abs(y2 - y);
            float num17 = MathF.Floor(z);
            float num18 = num17 + 1f;
            float num19 = (z > z2 ? z - num17 : num18 - z) / Math.Abs(z2 - z);
            float num20 = 1f / Math.Abs(x2 - x);
            float num21 = 1f / Math.Abs(y2 - y);
            float num22 = 1f / Math.Abs(z2 - z);
            while (true) {
                BoundingBox boundingBox = default;
                int collisionBoxIndex = 0;
                float? num23 = null;
                int cellValue = Terrain.GetCellValue(num2, num3, num4);
                int num24 = Terrain.ExtractContents(cellValue);
                if (num24 != 0
                    || !skipAirBlocks) {
                    Ray3 ray2 = new(ray.Position - new Vector3(num2, num3, num4), ray.Direction);
                    float? num25 = BlocksManager.Blocks[num24]
                        .Raycast(ray2, this, cellValue, useInteractionBoxes, out int nearestBoxIndex, out BoundingBox nearestBox);
                    if (num25.HasValue /* && (!num23.HasValue || num25.Value < num23.Value)*/) {
                        num23 = num25;
                        collisionBoxIndex = nearestBoxIndex;
                        boundingBox = nearestBox;
                    }
                }
                if (num23.HasValue
                    && num23.Value <= num
                    && (action == null || action(cellValue, num23.Value))) {
                    int face = 0;
                    Vector3 vector = start - new Vector3(num2, num3, num4) + num23.Value * ray.Direction;
                    float num26 = float.MaxValue;
                    float num27 = MathF.Abs(vector.X - boundingBox.Min.X);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 3;
                    }
                    num27 = MathF.Abs(vector.X - boundingBox.Max.X);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 1;
                    }
                    num27 = MathF.Abs(vector.Y - boundingBox.Min.Y);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 5;
                    }
                    num27 = MathF.Abs(vector.Y - boundingBox.Max.Y);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 4;
                    }
                    num27 = MathF.Abs(vector.Z - boundingBox.Min.Z);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 2;
                    }
                    num27 = MathF.Abs(vector.Z - boundingBox.Max.Z);
                    if (num27 < num26) {
                        //num26 = num27;
                        face = 0;
                    }
                    TerrainRaycastResult value = default;
                    value.Ray = ray;
                    value.Value = cellValue;
                    value.CellFace = new CellFace { X = num2, Y = num3, Z = num4, Face = face };
                    value.CollisionBoxIndex = collisionBoxIndex;
                    value.Distance = num23.Value;
                    return value;
                }
                if (num13 <= num16
                    && num13 <= num19) {
                    if (num2 == num5) {
                        break;
                    }
                    num13 += num20;
                    num2 += num8;
                }
                else if (num16 <= num13
                    && num16 <= num19) {
                    if (num3 == num6) {
                        break;
                    }
                    num16 += num21;
                    num3 += num9;
                }
                else {
                    if (num4 == num7) {
                        break;
                    }
                    num19 += num22;
                    num4 += num10;
                }
            }
            return null;
        }

        /// <summary>
        ///     一个不依赖于SubsystemTerrain的射线检测方法
        ///     注意！由于此方法不依赖于SubsystemTerrain，故射线检测某些方块（如家具等）会异常或报错
        ///     请谨慎使用
        /// </summary>
        /// <param name="terrain"></param>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <param name="useInteractionBoxes"></param>
        /// <param name="skipAirBlocks"></param>
        /// <param name="action"></param>
        /// <returns></returns>
        public static TerrainRaycastResult? Raycast(Terrain terrain,
            Vector3 start,
            Vector3 end,
            bool useInteractionBoxes,
            bool skipAirBlocks,
            Func<int, float, bool> action) {
            float num = Vector3.Distance(start, end);
            if (num > 1000f) {
                end = start + 1000f * Vector3.Normalize(end - start);
            }
            Ray3 ray = new(start, Vector3.Normalize(end - start));
            float x = start.X;
            float y = start.Y;
            float z = start.Z;
            float x2 = end.X;
            float y2 = end.Y;
            float z2 = end.Z;
            int num2 = Terrain.ToCell(x);
            int num3 = Terrain.ToCell(y);
            int num4 = Terrain.ToCell(z);
            int num5 = Terrain.ToCell(x2);
            int num6 = Terrain.ToCell(y2);
            int num7 = Terrain.ToCell(z2);
            int num8 = x < x2 ? 1 :
                x > x2 ? -1 : 0;
            int num9 = y < y2 ? 1 :
                y > y2 ? -1 : 0;
            int num10 = z < z2 ? 1 :
                z > z2 ? -1 : 0;
            float num11 = MathF.Floor(x);
            float num12 = num11 + 1f;
            float num13 = (x > x2 ? x - num11 : num12 - x) / Math.Abs(x2 - x);
            float num14 = MathF.Floor(y);
            float num15 = num14 + 1f;
            float num16 = (y > y2 ? y - num14 : num15 - y) / Math.Abs(y2 - y);
            float num17 = MathF.Floor(z);
            float num18 = num17 + 1f;
            float num19 = (z > z2 ? z - num17 : num18 - z) / Math.Abs(z2 - z);
            float num20 = 1f / Math.Abs(x2 - x);
            float num21 = 1f / Math.Abs(y2 - y);
            float num22 = 1f / Math.Abs(z2 - z);
            while (true) {
                BoundingBox boundingBox = default;
                int collisionBoxIndex = 0;
                float? num23 = null;
                int cellValue = terrain.GetCellValue(num2, num3, num4);
                int num24 = Terrain.ExtractContents(cellValue);
                if (num24 != 0
                    || !skipAirBlocks) {
                    Ray3 ray2 = new(ray.Position - new Vector3(num2, num3, num4), ray.Direction);
                    float? num25 = BlocksManager.Blocks[num24]
                        .Raycast(ray2, null, cellValue, useInteractionBoxes, out int nearestBoxIndex, out BoundingBox nearestBox);
                    if (num25.HasValue /* && (!num23.HasValue || num25.Value < num23.Value)*/) {
                        num23 = num25;
                        collisionBoxIndex = nearestBoxIndex;
                        boundingBox = nearestBox;
                    }
                }
                if (num23.HasValue
                    && num23.Value <= num
                    && (action == null || action(cellValue, num23.Value))) {
                    int face = 0;
                    Vector3 vector = start - new Vector3(num2, num3, num4) + num23.Value * ray.Direction;
                    float num26 = float.MaxValue;
                    float num27 = MathF.Abs(vector.X - boundingBox.Min.X);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 3;
                    }
                    num27 = MathF.Abs(vector.X - boundingBox.Max.X);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 1;
                    }
                    num27 = MathF.Abs(vector.Y - boundingBox.Min.Y);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 5;
                    }
                    num27 = MathF.Abs(vector.Y - boundingBox.Max.Y);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 4;
                    }
                    num27 = MathF.Abs(vector.Z - boundingBox.Min.Z);
                    if (num27 < num26) {
                        num26 = num27;
                        face = 2;
                    }
                    num27 = MathF.Abs(vector.Z - boundingBox.Max.Z);
                    if (num27 < num26) {
                        //num26 = num27;
                        face = 0;
                    }
                    TerrainRaycastResult value = default;
                    value.Ray = ray;
                    value.Value = cellValue;
                    value.CellFace = new CellFace { X = num2, Y = num3, Z = num4, Face = face };
                    value.CollisionBoxIndex = collisionBoxIndex;
                    value.Distance = num23.Value;
                    return value;
                }
                if (num13 <= num16
                    && num13 <= num19) {
                    if (num2 == num5) {
                        break;
                    }
                    num13 += num20;
                    num2 += num8;
                }
                else if (num16 <= num13
                    && num16 <= num19) {
                    if (num3 == num6) {
                        break;
                    }
                    num16 += num21;
                    num3 += num9;
                }
                else {
                    if (num4 == num7) {
                        break;
                    }
                    num19 += num22;
                    num4 += num10;
                }
            }
            return null;
        }

        public virtual void ChangeCell(int x, int y, int z, int value, bool updateModificationCounter = true, MovingBlock movingBlock = null) {
            bool pass = false;
            ModsManager.HookAction(
                "TerrainChangeCell",
                loader => {
                    // ReSharper disable AccessToModifiedClosure
                    loader.TerrainChangeCell(this, x, y, z, value, out bool Skip);
                    // ReSharper restore AccessToModifiedClosure
                    pass |= Skip;
                    return false;
                }
            );
            if (pass) {
                return;
            }
            if (!Terrain.IsCellValid(x, y, z)) {
                return;
            }
            int cellValueFast = Terrain.GetCellValueFast(x, y, z);
            value = Terrain.ReplaceLight(value, 0);
            cellValueFast = Terrain.ReplaceLight(cellValueFast, 0);
            if (value == cellValueFast) {
                return;
            }
            Terrain.SetCellValueFast(x, y, z, value);
            TerrainChunk chunkAtCell = Terrain.GetChunkAtCell(x, z);
            if (chunkAtCell != null) {
                if (updateModificationCounter) {
                    chunkAtCell.ModificationCounter++;
                }
                MaintainColumnTopHeight(x, y, z, value);
                // [v0.1.8] 超视距 LOD：把这一格所在的 16 m 单元标脏，远景低模才会跟着改（见 notes/75）
                SkylineLod.NotifyCellChanged(x, z);
                // [v0.1.22] 记下"刚被编辑的区块"，等它回到 Valid 时结算几何追平时延（见 notes/92）
                TerrainUpdater?.NotifyChunkEdited(x, z);
                // [v0.1.24] 批量写入（阈值以上）时把光照降级去抖：先攒着，等写完统一降级一次。
                // 普通编辑（低于阈值）行为与之前完全一致（立即降级）。
                if (!SkylineRuntime.ShouldDeferLight(chunkAtCell.Coords)) {
                    TerrainUpdater.DowngradeChunkNeighborhoodState(chunkAtCell.Coords, 1, TerrainChunkState.InvalidLight, false);
                }
            }
            m_modifiedCells[new Point3(x, y, z)] = true;
            try {
                ChangeCellToBehavior(x, y, z, cellValueFast, value, movingBlock);
            }
            catch (Exception e) {
                Log.Error($"Block behavior on terrain change execute error: {e}");
            }
        }

        /// <summary>
        /// [v0.1.9] **就地维护列顶高度**：`chunk.Shafts` 里的"顶面高度"本来由光照阶段
        /// （`TerrainUpdater.GenerateChunkSunLightAndHeight`）重算，但那要等区块状态机排队——
        /// 实测编辑后约 0.3 s（负载高时可到数秒）。而**超视距 LOD**（`SkylineLod.Harvest` 用
        /// `GetTopHeightFast`）、**寻找地表**（`Pathfinder.FindSurface`）、天气/降雪都直接读它；
        /// 不就地维护就会出现"脏重采已经发生、读到的还是旧高度"，且之后没有第二次触发
        /// （采样戳要等 `RefreshSeconds`）→ 用户报告的"挖/放之后 LOD 仍是旧数据"。
        /// 这里只动"顶面"一个字段（底/阳光高度仍由光照阶段负责），语义与光照阶段一致：
        /// * 放入非空气方块且 y 高于当前顶面 → 顶面 = y；
        /// * 挖掉的正好是顶面 → 向下找下一个非空气方块（找不到则置 0，与光照阶段的初始值一致）。
        /// </summary>
        void MaintainColumnTopHeight(int x, int y, int z, int value) {
            int top = Terrain.GetTopHeight(x, z);
            if ((value & 0x3FF) != 0) {
                if (y > top) {
                    Terrain.SetTopHeight(x, z, y);
                }
                return;
            }
            if (y != top) {
                return;                                  // 挖掉的不是列顶 → 顶面不变
            }
            int next = y - 1;
            while (next >= TerrainChunk.MinHeight
                   && (Terrain.GetCellValueFast(x, next, z) & 0x3FF) == 0) {
                next--;
            }
            Terrain.SetTopHeight(x, z, next < TerrainChunk.MinHeight ? 0 : next);
        }

        public virtual void ChangeCellToBehavior(int x, int y, int z, int oldValue, int newValue, MovingBlock movingBlock) {
            int num = Terrain.ExtractContents(oldValue);
            int num2 = Terrain.ExtractContents(newValue);
            SubsystemBlockBehavior[] blockBehaviors = m_subsystemBlockBehaviors.GetBlockBehaviors(Terrain.ExtractContents(oldValue));
            SubsystemBlockBehavior[] blockBehaviors2 = m_subsystemBlockBehaviors.GetBlockBehaviors(Terrain.ExtractContents(newValue));
            if (movingBlock?.MovingBlockSet != null) {
                if (movingBlock.MovingBlockSet.Stopped) {
                    for (int j = 0; j < blockBehaviors2.Length; j++) {
                        blockBehaviors2[j].OnBlockStopMoving(newValue, oldValue, x, y, z, movingBlock);
                    }
                }
                else {
                    for (int j = 0; j < blockBehaviors.Length; j++) {
                        blockBehaviors[j].OnBlockStartMoving(oldValue, newValue, x, y, z, movingBlock);
                    }
                }
                return;
            }
            if (num2 != num) {
                for (int i = 0; i < blockBehaviors.Length; i++) {
                    blockBehaviors[i].OnBlockRemoved(oldValue, newValue, x, y, z);
                }
                for (int j = 0; j < blockBehaviors2.Length; j++) {
                    blockBehaviors2[j].OnBlockAdded(newValue, oldValue, x, y, z);
                }
            }
            else {
                for (int k = 0; k < blockBehaviors2.Length; k++) {
                    blockBehaviors2[k].OnBlockModified(newValue, oldValue, x, y, z);
                }
            }
        }

        public virtual void DestroyCell(int toolLevel,
            int x,
            int y,
            int z,
            int newValue,
            bool noDrop,
            bool noParticleSystem,
            MovingBlock movingBlock = null) {
            int cellValue = Terrain.GetCellValue(x, y, z);
            int num = Terrain.ExtractContents(cellValue);
            Block block = BlocksManager.Blocks[num];
            if (num != 0) {
                bool showDebris = true;
                if (!noDrop) {
                    m_dropValues.Clear();
                    block.GetDropValues(this, cellValue, newValue, toolLevel, m_dropValues, out showDebris);
                    for (int i = 0; i < m_dropValues.Count; i++) {
                        BlockDropValue dropValue = m_dropValues[i];
                        if (dropValue.Count > 0) {
                            SubsystemBlockBehavior[] blockBehaviors =
                                m_subsystemBlockBehaviors.GetBlockBehaviors(Terrain.ExtractContents(dropValue.Value));
                            for (int j = 0; j < blockBehaviors.Length; j++) {
                                blockBehaviors[j].OnItemHarvested(x, y, z, cellValue, ref dropValue, ref newValue);
                            }
                            if (dropValue.Count > 0
                                && Terrain.ExtractContents(dropValue.Value) != 0) {
                                Vector3 position = new Vector3(x, y, z) + new Vector3(0.5f);
                                m_subsystemPickables.AddPickable(dropValue.Value, dropValue.Count, position, null, null);
                            }
                        }
                    }
                }
                if (showDebris
                    && !noParticleSystem
                    && m_subsystemViews.CalculateDistanceFromNearestView(new Vector3(x, y, z)) < 16f) {
                    m_subsystemParticles.AddParticleSystem(
                        block.CreateDebrisParticleSystem(this, new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), cellValue, 1f)
                    );
                }
            }
            ChangeCell(x, y, z, newValue, true, movingBlock);
        }

        public virtual void Draw(Camera camera, int drawOrder) {
            if (TerrainRenderingEnabled) {
                if (drawOrder == DrawOrders[0]) {
                    TerrainUpdater.PrepareForDrawing(camera);
                    TerrainRenderer.PrepareForDrawing(camera);
                    // [v0.1.58] 光影接入面：shadow 阶段（外部包在此准备它自己的深度/阴影资源）
                    SkylineShaderHook.Run("shadow", camera);
                    // [v0.1.113] **相机空间深度预通道**（里程碑 4 的"场景深度纹理"）：
                    //   必须在不透明地形**之前**跑，片元侧的 SSAO/接触阴影才能采样到完整的深度。
                    SkylineRuntime.ScreenDepthPass(camera);
                    TerrainRenderer.DrawOpaque(camera);
                    // [v0.1.58] 光影接入面：不透明地形画完之后（外部包在此写自己的 G-buffer / 覆盖地表 shader）
                    SkylineShaderHook.Run("opaque", camera);
                    // [v0.1.0] 超视距 LOD 层：在视距之外补一层粗网格（见 Game/SkylineLod.cs）
                    SkylineLod.Draw(camera);
                    // [v0.1.50] 32³ 表面壳网格实验层（默认关；见 Game/CubeSurface32Mesh.cs）
                    SkylineCubeSurfaceDemo.DrawIfEnabled(camera);
                    // [v0.1.51] 32³ 表面壳的**生产层**：只画"已经离开加载范围"的立方体（交接带内）
                    SkylineCubeShellStore.Draw(camera);
                    // [v0.1.58] 光影接入面：远景层（LOD + 壳）画完之后
                    SkylineShaderHook.Run("lod", camera);
                    TerrainRenderer.DrawAlphaTested(camera);
                    // [v0.1.58] 光影接入面：不透明 pass 全部结束（外部包在此做合成）
                    SkylineShaderHook.Run("composite", camera);
                    // [v0.1.120] **屏幕空间体积光**（Iris `volumetricLight.glsl` 的迁移）：
                    //   全屏加法 pass，用相机深度图重建世界位置 + 每步查太阳阴影图；
                    //   天空片元也走满 `ScreenVolumetricMaxDistance`（旧的地形内 8 步路线做不到这件事）。
                    //   默认关；关掉时这一帧不画任何东西。
                    SkylineRuntime.ScreenVolumetricLightPass(camera);
                    // [v0.1.58] G-buffer 调试直显（默认关）
                    SkylineGBuffer.DebugDrawIfEnabled(camera);
                    // [v0.1.113] 相机深度预通道的调试直显（默认关；左上角 1/4 画中画）
                    SkylineRuntime.ScreenDepthDebugDrawIfEnabled(camera);
                }
                else if (drawOrder == m_drawOrders[1]) {
                    TerrainRenderer.DrawTransparent(camera);
                    // [v0.1.58] 光影接入面：帧末（透明也画完了；后处理类效果在这里）
                    SkylineShaderHook.Run("final", camera);
                }
            }
        }

        public virtual void Update(float dt) {
            if (TerrainUpdaterEnabled) {
                TerrainUpdater.Update();
                ProcessModifiedCells();
            }
        }

        public override void Load(ValuesDictionary valuesDictionary) {
            m_subsystemViews = Project.FindSubsystem<SubsystemGameWidgets>(true);
            SubsystemGameInfo = Project.FindSubsystem<SubsystemGameInfo>(true);
            m_subsystemParticles = Project.FindSubsystem<SubsystemParticles>(true);
            m_subsystemPickables = Project.FindSubsystem<SubsystemPickables>(true);
            m_subsystemBlockBehaviors = Project.FindSubsystem<SubsystemBlockBehaviors>(true);
            SubsystemAnimatedTextures = Project.FindSubsystem<SubsystemAnimatedTextures>(true);
            SubsystemFurnitureBlockBehavior = Project.FindSubsystem<SubsystemFurnitureBlockBehavior>(true);
            m_subsystemsky = Project.FindSubsystem<SubsystemSky>();
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>();
            m_subsystemTimeOfDay = Project.FindSubsystem<SubsystemTimeOfDay>();
            SubsystemPalette = Project.FindSubsystem<SubsystemPalette>(true);
            Terrain = new Terrain();
            TerrainRenderer = new TerrainRenderer(this);
            TerrainUpdater = new TerrainUpdater(this);
            TerrainSerializer = new TerrainSerializer23(SubsystemGameInfo.DirectoryName);
            BlockGeometryGenerator = new BlockGeometryGenerator(
                Terrain,
                this,
                Project.FindSubsystem<SubsystemElectricity>(true),
                SubsystemFurnitureBlockBehavior,
                Project.FindSubsystem<SubsystemMetersBlockBehavior>(true),
                SubsystemPalette
            );
            TerrainGenerationMode terrainGenerationMode = SubsystemGameInfo.WorldSettings.TerrainGenerationMode;
            if (string.CompareOrdinal(SubsystemGameInfo.WorldSettings.OriginalSerializationVersion, "2.1") <= 0) {
                if (terrainGenerationMode == TerrainGenerationMode.FlatContinent
                    || terrainGenerationMode == TerrainGenerationMode.FlatIsland) {
                    TerrainContentsGenerator = new TerrainContentsGeneratorFlat(this);
                }
                else {
                    TerrainContentsGenerator = new TerrainContentsGenerator21(this);
                }
            }
            else if (string.CompareOrdinal(SubsystemGameInfo.WorldSettings.OriginalSerializationVersion, "2.2") == 0) {
                if (terrainGenerationMode == TerrainGenerationMode.FlatContinent
                    || terrainGenerationMode == TerrainGenerationMode.FlatIsland) {
                    TerrainContentsGenerator = new TerrainContentsGeneratorFlat(this);
                }
                else {
                    TerrainContentsGenerator = new TerrainContentsGenerator22(this);
                }
            }
            else if (string.CompareOrdinal(SubsystemGameInfo.WorldSettings.OriginalSerializationVersion, "2.3") == 0) {
                if (terrainGenerationMode == TerrainGenerationMode.FlatContinent
                    || terrainGenerationMode == TerrainGenerationMode.FlatIsland) {
                    TerrainContentsGenerator = new TerrainContentsGeneratorFlat(this);
                }
                else {
                    TerrainContentsGenerator = new TerrainContentsGenerator23(this);
                }
            }
            else if (terrainGenerationMode == TerrainGenerationMode.FlatContinent
                || terrainGenerationMode == TerrainGenerationMode.FlatIsland) {
                TerrainContentsGenerator = new TerrainContentsGeneratorFlat(this);
            }
            else {
                TerrainContentsGenerator = new TerrainContentsGenerator24(this);
            }
        }

        public override void Save(ValuesDictionary valuesDictionary) {
            TerrainUpdater.UpdateEvent.WaitOne();
            try {
                TerrainChunk[] allocatedChunks = Terrain.AllocatedChunks;
                foreach (TerrainChunk chunk in allocatedChunks) {
                    TerrainSerializer.SaveChunk(chunk);
                }
            }
            finally {
                TerrainUpdater.UpdateEvent.Set();
            }
        }

        public override void Dispose() {
            TerrainRenderer.Dispose();
            TerrainUpdater.Dispose();
            TerrainSerializer.Dispose();
            Terrain.Dispose();
        }
    }
}
