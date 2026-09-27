# Changelog —— SCAPI Skyline Project

本文件只记录 **Skyline 分支相对上游 SCAPI 源码**的特化改动。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

## [v0.1.7] - 2026-09-27

第十七个版本：**两处交互缺陷修复（负高度手动放置 / 命令辅助棒对空自选）**，并修好被 v0.1.4
字段变更打断的工具链（AgentBridge 与分支 DLL 的字段签名不兼容）。

**本版相对 v0.1.6 的变更**：

| 类别 | 内容 |
|---|---|
| **修复 A（负高度放置）** | `ComponentMiner.DoPlace` 的放置目标高度判定仍是上游的 `num3 > 0`——世界扩到 **-1024..1023** 后这里没同步，导致 **y<0 的格子无法手动放置方块**（挖掘与 `op:place` 之外的指令放置路径不受影响）。改为 `num3 >= TerrainChunk.MinHeight && num3 < TerrainChunk.HeightMinusOne` |
| **修复 B（命令辅助棒）** | 命令方块 mod（上游 v4.2.1 源码，本仓之外）`SubsystemCmdRodBlockBehavior.OnUse`：射线**未命中任何目标**时上游回退为"选中玩家自身位置"并写入 `m_recordPosition` / `m_recordEntityName="player"`，在正式玩法里表现为"对着空气右键就把自己记下来了"。新增静态开关 `AllowPlayerFallback`（**默认 false** = 未命中原样返回、不记录）；需要旧调试行为时显式置 true |
| **修复 C（工具链）** | AgentBridge 从 NuGet `SurvivalcraftAPI.Survivalcraft 1.9.3` 改为**直接引用分支自编译产物**（`Survivalcraft`/`Engine`/`EntitySystem` + `0Harmony`，全部 `Private=false`）——v0.1.4 把 `TerrainChunk.Cells` 由 `int[]` 改为 `int[][]`，旧包的字段签名让 `teleport`/寻路直接抛 `MissingFieldException`（`Pathfinder.cs` / `TerrainIndex.cs` 只做 `== null` 判空，重编译即可） |

### Verified

* **修复 A**：同一目标区两条通道各验一次——① `op:place` 以箱子（2504,-1000,6774）为锚，在 **(2505,-1000,6774)** 放下花岗岩成功；② **真实输入通道**（`act` 写入 `PlayerInput.Interact`，即玩家右键同一条链）在 **y=-999** 放下花岗岩，`cell` 读回 `GraniteBlock`。修复前该分支落到 `Place() returned false`；
* **修复 B**：手持命令辅助棒对空右键——`AllowPlayerFallback=false`（默认）时 `m_recordPosition` 保持 `null`；A/B 置 true 后同一动作记录 `[2504,-999,6774]` 且 `m_recordEntityName="player"`（缺陷复现）；再置回 false 并把玩家移到 (2507.5,-998.99,6774.5) 后右键，记录**未更新**（证明默认不再自选玩家）；
* **修复 C**：重编译部署后 `teleport` 不再抛 `MissingFieldException`（返回正常业务结果），`cell`/`state`/寻路恢复可用；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.6] - 2026-09-27

第十六个版本：**LOD 采样策略改进（最低顶面 → 中位高度）**。

**本版相对 v0.1.5 的变更**：

| 类别 | 内容 |
|---|---|
| **改进 A（采样）** | `SkylineLod` 的单元高度从"该单元全部列里**最低**的顶面"改为"**中位**高度"：粗层（16 m，256 列）与细层（8 m，每子块 64 列）各自把 `(height<<32|value)` 打包排序取中位。"最低"策略会被单个深坑拉低，把整片地形画成下沉平板；中位对树冠/坑洞都稳健（少量异常列不改变中位）。采样缓冲移到 `Harvest` 循环外（消除 CA2014 stackalloc-in-loop） |
| 兼容 | 存储/存档格式不变（仍 v2）；**已采集的旧单元保持不变**，只有新采/补采用新策略——需要全部刷新时 `LodReset()` 后重采（`heightlab/skyline-v010-lod-verify.py --phase collect` 可复现） |

### Verified

* 构建 0 警告 0 错误（含修复 CA2014 ×6 与 XML 注释转义）；
* `LodReset()` + 重采 45 s 后 `cells=396(+1584f)`（细层单元数约为粗层的 4 倍，符合"每 chunk 4 个 8 m 子单元"）；
* 图像观察：远景 LOD 的低模形态比"最低采样"时期更接近地形轮廓（当轮场景与上一版不同——天气/时间已变，未做严格 A/B）。

## [v0.1.5] - 2026-09-27

第十五个版本：**LOD 光影接口预适配（Dawnlight / Iris）**（用户里程碑 5："需要对 LOD 的 Dawnlight
光影接口，以及学习 Iris 光影接口做预适配"）。

**本版相对 v0.1.4 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（接管点）** | `SkylineLod` 增加 —— `ExternalShaderHooked`（**默认 false = 与 v0.1.4 逐位一致**；光影包置 true 表示接管 LOD 绘制）、`CustomDraw(Camera)`（外部绘制回调，内部异常被捕获并记入 `lastError`）、`MeshMetadata()`（层/单元尺寸/半径/索引数的 JSON 元数据）。桥转发：`skyline.LodExternalShaderHooked` / `skyline.LodMeshMetadata()` |
| **文档 B** | `notes/73-LOD光影接口预适配.md`：**Dawnlight 实读结论**（MonoMod.RuntimeDetour + 77 个 shader 的延迟管线，**没有给外部 LOD 留接口**→ 接口应由我们定义）；**Iris 结构调研**（`shaderpack/pipeline/uniforms/targets/samplers/vertices/shadows/compat` 七模块与我们的对应表；其仓库自带 `DHApi.jar` 证明"Iris+DH"路线）；真接入路线图（只读网格访问器→阴影阶段→G-buffer/法线/材质 id→与 Dawnlight detour 的对接方式→以"远景明暗/阴影一致"为验收） |

### Verified

* 开关默认关闭时行为不变（`Draw` 走原路径）；开启且 `CustomDraw` 为空时安全回退内置路径
  （代码路径审查 + 构建 0 警告 0 错误）；
* `LodMeshMetadata` 输出示例：`{"coarseCells":…,"coarseIndices":…,"coarseCellSize":16,
  "fineCells":…,"fineIndices":…,"fineCellSize":8,"radiusMetres":1024,"fineRangeMetres":2048,
  "externalHooked":false}`。

## [v0.1.4] - 2026-09-27

第十四个版本：**32³ 路线第 1 步——竖直分节（16×16×256 子列，按需分配）**（用户里程碑"32³ 竖直分节"）。

**本版相对 v0.1.3 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（竖直分节）** | `TerrainChunk` 的格子存储从"整列 `Size×Size×Height` 一整块 `int[]`"拆成 **`ColumnSlicesCount = 8` 个 256 层子列（`int[][]`）**，**惰性租借**：<br>• 读未分配段 → 返回 0（空气），与"整列空段"语义一致；<br>• 写"空气"（`(value & 0x3FF) == 0`，**含带光照的空气**）→ 不触发分配；只有真正写入方块的段才租内存；<br>• `Dispose` 逐段归还到 `ArrayCache`。对外仍只经 `Get/SetCellValueFast`（(x,y,z) 与 (index) 两种）。 |
| **透明兼容** | `TerrainSerializer23` 读写走 `Get/SetCellValueFast`，**对分节完全透明**；**存档格式不变**（仍是 2048 层流，v0.0.3+ 的 yBase 兼容逻辑不变）。本次做到"同一份存档，分节前后逐格一致"。 |
| 指标读取 | `TerrainChunk.AllocatedColumnSlices`（0..8）——可直接经桥读单个 chunk 的分节数 |

### Verified

* **分节数**：玩家所在普通地面 chunk **`AllocatedColumnSlices = 1`**（原=8）；"高空测试台 + 地面"的 chunk = **2**（y≈-1024..100 的地面段 + y≈768..1023 的台子段）；
* **方块完整性**：`(2884,67,7948)=GrassBlock`、`(2884,66,7948)=DirtBlock`、`(2884,1009,7948)=SnowBlock`、`(2884,976,7936)=FurnitureBlock` 全部读回正确；
* **存档往返**：`SaveProject(true,false)` → 重启 → 同一批格子逐格一致、分节数保持；
* **内存换算**：单 chunk 从 `16×16×2048×4B = 2 MiB` 降到 1 段 `= 256 KiB`（**-87.5%**）；
  高空建筑场景（只有顶部 1-2 段有内容）同样受益。

### Known issues（如实）

* **`TerrainChunk.Cells` 的类型从 `int[]` 变为 `int[][]`**——本仓库与上游源码内没有直接
  访问它的代码（全部经 `Get/SetCellValueFast`，已 rg 核实），但**外部 mod 若直接读写 `Cells` 会编译失败**；
* 目前"哪些段被分配"由**实际写入**驱动（光照写空气不会分配 ✓），尚未做"按玩家高度主动
  预分配/回收"的加载器策略——那是 32³ 路线的下一步（notes/67 §3.2）。

## [v0.1.3] - 2026-09-27

第十三个版本：**视觉球"三档"实装**（用户 2026-09-27 指示："短球形视距内渲染 / 短球形视距和完全球形
视距之间的占位 / 球形视距外的 LOD 技术替代……不完全遮挡的家具是分配占位还是完全渲染"）。

