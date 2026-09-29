namespace Game {
    /// <summary>
    /// [v0.1.128] 里程碑 5.1：**原生地形生成器的"低基线"换算**（21/22/23/24 四个版本共用）。
    ///
    /// 用户口径："将基岩层下调到与现有最低点对齐，**基岩应该在 -1024 处**，
    /// 尝试以此高度自然生成游戏原生引擎的地形。"
    ///
    /// 背景（源码实况）：本分支世界是 **-1024..1023**，而上游四个原生生成器的竖直语义
    /// 全部写死在"**海平面 64、世界 0..255**"上：
    ///   * `OceanLevel => 64 + SeaLevelOffset`；
    ///   * `CalculateHeight` 结尾 `Clamp(64f + 起伏, 10f, 251f)`；
    ///   * 密度网格 `new(num/4+1, 33, num2/4+1)`（8 格一采样 ⇒ 0..256）；
    ///   * 一票列扫描 `for (y = 254; y >= 0; y--)`，以及雪线 120 / 沙滩线 66 / 草线 84/103 这类阈值。
    ///
    /// 做法：**算法继续按"旧语义坐标"跑**（阈值一个都不用重标），只在**读写方块/采样噪声**时
    /// 用 `Wy()` 映射到世界坐标；基岩一律写在世界最低点。
    /// `TerrainLevel > 0`（上游默认 64）时 `Offset = 0` ⇒ 逐位回到上游行为。
    /// </summary>
    public static class SkylineTerrainBaseline {
        /// <summary>旧语义的海平面（上游写死值）。</summary>
        public const int LegacySeaLevelY = 64;

        /// <summary>地形基线：世界设置里 `TerrainLevel` 为非正数就用它，否则用 64。</summary>
        public static int Resolve(WorldSettings settings) =>
            settings != null && settings.TerrainLevel <= 0 ? settings.TerrainLevel : LegacySeaLevelY;

        /// <summary>世界 y − 旧语义 y。</summary>
        public static int Offset(WorldSettings settings) => Resolve(settings) - LegacySeaLevelY;

        /// <summary>旧语义 y → 世界 y。</summary>
        public static int Wy(WorldSettings settings, int legacyY) => legacyY + Offset(settings);

        /// <summary>
        /// [v0.1.128] 把"从 y=0 起算的层数"钳到世界底。
        ///
        /// 上游 `GenerateSurface` 用 `MathUtils.Min(0..7, num4)` 表示"最多铺到 y=0 为止"；
        /// 世界底变成 **−1024** 之后，表面对应 y 可以是负数 ⇒ 直接 `Min(层数, 负数)` 会得到负数，
        /// `for (k = num11 − num7; k &lt; num11; k++)` 一次都不执行 ⇒ **表层（泥土/沙/黏土）完全不铺**
        /// （实机现象：低基线世界的地表是**裸露的岩石** Granite/Basalt/Limestone）。
        /// 这里把语义改成"最多铺到世界最低层为止"，`TerrainLevel &gt; 0` 时与上游逐位一致。
        /// </summary>
        public static int LayersAboveBottom(int count, int worldY) =>
            Math.Min(count, worldY - TerrainChunk.MinHeight);

        /// <summary>覆盖整个世界高度的 8 格密度采样数（上游写死 33）。</summary>
        public static int DensitySamplesY =>
            (TerrainChunk.HeightMinusOne - TerrainChunk.MinHeight) / 8 + 2;
    }
}
