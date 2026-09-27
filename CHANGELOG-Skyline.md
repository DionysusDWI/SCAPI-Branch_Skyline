# Changelog —— SCAPI Skyline Project

本文件只记录 **Skyline 分支相对上游 SCAPI 源码**的特化改动。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

## [v0.1.52] - 2026-09-28

第六十二个版本：**分距离 LOD 精度阶梯 + 网格滑动窗口**（本轮新目标 3.1/3.2）。
用户口径："只有视距之外一定距离内的 LOD 块才需要做到完整 32³ 精度，更远一点可以降到 16³、8³、4³、2³，
最后变成一整块；注意这里的精度指的同时是**最小体素**和**最小材质分片**的精度。"

**本版相对 v0.1.51 的变更**：

| 类别 | 内容 |
|---|---|
| **精度阶梯** | 新增档位表 `TierMetres = [48,96,192,384,768]`（相对视距的米数）→ `step = 1/2/4/8/16/32`（即 32³/16³/8³/4³/2³/整块），`skyline.CubeShellTierProbe` 可直接验证映射 |
| **降精度的做法** | 壳**仍按 1 m 采集**（16 KiB 不变）：采集只有一次机会，采粗回不去；**降精度发生在建网格时** —— `CubeSurface32Mesh.Aggregate` 把 `step×step` 的壳格聚合成一格（高度取"主流材质列"的中位、材质取众数，与现有 LOD 同口径），`step` 同时就是**最小体素**与**最小材质分片**的边长 |
| **网格滑动窗口** | `MeshSlidingWindow`（默认开）：地形已加载、或距离超出绘制范围 → **释放网格**（壳留着），需要时按当前档重建；桥 `CubeShellMeshTiers(cx,cy,cz)` 给出"同一张壳在各档下的代价表" |
| **解耦修复** | `BandMetres` 原来取档位表最后一项 → 把档位表调粗会**把绘制范围一起缩没**（实测 `meshResident=0`、一张都没画）。已改为带宽独立（默认 768 m） |

### Verified（AgentLab，视距 128 → 64）

| 项 | 值 |
|---|---|
| **档位映射**（相对视距 → 最小体素/分片） | 0–48 → **1 m**、49–96 → **2 m**、97–192 → **4 m**、193–384 → **8 m**、385–768 → **16 m**、≥769 → **32 m（整块）**；边界逐一命中 |
| **同一张壳在各档下的代价**（真实立方体 `(136,2,285)`，朴素口径 2,822 四边形） | 四边形 **878 → 118 → 35 → 13 → 10 → 1**；建网格 **3.222 → 0.122 → 0.066 → 0.058 → 0.052 → 0.050 ms**；**每档覆盖精确（1024/256/64/16/4/1）、合并违规全 0** |
| **真实渲染三态** | A(壳关) 亮度 128.83 / B(默认阶梯) 121.57 / C(压到最粗) 119.70；**A↔B 224,118 px**、**B↔C 197,122 px**；网格内存 B **1.11 MiB** vs C **0.03 MiB（37×）** |
| 放大对照（`cmp-ladder-abc.png`） | **现状（A）那一片几乎是空的**（视距外那圈既没有真实几何、现有 LOD 也没铺满）；**B 把地形连同真实材质补上**（沙色地形/石脊/绿边、1~4 m 起伏）；**C 用大块便宜地填** |
| **网格滑动窗口** | 把某壳的 2×2 区块驻留回内存 → 常驻网格 **73 → 9**、累计释放 **65 → 129（+64）**；驻留关掉后回到 73 |
| 采集侧未变 | 壳仍 16 KiB/立方体；本轮 82 个立方体 = 壳 1.28 MiB；fps **29.8 → 29.8** |

**本版踩到的坑（如实记）**：①档位表把带宽带跑了（已解耦）；②`op:teleport` 的 `surface:true` **忽略 x/z**
（它是"以当前位置为种子找最近地表"的脱困模式），且目标列不一定有可站立地表 →
测试改用"驻留制造卸载"，本版不再依赖传送；③**驻留强拉起来的区块永远到不了 Valid**（光照/几何没算）→
不能作为壳的数据源（实测 `skippedNotReady` 19 → 126、一个都没采到），这是设计使然，也正对应"走过一次才有远景"。

**还没做**：①**壳存档 P4**（关机即丢）；②更宽的采集口径 + `RequireNeighbors`；
③非完整方块 → **材质占位方盒**（新目标 3.3）；④家具 data 缺口。
证据：`data/sessions/skyline-v0152/`、`notes/127`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.51] - 2026-09-27

第六十一个版本：**4.3 第三步 —— 32³ 表面壳接上生产路径**（卸载即采集 → 交接带渲染 → LOD 让位）。
v0.1.49 造壳、v0.1.50 建网格并证明能画，但都是"手动采集 + 实验层"；本版把它接成和
Distant Horizons 同构的机制：**走过 → 离开加载范围 → 存成壳 → 真实地形释放后由壳顶上**。

**本版相对 v0.1.50 的变更**：

| 类别 | 内容 |
|---|---|
| **卸载预扫（关键修复）** | 第一版把采集挂在逐个区块的卸载钩子上，实测 **197 次全部 `skippedNotReady`**：卸载是同一个循环里逐个 `FreeChunk` 的，轮到某区块时它同一 32³ 立方体的兄弟区块往往已被释放。改为在释放循环**之前**做一次**预扫**（先把整批要离开的区块列出来，此时谁都还在），再按立方体完整采集；释放语义一点没动 |
| **壳仓** | `SkylineCubeShellStore`：立方体坐标 → 壳（16 KiB），可选"四邻齐全"过滤、上限 `MaxCubes=4096` 与 LRU 淘汰、按预算建网格、统计齐全（`CubeShellSurvey()`） |
| **渲染规则** | ①**地形还在就不画**（2×2 区块任一已分配 → 跳过，`skippedBecauseLoaded`）；②只在**交接带** `[视距−8, 视距+96]` 内画（与 v0.1.45 的 4 m 近环同一带，更远交给现有 16 m LOD）；③**LOD 让位**（`RestrictLod`：该立方体带内有壳 → 现有 LOD 不画它）；④让位判定与绘制判定**同口径**（同一个立方体中心），避免"让位了却没画"的洞；⑤带内抬高 0.35 m |
| **预算** | 采集单次上限 `InlineHarvestPerCall=64`（实测 **0.47 ms/立方体**）、建模 `MeshBudgetMs=4`、每帧最多画 `MaxDrawPerFrame=192` |
| **桥** | `skyline.CubeShellSurvey / CubeShellClear / CubeShellEnabled / CubeShellRender / CubeShellGreedy / CubeShellRestrictLod / CubeShellRequireNeighbors` |

### Verified（AgentLab，视距 128 → 64 → 128）

| 项 | 值 |
|---|---|
| 采到的立方体 | **40**（`queued=0` 全部建完网格） |
| 内存 | 壳 **0.62 MiB**（40×16 KiB）+ 网格 **1.41 MiB**（≈36 KiB/立方体） |
| 采集耗时 | **0.47 ms/立方体**；`skippedOverBudget=0`、`skippedNotReady=19` |
| fps | **29.4 → 30**（没有掉帧） |
| **LOD 让位** | 近环 4 m 单元 **1,735 → 131（−92%）**、细层 8 m 127 → 89、粗层 16 m 304（远场不动） |
| **渲染 A/B**（A=现状 / B=壳开不让位 / C=壳开+让位） | **A↔C 80,105 px**、**B↔C 33,787 px**；亮度 120.28 / 120.83 / **121.60** |
| 观感（放大对照 `cmp-band-abc.png`） | **现状 LOD 栈本身就是花白的**（4/8/16 m 三层互相穿插）；**壳 + 让位反而干净**（一张连贯的 1 m 表面） |

**还没做（如实）**：①壳的覆盖天然是补丁状（只有走过并离开过加载范围的立方体才有壳），想要交接带**整圈**有壳需要更宽的采集口径；
②让位是逐立方体判断，不是整带接管，边界仍有锯齿；③**壳存档（P4）没做**（关机即丢）；
④家具 data 缺口仍在；⑤`MaxCubes=4096` 时壳 64 MiB + 网格约 145 MiB，网格是更大的开销，后续要改成"壳常驻、网格滑动窗口"。
证据：`data/sessions/skyline-v0151/`、`notes/124`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.50] - 2026-09-27

第六十个版本：**4.3 第二步 —— 32³ 表面壳的面剔除 + 贪心合并 + 真绘制**。
v0.1.49 的壳解决了"内存"（16 KiB vs 128 KiB），但如实记了一条硬伤：**朴素口径每个非空壳格 1 个四边形**
（平均 4,470 个/立方体）—— 省了内存、赔了绘制。本版把这条补上，并第一次把壳**画进画面**。

**本版相对 v0.1.49 的变更**：

| 类别 | 内容 |
|---|---|
| **网格化** | `CubeSurface32Mesh.cs`（新）：壳的顶面本就是 32×32 高度场 → **面剔除**（相邻列等高就不生成内部面，只有落差处拉裙边）+ **2D 贪心合并**（等高+同材质+同光照的矩形并成一个四边形；裙边按连续等高邻居合并成条带） |
| **两条判据** | `coveredCells` 必须 == `nonEmptyCells`（覆盖不漏不多）、`mergeViolations` 必须为 0（合并矩形内处处等高同材质）——每次采集都自检并报出来 |
| **真绘制** | `SkylineCubeSurfaceDemo`：用**与真实地形/LOD 同一套 shader + 图集 + 雾参数**画壳网格，挂在地形不透明 pass 之后；`skyline.CubeSurfaceHarvest/Draw/Greedy/DrawOffset` |
| **光照口径修正（真 bug）** | 第一次画出来是**全黑四边形** → 根因：SC 里"格子自己的 light 位"只有暴露在光里的那一格才有值（实测平台石砖 `contents=26, light=0`，它上面的空气 `light=15`；自然地表雪块 15，其下泥土 0）。v0.1.49 取了列顶方块自己的 light → 全 0 → 黑。改为取**空气那一侧**邻格的 light（`CubeSurface32.SurfaceLight`），顶/底/侧面一致 |
| **顺带记下的坑** | `ChunkResidencyMode` 硬拉起来的区块只到 `InvalidLight`（**光照没算**）→ 在那上面采集也会全黑；采集结果现在报 `zeroLightCells/zeroLightCubes` 作为判据 |

### Verified（AgentLab，视距 128 → 测试时降到 64 再恢复）

| 组 | 朴素壳口径 | 逐格（剔面） | 贪心合并 | 合并后顶点字节 | 省下 |
|---|---|---|---|---|---|
| 3×3 立方体 / 96×96 m（玩家处） | 23,872 | 13,012 | **5,334** | 426,720 B | **−59.0%**（对逐格） |
| 2×2 立方体 / 64×64 m（视距内 96 m） | 15,502 | 6,116 | **2,747** | 219,760 B | **−55.1%** |

| 项 | 值 |
|---|---|
| 覆盖判据 `coveredCells == nonEmptyCells` | 9,216 == 9,216（3×3）/ 4,096 == 4,096（2×2） |
| 合并判据 `mergeViolations` | **0**（两组都是） |
| 光照修正前 / 后 `zeroLightCells` | **6,371 / 9,216 → 0**；远处驻留组 3,022 → 0 |
| `CubeSurfaceSample` 复核（3 立方体 × 1024 列） | `topMismatches = 0`、`lightMismatches = 0` |
| **生产口径 A/B**（128 m 内采集 → 视距降到 64 → `loadedChunks` **201 → 51** → 画壳） | A(16 m LOD) ↔ B(逐格壳) **70,008 px 变化**；D(空参考) ↔ B(壳) **20,692 px**（壳补出的那片 1 m 地形）；B ↔ C(合并壳) 9,213 px |

**结论（如实）**：壳已经能"从真实地形采集 → 剔面 + 合并 → 用游戏自己的地形 shader 画出来"，
覆盖与合并两条判据每次自检，光照修正后不再发黑。
**还没做**：①没接生产路径（还没有"加载即采 / 卸载前采"自动挂接，也没有"哪些立方体用壳、哪些用现有 LOD"的距离分层）；
②合并网格的**贴图拉伸**取舍（一个 tile 铺满整块矩形）未定；③家具 data 缺口仍在；④壳的存档（P4）格式未定。
证据：`data/sessions/skyline-v0150/`、`notes/123`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.49] - 2026-09-27

第五十九个版本：**4.3 第一步 —— 32³「只存表面材质」影子原型**。
本版**不接入渲染路径**，只回答"把 LOD 区块当作一个 32³ 的家具、只存/只画表面材质"
这条路值不值得走：给出**壳结构**、**内存对照**、**四边形数**与**与引擎列顶的对表判据**。

用户口径（4.3）：把 LOD 区块视作一个 **32³ 体素精细度的"家具"**来渲染，**只渲染其表面材质、
不渲染实际方块状态**，从而降低内存压力。

**本版相对 v0.1.48 的变更**：

| 类别 | 内容 |
|---|---|
| **新增原型** | `Survivalcraft/Game/CubeSurface32.cs`：顶面（`short` 高度 + `ushort` 材质）+ 4 个侧面（`ushort` 材质，**层号即高度**）+ 底面。壳 = `1024×4 + 4×1024×2 + 1024×4` = **16 KiB**，vs 原始方块状态 `32³×4` = **128 KiB**（= `CubeChunk32.Bytes`）→ **8×** |
| **取舍（写进代码）** | 每壳格只存 **contents(bit0-9) + light(bit10-13)**，共 14 位，`ValueMask = 0x3FFF` —— 正好进 `ushort`；**不存 data(bit14+)**。SC 的 data 决定朝向/变体，**家具的设计索引就在 data 里**，因此"家具这类按 data 变化的方块"是本原型的**已知缺口**，用 `DataCells` 计数如实报出来，留给后续步骤 |
| **桥（判据）** | `skyline.CubeSurfaceSelfCheck()`（合成"挖空盒子"，确定性）、`skyline.CubeSurfaceSample(cx,cy,cz)`（真实立方体：壳格数、`quadCount`、`extractMs`、**与引擎列顶对表**、`unloadedColumns`） |
| **取证脚本** | `heightlab/skyline-v0149-surface-shell.py`：自检 + 围绕玩家的立方体网格采样 + 汇总 JSON（`data/sessions/skyline-v0149/`） |