**本版相对 v0.1.2 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（视觉球三档）** | `SkylineRender.VisualSphereEnabled`（**默认 false = 与 v0.1.2 行为一致**）：开启后区块档位按三档判定——**① 短球内**（`视距 × ShortSphereFactor`，默认 0.5，椭球判据用 `VisibilityRangeYMultiplier`）**强制 Full**（"短球形视距内渲染"，连被 d_box 判为可占位的被遮挡件也全精度）；**② 短球与完全球之间**沿用 `d_box(E)` 三态（这就是"不完全遮挡"的量化判据）；**③ 完全球之外**（> 视距）**一律 Boxed**（家具占位，地形交给 `SkylineLod` 的低模层）。5% 迟滞与每 tick 重建预算沿用 |
| 接口 | `skyline.VisualSphereEnabled`（bool）/ `skyline.VisualSphereShortFactor`（float）——运行时可切、可 A/B |

### Verified

* 近处（fill 区上方、4096 件高复杂度家具）：球**开**时短球内强制全精度——统计增量
  **full +12,287 / box +4,114**（被遮挡件不再退占位）；球**关**时按 `d_box(E)` 判（同位置
  full/box 与 v0.1.2 一致）；
* 世界加载早期（相机未就位）家具区先被判 Boxed，相机就位后按球/曲线恢复——无残留。

## [v0.1.2] - 2026-09-27

第十二个版本：**超视距 LOD 的精细度分层（双分辨率网格）**。用户反馈原文：
"视距外 LOD 精细度分层（需要通过与玩家的距离判断应采用何种精细度，以视觉上几乎无差异为目标），
目前的 LOD 非常粗糙，缺乏对地形起伏和不同材质方块的高清映射，在玩家视距比较小（比如现在 128 格视距下）非常粗糙。"

**本版相对 v0.1.1 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（多层 LOD）** | `SkylineLod` 从"单一 16 m 网格"升级为**双分辨率**：**8 m 精细层**（`FineShift=3`）覆盖 `视距 ~ 视距×FineRangeFactor(默认 2)` 的**近环**，**16 m 粗层**覆盖远环（到 `RadiusMetres`）。采集时**同一次扫描**同时填 4 个 8 m 子单元（各 8×8 列取最低顶面）+ 1 个 16 m 单元；重建时两个环**各建一套顶点/索引缓冲**（粗层先画、细层后画）；`LodSurvey/Describe` 输出两层的 cells/inMesh/indices |
| **兼容 B** | 存档 `SkylineLod.bin` 升到**版本 2**（粗层段 + 精细层段）；读版本 1（v0.1.0/0.1.1 写的）时按纯粗层兼容；**已采过的区域会自动补采 8 m 细层**（Harvest 判据改为"粗层与 4 个细子单元都齐才跳过"） |

### Verified

* 同会话实测（AgentLab、视距 128 m）：`cells=1722(+1768f) inMesh=1160(+620f)
  indices=18120(+11592f) cell=16/8`——细层已有 1768 个单元、620 个进入近环网格；
* 近环（`132~256 m`）由 8 m 单元绘制、远环由 16 m 单元绘制，图层连续；
* 旧数据兼容：本次会话把 v0.1.1 的 1698 个粗单元 + 补采的细层混合使用，无异常。

### Known issues（如实）

* **采样仍是"单点最低顶面"**：在水面/低洼处会取到水底材质（画面出现"白/浅色平面"），
  与周围沙地的观感有差；下一步应换"多点高度/材质（如 4 角采样 + 中位高度）"；
* 细层只覆盖"曾被加载/驻留过"的区域——**未探索区域仍然只有粗层**（与 DH 的"预生成"差距在同一处）；
* 尚未做"按距离的更多级"（只有 8/16 两级）。

## [v0.1.1] - 2026-09-27

第十一个版本：**性能 HUD 真实化 + LOD/视距边缘的雾统一**（本轮新里程碑的前两项交付）。
用户反馈原文："显示性能信息……目前的占有仅针对单核；GPU 占用（存疑）"；
"LOD 区块与视距边缘区块的交接感非常明显"、"玩家在雾下时，雾不会遮挡玩家对雾上 LOD 的视野"。

**本版相对 v0.1.0 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（性能 HUD）** | 原 HUD 的 `CPU x%` 只是"**主线程耗时 / 帧时间**"（单核口径）；本版新增 **SYS（系统 CPU 总占用，`GetSystemTimes` P/Invoke 差分）**、**PROC x%/16 cores（游戏进程占全机 CPU，`Process.TotalProcessorTime` 差分）**、**GPU x%（NVAPI `NvAPI_GPU_GetDynamicPstatesInfoEx` 利用率，经 `SkylineNvidia`）**。实测一串：`CPUMEM 592MB, GPUMEM 176MB(5140), CPU 2% (main thread), SYS 10%, PROC 0.7%/16 cores, GPU 25%, 29.9 FPS`。仍可通过 `settings.DisplayFpsCounter` 开关 |
| **修复 B（LOD 雾统一）** | 原版真实地形的视图雾在 `视距 × (0.8~1.0)` 处就 100% 雾化（实测 128 m 视距 → 102 m 全雾），而 `SkylineLod` 自算 `[0.55R, R]` 的雾带——**交接处"真实地形全雾消失 / LOD 无雾跳出"**，且"玩家在雾下看雾上 LOD"时两层雾行为不一致。现在 `SkylineAtmosphere.AdjustHazeSpan` 把**视图雾的跨度**拉远到 `LOD 半径 × 0.9`（默认 922 m），**LOD 层直接采用 `sky.ViewHazeStart/ViewHazeDensity`**——真实地形与 LOD 全程共用同一条雾曲线（交接连续、"雾下/雾上"行为一致）。A/B 实测：`LodFogExtend` 开/关的像素差 **182,634 px**（噪声 ~21k） |
| 接口 | `skyline.LodFogExtend`（bool，默认 true）——可在运行中 A/B 对照 |

### Verified

* HUD 五项指标经桥读出核对（`type:PerformanceManager.m_statsString`），数值与系统观测一致
  （30 fps 上限下 PROC≈0.7%/16 核、GPU≈25%、SYS≈10%）；
* 雾统一：同一观察点（玩家 y=65，正处雾带 67.6~80.2 之下）拍 `LodFogExtend` 开/关/开三连，
  差异 182,634 px、最大斑块 140,956 px，噪声 21k——**效果显著且画面无跳变**。

## [v0.1.0] - 2026-09-27

第十个版本、**第二个小版本号**：三层渲染路线的第一轮完整落地——**多层家具 LOD（占位球）实装到渲染**
与**超视距 LOD（Distant Horizons 式加载即采样）**，外加 40 GB 硬指标验证与 32³ 决策报告。
本轮用户里程碑（原文）："①多层渲染优化 ②32³ 区块加载（失败则写不可行报告）③超视距 LOD
④单区块高复杂度随机家具总内存 ≤40 GB"，并重申物理内存红线 56 GB。

**本版相对 v0.0.9 的变更**：

| 类别 | 内容 |
|---|---|
| **实装 A** | **多层家具 LOD**（新增 `Game/SkylineRender.cs`）：把 v0.0.9 实测的占替距曲线 `d_box(E)=39.6·E^0.300`（≈投影 ≤70 px²，E=暴露度）**实装到渲染决策**——超出 `d_box` 的家具实例渲染为**主材质方盒**（`SkylineFurniture.DominantMaterial`），并按区块做 Full/Mixed/Boxed 三态状态机（5% 迟滞 + 每 tick 至多 1 次重建 + 分布因子 1.4 覆盖"多窗口暴露"）。`RenderEnabled` 默认 true，`Survey/Describe` 保持只读统计能力 |
| **修复 A2** | **多层 LOD 的高空失效**：区块状态机原来只用水平距离——"水平 12 m / 垂直 ~800 m"的家具区块被判 Full（全精度），高空场景下 LOD 完全失效（实测 full=35,736）。现在用区块中心列顶部高度补竖直分量（地面场景行为不变）。双向对照：高空增量 **0 full / 8,210 box**；近处增量 **20,478 full / 4,143 box**（被遮挡件按曲线占位） |
| **实装 B** | **超视距 LOD**（新增 `Game/SkylineLod.cs`，接线 `SubsystemTerrain.Draw` + `SkylineRuntime.Tick`）：加载即采样（每 tick 2 列的 16×16 粗网格：最低顶面高度+方块值）→ 每 60 s 落盘世界目录 `SkylineLod.bin` → 半径 1024 m 内生成低模（顶面四边形 + 双面裙边墙），用地形不透明 shader 与自己的雾带 `[0.55R, R]` 渲染；`LodEnabled/LodRadiusMetres/LodDescribe/LodSurvey/LodReset/LodSaveNow` 经 `SkylineRuntime` 暴露 |
| **修复 B2** | **超视距 LOD 的两处实证缺陷**：①**视距内覆盖**——原来把 256 m 内的 806 个单元也画出来，16 m 粗平面盖住真实几何（on/off 差异 50,903 px、最大斑块 40,780 px）；修复为跳过 `≤视距+8 m` 的单元后降到 **3 px / 0**。②**采样列取树冠导致浮空平板**——采集改为扫全 256 列取最低顶面，并给相邻单元高度差加双面裙边；修复后同观察点 on/off 差异 15,954 px、最大斑块 13,618、噪声 0（地形连续成片） |
| **修复 B3** | **跨世界数据污染**（跨世界测试发现）：`SkylineLod.Load()` 原来在"目标世界没有 `SkylineLod.bin`"时不 `m_cells.Clear()`，且 `FilePath()` 只在 Save（≤60 s）里调用——切世界会把上一个世界的 LOD 数据带过去、并写进新世界的 bin（实测 World 的 bin 被污染成 1,873 个 AgentLab 坐标）。修复：`Tick()` 每帧先调 `FilePath()`（换世界立即 Load）+ `Load()` 无条件清空。复测：切到 World 后 `cells=402` 全为新采（无残留）；切回 AgentLab **立即**恢复 `loaded 1193 cells` |
| **验证 C** | **40 GB 硬指标**：单区块 16³ 随机拼配填满 **4096 件**高复杂度家具（8 种设计，每种非空体素 >10,000，每层随机组合、32 种摆放朝向）→ **物理内存峰值 21.9~26.1 GB**（两次会话实测，随当时已加载区块/LOD 数据量浮动；红线 56 GB）、进程 WS 4.9~8.9 GB、GPU 几何 116→167 MB、fps 最低 28.6、写入 458 ms |
| **报告 D** | `notes/67-32³决策报告.md`：**本轮不实装 32³**（10 小时无人值守 + 单实例 + 不可破坏存档的约束下风险收益比不划算），给出竖直分节（16×16×256 子列）的细化设计、验收判据与护栏；`notes/66-超视距LOD.md`（机制+两修复+正反面证据+持久化）；`notes/68-dt冻结陷阱与API直连控制.md`（`BasicGameTimeFactor=0` 会让所有按 dt 缩放的行为停摆——移动/视角/物理，表现为"输入注入失效"；并给出 API 直连控制法） |

