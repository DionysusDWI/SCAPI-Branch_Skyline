# Changelog —— SCAPI Skyline Project

本文件只记录 **Skyline 分支相对上游 SCAPI 源码**的特化改动。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

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