### Verified（AgentLab，玩家立方体 `(136,2,285)`）

| 项 | 值 |
|---|---|
| 自检 | `ok=true`、`solidVoxels=5768`= `32³ − 30³`（壳格数）、`interiorAirVoxels=27000`、`ratio=8` |
| 真实取样 | 5×5×2 = **50 个立方体，全部已加载**（`unloadedColumns=0`） |
| **与引擎列顶对表** | **25,559 列 `topChecked`，`topMismatches = 0`**（比 contents+light） |
| 四边形（不合并的朴素口径） | 合计 **223,496**；**平均 4,470 / 立方体**，最高 **6,144**（= 顶 1024 + 底 1024 + 侧 4×1024） |
| 采样耗时 | 平均 **0.72 ms**，最大 **6.14 ms**（首次含 JIT；后续 0.15~0.7 ms） |
| 未加载立方体 | `unloadedColumns=1024` → 全 0（**"假空"**，与 `op:shape` 那个坑同源，本版把它作为判据显式报出） |
| 带 data 位的壳格 | 6,812 / 223,496 ≈ **3.0%**（自然地形区里比例小；建筑/家具密集区会更高） |
| 内存外推（与 `notes/114` 的 P3 计划合流） | v0.1.40 的地面窗口保留 **108** 个立方体：满方块状态 **13.5 MiB** → 表面壳 **1.69 MiB**（**−87.5%**） |
| 回归门禁（本版跑过） | `CheckChunkAddressing` **2816 格 / 0 不一致**、`CubeInvariantsCheck(4096)` **4122 格 / 0 不一致** |

**结论（如实）**：这条路线**可行且账算得过来**——壳比满状态小 8×，取一次亚毫秒~毫秒级，
与引擎自己的列顶编码逐列一致（25,559 列 0 不符）。**但它现在只是个影子原型**：
渲染侧还没接入，而"每非空壳格 1 个四边形"的朴素口径平均每个立方体要 **4,470** 个四边形
（整片实心区就是 6,144），所以下一步必须先做**面剔除/贪心合并**，否则省下的是内存、赔进去的是绘制。
**未动**：渲染路径、存档格式（P4）、4.5 与 5 的效果类按用户口径暂缓。
证据：`data/sessions/skyline-v0149/`（`selfcheck.json` / `cube-samples.json` / `summary.json`）、`notes/122`。
构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.48] - 2026-09-27

第五十八个版本：**4.4 非完整方块在 LOD 里按材质表现** —— 把这条口径**写成显式规则**并做**全方块审计**，
结论是：在 SC 里"按材质"与"按顶面"**等价**（所有非完整方块的顶面槽都等于侧面槽），
所以 4.4 在纹理选择层面**已经满足**；本版把规则与判据固化下来，随时可复核。

**本版相对 v0.1.47 的变更**：

| 类别 | 内容 |
|---|---|
| **规则 A** | `SkylineLod.MaterialTextureSlot(block,value)`：**完整方块（`CubeBlock`）→ 顶面**；**非完整方块（门/栅栏/栅栏门/台阶/植物…）→ 侧面（= 材质面）**；取槽失败回退顶面。网格构建改用它；开关 `skyline.LodMaterialAware`（默认 true；false = v0.1.47 的"一律顶面"） |
| **诊断 B** | `skyline.LodMaterialProbe(contents)`（单方块：是否完整/顶面槽/侧面槽/实际选择）、`skyline.LodMaterialAudit()`（全方块扫描：完整/非完整计数、顶面≠侧面的样例、槽位异常样例） |

### Verified（AgentLab）

| 项 | 值 |
|---|---|
| 方块总数 / 完整 / 非完整 | 259 / 62 / 197 |
| **非完整方块"顶面槽 ≠ 侧面槽"** | **0** |
| 槽位异常（≤0） | 2（`ExperienceBlock(248)`、`ShadowBlock(257)` —— 技术方块，无纹理槽） |
| 样例 | 栅栏 94：4/4；门 58：80/80；台阶 53：16/16；楼梯 217：1/1；树叶 12（完整）：52/52 |

**结论（如实）**：SC 的非完整方块顶面槽与侧面槽**恒相等** → LOD 现在（且此前）就是按材质把
门/栅栏/台阶渲染成"该材质的整格"，而不是按碰撞箱/建模画细条，用户的 4.4 要求**已满足**；
本版把这条口径写进代码并配审计工具，未来若出现"顶面≠侧面"的方块会自动选材质面且可复核。
**未动**：4.3（32³ 表面材质渲染/降内存）未开始；4.5 与 5 的效果类按用户口径暂缓。
证据：`data/sessions/skyline-v0148/`、`notes/121`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.47] - 2026-09-27

第五十七个版本：**内置雾/视距边缘渐变默认移除（4.1）+ 卸载前 LOD 最后一采（4.2）**
—— 目标在本轮被用户更新（细化 4.1~4.5 与 5），本版先落地其中两项可立即完成、可验证的。

**本版相对 v0.1.46 的变更**：

| 类别 | 内容 |
|---|---|
| **4.1 去雾默认** | `skyline.FogDisabled` **默认改为 true**：内置雾 + 视距边缘霾带默认移除（用户口径："可以移除内置雾气效果和视距边缘渐变效果，后续配合光影系统独立开发"）；`false` 即逐位回到原版；LOD 雾参数走同一开关 |
| **4.2 卸载前最后一采** | `SkylineLod.NotifyChunkUnloading(chunk)` + `TerrainUpdater.AllocateAndFreeChunks` 钩子（在 `SaveChunk/FreeChunk` 之前、**主线程**）；`Harvest` 里加"强制采一次"通道；`ResampleReason.Unloading`；诊断 `Survey().refreshedOnUnload` |
| **5 口径数据（不含新特性）** | 新增 `heightlab/skyline-shadow-unify.py`：同一机位四种组合（都关/只 CPU/只 GPU/都开）测平均亮度、像素差、fps |

### Verified（AgentLab）

* **4.1**：重启后不做任何手动设置，`FogDescribe → disabled=True`（内置雾与边缘渐变默认移除）；
* **4.2**：建 7 格石砖"标记柱"于 `(4420,66..72,9200)` → LOD 单元 `(276,575)` 采到 `coarse=68 / fine=[72,68,68,68]`；
  把 `settings.VisibilityRange` 128 → 64 强制释放：**`loadedChunks` 201 → 51**、**`refreshedOnUnload` 0 → 150**，
  标记单元卸载后仍为 `coarse=68 / fine=[72,68,68,68]` 且 **`dirty=False`**；已恢复 128、fps 30.2；
* **5 口径**：A 基线 luma 109.71 / B 只 CPU 109.44（差 85,681 px）/ C 只 GPU 105.64（差 **207,552 px**）/
  D 都开 **103.63**（差 276,312 px）——**GPU 单独影响最大，两套同开会叠暗**（留作数据点，本轮不改行为）。

**新目标里未动的条目（如实列出）**：4.3（LOD 按 32³ 表面材质渲染、降内存）、4.4（门/栅栏等非完整方块按材质表现）
未开始；4.5 按用户口径暂缓；5 的"效果类"（柔和光/天空盒/物体阴影/体积云/后处理/水面反射）按用户口径暂缓。
证据：`data/sessions/skyline-v0147/`、`notes/120`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.46] - 2026-09-27

第五十六个版本：**交接带"立刻铺满" + 三件套常备验收脚本** —— 补上 v0.1.45 如实记录的两个口子：
近环层覆盖率只有 53%（只对"采样那刻在带内"的区块写入）、审计要手工三步拼。

**本版相对 v0.1.45 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（主动标脏）** | `SkylineLod.MarkNearBandDirty()`：把交接带（含 16 m 容差）里所有 16 m 单元标脏 → 推通道优先重采；`NearBandTick()` 由 `Tick` 每帧调，**相机移动 ≥ `NearMarkMoveThreshold`（默认 24 m）** 自动重标；桥 `skyline.LodNearBandMark()` 手动触发（返回 `{ok, marked, totalMarked}`，开关关时返回 0） |
| **新增 B（常备验收脚本）** | `heightlab/skyline-lod-band-audit.py`：一条命令跑完 **驻留 → 等 `contentsReady` → 审计 → A/B（近环开/关）→ 存 JSON**；结束自动复位开关；文档里写清"矩形必须落在交接带内"（本轮踩过：rect 太远只命中 53%） |
| **诊断 C** | `Survey` 增 `nearMarkedCells / nearLayerEnabled`；`NearMarkedCells` 累计计数 |

### Verified（相机 (4451.6,9210.5)，视距 128 → 带 [124,188] m；矩形 4580,9185→4630,9235；4000 样本）

| 指标 | 关（8 m 细层） | 开（4 m 近环） | 变化 |
|---|---|---|---|
| **`nearHits`** | 0 | **4000 / 4000（100%）** | 主动标脏生效（本轮标记 407 个 16 m 单元） |
| **平均 \|Δh\|** | 1.138 m | **0.661 m** | **−42%** |
| \|Δh\|>1 m 比例 | 26.22% | **12.25%** | **−53%** |
| \|Δh\|>4 m 比例 | 3.22% | **0.75%** | **−77%** |
| 材质命中率 / 光照吻合(≤1) | 91.32% / 94.08% | **93.00% / 95.62%** | +1.7 / +1.5 pp |

对比 v0.1.45 的 53% 覆盖版本（0.96 m / 3.03%）：**铺满后 0.661 m / 0.75%** —— 覆盖不全本身就是测量偏差来源，
先铺满再谈精度。证据：`data/sessions/skyline-v0146/`、`notes/119`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.45] - 2026-09-27

第五十五个版本：**LOD 交接带 4 m 近环层** —— v0.1.43/0.1.44 的审计把"高度损失"量化成
"平均 |Δh| 1.4 m、近三成列 >1 m"，根因是"一个 8 m 单元只存一个高度"；
本版在**视距边界那一圈**再加一层 **4 m 单元**，把交接带的高度误差砍掉三到五成。

**本版相对 v0.1.44 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（第三层）** | `SkylineLod`：`NearShift=2`（4 m 单元）、`NearBandMetres=48`；采集时把一个 16 m 区块切成 **4×4 个 4 m 子单元**，各自取 16 列的中位（高度/材质/光照三口径一致）；**只在区块中心落在 `[视距+4-8, +48+16]` 带内时采集** |
| **网格 B** | `RebuildMeshCore(..., int layer)` 三层化 + `SetLayerMesh(...)`；绘制顺序 **粗(16 m) → 细(8 m) → 近环(4 m)**；`PruneNearCells(maxDist)` 在重建时删带外单元；`LodReset()`/`Dispose` 一并清理 |
| **开关 C** | `skyline.LodNearLayerEnabled`（默认 true）；false + `LodReset()` 回到 v0.1.44 行为（A/B 用） |
| **审计 D** | `SkylineLodAudit` 增 `nearHits`（命中 4 m 近环层的列数）；`Survey` 增 `nearCells/nearCellsInMesh/nearMeshIndices/nearBandMetres` |
| **不落盘** | 近环层是纯临时层（只服务当前所在的交接带），`SkylineLod.bin` 仍是版本 3（粗+细） |

### Verified（AgentLab；矩形 4610,9180→4660,9280；驻留 40 列；同区域同 6,000 样本、两次均为新采数据）

| 指标 | 关（8 m 细层） | 开（4 m 近环） | 变化 |
|---|---|---|---|
| **平均 \|Δh\|** | 1.296 m | **0.96 m** | **−26%** |
| \|Δh\|>1 m 比例 | 28.65% | **18.97%** | **−34%** |
| \|Δh\|>4 m 比例 | 5.72% | **3.03%** | **−47%** |
| 材质命中率 / 光照吻合(≤1) | 96.15% / 97.97% | **96.33% / 98.18%** | 略升（前两版已修过） |
| 层命中 | fine 6000 | near **3172** + fine 2828 | — |

层规模（稳定后）：近环 **1,456 单元 / 982 进网格 / 21,456 索引**（对照细层 1,816 / 404 / 7,812）；
内存量级可忽略（≈1.5k 个 4 m 单元）。**如实记录**：本次窗口里近环只覆盖 53% 的采样列——
它只对"采样那一刻在带内"的区块写入，而测试只等了 ~20 s（Refresh 默认 300 s）；
下一步候选是"开启开关时主动标脏带内区块"以立刻铺满。证据：`data/sessions/skyline-v0145/`、`notes/118`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.44] - 2026-09-27

第五十四个版本：**LOD 基色改用采集时光照**（里程碑 4 交接带亮度）—— 近景顶点色由区块光照决定，
而 LOD 一直用常数基色 `220`（≈光照 13），交界处因此有亮度台阶；本版把 LOD 基色改成**采集时的真实光照**。

**本版相对 v0.1.43 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（光照字段）** | `SkylineLod.Cell` 增 `byte Light`；`MedianInto` 把**代表样本**的光照一并输出（打包的 `(uint)value` 已含光位，`Terrain.ExtractLight` 直接取）；粗/细层都写 |
| **新增 B（基色）** | 网格基色 `light×17`（与游戏"光照→顶点色"同口径），再叠原有坡向/自阴影增益；开关 `skyline.LodLightFromSamples`（默认 true，false = 旧常数基色做 A/B） |
| **存档 C（版本 2→3）** | 每项多一个 `Light` 字节；**旧版本 2 存档读回 `Light=15`**（亮度与改前一致，不会变黑），世界文件 `SkylineLod.bin` 向后兼容 |
| **审计 D** | `SkylineLodAudit` 增加光照口径：`lightWithinOneRatio`（LOD 基色光照 vs 真实顶面光照差 ≤1 的比例）、`meanAbsLightDiff`、`maxAbsLightDiff`，以及**改前基线** `constantBaseLightWithinOneRatio` |

### Verified（AgentLab；驻留 72 列；`LodRectAudit(4540,9170,4660,9290,6000)`）

| 指标 | 值 |
|---|---|
| **`lightWithinOneRatio`（改后）** | **91.92%** |
| **`constantBaseLightWithinOneRatio`（改前基线）** | **8.65%** |
| `meanAbsLightDiff` / `max` | 1.028 / 15 |
| 复核：`meanAbsHeightDiff` / `materialMatchRatio` | 1.413 m（v0.1.43：1.431）/ **88.55%**（88.28%） |