### Added

* `Game/SkylineRender.cs`（多层家具 LOD + 三层球只读统计）；`Game/SkylineLod.cs`（超视距 LOD）。
* `SubsystemTerrain.Draw` 追加远景层绘制；`SkylineRuntime.Tick` 驱动采集/重建/落盘。
* `heightlab/skyline-v010-lod-verify.py`（pre/post/collect/post2-4/persist/probe 七阶段验证）、
  `heightlab/skyline-v010-lod-test.py`（4096 件高复杂度家具填充与内存测量）、`heightlab/skyline-v010-soak.py`（长时混合 soak）。

### Verified（Windows / 世界 AgentLab / RTX 4060 8GB）

* **超视距 LOD 正面证据**：驻留加载 316–583 m 地带（136 列 ≈272 MB）→ 采集 → 释放后，
  从 y=200 高空观察点做 on/off 对照：关=LOD 只有雾、开=出现低模地形，差异 15,954 px（噪声 0）。
* **持久化**：两次重启验证 `SkylineLod.bin` 恢复（`loaded 1050 cells` / `loaded 989 cells`，磁盘↔内存一致）。
* **多层 LOD 双向对照**见上表修复 A2；`RenderDescribe` 常驻可读（full/box/rebakes/trackedChunks）。
* **40 GB 指标**、几何/帧率/内存数据见验证 C。

### Known issues

* **三层球的"视觉球/加载球"仍是只读统计**（`notes/64 §7.3`）：椭球判据（`InsideVisualSphere`）
  尚未接入实际加载/渲染裁剪；"短球内全渲染 / 短-全球间占位 / 全球外 LOD"三档中，占位与 LOD
  已实装，视觉球裁剪留待 v0.1.1。
* **32³ 未实装**（决策报告见 `notes/67`）；竖直分节设计与验收判据已就绪。
* 超视距 LOD 的已知限制（`notes/66 §5`）：16 m 单点高度采样、无多级 LOD 树、未与家具占位球联动、
  高空观察时被高度雾涂淡、采集依赖"加载过"的区域。

## [v0.0.9] - 2026-09-27

第九个版本：**两处"尺度常量没同步"的修复 + 高复杂度家具的安全阀 + 一轮打崩级的压力测试**。
本轮用户里程碑是"①单区块内的高复杂度（单体 >10000）随机家具填入测试，找严重性能问题、看瓶颈、尝试修复、继续测试；
②继续深挖球形视距与 32³ 区块加载机制"，并且**首次给出内存红线**（物理占用 56 GB）。

**本版相对 v0.0.8 的变更**（v0.0.8 无引擎实现改动，交付的是稳定性证据与探索报告）：

| 类别 | 内容 |
|---|---|
| **修复 A** | **"高空写入的方块有碰撞、无渲染"**：`TerrainUpdater.CalculateChunkSliceContentsHashes` 的切片区间钳位写成 `SliceHeight - 1`（=15，上游 16 切片时代的正确值），高度扩到 2048（`SlicesCount=128`）后没同步；且 Y 区间用 `slice*SliceHeight`（0 起算）而几何生成用 `MinHeight + SliceHeight*index`（-1024 起算）→ 16 号以上切片内容哈希恒定、几何永不重建。修复后 `y=196` 的方块立即渲染，**空闲几何重建从 ≈3 万切片/10 s 降到 0~300/10 s** |
| **修复 B** | **高复杂度家具的几何预算安全阀**（新增 `Game/SkylineFurniture.cs`）：家具几何是"每实例重复烘进区块顶点缓冲"，实测一件分辨率 28 的棋盘家具（10,976 非空体素）≈263,424 顶点 ≈6.5 MB 显存 +21 MB 内存 +10 ms 生成；单区块塞满 16³ 外推 26 GB 显存 +86 GB 内存。现在每个几何生成阶段有 2,000,000 顶点预算，超出的家具**退化成方盒占位**（可见、保留碰撞），并计数 `kept/culled/boxes`，开关经 `SkylineRuntime.Furniture*` 暴露 |
| 测试/证据 | `heightlab/skyline-v009-furniture-stress.py`（普通家具 214 条调色板、分阶段密集填充、贴图多样性扫描、竖向 128 层、节流最坏情况、无写入对照轮、内存守卫）、`heightlab/skyline-v009-hi-furniture.py`（高复杂度家具：造设计/增量/网格）、`heightlab/skyline-v009-medium-regression.py`（现有特性中压回归 + 内存对照） |
| 文档 | `notes/59`（单区块家具压力测试 + 渲染修复）、`notes/60` §10（球形势距与家具三档渲染接口）、`notes/61` §4.5（32³ 与几何预算）、`notes/62`（高复杂度家具与几何预算）；并更正 v0.0.8 的勘误说明（首跑 `stress/summary.json` 假通过 → 重跑 44/44） |

### Fixed

* **高处/深处写入的方块不再"透明但有碰撞"**（见上表修复 A）。
* 顺带修复的语义坑（写进脚本，不再误导结论）：`cell` 批量写的回包 `ok` 是"读回值==写入值"，
  而引擎写入会先清光照位再由光照系统填回（villa 家具实测 value 差 15360 = 光照 15）——
  判定必须按 `now.contents/now.data`，否则会把成功写入记成失败（v0.0.9 前两轮复测就踩过）。

### Added

* `Game/SkylineFurniture.cs` + `FurnitureBlock`/`TerrainUpdater`/`SkylineRuntime` 三处接线（几何预算与方盒回退）。
* 三个可复现测试脚本（普通家具压力、高复杂度家具、现有特性中压回归）与全部原始证据目录
  `data/sessions/skyline-v009/{furniture,hi-furniture,regression,sphere-probe,render-fix}/`。

### Verified（Windows / 世界 AgentLab / RTX 4060 8GB）

* **普通家具（19 设计×4 朝向 + 46 类复杂方块，214 条调色板）**：单区块 16³（4096 格）写入
  0.076~0.093 ms/格（整箱 78~96 ms）、fps 最低 **26.3**、settle 4.5~25.6 s；
  贴图多样性 k=1…214 时 **buffer 恒为 1、draw call 恒 2433**（不是 draw call 瓶颈）；
  竖向 16×16×128（28,672 格）写入 3.62 s、fps 最低 29.2；节流 2×2 区块最低 **19.4 fps**；
  瓶颈=**光照重算（0.6~2.0 s/突发）+ 几何重建（修复后 0.9~1.5 s/突发）**。
* **高复杂度家具（分辨率 28、非空体素 10,976）**：修复前单件 ≈6.5 MB 显存 / 21 MB 内存 / 10 ms；
  256 件就把 GPU 几何推到 2,046 MB、物理内存 35.2 GB、进程工作集 14.6 GB；
  **修复后单区块 4096 件**：GPU 150→197 MB、物理内存不变、fps 最低 28.6、写入 458 ms；
  **2×2 区块 ×16³ = 16,384 件**：GPU 197→380 MB、物理 23.9→24.2 GB、fps 最低 28.4。
* **现有特性中压回归**：3×3 区块列驻留 ×3 轮（远置 500 m，释放后 allocated=0）、贝塞尔 257 站/1724 格 ×2 轮（Undo 全还原）、
  云雾 ×10、NVAPI ×10、存档 486 ms、日志异常 0；**"只 reserve/release 不建造"6 轮 + 静置 95 s**
  → 工作集 5599→5185 MB 后回落到 3828 MB（**无泄漏**，引擎区块数组缓存 60 s 到期归还）。
* 内存：全程物理占用峰值 **≈35.2 GB**（修复前那一次），修复后 ≤24.2 GB；用户红线 56 GB 未触，
  脚本内置 45/54 GB 守卫与 GPU 2 GB 守卫。

### Known issues

* 几何预算当前是"**按生成顺序先到先得**"的粗粒度安全阀：同一区块里哪几件家具拿到全精度几何是任意的，
  既不按距离也不按遮挡。用户已给出推进方向（**完全遮挡→方盒占位；不完全遮挡待判据；并与
  "短球形视距内全渲染 / 短-全球之间占位 / 全球之外 LOD"三档绑定**），接口与验收判据见 `notes/60 §10`，
  真正的分档渲染需要"多档烘焙"或"实例化/LOD 渲染路径"，留待后续版本。
