namespace Game {
    /// <summary>
    /// SCAPI Skyline Project —— 面向"创意建筑"的运行时开关与常量（**新挂载的模块**，非 mod）。
    ///
    /// 设计口径（v0.0.3）：
    ///   * 世界竖直范围 = <see cref="TerrainChunk.MinHeight"/> .. <see cref="TerrainChunk.HeightMinusOne"/>
    ///     （当前 -1024..1023，共 2048 层）。
    ///   * **生存余量** <see cref="SurvivalMargin"/> = 64：建筑范围上下各留 64 格，
    ///     角色在 [BuildMinY-64, BuildMaxY+64] = [-1088, 1087] 内不掉血、不缺氧；
    ///     超出这个范围才按原版逻辑扣虚空伤害/缺氧。
    ///   * <see cref="FreeViewMode"/>（取景模式）：打开后**完全不受高度/深度伤害限制**，
    ///     为将来"超限高度飞行取景/漫游"做底层准备（本版暂不加 UI 按钮）。
    ///
    /// 这些静态成员可以被外部直接调用，例如 AgentBridge：
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime.FreeViewMode","value":true}
    ///   {"op":"invoke","target":"type:Game.SkylineRuntime","member":"Describe","args":[]}
    /// </summary>
    public static class SkylineRuntime {
        /// <summary>建筑范围之外、仍允许角色生存的余量（格）。</summary>
        public const int SurvivalMargin = 64;

        /// <summary>取景模式：不受高度/深度伤害与缺氧限制（默认关闭）。</summary>
        public static bool FreeViewMode { get; set; }

        /// <summary>可建造的最低层。</summary>
        public static int BuildMinY => TerrainChunk.MinHeight;

        /// <summary>可建造的最高层。</summary>
        public static int BuildMaxY => TerrainChunk.HeightMinusOne;

        /// <summary>角色生存的下限（含 64 格余量）。</summary>
        public static int SurvivalMinY => TerrainChunk.MinHeight - SurvivalMargin;

        /// <summary>角色生存的上限（含 64 格余量）。</summary>
        public static int SurvivalMaxY => TerrainChunk.HeightMinusOne + SurvivalMargin;

        /// <summary>该 y 是否在建筑范围内。</summary>
        public static bool IsInsideBuildRange(int y) => y >= BuildMinY && y <= BuildMaxY;

        /// <summary>该 y 是否允许角色存活（取景模式恒为真）。</summary>
        public static bool IsInsideSurvivalRange(float y) =>
            FreeViewMode || (y >= SurvivalMinY && y <= SurvivalMaxY);

        /// <summary>一行状态，便于桥/日志读取。</summary>
        public static string Describe() =>
            $"Skyline build=[{BuildMinY},{BuildMaxY}] survival=[{SurvivalMinY},{SurvivalMaxY}] freeView={FreeViewMode}";
    }
}