**视觉 A/B 如实记录**：两个机位 off/on 的像素差只有 **160 / 232 个（>8）**——因为冬季雪原里绝大多数列真实光照是 15、
与常数 13 只差 ~7% 亮度，且画面里 LOD 占比小；另外**旧存档单元默认 `Light=15`**，要等 Refresh 周期（默认 300 s）
重采后才完全生效。该条的判据以审计指标为准（8.65% → 91.92%）。证据：`data/sessions/skyline-v0144/`、`notes/117`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.43] - 2026-09-27

第五十三个版本：**LOD 交接带审计（可量化）+ 材质取众数** —— 把里程碑 4 里"LOD 很粗糙、材质不符"
这条主观反馈变成逐列对照的数字，并先修掉其中的材质部分（+4.65 个百分点，几何不变）。

**本版相对 v0.1.42 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（审计工具）** | `SkylineLodAudit.cs` + `SkylineLodBoundary.cs`：`skyline.LodBoundaryAudit(inner,outer,samples)`（环带）/ `skyline.LodRectAudit(x1,z1,x2,z2,samples)`（矩形，配驻留用）——环带/矩形内随机抽列，把 **LOD 单元的高度/材质**与**真实 `GetTopHeight`/顶面方块**逐列对照，输出平均/最大 \|Δh\|、\|Δh\|>1/>4 比例、**材质命中率**（细/粗层分列） |
| **修复 B（材质取众数）** | `SkylineLod.MedianInto`：材质由"中位高度那一列的方块"改为**样本 content 频次最高的方块**（再从该 content 里挑中位高度作代表）；**高度仍取整体中位 → 几何不变** |

### Verified（AgentLab；矩形 4540,9170→4660,9290；驻留 72 列；6,000 列样本）

| 指标 | 改前 | 改后 |
|---|---|---|
| 平均 \|Δh\| | 1.433 m | **1.431 m**（不变） |
| \|Δh\|>1 m / >4 m 比例 | 29.78% / 6.37% | **29.75% / 6.37%**（不变） |
| **材质命中率** | 83.63% | **88.28%（+4.65 pp）** |

**前提与坑**：128 m 外真实地形默认未加载（`GetTopHeight` 返回 MinHeight），第一次审计 `loadedColumns=0`；
必须先 `ChunkResidencyMode=true` + `EnsureRegionLoaded` 并等 `contentsReady == chunks` 再审计（已在 `notes/116` 写成可复现步骤）。

**仍存在的固有损失（如实）**：8 m 单元只有一个高度 → 平均 \|Δh\| 1.43 m、近三成列 >1 m、6.4% >4 m（山脊/树冠）。
下一步候选：单元存 (min,max) 双高度 + 顶面用 max、skirt 拉到 min（几何改动，用本版审计工具 A/B）。
证据：`data/sessions/skyline-v0143/`、`notes/116`。构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.42] - 2026-09-27

第五十二个版本：**大规模建筑压测 #2 + `op:shape` 写入门禁**（桥侧修复）—— 压测顺手抓到并修掉
"`op:shape` 报成功、实际一格没写"的假成功问题。

**本版相对 v0.1.41 的变更**：

| 类别 | 内容 |
|---|---|
| **修复 A（写入门禁）** | `Shapes.Run` 逐格走 `AgentActions.EnsureWriteTargetLoaded(...)`（与 `op:cell` 同一条门禁；`force` 语义一致），写入后**逐格回读**校验；返回新增 `cellsWritten`（= 校验通过数）、`verifyMismatches`、`cellsSkippedNotLoaded`、`forced`，未写入时附 `hint`（含 `firstError`） |
| **修复 B（误导提示）** | `EnsureWriteTargetLoaded` 改 `internal`（供 Shapes 复用），并把"列根本没分配"的提示改对：**`force:true` 在这种情况下没用**（没有存储可写），正确路径是 `ChunkResidencyMode=true` + `EnsureRegionLoaded(x1,z1,x2,z2)` + 轮询 `ResidencyStatus` 到 `contentsReady == chunks` |

### Verified（AgentLab；压测三栋：大厅 13,736 格 / 高塔 15,752 格（y 68..323）/ 实心 32³ 32,768 格）

| 场景 | planned | written | skipped | **skippedNotLoaded** | verifyMismatches |
|---|---|---|---|---|---|
| 远端建塔（玩家 ~130 m 外） | 15,752 | **0** | 0 | **15,752** | 0 |
| 同上 + `force:true` | 15,752 | 0 | 0 | **15,752**（提示已改） | 0 |
| **先 `EnsureRegionLoaded`（4 列/8 MB）再建塔** | 15,752 | **15,752** | 0 | **0** | **0**（6.7 ms） |
| 大厅重跑（玩家 30 m 内） | 13,736 | 10,986 | 2,750 | 0 | 0 |
| 实心 32³（驻留 12 列/24 MB） | 32,768 | **32,768** | 0 | 0 | **0**（46.4 ms） |

* **单位成本**：空心壳 **0.44 µs/格**（13,736 / 6 ms），实心体 **1.17 µs/格**（32,768 / 38.5 ms）——实心约 2.7×；
* **压测后门禁**：`CheckChunkAddressing` **2816 格 / 0 不一致**；`CubeInvariantsCheck(4096)` **4122 格 / 0 不一致**；
* **光影回归**：`GpuShadowCapture` 在三栋在场时 ok、覆盖 155,565 px（14.836%）、**自检 True**（10 点）、
  200 列 + 200 alpha 列、近图 791,462 px / 0.25 m/texel；fps 29.7~30；进程 749 → 826 MB（含 12 列驻留 ≈ 24 MB）。

证据：`data/sessions/skyline-v0142/`、`notes/115`。桥工程构建：1 个既有警告 0 错误。

## [v0.1.41] - 2026-09-27

第五十一个版本：**Axiom（用户给定仓库）对照归档 + `op:shape mode=spiral`** —— 学习项 #6 的首轮归档，
并把该插件里我们缺的"螺旋楼梯"在桥里**原生**实现（逐格朝向）。

**本版相对 v0.1.40 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（螺旋形状）** | `Bridge/Shapes.cs`：`op:shape mode=spiral` —— 绕 `center` 竖直轴，`height` 级、`turns` 圈；**每级朝向按切向**写进楼梯 data 低 2 位（映射取自游戏 `StairsBlock.GetPlacementValue`：0=−Z、1=−X、2=+Z、3=+X）；可选 `core`/`coreContents` 芯柱、`upsideDown`、额外 `data`、`dryRun`。`Shapes.Run` 为此新增**逐格 data** 通道（写入时优先用该格的 value） |
| **归档 B（学习项 #6）** | `notes/106-Axiom能力对照与缺口.md`：先厘清 **用户给的 `kkroesch/axiom` 不是那个以贝塞尔笔刷闻名的 Axiom mod**（前者是 9 个 Java 文件的小型 PaperMC 插件，全仓库 `bezier|spline|curve` **0 命中**），逐命令给出对照表（cleararea/buildwall/digtunnel/spiralstair/buildportal/starterkit/mineshaft）与我们已有能力的映射；"贝塞尔曲线修建"那条我们**早已有** `SkylineBuilder.SweepBezier`（v0.0.6：贝塞尔→弧长采样→平行传输→profile 扫掠，1258 格/0.5 ms） |

### Verified（AgentLab）

| 项 | 值 |
|---|---|
| `dryRun` | `cellsPlanned=33`（16 级 + 17 芯柱） |
| 实建 | `cellsWritten=33`、`cellsSkipped=0`、`chunksTouched=2`、**2.6 ms** |
| 回读（`findBlock StoneStairsBlock` 半径 40） | 命中 **16 级**，朝向直方图 **{0:4, 1:4, 2:4, 3:4}**（整圈四向各 4 次 → 切向朝向正确） |
| 视觉 | `data/sessions/skyline-v0141/spiral-view.png` |

证据：`data/sessions/skyline-v0141/`、`notes/106`。桥工程构建：1 个既有警告 0 错误。
源码包附带 `agentbridge/` 源码，形如 `Shapes.cs` 的本版改动都在里面。

## [v0.1.40] - 2026-09-27

第五十个版本：**P3 分配计划 + 共享 32³ 页路由自检** —— 把 v0.1.39 的立方体级判据落成
"真要分配多少页/多少字节"，并用影子页验证 P3 的路由；顺带得出一个**要不要换存储**的结论。

**本版相对 v0.1.39 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（共享页路由 + 自检）** | `SkylineCubeStore.cs`：`CubePageCoordOf(worldX,worldY,worldZ)` —— 列(16×16)+分带(32) → 共享页 `(colX>>1, y>>5, colZ>>1)`，页内 `lx=(colX&1)*16+(x&15)`、`ly=y&31`、`lz=(colZ&1)*16+(z&15)`；`skyline.CubeStoreSelfCheck(cubes,writes)` 随机写读 + 邻居页不串 + 每页引用列数分布 |
| **新增 B（分配计划）** | `TerrainUpdater.CubeAllocationPlan(...)` + 桥 `skyline.CubeAllocationPlanHere(radius,yRadius)` / `CubeAllocationPlanAt(...)`：立方体级保留集合 → 页数与字节，与列式（列 × 32 层分带 = 32 KiB/段）逐项对照，并给出"部分列有内容"的立方体数（共享页的浪费来源） |

### Verified（AgentLab；视距 128；`SphereLoadingEnabled=true`）

* **路由自检**：`CubeStoreSelfCheck(64,512)` → 64 页 / **32,768 写** / **32,768 次路由检查 / 0 不一致**；
  每页被 2 列写过 3 页、3 列 16 页、4 列 45 页；
* **分配计划（地面窗口）**：球内 134 个立方体 → 保留 **108**；涉及 184 列 / **420 段**；
  **立方体页 13.5 MiB vs 列式分带 13.1 MiB → +0.38 MiB（+2.9%）**；
  "有内容的列数"分布 4 列:100、3 列:5、2 列:2、1 列:1（浪费出在那 8 个部分列立方体上）；
* **高空窗口**：0 页（球窗以玩家为中心，纯空气区不分配）。

> **结论（可决策）**：在"地表连续地形 + 少量高空建筑"的典型场景里，P3 的共享立方体页**不省内存、反多约 3%**；
> 它的价值在**分配数更少**（108 次 vs 420 次）与**寻址简化**。既然 v0.1.29 的列内 32 层分带已把内存砍了
> 50%~69%，"为了省内存"不是换 P3 的理由；是否换取决于后续是否需要"按立方体页做渲染侧裁剪/共享"。

边界（如实）：本版是**影子实现**（`CubeChunk32` 当页容器，不接入 `TerrainChunk`、不碰存档），
实验开关收尾已复位。若将来真上 P3：世界副本 + 施工前后 `CheckChunkAddressing`/`CubeInvariantsCheck` 两条门禁 +
内存/fps/分配数三项对照。证据：`data/sessions/skyline-v0140/`、`notes/114`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.39] - 2026-09-27

第四十九个版本：**32³ 立方体级窗口判定（三维坐标）+ 立方体寻址门禁** —— 里程碑 3「32³ 第 2 步」的
判据与门禁部分。v0.1.28 的球窗判据是**列级**（椭球竖直切片里该列任何一层有内容就保留整列），
本版给出**立方体级**判据：三维坐标 `(cx,cy,cz)`，只认"这个 32³ 立方体所在那个 32 层分带"的掩码位；
并配一个**专项窗口报告**与一条寻址门禁。**不动存储布局**，随时可回退。

**本版相对 v0.1.38 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（立方体级判据）** | `TerrainUpdater.CubeWindowDecide(cx,cy,cz,...)`：一个 32³ 立方体横跨 **2×2 个列**，内容判定 = 四列 64 位分带掩码**对应位的 OR**（`bit = (mask >> (cy + 32)) & 1`）；两层判据 = 椭球（水平 `dx²+dz² ≤ C²`、竖直 `|Δy| ≤ sqrt(C²−dx²−dz²)·yMul`）∩ 该分带真有内容 |
| **新增 B（专项窗口报告）** | `TerrainUpdater.CubeWindowSurvey(pcx,pcy,pcz,r,yr)` + 桥 `skyline.CubeBandSurveyHere(r,yr)` / `CubeBandSurveyAt(...)`：统计 `total/inSphere/cubeHasContent/kept`，并对 v0.1.28 列级判据给出 `columnRuleKeep` 与 **`emptyButColumnRuleKeeps`**（P3 收益口径），附每层分布 |
| **新增 C（寻址门禁）** | `skyline.CubeInvariantsCheck(samples)`：断言立方体坐标用算术右移（floor）、`bandIndex == cy + 32`（MinHeight −1024 与 32 对齐）、立方体装得下该格、中心反查回同一立方体 |
| **桥** | `skyline.CubeWindowDecision(cx,cy,cz)`（单立方体文本判定，含四列 allocated/mask/bit 明细） |

### Verified（AgentLab；`SphereLoadingEnabled=true`；视距 128）

* **门禁**：`CubeInvariantsCheck(8192)` → **ok=true / checkedCells=8218 / mismatches=0**；
* **单立方体**：`CubeWindowDecision(136,2,285)` → 四列 `(272,570)..(273,571)` 均 `allocated=True`、
  `mask=0x00000007FFFFFFFF`、`bandIndex=34`、`bit=1` → `inSphere=True cubeHasContent=True kept=True`；
* **专项窗口（±4 / 竖直 ±3，567 个候选）**：

| | 地面（cy=2） | 高空（cy=13，玩家站 y=420 柱顶） |
|---|---|---|
| `inSphere` / `kept` | 132 / **108** | 133 / **8** |
| `columnRuleKeep`（v0.1.28 列级） | 132 | 9 |
| **`emptyButColumnRuleKeeps`** | **24（≈18%）** | 1 |
| 每层 kept | 0:16, 1:43, 2:48, **3:1** | 11:1, 12:3, 13:3, 14:1 |

* **账本基线复跑**（v0.1.13 `CubeWindowSurvey`）：地面 443 立方体 / 233 列（55.4 MiB vs 58.3 MiB，0.141 ms）；
  高空 448 / 233（56.0 MiB，0.283 ms）——注意账本的 `cubesWithContent` 是单点采样的**粗糙口径**，
  立方体级 survey 用分带掩码才是准的。