* 仍未做 ≥30 分钟的长时混合 soak；`notes/54 §4` 的泄漏对照本轮已补（见 Verified）。
* 三份探索报告里仍标注为"未验证"的项（体积雾的深度纹理可用性、Aftermath 取设备、DRS 在 Optimus 上的效果）不变。

## [v0.0.8] - 2026-09-26

第八个版本：**稳定性验证 + 三项探索**。对 v0.0.5~v0.0.7 的全部新特性做了一轮中低压力复测（重跑版 **44/44 步通过**、
日志零异常），并把下一阶段的三条技术路线（球形势距 / 32³ 区块、体积雾、NVIDIA 特性）写成可决策的探索报告。
**本版不含引擎实现改动**（用户明确："涉及游戏加载引擎的改动暂不用落实"）。

> **勘误（2026-09-26 20:4x，同版本内更正）**：本版首次提交引用的 `stress/summary.json` 存在**假通过**——
> 驻留组 `reserve3x3#1..#4` 的回包是 `{"ok":false,"error":"ChunkResidencyMode_disabled"}`（脚本"释放"时关掉模式后
> 没有再打开），却被记成 `ok`，末尾 `passed` 是写死的 `42/42`；当时的复测脚本也没留在工作区。
> 现已重写脚本 `heightlab/skyline-v008-stress.py`（每一步 ok 都从回包与回读断言推导）并重跑，
> 证据 `data/sessions/skyline-v008/stress2/`，结果 **44/44 PASS**（"中低压下稳定"的原结论不变）。
> 详见 `notes/54-中低压稳定性测试.md` §5。

**本版相对 v0.0.7 的变更**：v0.0.7 只加了云雾三带预设；v0.0.8 **没有功能代码改动**，
交付的是"**现有构建的稳定性证据**"与"**三条路线的可行性结论**"（改的是文档与证据，不是引擎）。

### Added

- 稳定性复测脚本 `heightlab/skyline-v008-stress.py` + 证据 `data/sessions/skyline-v008/stress2/summary.json`（重跑版）；
  首跑产物 `data/sessions/skyline-v008/stress/summary.json` 保留作对照（其中驻留组无效，见上面的勘误）。
- 探索报告：`notes/56-球形势距与32³区块-探索.md`、`notes/57-体积雾-探索.md`、`notes/58-NVAPI特性可行性-探索.md`。

### Verified（中低压稳定性，**重跑版**；世界 AgentLab / RTX 4060 原生）

| 组 | 内容 | 结果 |
|---|---|---|
| 驻留 | 3×3 列 Reserve→Release ×5 | **10/10**；reserve 0.12~0.14 s、release 0.09~0.10 s（每轮轮询 `contentsReady==chunks` / `allocated==0`） |
| 贝塞尔 | 154 站 / 1386 格 Sweep + Undo ×5 | **10/10**；写入 1258 格、`failed=0`、`skipNotLoaded=0`、0.47~0.49 ms（一次 11.3 ms 尖峰）、Undo 每轮还原 1258 格、回读清零 |
| 云雾 | `LayeredPreset()` ↔ `Reset()` ×10 | **20/20**；`lastCloudYs=[900,880,380,340]` ↔ `enabled=False` |
| NVAPI | `ForceProbe` + `Describe` + 50 次轮询 | **3/3**；`state=ok`（RTX 4060 / driver `r610_85`），0 次失败 |
| 存档 | `SaveProject(true,false)` | ok |
| 日志 | 脚本窗口 `Game.log` | 新增 51 行、**异常 0**（`IndexOutOfRange` 0） |
| 帧率/内存 | 全程采样 | fps 29.9（帧率上限 30）；工作集 3370.6 → 3432.6 MB（列已在内存，**不判泄漏**） |

### 探索结论（摘要，详见 notes）

- **球形势距**：现有 `ChunkResidencyMode` 的"覆盖圆 + 预算守卫"就是球形势距的天然落点；
  建议先做"内容/几何/全细节"三环判据（`k` 加权竖直距离，`k=0` 可退化为现状），**不必先改存储**。
- **32³ 区块**：现状是 16×16×2048 的**整列柱**（524,288 格 ≈ 2 MB、128 片几何、整高度存档）；
  改成 32³ 等于 **尺寸/索引 + 列元数据分节 + 加载器三维化 + 几何分块 + 存档版本化**五处结构性改造，
  量级为"多轮小版本"；推荐顺序：球形势距 → 竖直分节 → 32³ 文件格式与三维区块坐标。
- **体积雾**：三条路线（A 解析式高度雾+噪声 / B 全屏 raymarch / C 层化近似）；
  **关键未知是"引擎有没有深度纹理与后处理管线"**；建议先做 A（零新增 pass），同时摸清深度可用性再决定 B/C。
- **NVIDIA 特性**：只读遥测现在就能扩（时钟/功耗/PCIe/显存用量）；`NvAPI_DRS` 可探索（写驱动 profile，需开关+回滚）；
  Aftermath 在 ANGLE-D3D11 路径"有条件"；**Reflex / DLSS / 光追都依赖 D3D12/Vulkan，当前 GLES 后端不可用**，
  要做需要先做后端预研（多轮 + 高风险）。

### Known issues

- 工作集增长（首跑 +292 MB / 重跑 +62 MB）未做"只 reserve/release、不建造"的对照轮 → **不能断言有无泄漏**，下一轮补对照。
- 本轮只为"中低压"；万格级连续建造与 ≥30 分钟 soak 留待后续。
- 三份探索报告里标注为"未知/未验证"的项（深度纹理可用性、Aftermath 取设备、DRS 在 Optimus 上的效果）都需要实测才能定论。

## [v0.0.7] - 2026-09-26

第七个版本：把用户口径的**三段高度带**变成一个"一键预设"——低层雾（近地面）+ **底层云 +300~+400** +
**高层云 ≈+900**，都按**绝对高度**规划（不做体积云/体积雾）。

**本版相对 v0.0.6 的变更**：v0.0.5 已交付 `SkylineAtmosphere`（云层高度可配、可逐层指定、可跟随相机；
雾带可偏移/跟随），但"底云 +300~400 / 高云 ≈900"这个**具体分带**要手写一串属性；
v0.0.7 只加一个 `LayeredPreset()`（以及把它的数字写进文档），其余模块与行为不变。

### Added

- **`SkylineAtmosphere.LayeredPreset()`**：一键把 4 层云按用户口径排成两带——
  头顶两圈 → **880/900**（高层云），外圈两圈 → **340/380**（底层云）；
  同时 `FogAltitudeOffsetY=80` + `FogAltitudeBlend=1`（低层雾贴着地形/相机走），`CloudAltitudeBlend=0`（云用绝对高度，分带才成立）。
  实测 `lastCloudYs=[900,880,380,340]`、`Explain()` 逐层给出 `currentY`；`Reset()` 仍然回到原版口径（`enabled=False`、`cloudModified=0`）。

### Verified

| 判据 | 结果 |
|---|---|
| `LayeredPreset()` 后逐层云高 | `[900,880,380,340]`（= 高云带 880~900、底云带 340~380） |
| 低层雾 | `fogOffset=80 fogBlend=1`，雾带跟随视图高度（`lastFogBottom/Top` 可读） |
| 关闭对照 | `Reset()` → `enabled=False cloud=60..600 cloudBlend=0`，`cloudModified=0`（与原版逐位一致） |
| 截图 | `data/sessions/skyline-v005/atmosphere/layered-high-on.png`（地面朝上：两条云带都在头顶） |

### Known issues

- 4 层云是"两带"的最小实现（每带 2 层、间距 20 格）；要做**厚度可调/更多层数**需要改 `SubsystemSky` 的层数，
  本版没做（用户也明确说"暂不需要体积云"）。
- 分带后云与云之间会出现"空白天带"（340~880 之间没云），这是有意的；不想要就 `LayerHeightsY` 自己重排。

## [v0.0.6] - 2026-09-26

第六个版本：补齐 v0.0.5 里被推迟的**曲线建筑生成**——按用户口径"**不是某个圆或者椭圆的一部分，而是通过贝塞尔曲线生成的**"：
把**从世界里切出来的一段横截面**（车道/车道线/护栏/路肩）沿**三次贝塞尔曲线**扫出去，
支持多段拼接（Catmull-Rom 过点）、贴地形、横向/竖向偏移、镜像、材质替换、`dryRun` 预览与一次撤销。

**本版相对 v0.0.5 的变更**：v0.0.5 交付的是"区块列驻留 + 蓝图/区域变换 + 分层云雾 + NVAPI 只读深化 +
最底层几何崩溃修复"，但**曲线生成当时没做出来**（被推到下一版）；v0.0.6 只补这一件——
新增 `Game/SkylineBuilder.cs`（贝塞尔曲线铺设）与一份可重跑的回归脚本，其它模块与行为不变。

### Added

- **`Game/SkylineBuilder.cs`（新文件，桥根 `skylinebuilder`）**：
  * **曲线**：三次贝塞尔（4 控制点）；控制点多于 4 个时用 Catmull-Rom 转贝塞尔（曲线**过**每个控制点、C1 连续）；
  * **弧长等距采样**：先建累计弧长表再等距取站，所以弯道处站点密度均匀（按 t 等分会在弯道处变密）；
  * **平行传输坐标系（rotation-minimizing frame）**：把法向绕相邻切线夹角轴旋转过去，避免剖面扭转；
  * **剖面**：从世界里切一段横截面（局部轴 = 横 R / 竖 U / 沿 A），逐格沿曲线变换到世界坐标写入；
  * **参数**：`step`（每几格一站）、`lat`/`vy`（横向/竖向偏移）、`mirror`（左右镜像剖面）、
    `groundFollow`/`groundOffset`（按曲线 XZ 的地表高度贴地）、`onlyAir`（默认只填空）、
    `dry`（只算不写）、`max`（格数上限）、`ensureLoaded`（自动向区块驻留申请范围）；
  * **撤销**：每次扫掠记录被覆盖格子的旧值（上限 200 万格），`Undo()` 一次整条还原；
  * **如实统计**：`stations / voxels / written / skipAir / skipNotLoaded / skipOutside / failed / recalcChunks / ms / bounds`。
