using Engine;
using Engine.Media;
using System;
using System.IO;

namespace Game {
    /// <summary>
    /// [v0.1.123] **里程碑 5 的"清管线风险"实验：吃外部高度场的地形生成器**。
    ///
    /// 为什么先做它（`notes/176 §4.3` 定的下一步）：terrain-diffusion（InfiniteDiffusion）
    /// 与 SCAPI 的接缝是 `ITerrainContentsGenerator`——它的形状与"seed + (x,z) → O(1) 取值"同构。
    /// 但"SC 的地形管线能不能吃**外部高度场**"这件事与扩散模型无关，可以**先单独验证**：
    /// 用一张离线生成的 1024×1024 高度图（当"真值"）驱动生成器，看地形能不能按它长出来。
    /// 这一步**不引入模型、不改世界设置**（默认不选它，只能从测试接口临时装上）。
    ///
    /// 与 `TerrainContentsGeneratorFlat` 的关系：结构照抄（水面/湖面/基岩那套逻辑一致），
    /// 只把"每个列的地表高度"从常数 `TerrainLevel` 换成**双线性采样的高度图**；
    /// 温度/湿度暂时沿用 Flat 的常数（真接模型时这里换成一个"生物群系头"的输出）。
    /// </summary>
    public class TerrainContentsGeneratorHeightmap : ITerrainContentsGenerator {
        public SubsystemTerrain m_subsystemTerrain;

        public WorldSettings m_worldSettings;

        /// <summary>高度图（灰度，0..255 → 归一化 0..1）。</summary>
        public Image m_heightmap;

        /// <summary>高度图覆盖的世界尺度（米）：默认 4000 m（与"一张 1024² 覆盖 100 km"同形，先小一点便于测试）。</summary>
        public float SpanMetres = 4000f;

        /// <summary>起伏幅度（米）：高度 0..1 映射到 `TerrainLevel ± AmplitudeMetres/2`。</summary>
        public float AmplitudeMetres = 96f;

        /// <summary>采样原点（世界坐标的左上角）。</summary>
        public Vector2 OriginXZ = new(-2048f, -2048f);

        public int OceanLevel => m_worldSettings.TerrainLevel + m_worldSettings.SeaLevelOffset;

        public TerrainContentsGeneratorHeightmap(SubsystemTerrain subsystemTerrain, string path,
                                                 float spanMetres = 4000f, float amplitudeMetres = 96f) {
            m_subsystemTerrain = subsystemTerrain;
            SubsystemGameInfo subsystemGameInfo = subsystemTerrain.Project.FindSubsystem<SubsystemGameInfo>(true);
            m_worldSettings = subsystemGameInfo.WorldSettings;
            SpanMetres = spanMetres;
            AmplitudeMetres = amplitudeMetres;
            string full = Storage.CombinePaths(Storage.GetSystemPath("app:/"), path);
            // 注意：`Image.DetermineFileFormat` / `Image.Load` 都是**回绕采样**（ImageSharp 要 seek 头部），
            // 直接传文件流会踩"第二次 DetectFormat 时位置已不在 0"的坑 ⇒ 先整份读进 MemoryStream。
            using (Stream stream = Storage.OpenFile(full, OpenFileMode.Read)) {
                using MemoryStream memory = new();
                stream.CopyTo(memory);
                memory.Position = 0;
                m_heightmap = Image.Load(memory, Image.DetermineFileFormat(memory));
            }
            if (m_heightmap == null || m_heightmap.Width < 2 || m_heightmap.Height < 2) {
                throw new InvalidOperationException($"heightmap not usable: {full}");
            }
        }

        /// <summary>把世界 (x,z) 双线性采样成高度（米）。越界按边缘钳制。</summary>
        public float SampleHeightMetres(float x, float z) {
            float u = (x - OriginXZ.X) / MathF.Max(SpanMetres, 1f);
            float v = (z - OriginXZ.Y) / MathF.Max(SpanMetres, 1f);
            u = Math.Clamp(u, 0f, 0.9999f);
            v = Math.Clamp(v, 0f, 0.9999f);
            float px = u * (m_heightmap.Width - 1);
            float py = v * (m_heightmap.Height - 1);
            int x0 = (int)px, y0 = (int)py;
            int x1 = Math.Min(x0 + 1, m_heightmap.Width - 1);
            int y1 = Math.Min(y0 + 1, m_heightmap.Height - 1);
            float fx = px - x0, fy = py - y0;
            float H(int ix, int iy) => m_heightmap.GetPixel(ix, iy).R / 255f;
            float a = MathUtils.Lerp(H(x0, y0), H(x1, y0), fx);
            float b = MathUtils.Lerp(H(x0, y1), H(x1, y1), fx);
            float h = MathUtils.Lerp(a, b, fy);
            return m_worldSettings.TerrainLevel + (h - 0.5f) * AmplitudeMetres;
        }