边界（如实）：本版只是**判据 + 诊断 + 门禁**，不改存储；真正的 P3（跨 2×2 列共享 32³ 立方体、
分配单元 128 KiB、存档版本化 P4）仍需世界副本 + 施工前后各跑两条门禁 + 内存/fps 对照。
证据：`data/sessions/skyline-v0139/`、`notes/112`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.38] - 2026-09-27

第四十八个版本：**近/远两级级联阴影贴图** —— 远图（512 m / 1024² = 1 m/texel）之外再加一张
**近图（128 m / 1024² = 0.25 m/texel）**，片元命中近盒就用近图，接触阴影从"1 m 大斜块"变成细密结构。

**本版相对 v0.1.37 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（双图捕获）** | `SkylineGpuShadow.cs`：把捕获主体抽成 `RenderDepthMap(...)`（LOD + 真实区块不透明/alpha-tested 一次画完，含自己的矩阵/原点/eye/depthMax），远图与近图各调一次；开关 `GpuShadowCascadeEnabled`（默认 true）、`GpuShadowNearRadius`（默认 128 m） |
| **新增 B（近图优先采样）** | `SkylineGpuShadowSample.cs`：片元分别投影进两张图，`u_nearCascade` 且命中近盒 → 用近图（新增 `u_shadowMapNear/u_shadowSamplerNear/u_sunViewProjectionNear/u_sunOriginNear/u_eyeNear/u_depthMaxNear`），否则回退远图；`u_nearCascade=0` 时与 v0.1.37 逐位一致 |
| **诊断 C** | `GpuShadowDescribe` 报 `cascade/nearRadius`；捕获 JSON 增 `nearCovered/nearTexelMeters/nearDepthStepMeters/texelMeters`；`GpuShadowDebugMode=2` 画"用了哪张图"（蓝=近 / 红=远 / 绿=都没命中） |

### Verified（AgentLab；关雾；16 bit；strength 0.45 / bias 0.0002）

| 项 | 远图 | 近图 |
|---|---|---|
| 半径 / texel / 深度步长 | 512 m / **1 m** / 0.0625 m | 128 m / **0.25 m** / 0.01563 m |
| 覆盖率 / 自检 | 156,282 px / True | 785,073 px / True |

采样 A/B（**1×1 格树叶棋盘棚**，2 m 周期，低头看地）：

| | 单图（级联关） | 级联开 |
|---|---|---|
| 平均亮度 | 170.3 | 179.2 |
| 像素差（>8） | — | **185,728 px**（变亮 **129,017** = 粗假阴影被纠掉；变暗 **56,711** = 细阴影被正确加上） |

### 三个真实坑（已修并记录）

1. **只绑纹理不绑采样器**（漏 `u_shadowSamplerNear`）→ 近图分支**静默不生效**（A/B 0 像素差），
   靠 `GpuShadowDebugMode=2` 才定位；
2. **两张图归一化基准不同**：比较时若仍用远图 `eye`，`fragDepth` 饱和成 1.0 → 近盒内**整片判成阴影**
   （实测平均亮度 121.4、A/B 全变暗）→ 近图分支必须同时换 `eye` 与 `depthMax`（新增 `u_eyeNear`）；
3. **C# verbatim 字符串里的注释不能用 ASCII 双引号**（会提前结束字符串，一次报 60 个语法错误）→ 用「」。

边界（如实）：近盒（±128 m）之外仍走远图，交界处 texel 不同可能有细节跳变（下一步可做交接带混合/二级中图）；
bias 未按 texel 缩放；家具/实体仍未进深度图。证据：`data/sessions/skyline-v0138/`、
`heightlab/skyline-v0138-cascade.py`、`notes/111`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.37] - 2026-09-27

第四十七个版本：**家具写入侧护栏**（桥/AgentBridge 侧改动）—— 用户报过的"家具透明但有碰撞"
（机制见 v0.1.31 / `notes/97`、`notes/101`）从此在**写入当场**就能看到提示：
写 `contents=227` 时核对 design 索引在当前世界是否存在，不存在就给出 `missingDesign` 计数与例子。

**本版相对 v0.1.36 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（写入侧护栏）** | `AgentActions.FurnitureWriteGuard(...)`：对本批写入后确实是家具的格子做 `data → FurnitureBlock.GetDesignIndex → behavior.GetDesign` 判定，返回 `checked / missingDesign / examples[]（最多 5 个）/ hint`；`op:cell` 的单格与批量两条分支都已接线 |
| **新增 B（形状通道）** | `op:shape`：形状内所有格子是同一个 value，因此 design **只查一次**，`checked = cellsWritten`（同理给 `designIndex/missingDesign`） |
| **行为约定** | **不改写入语义**：合法/非法都照样写下去；护栏只做"当场告知"，hint 指向 `skyline.FurnitureDesigns`（当前世界真实存在的设计索引）与 `data = designIndex << 2 \| rotation` |
| **发布侧** | `make-source-bundle.ps1` 起把 `.projects/AgentBridge`（`*.cs` + `csproj` + `Bridge/*.cs`）放进 `agentbridge/` 一并发布——这类 mod 侧能力以前无法从包里复现 |

### Verified（AgentLab；世界内 214 个家具设计）

| 用例 | 请求 | 结果 |
|---|---|---|
| 合法 design | `op:cell` data=20（design 5） | `checked=1, missingDesign=0` |
| **非法 design（用户 bug 场景）** | `op:cell` data=70848（design 2352） | `checked=1, missingDesign=1`，例子 `[4361,68,9125] designIndex=2352` |
| 批量混合 | 2 格：data=20 + data=72019（design 2644） | `checked=2, missingDesign=1` |
| 合法 / 非法（形状） | `op:shape` box contents=227 | `designIndex=5 missing=0` / `designIndex=2352 missing=1` |

证据：`data/sessions/skyline-v0137/furniture-guard.json`、`notes/110`。
桥工程构建：`build-mod.ps1`，**1 个既有警告（InputInjector 过时 API）0 错误**。

## [v0.1.36] - 2026-09-27

第四十六个版本：**alpha-tested 几何进太阳深度图（树叶阴影）+ 修掉一个真实并发缺陷** ——
树叶/草/栅栏这类"镂空方块"（几何子集 5）以前完全不进深度图，森林与树冠投不出影子；
本版给深度 pass 加了一个**带贴图 alpha 丢弃的变体**，让它们也能投影。
同时修掉日志里实测抓到的 `SkylineLod.MarkDirty` 并发崩溃（TerrainUpdater 更新线程 vs 主线程）。

**本版相对 v0.1.35 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（alpha 深度变体）** | `SkylineGpuShadow.cs`：`GpuShadowAlphaVsh/Psh` —— 与深度 pass 同结构，顶点多输出 `v_texcoord`，片元按地形贴图 `alpha < 0.5` **discard**；`DrawChunkIntoDepth` 重构为 `DrawChunkSubsets(shader, chunk, mask)`，不透明 `0x1F` + alpha-tested `0x20` 画进**同一张深度图/同一个深度缓冲**；开关 `skyline.GpuShadowIncludeAlphaTested`（默认 true），诊断与 JSON 增 `alphaTested` / `alphaChunksDrawn` |
| **修复 B（第 4 个 shader 坑）** | GLSL 顶点着色器必须带 `<Semantic Name='POSITION' Attribute='a_position' />` 等元数据注释，否则编译报 `Attribute "a_position" has no semantic defined in shader metadata.`（首轮捕获 `ok=false`，补齐三行注释后通过） |
| **修复 C（并发）** | `SkylineLodRefresh`：`m_dirtyCells/m_dirtyQueue/m_dirtyTime` 换成 `ConcurrentDictionary/ConcurrentQueue`（消费侧 `Peek/Dequeue` → `TryPeek/TryDequeue`）。原因：`MarkDirty` 会被 **TerrainUpdater 更新线程**调用（区块→Valid 推通道、`ChangeCell` 写入侧），与主线程的 `SkylineLod.Tick` 撞车 → `InvalidOperationException: … non-concurrent collections …`。修复后两次世界加载 + 一次跨区传送（强制区块加载）**0 新增异常** |

### Verified（AgentLab；关雾；16 bit 深度；strength 0.45 / bias 0.0002）

| 项 | alpha off | alpha on |
|---|---|---|
| 深度图覆盖率 | 155,847 px（14.863%） | 155,857 px（14.864%） |
| 画入区块（其中走 alpha 子集） | 201 | 201（201） |
| 回读自检 / 耗时 | True / 147.6 ms | True / 128.6 ms |
| **深度图逐像素变化** | — | **4,036 px**，其中 **3,175 px 更接近太阳**（= 树叶进图的直接证据，平均 14.3 m） |
| **站在 2×2 树叶格子棚下看地面** | 地面完全受光、平均亮度 **219.03** | **格子状斑驳阴影**、平均亮度 **169.79**；像素差 **405,014 px 全部变暗（变亮 0）** |

边界（如实）：深度图 1 texel/m，叶片贴图内部小于 1 m 的镂空在本分辨率下分辨不出（只有块级空隙可见）；
目前只接入地形方块（家具/实体未进深度图）。证据：`data/sessions/skyline-v0136/`、
`heightlab/skyline-v0136-alpha-shadow.py`、`notes/109`。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.35] - 2026-09-27

第四十五个版本：**深度图 16 bit 双通道 + 关雾开关** —— 把 v0.1.32 起的太阳深度图从
"每步 16.06 m"提升到"每步 0.0625 m"，真实地形阴影从此能表现米级物体与自阴影；
同时按用户要求在分支里提供 **`skyline.FogDisabled`**（测试光影时关雾）与 **`skyline.CloseDialogs()`**
（自动化测试用，解掉"日志对话框挡住 Navigator"的阻塞）。

**本版相对 v0.1.34 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（16 bit 深度）** | 深度 pass 片元写 `d16 = round(d*65535)`，`R=hi/255`、`G=lo/255`、`B=d`（8 bit 预览）；采样侧按**捕获时**的编码解码（`u_shadowDepth16`）。开关 `skyline.GpuShadowDepth16`（默认 **true**；false 逐位回退旧 8 bit，专供 A/B）。量化步长：8 bit 4096/255 = **16.0628 m** → 16 bit **0.0625 m** |
| **修复 B（precision）** | 深度 pass 与采样变体的 GLSL 片元改 `precision highp float;` —— `mediump` 尾数只有 ~10 bit，`d*65535` 会被压到 ~1024 级，16 bit 等于白做。解码侧用 `floor(R*255+0.5)` 吸附整数，避免 ±1 ULP 吃掉低位 |
| **新增 C（关雾开关）** | `SkylineFogControl.cs`：`skyline.FogDisabled`（默认 false = 原样）。开启时把雾密度清零（雾带 z=0、霾 (0,0)），接入 `TerrainRenderer`（opaque/alphaTested/transparent）、`SkylineLod`、`SubsystemModelsRenderer`；不动地形/光照/存档，`skyline.FogDescribe()` 诊断 |
| **修复 D（自动化阻塞）** | `skyline.CloseDialogs()`：强制摘除当前所有对话框（原版 `HideAllDialogs()` 在 `ViewGameLogDialog` 上抛 NRE，会让 Navigator 永远停在 `status='dialog'`）。另记录：相机精确摆位应直写 `player.ComponentLocomotion.LookAngles`（弧度），`lookAt` 路线实测停在 pitch 偏差 ~38° 处 |

### Verified（AgentLab；测试台 z=9145 的 16/8/4/2/1 m 阶梯墙 + 35×27 石砖平台；关雾）

| 项 | 8 bit | 16 bit |
|---|---|---|
| 覆盖率（同一相机/同一几何） | 155,955 px（14.87%） | 155,955 px（14.87%） |
| 深度范围 | [119, 173] | [30519, 44437] |
| **量化步长** | **16.0628 m** | **0.0625 m** |
| **深度 PNG 的不同值个数** | **48** | **9235** |
| 值恰为 257 的倍数 | **100%**（纯 8 bit 栅格） | 0.342%（≈1/257 随机） |
| 回读自检 / 耗时 | True / 56.8 ms | True / 45.1 ms |
| 采样截图（strength 0.45、bias 0.0002） | **整块平台被假自阴影糊暗**（量化 ±8 m 误判自遮挡） | 平台正常受光 + 阶梯墙**清晰阶梯状投影** |

像素统计（8bit → 16bit）：**438,256 px 变化（>8，占 48%）**，变亮 367,590 / 变暗 70,666，
全帧平均亮度 **92.2 → 111.1**。证据：`data/sessions/skyline-v0135/`、脚本
`heightlab/skyline-v0135-16bit-depth.py`（幂等，可复跑）、`notes/107`。

边界（如实）：只接入**不透明** pass；粒子/移动方块/挖掘裂纹等自设雾参数的 pass 未接入关雾开关；
深度图仍按需生成。下一步：①alpha-tested 进深度图；②与 v0.1.30 CPU 顶点阴影统一口径 A/B；
③`GpuShadowRadius` 收到 256 m 的精度/覆盖取舍；④家具 design 写入侧护栏。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.34] - 2026-09-27

第四十四个版本：**阴影贴图采样收口** —— 不透明 pass 改用"地形 shader + 阴影采样"变体，
**真实地形**从此吃到 v0.1.32/33 生成的 GPU 太阳深度图阴影。

**本版相对 v0.1.33 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（采样变体）** | `SkylineGpuShadowSample.cs`：与游戏 `Opaque.vsh/psh` **同结构**（雾/顶点色/贴图一致），顶点多输出 `v_world`，像素按 `mapDepth + bias < fragDepth` 压暗 `×(1−strength)`；开关 `skyline.GpuShadowSampleEnabled / Strength(0.45) / Bias(0.0002) / FlipY / DebugMode`；诊断 `GpuShadowSampleDescribe()` |
| **新增 B（接入不透明 pass）** | `TerrainRenderer.DrawOpaque`：`Shader opaqueShader = SkylineRuntime.ResolveOpaqueShader(m_opaqueShader);`（未启用/无深度图时原样回退 → **默认行为逐位不变**），其余参数设置与绘制流程完全一致；`GpuShadowTick` 在"启用采样但无深度图"时自动补一次 `GpuShadowCapture()`（并自动打开捕获开关） |
| **修复 C（三处实测坑）** | ① GLSL 片元必须显式 `#ifdef GL_ES precision mediump float; #endif`；② 采样侧必须减**捕获时记录的**太阳原点（`u_sunOrigin`）——否则原点被加两次、UV 全在图外（与 notes/88 "CreateLookAt 自带平移"同源）；③ bias 0.004（≈16 m）会把 16 m 高墙的投影抹掉 → 默认改 **0.0002**（≈0.8 m） |
| **诊断 D** | `GpuShadowDebugMode=1` 把"采样到的深度"直接画到地形颜色上（UV/绑定对齐取证）；`Describe` 增加 resolved/fallbacks/lastReason |