- `heightlab/skyline-v006-bezier-test.py`：可重跑的实测脚本（驻留 → 建横截面 → Preview → Sweep → 抽样回读 → 截图 → Undo → 回读）。

### Verified（Windows / 世界 AgentLab，2026-09-26）

测试内容：在 (2900,100,7000) 附近建 1 厚 × 2 高 × 7 宽的"3 车道 + 车道线 + 路缘"横截面
（石砖 / 玻璃车道线 / 圆石路缘），再沿 4 控制点三次贝塞尔
`(2900,100,7000) (2940,104,7006) (2990,96,7024) (3040,100,7060)` 铺出去：

| 判据 | 结果 |
|---|---|
| 区块驻留就绪 | 84 列 `contentsReady=84`（168 MB 估算），耗时 ~8 s |
| `Preview`（只算不写） | 154 站 / 1386 格 / 包围盒 `[2899,99,6997]..[3041,102,7062]` / 0.3 ms |
| `SweepBezier`（实写） | **写入 1258 格**、`skipAir=128`、**`skipNotLoaded=0`**、`failed=0`、**0.5 ms**、重算 18 个区块列 |
| 沿曲线抽样（t=0/0.25/0.5/0.75/1.0，±3×±6×±3 窗口） | **5/5 都有路面方块**（窗口命中 20~54 格） |
| `Undo()` | 还原 1258 格 → 沿曲线再抽样 **剩余 0 格**（剖面自带的 9 格不属于本次扫掠，保留） |
| 截图 | `data/sessions/skyline-v005/builder/shots/bezier-road-on-road.png`（站在路上：可见路缘 + 玻璃车道线沿贝塞尔弯过去）、`bezier-road-above.png`（俯视） |

### Known issues

- 剖面要求"沿路径方向**厚 1 格**"；更厚的剖面会出现自交/重叠（只按 `onlyAir` 覆盖）。
- 不支持**倾斜/倾斜超高**（banking）与变截面（宽度沿曲线渐变）；曲线是 3D 贝塞尔，但剖面始终是刚体。
- `groundFollow` 用"曲线 XZ 处的地表高度"，陡坡/悬崖处会出现台阶（不是平滑坡道）。
- 撤销栈只保留**最近一次**扫掠（上限 200 万格），且不写入存档。

## [v0.0.5] - 2026-09-26

第五个版本：把分支从"范围可用、卡选得对"推进到**"能大面积盖、能高处看、能把建筑当对象搬"**——
四个新模块 + 一个既有崩溃修复（全部带运行时实测证据）：

1. **区块列驻留**：远处建造不再"静默失效"（列被钉在内存里；写入接口也不再假装成功）；
2. **NVIDIA 深化**：NVAPI 直连只读信息 + DLSS/光追**独立开关**（默认关闭，硬件支持与渲染器支持分开报告）；
3. **蓝图/区域变换**：把一个区域当对象**捕获/旋转/镜像/贴回/导入导出**，还有 Fill/Replace；
4. **分层云雾/天空高度**：云层与视图雾带的高度可配、可逐层指定、可跟随相机高度——解决"高空建筑在云上面"；
5. **修既有 bug**：世界最底层（y = MinHeight）放方块会让区块几何生成抛 `IndexOutOfRangeException`（第一列算到 `Cells[-1]`）。

**本版相对 v0.0.4 的变更**：v0.0.4 解决的是"竖直范围本身稳定 + 跑在最强显卡上"；
但**大范围施工**仍受"远处列没加载就静默丢弃"限制，**高空**仍受"云层写死在 60..600 绝对高度"限制，
建筑也只能一格一格改。v0.0.5 补的正是这三条**创作链路上的硬伤**（驻留 / 云雾高度 / 蓝图），
顺带把 NVIDIA 侧的信息面打开（为将来 DLSS / 光追铺路），并修掉一个会让主线程卡死的既有几何崩溃。

### Added

- **`Game/SkylineRuntime.cs`（+377 行）· 区块列驻留**：`ChunkResidencyMode`（**独立开关，默认关闭**）、
  `AddResidencyRegion / RemoveResidencyRegion / ClearResidencyRegions`、
  非阻塞 `EnsureRegionLoaded`（临时区 `__ensure`，TTL 默认 300 s，自动回收）、
  `ResidencyStatus()`（每区 chunks / allocated / contentsReady / geometryReady / estimatedMB）、
  `ResidencyFullDetail`（true=连几何一起生成，false=只加载内容省显存）、`ResidencyMaxChunks` 预算守卫（默认 192 ≈ 384 MB）。
  实现上只往 `TerrainUpdater` 的 update locations 追加"覆盖圆"，**不改相机可见距离**（与将来的游览模式球形视距不冲突）。
- **`Game/SkylineNvidia.cs`（新文件）· NVIDIA 深化（NVAPI 直连）**：运行时 `NativeLibrary.TryLoad("nvapi64.dll")`
  + `nvapi_QueryInterface`，函数 ID 与结构体布局逐条对照官方 `nvapi.h` / `nvapi_interface.h`；
  读出驱动版本/显卡名/显存/核心数/架构/PCI ID/温度/占用，**只读、不碰渲染管线**；
  `AllowDlss` / `AllowRayTracing` 是**独立开关（默认 false）**，且如实区分"硬件支持"与"当前渲染器支持"（本版是 OpenGL ES，两者都是 false）。
  默认打开（信息读取），可用 `-nvapi off` / 配置 `enabled=0` / 桥 `SetSwitches(false,…)` **整体关闭**；
  非 NVIDIA 机器上只是"一次 TryLoad 失败即返回"（实测 `state=no-dll`，无异常、无副作用）。
- **`Game/SkylineBlueprint.cs`（新文件）· 蓝图 / 区域变换**：`Capture / CaptureAt / Paste（旋转 0/90/180/270、XZ 镜像、只填空位）/
  Copy / MirrorRegion / Fill / Replace / Export / Import / ListFiles / Info / LastResultJson`；
  写世界一律走 `ChangeCell` + 光照重算，写完**回读校验**，并把"列没加载"如实报成 `skipNotLoaded`（绝不静默成功）。
- **`Game/SkylineAtmosphere.cs`（新文件）· 分层云雾 / 天空高度**：`CloudBaseY~CloudTopY` 可配、
  `LayerHeightsY="300,320,340,360"` **逐层指定绝对高度**、`CloudAltitudeBlend` 让云层跟随相机高度；
  `FogAltitudeOffsetY / FogAltitudeBlend` 让视图雾带随高度抬升；另有 `Explain(viewY)` 预演、`Status()`、`HighAltitudePreset()`、`Reset()`。
- `Subsystem/SubsystemSky.cs`：两处钩子（云层高度、视图雾带），**关闭时与原版逐位一致**（`cloudModified == 0` 可判据）。
- 桥侧（另一个仓库）：反射根 `skylinenvidia` / `skylinebuilder` / `skylineblueprint` / `skylineatmosphere`；
  `op:cell` 增加写入守卫（未分配列 → 明确报错并提示开驻留；内容未加载 → 报错；写后回读 → `write_did_not_stick`；`"force":true` 可跳过前两项）。

### Fixed

- **`Game/BlockGeometryGenerator.cs`（既有 bug）**：y = `TerrainChunk.MinHeight` 的方块在**区块第一列（localX=0 / localZ=0）**
  生成几何时，三处"取下面一格"的取样（`GetCellValueFast(x, y-1, z)` 与 faces 光照的 `index - 1`）会算到 `-1`，
  抛 `IndexOutOfRangeException`（原版 y=0 是基岩、不参与几何，所以没暴露）。现在最底层一律按"没有下一格"处理（空气、无光）。
  运行时 A/B（全高墙 x=2560..2575 / z=6736，横跨全高）：修复前 **30~40 条/分钟 + 主线程卡死**，修复后 **0 条、进程 Responding=True**。
- `Game/SkylineBlueprint.cs`：回读校验原本逐位比较（含 light），而 `ChangeCell` 后引擎会重算光照 →
  正常写入被误报成 `failed`（实测 192 格里有 6 格误报）。改为只比 `contents + data`（`SameBlockIgnoringLight`）。

### Changed

- `Game/TerrainUpdater.cs`（+46 行）：`ApplySkylineResidencyLocations()` 把驻留矩形铺成覆盖圆塞进 update locations（哨兵键从 -1000 起），
  只在 `SkylineRuntime.ResidencyVersion` 变化时重建；关掉开关就完全回到原版行为。
- `Game/SkylineGpu.cs`：ANGLE 路径在上下文建立后**重新应用帧率上限**（否则实测会跑成不限帧）；
  新增 `-skylinegpu angle` 强制走 ANGLE 直选（与文档口径一致）；`UsingAngle` 标记记录"是不是 Skyline 自己写的"，
  下次回到"系统偏好 + 原生 GL"时**只删自己写的那个标记**（用户手工建的不动）。
- `Game/Program.cs`：接入 `SkylineNvidia.Prepare()` / `OnGameInitialized()` / `Tick()`（NVAPI 初始化在创建窗口之前，温度/占用轮询 1 秒节流）。

### 实测证据（要点）