        public float CalculateHeight(float x, float z) => SampleHeightMetres(x, z);

        public float CalculateMountainRangeFactor(float x, float z) => 0f;

        /// <summary>本实验里全是"陆地"（水面由高度低于 OceanLevel 的列自然形成）。</summary>
        public float CalculateOceanShoreDistance(float x, float z) => 1f;

        public Vector3 FindCoarseSpawnPosition() =>
            new(OriginXZ.X + SpanMetres * 0.5f, CalculateHeight(OriginXZ.X + SpanMetres * 0.5f,
                                                               OriginXZ.Y + SpanMetres * 0.5f),
                OriginXZ.Y + SpanMetres * 0.5f);

        public int CalculateTemperature(float x, float z) => Math.Clamp(12 + (int)m_worldSettings.TemperatureOffset, 0, 15);

        public int CalculateHumidity(float x, float z) => Math.Clamp(12 + (int)m_worldSettings.HumidityOffset, 0, 15);

        public void GenerateChunkContentsPass1(TerrainChunk chunk) {
            int ocean = OceanLevel;
            for (int i = 0; i < TerrainChunk.Size; i++) {
                for (int j = 0; j < TerrainChunk.Size; j++) {
                    int wx = i + chunk.Origin.X;
                    int wz = j + chunk.Origin.Y;
                    chunk.SetTemperatureFast(i, j, CalculateTemperature(wx, wz));
                    chunk.SetHumidityFast(i, j, CalculateHumidity(wx, wz));
                    int surface = (int)MathF.Floor(Math.Clamp(SampleHeightMetres(wx, wz), 1f,
                                                              TerrainChunk.HeightMinusOne - 1f));
                    int cellIndex = TerrainChunk.CalculateCellIndex(i, 0, j);
                    for (int k = 0; k <= TerrainChunk.HeightMinusOne; k++) {
                        int value = Terrain.MakeBlockValue(0);
                        // [v0.1.128] 里程碑 5.1：基岩层挪到**世界最低点**（与原生生成器同一口径）
                        if (k < TerrainChunk.MinHeight + 2) {
                            value = Terrain.MakeBlockValue(1);                       // 基岩层
                        }
                        else if (k < surface) {
                            value = Terrain.MakeBlockValue(
                                m_worldSettings.TerrainBlockIndex == 8 ? 2 : m_worldSettings.TerrainBlockIndex);
                        }
                        else if (k == surface) {
                            value = Terrain.MakeBlockValue(m_worldSettings.TerrainBlockIndex);
                        }
                        else if (k <= ocean) {
                            value = Terrain.MakeBlockValue(m_worldSettings.TerrainOceanBlockIndex);
                        }
                        chunk.SetCellValueFast(cellIndex + k, value);
                    }
                }
            }
        }

        public void GenerateChunkContentsPass2(TerrainChunk chunk) => UpdateFluidIsTop(chunk);

        public void GenerateChunkContentsPass3(TerrainChunk chunk) { }

        public void GenerateChunkContentsPass4(TerrainChunk chunk) { }

        public void UpdateFluidIsTop(TerrainChunk chunk) {
            _ = m_subsystemTerrain.Terrain;
            for (int i = 0; i < TerrainChunk.Size; i++) {
                for (int j = 0; j < TerrainChunk.Size; j++) {
                    int num = TerrainChunk.CalculateCellIndex(i, TerrainChunk.HeightMinusOne, j);
                    int num2 = 0;
                    int num3 = TerrainChunk.HeightMinusOne;
                    while (num3 >= 0) {
                        int cellValueFast = chunk.GetCellValueFast(num);
                        int num4 = Terrain.ExtractContents(cellValueFast);
                        if (num4 != 0 && num4 != num2 && BlocksManager.Blocks[num4] is FluidBlock) {
                            int data = Terrain.ExtractData(cellValueFast);
                            chunk.SetCellValueFast(num, Terrain.MakeBlockValue(num4, 0, FluidBlock.SetIsTop(data, true)));
                        }
                        num2 = num4;
                        num3--;
                        num--;
                    }
                }
            }
        }
    }
}