### Verified（AgentLab；相机固定 yaw80/pitch12；测试墙 8×16×8 石砖）

| 项 | 值 |
|---|---|
| 采样变体真实生效 | `resolved=184`、`err=''`、fps 30 |
| 调试模式 | 地形显示"采样到的深度"（地面中点灰 ≈0.5 = 深度中值）→ **UV/绑定正确** |
| **最终 A/B**（默认参数，off → on） | **193,830 px 变化（>8）**、全帧平均亮度 **173.8 → 152.6**；墙向相机一侧出现**大片连续投影**、其余地面保持明亮 |

边界（如实）：阴影为硬边（1024²/1024 m ≈ 1 m/px）+ 8 bit 深度（≈16 m/步）；目前只接入**不透明** pass；
深度图按需生成（太阳方向为常量，无需每帧重画）。下一步：16 bit 深度、CPU/GPU 阴影统一口径、alpha-tested 接入。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.33] - 2026-09-27

第四十三个版本：**太阳深度图加入真实区块几何**（补上 v0.1.32 的近景空洞）。

**本版相对 v0.1.32 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（区块进深度图）** | 新增 `skyline.GpuShadowIncludeChunks`（默认开）：LOD 网格之后遍历 `Terrain.AllocatedChunks`，把 `State==Valid` 区块的**不透明子集 0..4** 用同一个深度 shader 画进**同一张深度图**（`DrawChunkIntoDepth`，索引区间合并省 draw call、忽略贴图）→ 近景与远景共享同一深度缓冲，自然互相遮挡 |
| **新增 B（近景自检）** | 自检采样点从"仅远景 LOD 单元"扩展为 **LOD 单元 + 近景真实区块点**（玩家周围几列的真实地表顶面）；**正交盒之外的采样点**（例：y=801 高台，距太阳轴线 ~603 m > R=512 m）标记 `"outside": true` 并**排除出判定**（覆盖半径是配置项，不是缺陷） |

### Verified（AgentLab，1024²，R=512 m）

| 项 | v0.1.32（仅 LOD） | **v0.1.33（+真实区块）** |
|---|---|---|
| 覆盖 | 13.02% | **15.85%**（166,151 px） |
| 深度 | [118,173] | [115,173]，均值 142.9 |
| 画入区块 | 0 | **200** |
| 逐点自检 | 6/6 | **10/10**（6 LOD + 3 近景区块点 absErr=0，1 点 outside 排除） |
| 耗时 | 133.6 ms | **134.96 ms**（含回读） |

近景三点（全部 `absErr = 0`）：测试墙顶 (4316,9099) top=82 → 126/126；石球顶 (4280,9080) top=87 → 127/127；
平地 (4300,9100) top=65 → 127/127。
PNG：`ScreenCapture/skyline-gpushadow-20260927-154430.png`（中间空洞已被真实区块填满，墙/平台/树木清晰可辨）。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.32] - 2026-09-27

第四十二个版本：**GPU 阴影贴图第一步 —— 自编译深度 shader + 太阳视角深度图（逐点自检）**。

**本版相对 v0.1.31 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（自编译 shader）** | 证实 `Engine.Graphics.Shader` 可直接用**字符串源码**构造（`new Shader(vsh,psh)`；`ShaderCodeManager.GetFast` 只是取文本的一条途径）→ **自定义 pass 无需改 Content.zip**；`SkylineGpuShadow.cs` 内嵌 vsh/psh（HLSL+GLSL 双份）。踩坑：GLSL 片元必须显式 `#ifdef GL_ES precision mediump float; #endif`（首轮编译失败即此因） |
| **新增 B（深度图 pass）** | 太阳正交相机（`eye=center+sun·distance`、`CreateOrthographic(2R,2R,1,4·distance)`）画 LOD 粗+细网格；深度**与矩阵约定无关**：`d = dot(eye − worldPos, sunDir)` 归一化到 `[0,1]` 写进颜色（背景清 1）；`skyline.GpuShadowEnabled/Size(1024)/Radius(512)`、`GpuShadowCapture()`、`GpuShadowSavePng()` |
| **新增 C（逐点自检）** | 从 LOD 粗层单元抽 6 个（150~400 m 环带，保证在网格里）→ 同点投影进深度图 → 与 CPU 公式对照；**6/6 absErr=0** |
| **新增 D（取证接口）** | `SkylineLod.ProbeCells(centerX,centerZ,minDist,maxDist,max)`：抽"确实在当前网格里的"单元（自检采样点两次踩坑的产物：近处点/未加载远点都会落到背景） |

### Verified

| 项 | 值 |
|---|---|
| 覆盖 | **13.02%**（136,537 px / 1024²） |
| 深度范围 | min 118 / max 173 / mean 146.3（8 bit，depthMax=4096 m） |
| 逐点自检 | **6/6，absErr = 0**（例：cell(262,544) top=69 → expected 147 / map 147） |
| 耗时 | **133.6 ms/次**（含 1024² 回读，按需 pass） |
| fps | 30.1（不受影响） |
| PNG | `ScreenCapture/skyline-gpushadow-20260927-154015.png`：太阳视角 LOD 岛屿 + 中间空洞（= 网格刻意跳过的 ≤视距+8 m 近景区 → 下一步把真实区块也画进深度图） |

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.31] - 2026-09-27

第四十一个版本：**家具"透明但有碰撞"诊断（用户报告 bug 的机制确认）** + 地形阴影"随太阳重烘焙"预留。

**本版相对 v0.1.30 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（家具诊断）** | `skyline.FurnitureDiagnose(x,y,z)`：解析 contents/data → designIndex/rotation → 查 `SubsystemFurnitureBlockBehavior.GetDesign`，明确区分"行为子系统缺失 / **design 不存在（不生成几何 = 透明但有碰撞）** / 正常（报 resolution+顶点数）"；`skyline.FurnitureDesigns(limit)` 列出当前世界**真实存在**的设计索引 |
| **复现 B（用户 bug）** | 同排对照（相机 (4290.5,65,9095.5) 朝西）：`data=20`（design 5）→ `design=OK res=12 verts=1004` **渲染可见**；`data=70848/72019`（照搬 `data/furniture.json` 的 value → design 2352/2644）→ `design=NULL`，**完全不可见但 `isCollidable=True`**。结论：**`data` 里的 design 索引在当前世界不存在时，家具必然"透明但有碰撞"**（跨世界搬运 / 只写 contents 的旧命令通道是典型触发）；同时修正 `notes/97 §2` 的误判（此前把"家具正常渲染"读错了） |
| **新增 C（阴影重烘焙，预留）** | `TerrainShadowRebakeEnabled` / `TerrainShadowSunThresholdDegrees`（默认 4°）/ `TerrainShadowMinRebakeSeconds`（默认 8 s）/ `TerrainShadowRebakeRadius`（默认 256 m）：太阳方向变化超阈值后，对相机周围区块强制重建（`forceGeometryRegeneration=true`）以重烘焙顶点阴影。**实测说明**：`LightingManager.DirectionToLight1` 是 `static readonly` 常量（源码级证据）→ 当前引擎光照方向不随时间变化，该机制现版本不触发（仅开启后的首次纠偏一次），是给未来动态太阳（Iris/Dawnlight 线）预留的挂钩 |

### Verified

* `FurnitureDesigns`：AgentLab 有 **214 个真实设计**（如 `[5] res=12 verts=1004`、`[7] res=12 verts=504`）；
* 同排对照截图 `data/sessions/skyline-v0131/furniture-valid-vs-invalid.png`：合法设计可见、三个无效设计完全不可见
  （且不遮挡视线——相机正是穿过它们看向合法家具的）；
* 阴影重烘焙：开启后首次纠偏 `rebakes=1 / lastRebakeChunks=200`（`sunDot→1`），随后因光照方向为常量而不再触发；
  代价 ≈1 ms/区块、fps 29~31 无回归。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.30] - 2026-09-27

第四十个版本：**近景地形真阴影（CPU 第一刀）** —— 里程碑 5（Iris 真接入）把阴影判定从"只影响 LOD 低模"
扩展到**游戏自己的地形渲染**：区块几何生成完成时，用 LOD 高度场（8/16 m）射线步进判定遮挡，
把命中的 `TerrainVertex.Color` 按强度压暗。只改顶点颜色、不动方块/光照数据 → 重建一次即完全恢复。

**本版相对 v0.1.29 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（阴影采样）** | `SkylineLod.TerrainShadowSample(x,y,z)`：世界坐标 → LOD 高度场射线步进（细 8 m 优先/粗 16 m），返回 1 受光 / 0 遮挡 |
| **新增 B（顶点烘焙）** | `SkylineTerrainShadow.cs`（`SkylineRuntime` partial）：`ApplyTerrainShadow(chunk)` 遍历 `TerrainGeometry.Subsets[*].Vertices`（含 `Draws` 子几何），按 `TerrainShadowStrength`（默认 0.45）压暗顶点颜色；顶点级 memo 去重（≈4× 采样节省） |
| **新增 C（钩子/开关）** | 钩子挂在 `TerrainUpdater.InvalidVertices2`（两次顶点生成后、`NewGeometryData=true` 前）→ 走引擎上传路径；开关 `skyline.TerrainShadowEnabled`（默认关）+ `TerrainShadowStrength`；诊断 `TerrainShadowDescribe()`；A/B `TerrainShadowApplyLoadedChunks()` |

### Verified（AgentLab；测试墙 8×16×8 石砖；相机固定）

| 项 | 结果 |
|---|---|
| 应用代价 | 200 区块 / 774,224 顶点 / 436,194 阴影，**228.8 ms（≈1.1 ms/区块）** |
| 视觉 | 0.45：与基线差 25,359 px（>8）；**0.90：墙体投影 + 远景暗带清晰可见** |
| fps | 29~31（无回归） |
| 可逆性 | 关闭 + 强制几何重建后与基线仅差 **35 px**（雾/云量级） |
| 数据安全 | 方块/光照数据不变（抽样 light=14 正常） |

**踩坑（已写入 notes/100）**：顶点缓冲在"生成时"编译——对已上传几何"补一次"不会上屏，
A/B 必须强制一次几何重建（`DowngradeAllChunksState(InvalidVertices1, true)`）。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.29] - 2026-09-27

第三十九个版本：**列内 32 层分带存储**（里程碑 3 的 P3 第一刀）—— 竖直分配单元从 256 层改为 **32 层**
（16×16×32 = **32 KiB** = 1/4 个 32³ 立方体的竖直一片）：代表区块内存 **−50% ~ −69%**、
进程工作集 **−123 MB（−15.7%）**；寻址门禁 0 不一致、旧存档重载完整、渲染与几何追平无回归。

**本版相对 v0.1.28 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（存储粒度）** | `TerrainChunk.ColumnSliceHeight: 256 → 32`（每列 64 段）；分配/回收走 `m_cellsCache`（32 KiB 块）；硬编码 `rel>>8` / `rel&255` 全部改为新增常量 `ColumnSliceHeightBits` / `ColumnSliceMask`（避免下次改粒度再踩同一坑） |
| **兼容 B（存档）** | 存档格式**不变**：`TerrainSerializer23` 通过 (x,y,z) 存取器读写，分带是纯运行时布局 → 旧档直接可读、可回滚到 v0.1.28 的 DLL |
| **纪律 C（门禁/备份）** | 改动前后各跑一次 `SkylineInvariants.CheckChunkAddressing()`；施工前把 AgentLab 世界完整备份到 `heightlab/backup-world-v0129-agentlab`（131 项） |

### Verified

* **门禁**：2816 格写读往返 **0 不一致** + 352 次"索引减一 = y 减一"走查 **0 不一致**（改动前后一致）；
* **旧档完整性**：重载后抽样 10 格全中（地面 Ice/Snow、box 26、球壳 26、y250 角标 26、y800 平台 26、y165 平台 26）；
* **写入/渲染**：`op:shape` 8³ 空壳 **296/296 格 0.3 ms**、`EditSettleDescribe` last=**90.7 ms**、新建箱体截图渲染正常；fps 29~31；
* **内存**（同世界、200 列载荷）：

| 区块 | 旧（256 层） | 新（32 层） | 变化 |
|---|---|---|---|
| (266,568)（地表 + y249 + y419 平台） | 512 KiB | **160 KiB** | **−69%** |
| (268,568)（地表 + y250 标记） | 512 KiB | **192 KiB** | **−62.5%** |
| (265,565)（纯地表） | 256 KiB | **128 KiB** | **−50%** |
| (267,560)（纯地表） | 256 KiB | **96 KiB** | **−62.5%** |

进程工作集：**785.3 MB → 662.3 MB（−123 MB，−15.7%）**。

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.28] - 2026-09-27

第三十八个版本：**球窗的 32³ 分带内容判据**（里程碑 3 的"P2 剩余"第一刀）—— 球形加载窗的竖直判据
从"5 点采样内容带"升级为 **32³ 分带内容掩码**（256 列聚合），修掉"区块角上内容被误卸载"的盲区，
为 P3（32³ 立方体存储）铺路。