| 模块 | 判据 | 结果 |
|---|---|---|
| 区块列驻留 | 离玩家 467.8 m 的 64×64 区：开关关→写远处报错；开→`contentsReady=chunks` 后可写可读；关开关→释放；重开→从磁盘恢复 | **6/6 PASS ×2 轮**（释放 4.6~24.5 s，与区块数成正比：每区块落盘 ≈2 MB） |
| 蓝图/区域变换 | 8×6×4 图案：捕获 192 → 原样贴回逐格全等 → 旋转 90°（4×6×8、材质多重集一致、两次粘贴逐格一致）→ 就地镜像（192 格全对称、再镜像还原）→ Export/Forget/Import 往返 → 关驻留后远处粘贴 `written=0 / skipNotLoaded=192` | **9/9 PASS** |
| 分层云雾 | 高空 (y≈1022) 上半幅"云占比"：关闭 51.1% → 打开 76.4%；地面 7.226% → 7.224%；雾带 [79.2,91.5] → [1101.8,1114.0] | **6/6 PASS** |
| NVIDIA | `state=ok driver="r610_85" gpu="RTX 4060 Laptop GPU" vram=8187MB cores=3072 arch=Ada(AD100) temp/util 随负载变化`；指向不存在的 dll → `state=no-dll` 且无异常 | 实机复核通过 |

## [v0.0.4] - 2026-09-26

第四个版本：把 **-1024..1023** 的竖直范围从"能写能存"推进到**"各方面都稳定"**——
清掉 16 处写死的竖直上下界（`0 / 255 / 256`）并修好由此产生的 6 类行为缺陷；
同时新增**显卡自动选择**（DXGI 评分选卡 + 两种切换机制 + 重启提示）。

**本版相对 v0.0.3 的变更**：v0.0.3 只保证"范围本身可用"（写入/存档/光照/几何），
v0.0.4 补的是**范围之外的下游行为**——方块行为子系统、方块实体、寻路、阴影、降雪、VR 落点等
仍然按 0..255 的老口径工作；另外 v0.0.3 完全没有显卡选择能力。

### Added

- **`Game/SkylineGpu.cs`（新文件）**：Windows 显卡自动选择模块（非 mod，游戏本体内置），流程按需求实现：
  1. **第一次运行**用**默认显示适配器**跑起来 → DXGI 枚举全部适配器 → 按"软件适配器排除、独显 > 核显、NVIDIA 优先、显存大者优先"评分；
  2. 选出最优卡后写入 Windows"本应用显卡偏好"（`HKCU\Software\Microsoft\DirectX\UserGpuPreferences`，值名 = **exe 全路径**，值 `GpuPreference=2;`），并在主菜单弹一次**"检测到更强显卡，重启后生效"**[立即重启][稍后]；
  3. **重启后**用 `Display.DeviceDescription` 复核是否真的跑在目标卡上，结论写回 `SkylineGpu.cfg` 与日志（`applied` / `escalated` / `failed`）；
  4. 兜底：系统偏好不可用时改走 **ANGLE 按 LUID 直选 D3D11 适配器**（`eglGetPlatformDisplayEXT` + `EGL_PLATFORM_ANGLE_DEVICE_ID_HIGH/LOW_ANGLE`），这条**本次启动即可生效**。
  关闭开关：启动参数 `-skylinegpu off`；清空目标：删掉游戏目录 `SkylineGpu.cfg`。
- `Engine/Engine.Graphics/EGL.cs`、`GLWrapper.cs`：新增 `TryCreatePlatformDisplay(luidHigh, luidLow, …)` 预检并把已初始化的 `EGLDisplay` 交给 `GLWrapper` 复用（只创建一次设备）。
- `Build-Windows.ps1 -Deploy` 的部署清单增加 `libEGL.dll` / `libGLESv2.dll` / `glfw3.dll`（按 LUID 选卡依赖它们）。
- 桥侧（另一个仓库）：`skylinegpu.Describe() / Rescan() / ClearTarget()` 反射根，可用 `{"op":"invoke","target":"skylinegpu","member":"Describe","action":"call"}` 直接读状态。

### Changed（竖直范围：16 处硬编码上下界）

| 文件 | 原来写死 | 现在 |
|---|---|---|
| `Subsystem/BlockBehavior/SubsystemCollapsingBlockBehavior.cs` | `p.Y <= 0` 直接返回、`for (i = p.Y; i < 256; i++)` | `<= TerrainChunk.MinHeight`、`i <= HeightMinusOne` |
| `Subsystem/SubsystemBlockEntities.cs` | `Coordinates.Y >= 0` 才登记 | `>= TerrainChunk.MinHeight`；另**容忍重复键**（见 Fixed） |
| `Subsystem/BlockBehavior/SubsystemFluidBlockBehavior.cs` | `y < 255`、`y >= 0 && y < 255`、`while (y < 255)` | `y < HeightMinusOne`、`y >= MinHeight && y < HeightMinusOne` |
| `Subsystem/BlockBehavior/SubsystemPlantBlockBehavior.cs` | `y <= 0 \|\| y >= 255` 不生长 | `y <= MinHeight \|\| y >= HeightMinusOne` |
| `Subsystem/BlockBehavior/SubsystemWoodBlockBehavior.cs` | `Max(y - 3, 0)`、`Min(y + 3, 255)` ×2 处 | `Max(y - 3, MinHeight)`、`Min(y + 3, HeightMinusOne)` |
| `Subsystem/BlockBehavior/SubsystemDeciduousLeavesBlockBehavior.cs` | `p.Y >= 1 && p.Y < 256` | `p.Y > MinHeight && p.Y <= HeightMinusOne` |
| `Subsystem/BlockBehavior/SubsystemSoilBlockBehavior.cs` | `y > 0 && y < 254`（湿润判定） | `y > MinHeight && y < HeightMinusOne - 1` |
| `Subsystem/BlockBehavior/SubsystemMetersBlockBehavior.cs` | `num12 < 0 \|\| >= 256`、`num33 >= 0 && < 256` | `MinHeight` / `HeightMinusOne` |
| `Subsystem/SubsystemPathfinding.cs` | 阻挡检测夹 `0..255` | 夹 `MinHeight..HeightMinusOne` |
| `Subsystem/SubsystemShadows.cs`、`Subsystem/SubsystemModelsRenderer.cs` | 阴影取样 `Min(y, 255)`、`Max(y-2, 0)` | `HeightMinusOne` / `MinHeight` |
| `Subsystem/SubsystemWeather.cs` | 积雪判定 `num6 + 1 >= 255` | `num6 >= HeightMinusOne` |
| `Subsystem/SubsystemCreatureSpawn.cs` | 鳐鱼水底扫描 `num29 > 0` | `num29 > TerrainChunk.MinHeight` |
| `Component/ComponentInput.cs` | VR 落点净空 `Max(cellY, 0)` / `< 255` | `MinHeight` / `HeightMinusOne` |
| `Component/Behavior/ComponentFlyAwayBehavior.cs`、`ComponentRunAwayBehavior.cs` | 落脚点扫描 `255..0` | `HeightMinusOne..MinHeight` |

### Fixed

- **沙/砾石柱不再在范围内卡住**：原来 `p.Y <= 0` 让 y<=0 直接不判定、`< 256` 让 y>255 扫不到柱顶；
  实测 y=1001 与 y=-19 的沙柱失去支撑后**悬空不动**，现在与 y=2 的对照一样正常塌落。
- **y<0 的方块实体（箱子/熔炉/工作台/发射器…）打不开**：`SubsystemBlockEntities` 原来用 `Coordinates.Y >= 0` 过滤，
  y<0 的方块实体不会进索引 → `GetBlockEntity()` 永远返回 null → 交互链直接失败。实测 y=-3 的箱子现在能打开（弹出 `ChestWidget`）。
  同时**容忍重复键**：旧版在 y<0 不登记，同一坐标会被反复创建并一起存进存档；修好范围后这些重复项会让
  `Dictionary.Add` 抛异常 → **整个存档加载失败**。现在重复项只保留先登记的并记一条警告。
- **y>254 的液体 `isTop` 不更新**：水面被当成"未标记顶部"渲染成整块立方体；实测 y=1000 与 y=200 的水现在**同为 57 格铺开 +
  边缘 6 格外溢**，水源 `data` 都带 `0x10`（isTop）位。顺带修好 `GetSurfaceHeight` 在 y<0 / y>254 取不到液面（游泳/浮力/液面高度）。
- 一并恢复的小项：植物在地下/高处不生长、伐木后树叶连带检查越界、落叶柱扫描失效、耕地湿润判定失效、
  生物在扩展高度找不到逃跑/飞走落脚点、高处角色与生物阴影取样被夹到 y=255、高处不积雪、VR 传送落点净空只看 0..254。

### Verified（运行时 A/B 实测，2026-09-26）

| 项 | 改前（v0.0.3 构建） | 改后（v0.0.4 构建） |
|---|---|---|
| 沙柱失去支撑 y=2（对照） | 塌落 | 塌落 |
| 沙柱失去支撑 **y=1001** | **悬空不动** | 塌落到 y=1000 |
| 沙柱失去支撑 **y=-19** | **悬空不动** | 塌落（离开探测窗口 = 继续下落） |
| 箱子 **y=-1000** 交互（原始报错现场） | `handled=0`、无界面 | **`handled=1` + 弹出 `ChestWidget`**（`heightlab/skyline-v004-chest-extremes.py`，玩家先被传送到 y=-999.99 并校验落点） |
| 箱子 **y=-3** 交互 | （同上口径） | `handled=1` + 弹出 `ChestWidget` |
| 箱子 **y=+1000** 交互（对照） | — | `handled=1` + 弹出 `ChestWidget` |
| 水 y=200（对照） | 57 格铺开、isTop… | 57 格铺开、`data=0x10` |
| 水 **y=1000** | 可流动但取样/浮力失效 | 57 格铺开、6 格外溢、`data=0x10` |
| 爆炸 y=200 vs y=1000 | 均由局部 256³ 网格处理（无差异） | 同左 |
| 4852 方块批量放置（64×64 地板 + 3 高围墙） | — | **0.58–0.83 s（5,846–8,366 格/秒）**，0 失败，内存无增长 |
| `SaveProject(true,false)`（含地形区块回写） | — | 0.17–0.34 s，`Project.xml` 落盘 |