**本版相对 v0.1.27 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（分带判据）** | `TerrainChunk.ContentBandMask32`：64 位掩码（bit i ↔ y ∈ [MinHeight+32i, +31] 有内容，由该区块 256 列的 top/bottom 聚合）；球窗保留条件 = `掩码 ∩ 椭球竖直覆盖 [cy−reach, cy+reach] ≠ ∅`（reach = √(r²−h²)·m）；掩码带 stamp 缓存，并在区块卸载时写进列缓存（被丢过的列可按新判据复活） |
| **新增 B（开关/诊断）** | `skyline.SphereLoadingCubeBands`（默认开；切换即时触发窗口重算 `UpdateLocation.CubeBands`）；`skyline.ChunkWindowDecision(cx,cz)`（allocated/state/掩码/采样列(8,8)与标记列(3,3)的 top·bottom/reach·ySlice·bandOk·inRange）；`SphereLoadingDescribe()` 增加 `cubeBands/bandChecks/bandDrops` |
| **修复 C（5 点采样盲区）** | v0.1.14 的判据只看 5 个采样列：区块角上/非采样列的内容在窗口边缘会被误判"无内容"而被卸载 → 现在按 256 列聚合，不再漏（大规模建筑的边缘完整性） |

### Verified

| 场景（AgentLab，r=128，m=0.5~0.66） | 旧判据（cubeBands=OFF） | 新判据（cubeBands=ON） |
|---|---|---|
| 地表 y65 | 200 列 / 0 丢弃 | 200 列 / 0 丢弃（保守等价） |
| 高空 y420 | 32 列 / 168 丢弃 | 32 列 / 168 丢弃（保守等价） |
| **角上内容**：chunk(268,568) 的未采样列 local(3..5) 在 y250 放 3×3 石砖，相机 (4264,250,9094) | **5 列；该区块被误卸载**（`inRange=false`） | **8 列；保留**（`col(3,3) top=251`、`bandOk=true`、`inRange=true`），切换即时重算、被丢后能复活 |

构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.27] - 2026-09-27

第三十七个版本：**更正 v0.1.26 的"驻留区写得到、看不到"开放问题** —— 根因是**标记块选错**：
`contents=42` 的 `CopperIngotBlock` 属于 `IngotBlock`，而 `IngotBlock.GenerateTerrainVertices(...)`
是**空实现**（它只做手持/物品栏模型）——写进地形的铜锭**设计上就不渲染**；换成整方块（石砖 26）后，
同机位 before/after 立刻可见。`op:shape` 的 box/sphere/cylinder 自始渲染正常。

**本版相对 v0.1.26 的变更**：

| 类别 | 内容 |
|---|---|
| **更正 A（结论）** | 撤回"驻留加载区编辑不渲染"的结论；数据 / 碰撞 / 光照 / 几何链路均无异常（`EditSettleDescribe` 每次编辑都有样本、`last≈73 ms`，区块 `State=Valid`、列顶正确） |
| **新增 B（证据）** | `data/sessions/skyline-v0127/`：同机位 A/B（写石砖前 14:51:04 → 写 4 格石砖后 14:52:30 方块立刻出现）、box 近照（7 m）、box+cylinder 远景（16 m，被该世界浓雾洗白） |
| **流程 C** | 视觉验证规则：标记块只用**整方块**（26 石砖 / 66 石灰岩），取样距离 <10 m，先做同机位 before/after；`IngotBlock` 系（42 铜 / 43 铁 / 44 金…）**永远不渲染**，只能用于数据侧测试 |

### Verified

* 同机位（4291.5,65,9096.5 / yaw 90 / pitch 0）A/B 对照成立（见上表截图）；
* 源码级证据：`Survivalcraft/Block/IngotBlock.cs` 的 `GenerateTerrainVertices` 为空实现；
* `op:cell` 回读、`GetTopHeight`、区块状态机与批量光照去抖计数全部正常。

## [v0.1.26] - 2026-09-27

第三十六个版本：**原生形状建造 `op:shape`** —— 把命令方块 mod 的 place 家族形状变体在配套桥
（AgentBridge，与分支源码分仓）里**原生补齐**：box / wall / plane / line / cylinder / sphere，
带 `hollow` 与 `dryRun`；写入按区块聚集，天然吃到 v0.1.24 的批量光照去抖。

**本版相对 v0.1.25 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（桥侧形状建造）** | `op:shape`：六种模式 + `hollow`（空壳/管壁/球壳）+ `dryRun`（只算不写，返回体素数/包围盒/每区块格数）；30 万格上限；写入一律 `SubsystemTerrain.ChangeCell`（与手放同链），并按 `(cx,cz)` 排序连片写 |
| **新增 B（取证脚本）** | `heightlab/skyline-v0126-shape-site.py`：驻留加载场地 → API 直连取景（`Body.Rotation`+`LookAngles`）→ 截图 → 批量回读 → `summary.json` |
| **修复 C（工具链）** | 形状代码草稿误用 `BlocksManager.Blocks.Count`（该成员是 `Block[]` 数组、只有 `Length`）→ 改走 `AgentActions.ResolveAllContents` 统一解析（类型名 / craftingId / `GetBlockIndex` 回退） |

### Verified

* **dryRun 矩阵 6/6 与解析式一致**：box 296（8³−6³）、wall 64、plane 512、line 32、cylinder 218、sphere 410；
  包围盒与 `chunksTouched` 全对；
* **实建 896/896 格、0 跳过**：box 296（石砖）/ sphere 410（石砖）/ cylinder 122（石砖）/ plane 64（铜），
  批量回读壳与内部全部符合（内部为空气、壳体为 26 StoneBrickBlock / 42 CopperIngotBlock）；
* **碰撞成立**：玩家可站上 box 顶面 `(4267,88,9067)` 与高空铜球顶面 `(4256,205,9056)`；
* 现场图 `data/sessions/skyline-v0126/shape/shape-{wide,box,sphere}.png`（另 3 张手动图 + `summary.json`）；
* **开放问题（如实记录）**：驻留加载区里出现"写得到、看不到"——数据与碰撞成立、`EditSettleDescribe` 数据侧结算正常
  （`last=338.9ms samples=515`），但同场截图（含"正前方 2 格单格铜"对照）未稳定出现新写入体素；
  与用户报告的"超高家具透明但有碰撞体积"同类。修复方向与实验计划见 `notes/96 §5`
  （驻留区 vs 流式区 A/B、re-stream 对照、y 带扫描）。

## [v0.1.25] - 2026-09-27

第三十五个版本：**光照去抖的端到端复验** —— 用同一个家具满场压测脚本确认 v0.1.24 的优化
**没有引入回归**，并说明为什么端到端的 `settle` 指标看不出干净的赢。

**本版相对 v0.1.24 的变更**：

| 类别 | 内容 |
|---|---|
| **A（端到端复验）** | 重跑 `heightlab/skyline-v009-furniture-stress.py --no-escalate`（16³ 家具 + 贴图多样性，19 步）：**19/19 全部通过**、清理 `leftover=0`、`generatedSlicesDelta 0`、写入 0.125~0.168 ms/格、帧率最低 25.3、进程 763→902 MB、显存 22→26 MB |
| **B（对比与解释）** | 与 `notes/91` 的 v0.1.21 基线逐阶段对比：光照重的阶段 **-15%~-35%**（fill-8 7.06→4.59 s），个别阶段 +14%~+42%；`settle` 含 1 s 静默阈值 → **量化粒度就是 1 s**，阶段间真实差异只有几百毫秒，指标分辨率不足（`notes/94` 已指出）→ 优化效果的**正确口径是相位表**，端到端脚本的价值是**回归检测** |

### Verified

* 19/19 步通过、无残留、无新增异常 → **去抖可以放心默认开启**；
* 光照重的三个阶段（8/12/16 层）分别 -35% / -22% / -15%，与相位表的 -76%~-85% 方向一致（被量化与并发工作稀释）；
* 留改进项：压测的 `settle` 口径应改为 `skyline.EditSettleDescribe()` 那套（或直接用相位表），下一批压测脚本统一。

## [v0.1.24] - 2026-09-27

第三十四个版本：**大批写入期的光照去抖** —— 直冲 `notes/93` 量出来的最大成本项（光照链路 0.557 s），
实测把它砍掉 **76%~85%**，而**普通编辑行为零变化**。

**本版相对 v0.1.23 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（去抖）** | `SkylineBulkEdit.cs`：50 ms 窗口内写入达到 **64 次**才认为"这是批量操作"；进入去抖后把受影响区块收进集合（同区块只记一次），**写入停止 150 ms 后统一降级一次**（替代原本"每次写入都降级邻域"）。普通编辑低于阈值 → **行为与之前完全一致** |
| **开关/诊断 B** | `skyline.DeferLightOnBulkEdits`（默认 **true**）、`BulkEditThreshold`（64）、`BulkEditWindowMs`（50）、`BulkEditSettleMs`（150）、`skyline.BulkEditDescribe()` |

### Verified（同一受控协议：清空并等追平 → 写 4096 格 → 交替方块保证哈希必变）

| 相位 | 去抖前（v0.1.23） | 去抖后样本 A | 去抖后样本 B | 变化 |
|---|---|---|---|---|
| Light | 0.317 s（144 次） | **0.069 s（31）** | **0.039 s（18）** | -78% ~ -88% |
| LightSources | 0.198 s（96 次） | **0.059 s（31）** | **0.036 s（18）** | -70% ~ -82% |
| LightPropagate | 0.042 s（96 次） | **0.008 s（31）** | **0.007 s（18）** | -81% ~ -83% |
| **光照链路合计** | **0.557 s** | **0.136 s** | **0.082 s** | **-76% ~ -85%** |
| 写入 / 顶点 | 0.54 / 0.004 s | 0.53 / 0.005 s | 0.53 / 0.005 s | 不变 |

* 光照**重算次数** 144/96/96 → 18~31（冗余消除干净）；单格编辑后 `deferring=False pending=0`（普通玩法不受影响）；
* "settle" 指标本轮看不出差别（协议里有 1 s 静默阈值，量化粒度就是 1 s）——**相位表才是正确口径**（`notes/92`）；
* 代价：批量建造期间光照/几何最多滞后 ~150 ms；为进一步优化（批量光源收集）留到后续（收益递减）；
* 给 AI 建筑 Agent 的经验：按区块连续写、256 格/批连续发即可自动吃到去抖收益（见 `notes/94 §5`）。

## [v0.1.23] - 2026-09-27

第三十三个版本：**"全局追平"的相位拆解** —— 把 `notes/92 §5` 的下一步做完：用引擎自带的分段
计时器量化 settle 的组成，结论再次精确化。

**本版相对 v0.1.22 的变更**：

| 类别 | 内容 |
|---|---|
| **A（相位拆解脚本）** | `heightlab/skyline-v0123-phase-breakdown.py`：受控场地 → 清空并等追平 → 快照 `m_statistics` 全部计时/计数 → 批量写 4096 格（交替方块）→ 等全局追平 → 输出各相位增量（Loading / Contents1..4 / Light / LightSources / LightPropagate / Vertices1,2 / FindBest / GeneratedSlices） |
| **B（结论）** | `notes/93-全局追平的相位拆解.md`：**光照链路 0.557 s（≈29%）** ＞ 写入 0.54 s ＞ 其余（轮询粒度等）；**几何只有 0.004 s** —— 优化顺序改为 ①光照链路 ②脏队列/LOD ③几何（已证明不慢） |

### Verified（受控场地 (4200,9000)，4096 格，budget 10 ms）

```
write=0.54s  settle=1.9s  fpsMin=29.5
  光照 Light        0.317 s (count 144)   ← 最大单项
  光源 LightSources 0.198 s (count 96)
  光传播 Propagate  0.042 s (count 96)
  顶点1/2           0.003 / 0.001 s (各 18 片)
  GeneratedSlices Δ=123   SkippedSlices Δ=2437
```

* 光照链路合计 **0.557 s**，与 `notes/71` 里"光照占地形更新 CPU 45~60%"的旧结论吻合；
* **几何可忽略**（4 ms）——第三次确认 `notes/92` 的修正；
* `SkippedSlices Δ=2437 ≫ GeneratedSlices Δ=123`：引擎的切片哈希跳过机制在正常工作，
  这正是"几何便宜"的结构性原因；
* 光照链路的两个候选优化（批量光源收集 / 大批写入期延迟光照）写进 `notes/93 §4`，并要求用本相位表做 A/B。

## [v0.1.22] - 2026-09-27

第三十二个版本：**"几何追平"的直接量测** —— 给引擎加了一个**"编辑 → 该区块回到 Valid"**的指标，
并据此**修正**了 `notes/91` 的瓶颈解读。

**本版相对 v0.1.21 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（直接量测）** | 写入侧 `ChangeCell → TerrainUpdater.NotifyChunkEdited(x,z)` 记下最近被编辑的区块；该区块状态机再次到达 `Valid` 时结算时延。诊断：`skyline.EditSettleDescribe()`（last/mean/samples）。零分配、只记最近一次 |
| **新增 B（预算旋钮）** | `skyline.TerrainUpdateBudgetMs`（默认 **10** = 引擎原本的硬编码值，范围 2~50）：每帧地形状态机的时间预算 |
| **修正 C（结论）** | 受控 A/B 显示：**被编辑区块自己的几何追平只要 45~80 ms**；`notes/91` 里家具满场的 "settle 4~8 s" 是**全局追平**（邻居区块 + LOD + 脏队列 + fps 恢复）——两者口径不同，优化方向据此修正 |

### Verified（同一场地 (4200,9000)，先清空再填，交替方块类型保证哈希必变）

| 轮次 | budget | 写入 | `GeneratedSlices Δ` | **编辑→Valid** | 累计均值 | 全局 settle | fps 最低 |
|---|---|---|---|---|---|---|---|
| 1 | 10 ms | 0.54 s | 164 | 79.7 ms | 47.8 ms | 1.90 s | 28.9 |
| 2 | 25 ms | 0.54 s | 192 | 45.1 ms | 41.1 ms | 2.74 s | 29.0 |
| 3 | 10 ms | 0.54 s | 119 | 65.6 ms | 42.1 ms | 2.47 s | 29.2 |
| 4 | 25 ms | 0.54 s | 174 | 69.7 ms | 38.5 ms | 1.94 s | 28.9 |

* **budget 10 → 25 ms 对两个口径都没有可辨差异**（落在噪声里）→ **空结果**，默认保持 10 ms 不改行为；
* 写入吞吐稳定 ≈7.6k 格/秒（与 `notes/90` 一致）；
* **测量坑（已修）**：第一版协议"清空→填同种方块"会让切片哈希回到原值 → 引擎跳过几何重建 →
  好几轮 `GeneratedSlices Δ = 0`（"根本没干活"）。现在交替方块类型；