> **性能口径（避免误读）**：`Settings.xml` 的 `PresentationInterval = 2` 是"帧率上限 30"，会把游戏恒定钉在 30 FPS——
> 与显卡无关。把 `Window.PresentationInterval` 设为 0（无上限）后，同一场景在 RTX 4060 上实测 **485–930 FPS**；
> 因此"核显 58.6 / 独显 31"这类对比是**设置差异**，不是显卡性能差异。做性能对比前先统一该值。
| 旧存档里的**重复方块实体**（y<0 反复创建留下的 `(2504,-1000,6774)` 两条） | 修好登记范围后 `Dictionary.Add` 抛 `An item with the same key has already been added` → **整个存档加载失败** | 容忍重复：保留先登记的并记 `Duplicated block entity at …` 警告，存档正常加载 |

显卡选择（`Bugs/Game.log` + 独立性能计数器 `heightlab/gpu-probe.ps1`）：

```
第一次启动：4 adapters, default="AMD Radeon 780M Graphics", best="NVIDIA GeForce RTX 4060 Laptop GPU"
          wrote HKCU GPU preference GpuPreference=2 for "...\Survivalcraft.exe"
          → 本次仍跑默认核显，主菜单弹出"重启后生效"提示
            （截图：data/sessions/skyline-v004/gpu-restart-prompt.png —— 该图取自修复"首启误报升级"之前的中间构建，
              所以文案是"系统显卡偏好未生效…请再重启一次"；修复后的首启只提示"重启后生效"，
              升级提示只在系统偏好确实没生效的下一次启动才出现）
重启后  ：default="NVIDIA GeForce RTX 4060 Laptop GPU"；Renderer=NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2
          → 独立复核：Survivalcraft 进程 GPU 引擎占用 67.9%~71.4% 全在 NVIDIA LUID，AMD 侧 ~1%
ANGLE 兜底：ANGLE forced to "NVIDIA GeForce RTX 4060 Laptop GPU" (luid 00000000-00013D69)
          Renderer=ANGLE (NVIDIA, NVIDIA GeForce RTX 4060 Laptop GPU (0x000028E0) Direct3D11 …, D3D11-32.0.16.1088)
```

干净复现（把整个游戏目录复制到新路径 = 等价"全新安装"，排除驱动对旧 exe 路径的记忆）：

```
runA(首次)：default="AMD Radeon 780M Graphics", best="NVIDIA …" → 写偏好 + state=pending_restart + 弹窗，本次仍跑 AMD
           截图：data/sessions/skyline-v004/gpu-run1-first-run-prompt-v004.png
runB(重启)：default="NVIDIA …" → Renderer=NVIDIA GeForce RTX 4060 Laptop GPU/PCIe/SSE2，state=applied
```

日志原文：`data/sessions/skyline-v004/gpu-runA-fresh-amd-first-run.log.txt` / `gpu-runB-restart-nvidia.log.txt`。
同一场景（别墅 y≈88）实测帧率：原生路径（NVIDIA）≈ 30 fps；ANGLE/D3D11（NVIDIA）≈ 56 fps
（`gpu-run3-angle-nvidia.log.txt`）。
**⚠ 这个 30 不是原生路径的锅**：把 `Window.PresentationInterval` 改成 0（关掉帧率上限）后，
同一场景同一位置的**原生 NVIDIA** 实测 **485–930 fps**——因为 `Settings.xml` 里 `PresentationInterval = 2`
就是设置界面的"帧率上限 30"（= 60Hz 下每 2 个 vblank 交换一次），原生路径老实遵守它，
而 ANGLE 那条路（EGL 后端）没有把 2 当成 30fps 上限，才显得"更快"。
结论：**两种路径的能力都远超 60fps**，差别只是对 `PresentationInterval` 的解释；做性能对比前先统一该设置。
兼容模式没有帧率优势，但它会切换渲染后端，所以**默认仍走"系统偏好 + 原生"**，
ANGLE 只作兜底/可选项（`SkylineGpu.cfg` 里写 `strategy=angle` 可强制）。

构建：`Survivalcraft.Windows` Release **0 警告 0 错误**。

### Known issues

- 区块是**按 XZ 整列**（16×16×2048）加载的；离玩家很远、尚未 Valid 的列里，`ChangeCell` 写入会被静默丢弃
  （引擎原有行为，不是本版引入）。脚本化建造时请把场地放在玩家附近，或先走过去把列激活。
- `ComponentFlyAway/RunAway` 的落脚点扫描从 256 次变成 2048 次（每次决策成本上升 ~8 倍）；实测未造成可见卡顿，
  后续可改成"从生物所在高度向下 bounded 扫描"。
- 云层/雾的视觉参数仍是原版口径（云在 y≈256 附近），高空建筑会位于云层之上。

## [v0.0.3] - 2026-09-26

第三个版本：地下深度从 **-128** 对齐到 **-1024**（世界竖直范围 **-1024..1023**，共 2048 层），
建筑范围（-1024..1023）之外上下各留 **64 格生存余量**（人物安全范围 **-1088..1087**），
新增**取景模式（`FreeViewMode`）**底层接口，并修复**地下（y<0）手持方块全黑**的光照问题。

### Added

- **`Game/SkylineRuntime.cs`（新文件）**：面向"创意建筑"的运行时口径与开关，全部为静态成员，可被外部（如 AgentBridge）直接调用：
  - `SurvivalMargin = 64`：建筑范围之外仍允许角色生存的余量；
  - `FreeViewMode`：取景模式。为 `true` 时**完全不受**高度/深度伤害与缺氧限制（本版只有底层接口，**没有 UI 按钮**）；
  - `BuildMinY / BuildMaxY` = **-1024 / 1023**，`SurvivalMinY / SurvivalMaxY` = **-1088 / 1087**；
  - `IsInsideBuildRange(y)` / `IsInsideSurvivalRange(y)` / `Describe()`（一行状态，便于桥与日志读取）。

### Changed

| 文件 | 改动 |
|---|---|
| `Game/TerrainChunk.cs` | `MinHeight` **-128 → -1024**；`Height` **1152 → 2048**（层数语义：-1024..1023）；`SlicesCount` **72 → 128**；`CalculateCellIndex` 的越界处理由**抛 `ArgumentOutOfRangeException`** 改为**夹紧（clamp）** |
| `Game/SkylineRuntime.cs` | 新增（见上） |
| `Component/ComponentHealth.cs` | 缺氧上界与虚空伤害阈值改用 `SkylineRuntime.SurvivalMaxY / SurvivalMinY`；取景模式下不缺氧、不扣虚空血（虚空伤害仍为每 2 秒 `VoidDamageFactor * 0.1`） |
| `Game/TerrainSerializer23.cs` | 存档层数起点改为通式 `yBase = storedLayers > 1024 ? 1024 - storedLayers : 0` |
| `Component/ComponentFirstPersonModel.cs`、`Component/ComponentVrHandsModel.cs` | 手持取光下界 `num5 >= 0` → `num5 >= TerrainChunk.MinHeight` |
| `Managers/LightingManager.cs` | `CalculateSmoothLight` 取样下界 `num2 >= 0` → `num2 >= TerrainChunk.MinHeight`（手部模型 / 模特 / 界面取样同源） |

### Fixed

- **地下（y<0）手持方块全黑**：v0.0.1 只把取光上界改到 `HeightMinusOne`，下界仍写死 `0`；
  一旦眼位 y<0，取光分支被整段跳过，`m_itemLight` 保持初值 **0** → 手持方块按 0 光渲染 = 纯黑。
  现在下界跟随 `TerrainChunk.MinHeight`，手持亮度与该格世界光一致（暗角为 0 属正确行为）。
- **超界邻居格让整片地形消失**：几何生成会读 y±1 的邻居格，越界时 `CalculateCellIndex` 抛异常会让
  整个 slice 的网格生成中断；改为夹紧后只读到边界层，不再中断。
- **旧存档读错位**：v0.0.2 的"整高度流才从 `MinHeight` 起"只认 1152 层，换到 2048 层后会把
  256 / 1024 / 1152 层的旧档读错位；改成通式后，各版本存档都落回原 y 位置。

### Verified

Windows / 世界 `AgentLab`（SCAPI 1.9.3.1 源码树本地构建）：

| 项目 | 结果 |
|---|---|
| `IsCellValid(-1025/-1024/-1000/0/1023/1024)` | `F/T/T/T/T/F` |
| 写入并读回 y=-200 / -1000 / -1024 | 全部成功 |
| 地下 y≈-1000 的房间（方块 / 光照 9–15 / 几何渲染） | 正常 |
| 存档往返（`SaveProject` → 重启 → 读回） | 数据保留 |
| 生存余量（普通模式） | y=1050 与 y=-1050：`Health`、`Air` 恒 1；y=1100 与 y=-1100：开始掉血 + 缺氧 |
| 取景模式 | `FreeViewMode=true` 时 y=±1200 的 `Health`/`Air` 恒 1；关闭后恢复扣血 |
| 接口读数 | `Skyline build=[-1024,1023] survival=[-1088,1087] freeView=False/True` |
| 手持取光 | 眼位 y=-998.76 → `m_itemLight = 11`（与该格世界光 11 一致）；y=1001 → 15；格光 0 的暗角 → 0（修复前地下恒 0） |