* 结论与下一步写进 `notes/92`：本地块几何只有几十毫秒，优化重点应放在**全局追平的组成**
  （邻居连锁降级 / LOD / 脏队列 / fps 恢复），"切片几何并行"优先级下降；大规模建造的工作流
  只需关注本指标与帧率，不必等全局 settle —— 这对 AI 建筑 Agent 的"边建边看"节奏很重要。

## [v0.1.21] - 2026-09-27

第三十一个版本：**家具满场压测（16³）+ 瓶颈确认** —— 量化上一批压测给出的"瓶颈在渲染层"。

**本版相对 v0.1.20 的变更**：

| 类别 | 内容 |
|---|---|
| **A（压测执行）** | 在 v0.1.20 构建上重跑 `heightlab/skyline-v009-furniture-stress.py --no-escalate`（16³ 家具填充 + 贴图多样性扫描），**19/19 步全部通过**（214 条调色板 / 4096 格）；产物 `data/sessions/skyline-v009/furniture/summary.{json,md}` |
| **B（结论文档）** | `notes/91-家具满场压测与瓶颈确认.md`：写入 0.09~0.16 ms/格，但**几何追平（settle）4.4~8.3 s** —— 渲层瓶颈的量化；内存 +582 MB/4096 格（显存仅 +4 MB）；贴图多样性 k≥16 触发 draw-call 分组（411→515） |

### Verified（AgentLab 区块 (239,543)，v0.1.20 构建）

* 分阶段填充 1024→4096 格：写入 90.8~161.2 ms/阶段、fps 最低 **26.5**（恢复 29~30）、
  **settle 4.55 / 7.06 / 5.83 / 8.32 s**、进程内存 1351→**1933 MB**、显存 54→58 MB、commit 30.93→31.43 GB；
* 贴图多样性扫描 k=1/2/4/8/16/214：k≥16 时 **draw calls 411→515**；k=214 时顶点时间 0.60→**2.15 s**；
* 清理阶段：4352 格清空、leftover 0、几何切片回落（`generatedSlicesDelta 4`）；
* 结论：**第一优化目标是"几何重建吞吐"**（写 100 ms vs 几何 4~8 s），与 `notes/71/74` 的
  切片几何并行路线一致；显存不是问题、**进程内存才是**（+582 MB），值得做几何预算+回收。

## [v0.1.20] - 2026-09-27

第三十个版本：**大规模建筑压测（96×96×41 大厅）** —— 目标要求的那条"测试大规模建筑压力"的线，
本版给出一份可复现的实测报告与脚本。

**本版相对 v0.1.19 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（压测脚本）** | `heightlab/skyline-v0120-bigbuild.py`：在指定场地建"地板 + 围墙 + 屋顶 + 中心塔"的大厅（默认 96×96×41 ≈ 34,752 格），测量写入吞吐、帧率、进程工作集、LOD 反应、抽样回读、日志新增异常，并截图存档（JSON 落到 `data/sessions/skyline-v0120/`） |
| **新增 B（起飞前自检）** | 脚本先对每种方块"放一格 + 回读"做**方块号自检**，任一不通过立即中止（exit 2）——首轮就是因为没做这一步，用一个"放不住"的方块号写了整面墙却每批都报 ok |

### Verified（AgentLab (3700,8700)，v0.1.19 构建）

* **34,752 格 / 136 批（256/批）/ 4.54 s ≈ 7,657 格/秒**，写失败 **0**，抽样回读 **6/6**；
* 帧率 建前 30.9 → 期间 31.6 → 建后 30.3（**无掉帧**）；进程工作集 **+58 MB**（1222.5→1280.8 MB）；
* LOD：`rebuilds 72→81`、`resampledDirty +≈1400`、`dirty 9→44` 随后排空、`inMesh 689(+272f)`；
* 新增日志异常 **0**；截图 `agentbridge-20260927-140013-390.png`（铜屋顶大厅在雪原上）；
* 结论写进 `notes/90`：**建造吞吐不是瓶颈**（瓶颈在渲染层），并给出下一批压测建议（家具满场 / 高空同体积 / 连续建拆）。

## [v0.1.19] - 2026-09-27

第二十九个版本：**LOD 自阴影（CPU 射线步进）+ 坡向明暗的昼夜调制**（里程碑 5 阴影路线的第 1 条）。

**本版相对 v0.1.18 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（LOD 自阴影）** | 对每个 LOD 单元从顶面中心沿**太阳方向**（`DirectionToLight1`）步进（步长 = 单元/2、最多 64 步 ≈512 m），查 LOD 高度场：被更高单元挡住 → 该单元顶点色压暗 `1-Strength`。**未采集单元跳过**（不造假阴影）。开关 `skyline.LodSelfShadowStrength`（默认 **0.35**，0=关）、`SelfShadowBias`（默认 0.75） |
| **新增 B（昼夜调制）** | 坡向明暗与自阴影都是"太阳"效应：每次网格重建取 `SubsystemSky.SkyLightValue/15` 作为日照量，按 `gain = lerp(1, gain, sunAmount)` 淡出（夜里远景不再有斜阳感，与近景随昼夜变暗一致） |
| **诊断 C** | `skyline.LodSelfShadowStats()`（阴影单元数/采样数 + 当前日照量）、`skyline.LodSelfShadowSelfCheck()`（合成"低地 + 迎光侧高墙"的确定性自检） |

### Verified

* 确定性自检：`behindWall=0.650`（=1-0.35）、`sunSide=1.000`、`ok=True`；
* 真实网格（AgentLab 雪原）：`shadowed=6/267 (2.2%) sun=1`；坡向明暗 `samples=267 gain min=0.713 mean=0.985 max=1`（两项叠加生效）；
* 自检期望写错一次（高墙放在背光侧 → 射线不经过它），已在 `notes/89 §3` 记为该类自检的通用教训；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.18] - 2026-09-27

第二十八个版本：**阴影阶段第一步 —— 太阳视角 pass + 回读验证**（里程碑 5）。

**本版相对 v0.1.17 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（太阳视角 pass）** | `SkylineShadowPass.cs`（`SkylineRuntime` partial，**默认关**）：用游戏自己的地形不透明 shader（雾关闭）把 `SkylineLod` 的粗+细网格渲染到自建 `RenderTarget2D`，相机为**太阳方向正交相机**。接口：`skyline.ShadowPassEnabled / ShadowPassSize(1024) / ShadowPassRadius(256) / ShadowPassClearColor / ShadowPassUseCameraView`、`CaptureSunView()`（回传统计）、`SaveSunView()`（存 PNG 到 `ScreenCapture/`） |
| **诊断 B** | `ShadowPassClearColor` 可设醒目色，用来**单独验证"渲染目标+清屏+回读"通路**（本次靠它把排查范围从整条管线缩到矩阵/几何）；`ShadowPassUseCameraView` 用相机矩阵渲染做二分 |

### Verified

* 清屏色回读校验：设 `(0.2,0.4,0.6)` → `meanRGB=(51,102,153)`（精确匹配）✅ 通路正确；
* 相机矩阵渲染 LOD：`visiblePixels=137,965 (13.16%)`、`rows=525` ✅（绘制链无问题）；
* **太阳矩阵渲染（修正后）**：`visiblePixels=403,608 (38.49%)`、`rows=943`、`meanRGB=(144,145,145)`、44.9 ms/次 ✅；
  存图 `ScreenCapture/skyline-sunview-20260927-135030.png`（363 KB）——图中可见雪原平台、LOD 单元裙边竖条与未覆盖区域；
* **踩坑（写进 notes/88）**：`Matrix.CreateLookAt` **自带平移**，补回 `u_origin` 后不能再减一次 eye
  （平移被扣两次 → 全黑）；正确写法 `CreateTranslation(origin3) * CreateLookAt(...) * CreateOrthographic(...)`；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.17] - 2026-09-27

第二十七个版本：**"区块 Valid 即刻补采"** —— 新区块（或玩家刚走过的区块）一达到 Valid 就立刻通知
超视距 LOD 采这个单元，而不是等轮转游标转过来（大场面游标一圈要十几秒）。

**本版相对 v0.1.16 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（补采钩子）** | `TerrainUpdater` 在 `chunk.ThreadState = Valid` 处调用新的 `SkylineLod.NotifyChunkValid(chunk)`：把该 16 m 单元 `MarkDirty`（幂等，同一 Tick 多次 Valid 只入队一次）。开关 `skyline.BackfillOnValid`（默认 **true**）；诊断 `skyline.LodBackfilledOnValid` 与 `LodSurvey().refresh.backfilledOnValid` |
| **文档 B** | `notes/87`：实现 + 两次对照测量（含"中小场面看不出吞吐收益"的诚实结论——轮转游标 60 块/秒本来就追得上 ~30 块/秒的 Valid 速率；补采的价值在 800+ 列的大场面把首帧覆盖时延从 ~13 s 压到 ~0.2 s） |

### Verified

* **钩子有效性**：世界加载完成后 `backfilled=285`；传送到新区 +8 s → **757（+433）**，`dirty` 消化到 0；
  关闭开关后再传送 → 计数**不变**（开关有效）；
* **对照**（200 列场面，+3 s）：ON `cells` +56 / OFF +78 —— 吞吐无差异（诚实记录，见 `notes/87 §3`）；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.16] - 2026-09-27

第二十六个版本：**交接带覆盖度的对照实验（负结果）+ 脏队列两处修复/诊断**。

**本版相对 v0.1.15 的变更**：

| 类别 | 内容 |
|---|---|
| **修复 A（脏队列锁死）** | 脏队列的"沉降期"判定原来在队首未到点时 `return false`（认为整队都没到点）——但**被反复编辑的队首会刷新时间戳**，于是整条脏队列被锁死（实测 `dirty=510/q=510` 长期不降）。现在改成"出队→入队尾→继续看后面的项" |
| **新增 B（自适应 + 诊断）** | `DirtyChunksMaxPerTick`（默认 64）：队列越长每 Tick 处理越多（单次重采只做 256 列顶面查询 + 256 次取样，代价很小）；新增诊断计数 `dirtyScans / dirtyTaken / dirtySkippedYoung / dirtySkippedUnloaded`（进 `LodSurvey().refresh`） |
| **新增 C（实验开关）** | `skyline.SphereLoadingContentRadius`（默认 **0** = 沿用 64）：把球形加载窗的内容距离抬到例如 256 m，用于验证"交接带覆盖度是否受内容距离限制"。**默认不改变现有行为** |

### Verified

* **负结果**（地表 y=70.5，视距 128）：内容距离 64 → **198 列 / 814.4 MB / LOD 细层 inMesh 1366**；
  内容距离 256 → **809 列（4×）/ 1207.7 MB（+393 MB）/ inMesh 仍 1366** —— 抬高内容距离**不能**改善
  LOD 覆盖（`SkylineLod.bin` 跨会话持久化，细环单元早已采过）→ 默认关，保留为诊断开关；
* **脏队列诊断**：批量加载时 `dirtyScans=1,159,794 / dirtySkippedUnloaded=1,159,789 / dirtyTaken=5` ——
  99.99% 的队列项属于"区块尚未 Valid"，`taken` 随区块逐个 Valid 缓慢上升（0.5~0.6 次/秒），
  是**平衡态而非锁死**；配合 A/B 两处改动，只要有可采项就会立即被采；
* 交接带的正确路线改写进 `notes/86 §4`：**回访式补采**（区块达到 Valid 时立刻采一次）+ 内环渐隐；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.15] - 2026-09-27

第二十五个版本：**LOD 坡向明暗**（里程碑 5 的第 2 步前置、同时服务里程碑 4.1 的交接带观感）——
远景低模不再是一张平光氈子，而是按地形起伏有明暗。

**本版相对 v0.1.14 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（坡向明暗）** | LOD 网格重建时，用**四个邻居单元高度**中心差分估顶面法线，再用**一盏太阳**（`LightingManager.DirectionToLight1`）算坡向亮度 `lit = dot(n,sun)/dot(up,sun)`（夹在 0.35~1.0），顶点色 = 固定光 × `lerp(1, lit, strength)`。开关/强度：`skyline.LodSlopeShadingStrength`（默认 **0.45**，0 = 回到 v0.1.14 平光） |
| **为什么不用 `CalculateLighting`** | 游戏用**两盏镜像方向光**，水平坡向互相抵消（实测增益恒为 1.000 = 等于没做）；单太阳后同一场景增益分布 min 0.708 / mean 0.972 |
| **诊断** | `skyline.LodSlopeShadingSelfCheck()`（合成地形：平地/东坡/西坡的确定性判据）、`skyline.LodSlopeShadingStats()`（最近一次网格重建全网格的增益 min/mean/max/样本数） |

### Verified

* **确定性自检**：`flat=1.000`、背光坡 `0.915`、迎光坡 `1.000`（夹住不炸亮）、`ok=True`；
* **真实网格**：造 6 格高平台后走到 250 m 外读统计 → `samples=635, gain min=0.708, mean=0.972, max=1`；
* **图像 A/B**（较平场景，strength 0 vs 1.0）：259 px（0.03%）变化——平场景本身坡度少，差异有限属预期，起伏场景由统计判据覆盖；
* **关闭等价**：strength=0 时不进入明暗路径（诊断 `no samples`），网格与 v0.1.14 一致；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.14] - 2026-09-27

第二十四个版本：**球形加载窗**（里程碑 3"球形视距 + 球形加载机制"的第一步）——
把相机的 update location 从"2D 圆"换成"**3D 椭球 + 列内容带**"。**默认关闭**（`skyline.SphereLoadingEnabled`）。

**本版相对 v0.1.13 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（球形加载窗）** | `UpdateLocation` 增加 `CenterY`/`SphereWindow`，新增 `SetUpdateLocation(int, Vector3, vis, content)`（`PrepareForDrawing` 改用；2D 重载保持不变）。判据 `dx² + (dy/m)² + dz² ≤ content²`：`dy` = 相机 y 到**该列内容带** `bottom..top` 的距离（相机在带内 → 退化为 2D），`m = SubsystemSky.VisibilityRangeYMultiplier`（与雾/`notes/64` 视觉球同一常数，随天气变化） |
| **新增 B（防抖动）** | 内容带来源：已加载且 `State ≥ InvalidVertices1` → 现场采样 5 个角/中心；否则查 `m_columnBandCache`（**卸载前记住**）；再否则按 2D 保守（未加载过的新列不会被"高度未知"跳过 → 世界生成不受影响） |
| 诊断 | `skyline.SphereLoadingDescribe()`（开关/已分配列数/内容带缓存/竖直系数）、`skyline.AllocatedChunkCount` |