### Known issues

- 每区块 **16×16×2048 = 524288 格 ≈ 2 MB**（int 4B）：约为原版（256KB）的 **8 倍**、v0.0.2（≈1.15MB）的 1.78 倍；
  视距大时注意内存（可降低 `settings.VisibilityRange`）。
- 地下（y<0）**没有地形生成**（生成器只填 y≥0），是天然空腔，适合创造模式挖建。
- 取景模式目前只有底层接口（伤害 / 缺氧豁免），UI 按钮与相机自由飞行未做。
- 旧版序列化器（14 / 22 / 129）仍按 0 起点读写（只服务很早的存档）。

## [v0.0.2] - 2026-09-26

第二个版本：世界竖直范围从 **0–1023** 扩到 **-128–1023**（地下 128 层），并保持旧存档兼容。

### Added

- 负高度（地下）支持：`TerrainChunk.MinHeight = -128`，`Height` 改为"层数"语义（= 1152 = -128..1023），
  `SlicesCount` 跟着变成 72。

### Changed

| 文件 | 改动 |
|---|---|
| `Game/TerrainChunk.cs` | 新增 `MinHeight=-128`；`Height=1152`（层数）；`CalculateCellIndex` 由**位打包**改为**算术偏移** `(y-MinHeight) + x*Height + z*Height*Size`（不再要求 Height 是 2 的幂）；`Get/SetCellValueFast` 同步加偏移；`IsCellValid` 收 `y∈[MinHeight,1023]`；`BoundingBox` 竖直范围改为 `[MinHeight, 1024)`；`CalculateTopmostCellHeight` 下探到 `MinHeight` |
| `Game/Terrain.cs` | `IsCellValid` / `GetChunkAtCell` 改用 `[MinHeight, HeightMinusOne]`；shaft 三个高度字段改为 **11 位并存储 `height - MinHeight`**（位域重排为 11+4+4+11+11=41 位，仍在 long 内） |
| `Game/TerrainUpdater.cs` | 日光/高度扫描改从 `MinHeight` 起；几何片区间改为 `MinHeight + 16*index`；**光源扩散的 `if (y > 0)` 改为 `> MinHeight`**（这是"地下灯不亮"的直接原因）；光照循环上界改 `HeightMinusOne` |
| `Game/TerrainSerializer23.cs` | 流的层数偏移：**先扫一遍流用总格数判断布局**（256 层=旧版 / 1024 层=v0.0.1 / 1152 层=新版），只有整高度的流才从 `MinHeight` 起，其余映射回 0 —— 保证老世界不下移 |
| `Component/ComponentBody.cs` | 碰撞扫格下界 `0 → MinHeight` |
| `Component/ComponentHealth.cs` | 虚空伤害下界 `Y < 0 → Y < MinHeight`；氧气/虚空上界改 `HeightMinusOne + 4/41` |
| `Game/TerrainContentsGeneratorFlat.cs`、`Subsystem/SubsystemCreatureSpawn.cs`、`Block/FireBlock.cs`、`Game/TerrainBrush.cs` | 把"把 Height 当最大 y 用"的地方改成 `HeightMinusOne` |

### Fixed

- **地下（y<0）完全不可用**：容量/索引/碰撞/光照扩散/存档偏移五处都假设 `y≥0`。
- **光谱不出地下的灯**：光源扩散里 `if (y > 0)` 硬编码。
- **老存档兼容**：v0.0.1 的 1024 层与更早的 256 层存档都能正确落回原 y 位置（实测别墅/家具无损）。

### Verified

Windows，世界 `AgentLab`：

| 项目 | 结果 |
|---|---|
| `IsCellValid(-129/-128/-64/0/1023/1024)` | `F/T/T/T/T/F` |
| 写入并读回 y=-1/-10/-64/-127/-128 | 全部成功 |
| 地下渲染 | 在 y=-65..-60 建石砖房间 + 油灯，截图正常（有明暗过渡） |
| 地下光照 | 空气格 12/13/14/15 递减（油灯 15） |
| 手持取光（y=-62 眼位） | 14 |
| 地下碰撞 | 从 x=2598 走向 x=2592 的墙，停在 **x=2593.25** |
| 地下存活 | 站在 y=-64 采样 10 次：`Health` 恒 1、`Air` 恒 1 |
| 存档往返 | `SaveProject` → 重启 → 房间/油灯/光照全部保留 |
| 旧世界无损 | 别墅地板 `MarbleBlock`、家具 `FurnitureBlock`、二层住宅都在 |

### Known issues

- 更深的层（如 -256/-512）只需调 `MinHeight`，但每区块单元数会按层数线性增长（当前 16×16×1152 ≈ 1.15 MB/区块）。
- 地下区域**没有地形生成**（生成器只填 y≥0），是天然空腔，适合创造模式挖建。
- 旧版序列化器（14/22/129）仍按 0 起点读写（只用于很早的存档）。
## [v0.0.1] - 2026-09-26

首个版本：本地仓库建立、Windows 基础构建通过、世界竖直空间从 **0–255** 扩展到 **0–1023**。

### Added

- `Build-Windows.ps1`：一键 *构建* / *构建并部署到游戏目录*（只针对 Windows 目标，
  避免 `SurvivalcraftApi.sln` 里 Android/iOS/Browser 目标所需的 workload）。
- `SKYLINE.md`：分支定位、构建部署步骤、首个特性说明与后续路线。
- 世界高度扩展：`TerrainChunk.Height = 1024`（`HeightBits = 10`、`SlicesCount = 64`、
  `HeightMinusOne = 1023`），每列 shaft 由 32 位 `int` 扩为 `long`（高度字段各 10 位）。

### Changed

| 文件 | 改动 |
|---|---|
| `Game/TerrainChunk.cs` | `HeightBits 8→10`、`Height 256→1024`、`HeightMinusOne 255→1023`、`SlicesCount 16→64`；`CalculateCellIndex` 的 z 位移改为 `HeightBits+SizeBits`；`Shafts: int[]→long[]`（含 `ArrayCache` 与 `Get/SetShaftValueFast`） |
| `Game/Terrain.cs` | shaft 位域 `8+4+4+8+8` → `10+4+4+10+10`；`Get/SetShaftValue`、`Extract*/Replace*`、`GetSeasonalTemperature/Humidity` 全部改 `long` |
| `Game/TerrainUpdater.cs` | 几何生成上界 `byte.MaxValue` → `TerrainChunk.HeightMinusOne` |
| `Game/TerrainSerializer23.cs` | `ChunkSizeY 256` → `TerrainChunk.Height` |
| `Game/TerrainSerializer14/22/129.cs` | 旧存档格式显式 `(int)`，只保存低 32 位（保持可读） |
| `Component/ComponentHealth.cs` | 氧气阈值 `259f → Height+4f`；虚空伤害阈值 `296f → Height+41f` |
| `Component/ComponentBody.cs` | 碰撞扫格上界 `255 → HeightMinusOne` |
| `Component/ComponentFirstPersonModel.cs`、`Component/ComponentVrHandsModel.cs` | 手持取光上界 `255 → HeightMinusOne` |
| `Dialog/GameMenuDialog.cs`、`Game/BlockColorsMap.cs`、`ModsManager/StandardCreatureSpawnRule.cs`、`Subsystem/SubsystemWeather.cs`、`Subsystem/SubsystemMovingBlocks.cs` | shaft 改 `long` 的编译期连带 |

合计 16 个文件，+85/−73 行。

### Fixed

- **y>255 的方块不显示**：几何生成被 `byte.MaxValue` 截断。
- **y>255 的方块没有碰撞体积**：`ComponentBody` 把身体扫格夹到 255。
- **站在高处手里方块全黑**：第一人称/VR 手持取光被 `<=255` 跳过。
- **y>255 的方块存不下来**：区块序列化的 `ChunkSizeY` 写死 256。
- **超高/超低自动扣血致死**（创造模式也扣）：`ComponentHealth` 的两个写死阈值。

### Verified

Windows，世界 `AgentLab`（创造模式，SCAPI 1.9.3.1 源码树本地构建）：

| 项目 | 结果 |
|---|---|
| `IsCellValid(255 / 256 / 1023 / 1024)` | `True / True / True / False` |
| 写入并读回 y=256 / 300 / 700 / 1000 / 1023 | 全部成功 |
| 存档往返（写 → `SaveProject` → 重启 → 读） | y=300 / 700 / 1000 方块全部保留 |
| 渲染 | y=301–305 的墙体在截图里可见 |
| 手持取光 `GetCellLightFast(眼位)` | 地面 14、y=301 为 14、y=700 为 14、y=1020 为 15 |
| 存活 | y=700 悬停 12 次采样：`Health` 恒 1、`Air` 恒 1 |
| 碰撞 | 站在 y=301 平台走向 x=2604 的挡墙，停在 x=2603.75 |
| 基础构建 | `dotnet build Survivalcraft.Windows -c Release` → 0 警告 0 错误 |

### Known issues

- 每区块单元数 65536 → 262144（内存约 ×4）；视距过大时注意。
- 命令方块 `place`（`SetCellValueFast`）不刷新 shaft/几何/光照 → 高处建造需用 `ChangeCell` 或 recalc。
- 上限 1023（`HeightBits=10`）；负 y（地下）未实现；旧存档 y>255 为空。
- 本源码树的 `Content` 比部分随包发布版新，部署时 `Content.zip` 必须与 dll 同源。