### Verified

* **高空（y=420，视距 128）**：开关关 → 已分配列 **206**、进程工作集 **1036.4 MB**；
  开关开 → 列 **36（-82.5%）**、工作集 **964.5 MB（-71.9 MB ≈ -7%）**，25 s 内稳定，`bandCache=170`；
* **地表（y=70.5）**：开 → 198~200 列（基线 206），40 s soak 稳定（无"卸载↔装回"抖动），
  fps 28.6~30.2（上限 30）；
* **下降回地表**：列数恢复 200，数据完好（`cell(3104,68,7937)=SnowBlock light=15`），无"掉进未加载地形"；
* **默认关闭零影响**：关开关 @420 列数 = 206（与历史一致）；
* 游戏日志无新增异常（仅既存的 `Models/Alpaca*` 资源缺失）；构建 `Survivalcraft.Windows` Release **0 警告 0 错误**。

## [v0.1.13] - 2026-09-27

第二十三个版本：**32³ 三维窗口账本（里程碑 3 的 P2 第一刀）**——在真实游戏循环里量出
"把加载窗口从列换成 32³ 立方体"的规模、内存与开销。**默认关闭，不参与加载/存档**。

**本版相对 v0.1.12 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（账本）** | `SkylineCubeWindow.cs`（`SkylineRuntime` partial，`SkylineRuntime.Tick` 每帧调用、内部按"中心移动 >8 格"限频）：按 `notes/64` 的椭球判据（`dx²+(dy/m)²+dz² ≤ R²`，R 默认取设置视距）统计 **球内立方体 / 有内容立方体 / 列** 的数量与内存，并记录重算耗时、候选数、预算命中。桥接口：`skyline.CubeWindowEnabled`、`CubeWindowRadiusBlocks`、`CubeWindowYMultiplier`、`CubeWindowMoveThreshold`、`CubeWindowCandidateBudget`、`skyline.CubeWindowDescribe()`、`skyline.CubeWindowSurvey()` |
| **判据** | 计数口径与现列式"被球波及的列都要加载"一致（包围盒 vs 椭球），因此两组数字可比；另给**内容感知**口径（只算高度区间与地形内容带相交的立方体） |

### Verified

* **独立交叉验证**（同一判据的 Python 实现 vs 引擎账本）：(3104.5,70.5,7937.5) → **433 / 233** vs **433 / 233** ✅；y=150 → 439/233 ✅；y=420 → 424/233 ✅；y=900 → 424/233 ✅（全部逐项一致）；
* **关键测量**（R=128，AgentLab）：地表 球内 433 / **有内容 229**（28.6 MB）vs 列 233（58.2 MB）；
  y=150 → 有内容 100（12.5 MB）；**y=420/900 → 有内容 0**，而现列式仍占 58.2 MB；
* **开销**：重算 0.10~0.44 ms（候选 729），远低于一帧预算；
* 默认 `CubeWindowEnabled=false` 时零遍历零分配（对现有游玩无影响）；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.12] - 2026-09-27

第二十二个版本：**32³ 立方区块影子原型（里程碑 3 的 P1）**——纯新增、不接入游戏路径，
用来把"立方体寻址 / 稀疏分配 / 内存量级"三件事先钉死，再谈加载窗口与存储迁移。

**本版相对 v0.1.11 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（影子原型）** | `CubeChunk32`：32×32×32 立方体（**128 KiB/个**，惰性分配——写"空气"不租内存，与 v0.1.4 同口径）、`CubeCoord/LocalCoord/LocalIndex/Key` 双向寻址、越界写入抛异常（把"写错立方体"变成显式错误而不是静默串台） |
| **新增 B（原型自检）** | `CubeChunk32Invariants.Check()`：①世界坐标（含负数与跨立方体边界）写读往返 + 邻立方体必须为 0；②稀疏分配账（全空气不分配、写 1 格只分配 128 KiB）；③**内存对比**：64×64×8 的典型地表带 → 立方体 **512 KiB** vs v0.1.4 列式 **4 MiB**（比值 **0.125**）。桥入口：`type:CubeChunk32Invariants` |

### Verified

* `CubeChunk32Invariants.Check()` → `ok:true, checkedCells:486, mismatches:0, singleCellAllocatesBytes:131072, airOnlyAllocates:false, terrainSample:{cubes32Bytes:524288, columnSlicedBytes:4194304, ratio:0.125}`；
* 与 v0.1.11 的 `SkylineInvariants.CheckChunkAddressing()`（现存储的寻址门禁）并存，互不影响；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**；未接入任何游戏路径（渲染/加载/存档行为不变）。

## [v0.1.11] - 2026-09-27

第二十一个版本：**两个剩余里程碑的第一步** —— ①里程碑 3（32³ 立方区块）的可执行**寻址门禁**；
②里程碑 5（Iris/Dawnlight 真接入）的**LOD 只读网格访问器**。

**本版相对 v0.1.10 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（寻址自检门禁）** | `Game.SkylineInvariants.CheckChunkAddressing()`：用**临时区块**做写读往返，断言"`CalculateCellIndex` + 索引访问器"与"(x,y,z) 访问器"落到同一格，并额外核对"索引减一 = y 减一"（跨 256 层段边界也成立）。这是 v0.1.8 那类回归（写入方与读取方各自自洽、潜伏 4 个版本）的可执行防线；桥可直接调用，32³ 第 2 步施工前后各跑一次作为**落地门禁** |
| **新增 B（Iris 第 1 步）** | 新文件 `SkylineLodIris.cs`（partial）：暴露只读网格访问器 —— `Coarse/FineVertexBuffer`、`Coarse/FineIndexBuffer`、`…IndexCount`、`MeshVersion`、`VertexLayout`（`TerrainVertex` 布局），`IrisMeshInfo()`（一次性握手 JSON）、`DrawWithShader(shader)`（用外部 Shader 画粗+细层，异常吞掉记 `lastError`）。默认 `ExternalShaderHooked=false` 时行为与 v0.1.10 **逐位一致** |
| **文档 C** | `notes/81-32立方区块第2步施工计划.md`（32³ 第 2 步的 5 期施工计划、每期验收判据与施工纪律：世界副本、门禁、可观测、可回滚）；`notes/82-Iris只读网格访问器.md`（本步形状、零影响保证、剩余两步的选型） |

### Verified

* **寻址自检**：`CheckChunkAddressing` → `ok:true, checkedCells:2816, mismatches:0, indexWalkChecks:352, indexWalkMismatches:0`；
* **Iris 握手**：`IrisMeshInfo` 返回 `meshVersion/单元与索引统计/顶点布局/单元查询与绘制入口`（例：`coarseCells:153, coarseIndices:1098, fineCells:1019, fineIndices:7890, vertexLayout:"TerrainVertex … stride=20"`）；
* **零影响**：同场景 `LodDescribe` 各项与 v0.1.10 一致、渲染/帧率无变化；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.10] - 2026-09-27

第二十个版本：**列顶高度就地维护**——编辑方块时立刻更新区块的"顶面高度"字段，不再等光照阶段排队。
这是 v0.1.8「LOD 刷新不及时」的最后一块拼图：既让 LOD 更快，也让**寻路找地表 / 天气降雪**等所有
读顶面高度的系统即时正确。

**本版相对 v0.1.9 的变更**：

| 类别 | 内容 |
|---|---|
| **新增 A（就地维护顶面）** | `SubsystemTerrain.ChangeCell` 写入格子后调用新的 `MaintainColumnTopHeight`：放入非空气方块且高于当前顶面 → 顶面 = y；挖掉的正好是列顶 → 向下找下一个非空气方块（找不到置 0，与光照阶段一致）。语义与 `TerrainUpdater.GenerateChunkSunLightAndHeight` 完全相同，只是**不再排队** |
| **调参 B** | 有了 A 之后，LOD 的"沉降期"从 0.5 s 收到 **0.15 s**（原来必须等光照阶段重算顶面） |

### Verified

* **顶面即时性**：整层写 256 格（y=75）后 `terrain.GetTopHeight` **260 ms 内**（含桥往返，实际 1~2 帧）
  由 69 → **75**；此前要等光照阶段排队（0.3 s~数秒）；
* **LOD 时延**：`verify-v018-lod-refresh.py mark` 判据（≤1.0 s 刷新高度、≤3.0 s 重建网格）
  **PASS，latency 0.167 s**（v0.1.8 时 0.50 s，v0.1.7 时 7 s+ 甚至完全不动）；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

## [v0.1.9] - 2026-09-27

第十九个版本：**"高空/地表手持方块全黑"修复**（用户点名的"高度超过 0-255 限制的光照问题"）
——根因是竖直分节把"未租借段"一律读成 0（无光空气），丢掉了"顶面以上空气 = 天空光 15"的语义。

**本版相对 v0.1.8 的变更**：

| 类别 | 内容 |
|---|---|
| **修复 A（光照语义）** | `TerrainChunk` 的两个读取访问器：当目标格落在**未租借的 256 层段**里时，不再无条件返回 0，而是判断"该格在其列的顶面之上 → 返回 **air + light 15**（`SkyLitAirValue`）"，否则才是无光空气。写入侧"向未租借段写空气不租内存"的省内存策略**不变**（因此不会把 8 段全租出来），存档/读档格式不变 |
| **修复 B（工具链）** | AgentBridge 的反射入口此前**不支持带命名空间的 `type:` 根**：target 按 `.` 切段，`"type:Game.SkylineLod"` 会被切成根 `type:Game` → `unknown type 'Game'`（文档里写的用法一直是坏的）。现在显式给 `member` 时把 `type:` 之后整段当类型名；省略 `member` 时按最后一个 `.` 拆成员名。两种写法都可用 |

### Verified

* **修复 A**：`cell(3104,420,7937)`（高空空气）light **0 → 15**；`fpm.m_itemLight` **0 → 15**（手持花岗岩由纯黑恢复为正常纹理，截图 `agentbridge-20260927-125512-374.png`）；地表 `cell(3104,70,7937).light=15`、`m_itemLight=15`；**反例**：地下 (2504,-998,6774)（该列顶面之下）仍为 `light=0`、`m_itemLight=0`（封闭空间不会误判成天空光）；
* **修复 B**：`{"op":"invoke","target":"type:Game.SkylineLod","member":"LodCellAt","action":"call","args":[194,496]}` 成功返回（此前报 `unknown type 'Game'`）；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**；AgentBridge 重打包 73,164 B（1 个既有 CS0618 警告，0 错误）。

## [v0.1.8] - 2026-09-27

第十八个版本：**LOD 失效与按需重采（里程碑 4.2）** + 在验证过程中挖出的**区块存储布局不一致**
回归修复（v0.1.4 竖直分节引入的"透明但有碰撞 / 顶面恒 0 / LOD 采到基岩"）。

**本版相对 v0.1.7 的变更**：

| 类别 | 内容 |
|---|---|
| **修复 A（重大，存储寻址）** | v0.1.4 把 `TerrainChunk.Cells` 改成 `int[][]`（8×256 层惰性段）时，**索引访问器**被写成"段号=index>>16、偏移=index&0xFFFF"的**另一套布局**，与 (x,y,z) 访问器（`x*2048`、`z*32768`）不一致。凡是走 `CalculateCellIndex` + 索引访问器的路径（光照/顶面高度/切片内容哈希/方块扫描器/**地形生成器**）都读写了"别的格子"：顶面高度恒 0 → 切片哈希区间只覆盖 y≈0 附近 → **地面几何永不重建**（"有碰撞、无渲染"）、光照写不进真实格子（手持方块/地表全黑）、新生成区块"树在、地面不在"、LOD 采到"基岩高度"。修复：新增 `SplitColumnIndex` 显式换算，索引语义不变（全仓 ~50 处 `CalculateCellIndex`+`index±1` 循环无需改），存档格式不受影响 |
| **新增 B（里程碑 4.2）** | `SkylineLod` 的"采过就永不重采"（v0.1.1 起的跳过逻辑）替换为**推 + 拉**双通道：`SubsystemTerrain.ChangeCell` → `SkylineLod.NotifyCellChanged` 标脏（推），`Harvest()` 里按采样戳（区块对象引用 + `ModificationCounter` + 保鲜期）兜底重采（拉）；细层（8 m）改为**覆盖更新**（旧行为"只在缺失时补"导致细层永远滞后）；`Reset/Load` 清空刷新状态 |
| 调参（实测驱动） | ① 脏重采**不挤占**轮转预算（`DirtyChunksPerTick` 默认 8，独立预算）；② **沉降期** `DirtySettleSeconds=0.5 s`——LOD 的采样数据源（区块顶面高度）由光照阶段排队重算，不等沉降会采到旧值且被采样戳当成"刚采过"；③ 脏重采后 `DirtyVerifySeconds=2 s` 兜底复核一次；④ `LodDisabled` 期间不采集（沿用 v0.1.0 行为） |

### Verified

* **修复 A**：`GetTopHeight(3104,7937)` **0 → 68**；`cell(3104,68,7937)` 由"空气+light 0" → **SnowBlock + light 15**；新生成区块 (4200,8600) 由"只剩水+基岩" → **雪/草/土/花岗岩/砾石完整柱**；截图对照（修复前 `agentbridge-20260927-123952-113.png` 地面不可见+黑手 → 修复后 `agentbridge-20260927-124504-143.png` 雪原/树/沙滩全部正常）；
* **新增 B**：`heightlab/staging/v018-lod-refresh/verify-v018-lod-refresh.py mark` —— 整层填平一个 16×16 单元后，LOD 单元高度在 **0.50 s** 内变成新平台高度（判据 ≤1.0 s **PASS**；修复 A 之前 7.2 s 才刷到、12:48 那次甚至 9 s+ 不刷新，因为采样源顶面高度还没沉降）；`watch` 20 s 内 `dirtyCells/pendingResamples` 回落 0、`lastDirtyLatencyMs≈0.5 s`；`persist`（`LodSaveNow`）写盘正常；
* 构建：`Survivalcraft.Windows` Release，**0 警告 0 错误**。

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
