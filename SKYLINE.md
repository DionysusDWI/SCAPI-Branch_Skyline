# SCAPI Skyline Project

> 基于 **SCAPI（Survivalcraft API）最新游戏源码** 的分支构建版本。
> 主仓库：<https://github.com/DionysusDWI/SCAPI-Branch_Skyline>
> 本地源文件夹：`SCAPI-Branch_Skyline-Project`

## 1. 定位

Skyline 分支**只为一件事服务**：让《生存战争》成为可以大规模、自动化、由 AI agent 参与的
**创意建筑平台**。上游 SCAPI 源码保持原样可编译，本分支在其上做"建筑特化"的源码修改与功能追加。

具体目标：

* **更高的世界**：竖直可建造空间从 0–255 扩到 **-1024 – 1023**（v0.0.1：0–1023；v0.0.2：-128–1023；
  v0.0.3：-1024–1023 + 64 格生存余量；v0.0.4：把范围内的行为子系统、方块实体、寻路/阴影/降雪等全部对齐到新范围）。
* **生存余量与取景模式**：建筑范围之外上下各留 **64 格生存余量**（人物安全范围 -1088..1087）；
  `SkylineRuntime.FreeViewMode`（取景模式）打开后不受高度/深度伤害与缺氧限制（v0.0.3 只做底层接口，暂无 UI 按钮）。
* **跑在最强显卡上**：v0.0.4 起内置 `SkylineGpu`——第一次运行用默认适配器扫描并提示重启，重启后自动跑在评分最高的显卡
  （独显优先、NVIDIA 优先、显存大者优先）；系统偏好不可用时用 ANGLE 按 LUID 直选。
* **建筑不必"站在现场"**：v0.0.5 起 `SkylineRuntime.ChunkResidencyMode` 可以把目标区域**驻留**在内存里——
  远处列不再被静默释放，写入不再"看起来成功、读回来是 0"（`EnsureRegionLoaded` + `ResidencyStatus` 轮询）。
* **建筑可以当对象搬**：v0.0.5 起 `SkylineBlueprint` 支持**捕获 / 旋转 / 镜像 / 贴回 / 导入导出**与 Fill / Replace，
  写世界走 `ChangeCell` + 光照重算 + 回读校验，列没加载就如实报 `skipNotLoaded`。
* **天空跟着建筑走**：v0.0.5 起 `SkylineAtmosphere` 让**云层与视图雾带的高度**可配、可逐层指定、可跟随相机高度
  （默认关闭 = 与原版逐位一致），高空建筑不再"跑到云上面"。
* **NVIDIA 深化（只读）**：v0.0.5 起 `SkylineNvidia` 用 NVAPI 读出驱动/型号/显存/核心/架构/温度/占用，
  为将来的 DLSS / 光追铺路；DLSS 与光追是**独立开关（默认关闭）**，`-nvapi off` 可整体关闭。
* **建筑辅助**：与外部操作桥（AgentBridge）/ 命令方块 mod 配合的批量建造、几何与光照失效自动化。
* **创造模式工具**：面向大体量建筑的放置、选择、复制、验证能力。

## 2. 与上游的关系

| 项目 | 值 |
|---|---|
| 上游 | SCAPI 游戏源码（`.resource/SurvivalcraftApi`，分支 `SCAPI1.9`，SCAPI 1.9.3.1） |
| 目标框架 | `net10.0`（Windows 桌面构建） |
| 版本 | **v0.0.1**（0–1023）、**v0.0.2**（-128–1023，含旧存档兼容）、**v0.0.3**（-1024–1023 + 上下各 64 格生存余量 + 取景模式接口 + 手持光照修复）、**v0.0.4**（竖直范围鲁棒性 16 处 + 显卡自动选择）、**v0.0.5**（区块列驻留 + 蓝图/区域变换 + 分层云雾/天空高度 + NVAPI 只读深化 + 最底层几何崩溃修复）、**v0.0.6**（贝塞尔曲线铺设 `SkylineBuilder`）、**v0.0.7**（分层云雾三带预设 `SkylineAtmosphere.LayeredPreset()`）、**v0.0.8**（稳定性验证 + 球形势距/32³ 区块、体积雾、NVIDIA 特性可行性探索，无引擎实现改动）、**v0.0.9**（切片哈希区间修复：高空方块不再"有碰撞无渲染"；高复杂度家具几何预算 `SkylineFurniture` + 方盒占位；单区块 16³ 4096 件高复杂度家具压力测试与中压回归）、**v0.1.0**（多层家具 LOD：占替距 `d_box(E)` 实装 + 区块三态 + 高空失效修复；超视距 LOD `SkylineLod`：加载即采样/持久化/1024 m 低模渲染 + 双面裙边；单区块 4096 件高复杂度家具总内存 21.88 GB；32³ 决策报告）——见 `CHANGELOG-Skyline.md` |
| License | 沿用仓库根的 `LICENSE`（上游内容版权归原作者，本分支仅作建筑特化修改） |

## 3. 目录结构

```
Engine/ EntitySystem/ Survivalcraft/          # 上游源码（本分支在其上做高度/光照特化修改，清单见 CHANGELOG-Skyline.md）
Engine.Windows/ EntitySystem.Windows/ Survivalcraft.Windows/   # Windows 构建目标
Engine.Android|Browser|IOS|Linux/ ...         # 其它平台目标（需要对应 workload，本分支不构建）
build/ docs/ scripts/                         # 上游构建脚本与文档
Build-Windows.ps1                             # 本分支新增：一键构建 / 部署
Content/ (位于 Survivalcraft/Content)          # 内容资源（构建时自动打包成 Content.zip）
```

## 4. 构建

```powershell
# 方式一：本分支脚本（推荐）
powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1
powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1 -Deploy     # 覆盖到游戏目录

# 方式二：直接 dotnet
dotnet build .\Survivalcraft.Windows\Survivalcraft.Windows.csproj -c Release
# 产物：Survivalcraft.Windows\bin\Release\net10.0-windows\win-x64\
#       Survivalcraft.dll / Engine.dll / EntitySystem.dll / Content.zip
```

> ⚠ 不要用 `SurvivalcraftApi.sln`：它还包含 Android / iOS / Browser 目标，需要
> `android` / `ios` / `wasm-tools` 工作负载（默认 SDK 没装）。

### 部署（替换游戏本体，不是 mod）

把产物覆盖到游戏目录（例如 `Windows-SCAPI_1.9.3.1\`）：`Survivalcraft.dll` / `Engine.dll` / `EntitySystem.dll` / `Content.zip`，
v0.0.4 起还要一起部署 `libEGL.dll` / `libGLESv2.dll` / `glfw3.dll`（ANGLE 按 LUID 选卡依赖它们）。
**Content.zip 必须与 dll 同源**——本源码树的 Content 比部分随包发布版新，混用会出现"缺少控件"之类的启动错误。
部署前请备份原版这些文件（`Build-Windows.ps1 -Deploy` 会自动覆盖）。

## 5. v0.0.4 特性：竖直范围鲁棒性（16 处硬编码上下界）+ 显卡自动选择

### 5.1 竖直范围：把 0..255 的老口径全部对齐到 -1024..1023

v0.0.3 只保证"范围本身可用"（写入/存档/光照/几何），但**下游行为子系统**仍按 `0 / 255 / 256` 工作，实测表现：

| 现象 | 根因 | 现在 |
|---|---|---|
| 高处的沙/砾石柱失去支撑**悬空不塌** | `CollapsingBlockBehavior` 的 `p.Y <= 0` / `< 256` | 跟 `MinHeight` / `HeightMinusOne` |
| 地下（y<0）的**箱子打不开** | `SubsystemBlockEntities` 用 `Coordinates.Y >= 0` 过滤 | `>= MinHeight`（并容忍旧档里的重复项） |
| y>254 的**水面渲染成整块**立方体 | `UpdateIsTop` 的 `y < 255` | `y < HeightMinusOne` |
| 地下/高处的**植物不生长**、耕地不湿润、木头/落叶扫描越界 | 各 block behavior 写死 0/255 | 全部改用 `MinHeight` / `HeightMinusOne` |
| 扩展高度**寻路/阴影/降雪**异常 | `SubsystemPathfinding` / `SubsystemShadows` / `SubsystemModelsRenderer` / `SubsystemWeather` | 同上 |
| VR 传送落点净空只看 0..254 | `ComponentInput.CountClearance` | `MinHeight..HeightMinusOne` |
| 生物在扩展高度找不到逃跑/飞走落脚点 | `ComponentFlyAway/RunAwayBehavior` 的 `255..0` 扫描 | `HeightMinusOne..MinHeight` |

实测口径（详见 `CHANGELOG-Skyline.md` 的 Verified 表）：沙柱在 y=2/y=1000/y=-20 三个高度行为一致；
箱子在 y=-3 可以正常打开（`handled=1` + `ChestWidget`）；水在 y=200 与 y=1000 都是 57 格铺开 + 6 格外溢 + `data` 带 isTop 位。

### 5.2 显卡自动选择（`Game/SkylineGpu.cs`）

```
# 看状态（游戏内 / 桥）
SkylineGpu.Describe()      → 适配器列表、评分选中的卡、当前渲染器、状态文件内容
SkylineGpu.Rescan()        → 立刻重新枚举（只读，不影响本次运行）
SkylineGpu.ClearTarget()   → 清掉目标卡（下次启动重新扫描）

# 关掉这套逻辑（用默认适配器跑）
run-game.ps1 -ExtraArgs "-skylinegpu off"

# 状态文件（删掉即重新扫描）
<游戏目录>\SkylineGpu.cfg
```

流程与需求一一对应：**第一次运行 = 默认显示适配器**（写 `GpuPreference=2` 并弹一次"重启后生效"）
→ **重启 = 目标卡**（复核 `Display.DeviceDescription`，写 `state=applied`）
→ 系统偏好没生效时，下一次启动自动改走 **ANGLE 按 LUID 直选**（`EGL_PLATFORM_ANGLE_DEVICE_ID_HIGH/LOW_ANGLE`），
本次启动即成；两条路都不行才记 `failed` 并提示看日志。

证据：`data/sessions/skyline-v004/gpu-run1-first-run-prompt-v004.png`（首启"重启后生效"对话框，最终构建）、
`gpu-runA-fresh-amd-first-run.log.txt` / `gpu-runB-restart-nvidia.log.txt`（"全新安装副本"的两步流程日志）、
`gpu-verified-after.txt`、`gpu-preference-proof.md`（"写注册表偏好 → 同一 exe 的 GL_RENDERER 从 AMD 变 NVIDIA"的因果实验）、
`heightlab/gpu-probe.ps1`（独立用 Windows 性能计数器归因，实测渲染占用全在 NVIDIA LUID）。

## 5b. v0.0.3 特性：世界竖直范围 → **-1024 – 1023** + 64 格生存余量 / 取景模式

在 v0.0.2（-128–1023）基础上把地下对齐到 **-1024**，并明确"建筑范围之外仍可生存"的余量口径：

| 项目 | 值 |
|---|---|
| 建筑范围 `SkylineRuntime.BuildMinY..BuildMaxY` | **-1024 .. 1023**（世界真实存在的格子） |
| 生存余量 `SurvivalMargin` | **64 格**（建筑上下限之外各一份） |
| 人物安全范围 `SurvivalMinY..SurvivalMaxY` | **-1088 .. 1087**（普通模式在此不掉虚空血、不缺氧） |
| 取景模式 `FreeViewMode` | 为 true 时**完全不受**虚空伤害 / 缺氧限制（底层接口，无 UI 按钮） |

改动点：

| 文件 | 改动 |
|---|---|
| `Game/TerrainChunk.cs` | `MinHeight -128 → -1024`；`Height 1152 → 2048`（层数）；`SlicesCount 72 → 128`；`CalculateCellIndex` **越界改为夹紧**（抛异常会让整片 slice 的网格生成中断 = "地形整块消失"） |
| `Game/SkylineRuntime.cs` | **新增**：`SurvivalMargin` / `FreeViewMode` / 范围属性 / `IsInside*Range()` / `Describe()` |
| `Component/ComponentHealth.cs` | 缺氧与虚空伤害阈值改走 `SkylineRuntime`；取景模式下豁免 |
| `Game/TerrainSerializer23.cs` | 存档起点通式 `yBase = storedLayers > 1024 ? 1024 - storedLayers : 0`（兼容 256 / 1024 / 1152 / 2048 层的旧档） |
| `Component/ComponentFirstPersonModel.cs`、`ComponentVrHandsModel.cs` | 手持取光下界 `0 → TerrainChunk.MinHeight`（**修掉地下手持方块全黑**） |
| `Managers/LightingManager.cs` | `CalculateSmoothLight` 取样下界 `0 → TerrainChunk.MinHeight` |

实测（Windows / 世界 `AgentLab`）：`IsCellValid(-1025/-1024/-1000/0/1023/1024) = F/T/T/T/T/F`；
y=-200/-1000/-1024 可写可读；y≈-1000 的房间渲染与光照（9–15）正常；存档往返保留；
普通模式下 y=±1050 不掉血不缺氧、y=±1100 开始掉血 + 缺氧；`FreeViewMode=true` 时 y=±1200 仍安全；
手持取光：y=-998.76 → 11（等于该格世界光）、y=1001 → 15、暗角（格光 0）→ 0。

接口（游戏侧 `Game.SkylineRuntime` 是静态成员，可被外部桥直接调用；下面的示例用配套的 AgentBridge 工具，
该 mod 与工具在另一个仓库，仅作调用形式说明）：

```powershell
python tools\scbridge.py raw '{"op":"invoke","target":"skyline","member":"Describe","action":"call"}'
# Skyline build=[-1024,1023] survival=[-1088,1087] freeView=False
python tools\scbridge.py raw '{"op":"invoke","target":"skyline.FreeViewMode","value":true}'   # 读取时不带 value
```

## 5c. v0.0.2 特性：世界竖直范围 → **-128 – 1023**（地下 128 层）

在 v0.0.1（0–1023）基础上把地下打通，关键是六处 `y≥0` 假设：

| 现象 | 位置 | 处理 |
|---|---|---|
| 负层不可写 | `TerrainChunk.CalculateCellIndex / Get-SetCellValueFast` | 位打包改**算术偏移** `(y-MinHeight)+x*Height+z*Height*Size` |
| 列元数据放不下负数 | `Terrain` shaft 高度字段 | 10 位 → **11 位并存 `height - MinHeight`**（11+4+4+11+11=41 位，仍在 long 内） |
| 负层不显示 | `TerrainUpdater` 几何片区间 | 片区间改 `MinHeight + 16*index` |
| 负层灯不亮 | `TerrainUpdater.PropagateLightSources` 的 `if (y > 0)` | 改 `> MinHeight` |
| 负层无碰撞 | `ComponentBody` 扫格下界 `Max(point.Y, 0)` | 改 `MinHeight` |
| 负层被上界卡 | `ComponentHealth` 的 `Y < 0` 虚空伤害 | 改 `Y < MinHeight`（-128 以内安全） |
| 老存档下移 | `TerrainSerializer23` 的层数起点 | **按流里总格数识别布局**（256 / 1024 / 1152 层），只有整高度流才从 `MinHeight` 起 |

实测：`IsCellValid(-129/-128/0/1023/1024)=F/T/T/T/F`；地下 y=-65..-60 建房间后渲染/光照
（12–15 递减）/碰撞（撞墙停在 x=2593.25）/存活（health 与 Air 恒 1）/存档往返全部通过，
且 v0.0.1 的别墅与家具无损。

## 5d. v0.0.1 特性：世界高度 0–255 → 0–1023

改 16 个文件、+85/−73 行，修掉五条独立的限制链：

| 现象 | 位置 | 说明 |
|---|---|---|
| 只能盖 0–255 | `TerrainChunk` / `Terrain.IsCellValid` | `Height 256→1024`、`HeightBits 8→10`、`SlicesCount 16→64`、`Shafts int[]→long[]` |
| 超过就不显示 | `TerrainUpdater` 几何上界 `byte.MaxValue` | 网格只生成到 255 |
| 超过就没有碰撞 | `ComponentBody.FindTerrainCollisionBoxes` 夹 `Y≤255` | 身体扫格上限 |
| 高处手持方块全黑 | `ComponentFirstPersonModel` / `ComponentVrHandsModel` 的 `<=255` | 手持取光被跳过 |
| 超高/超低扣血致死 | `ComponentHealth` 的 `259f` / `296f` | 改为 `Height+4f` / `Height+41f` |
| 高处方块存不下来 | `TerrainSerializer23.ChunkSizeY = 256` | 改为 `TerrainChunk.Height` |

实测（Windows / 世界 `AgentLab`）：`IsCellValid(255/256/1023/1024)=T/T/T/F`；
y=256/300/700/1000/1023 可写可读；存档往返后 y=300/700/1000 保留；y=301–305 可见；
手持取光在高处为 14–15；y=700 悬停不掉血不缺氧；y=301 平台撞墙停在 x=2603.75。

## 5a. v0.0.6 特性：贝塞尔曲线铺设（`SkylineBuilder`）

> 用户口径："**不是某个圆或者椭圆的一部分，而是通过贝塞尔曲线生成的**"——像 Axiom 那样，
> 把一段横截面（车道 / 车道线 / 护栏 / 路肩）沿任意贝塞尔曲线扫出去。

| 环节 | 做法 |
|---|---|
| 曲线 | 三次贝塞尔（4 控制点）；控制点多于 4 个时用 **Catmull-Rom 转贝塞尔**（曲线过每个控制点、C1 连续） |
| 采样 | **按弧长等距**（先建累计弧长表再等距取站）——按 t 等分会在弯道处站点变密 |
| 坐标系 | **平行传输（rotation-minimizing frame）**：法向绕相邻切线夹角轴旋转，避免剖面扭转 |
| 剖面 | 从世界里**切一段横截面**（局部轴 = 横 R / 竖 U / 沿 A，建议沿路径方向厚 1 格） |
| 写入 | `SubsystemTerrain.ChangeCell`（刷光照/几何/方块行为）+ 逐格统计 + 每次扫掠一个撤销组 |
| 选项 | `step`、`lat`/`vy`、`mirror`、`groundFollow`/`groundOffset`、`onlyAir`、`dry`、`max`、`ensureLoaded` |

桥调用（AgentBridge 根 `skylinebuilder` / `builder`）：

```powershell
$spec = "points:2900,100,7000;2940,104,7006;2990,96,7024;3040,100,7060 profile:2900,100,6997;2900,101,7003 step:1 onlyAir:1 groundFollow:0"
python tools\scbridge.py raw '{\"op\":\"invoke\",\"target\":\"skylinebuilder\",\"member\":\"Preview\",\"action\":\"call\",\"args\":[\"' + $spec + '\"]}'
python tools\scbridge.py raw '{\"op\":\"invoke\",\"target\":\"skylinebuilder\",\"member\":\"SweepBezier\",\"action\":\"call\",\"args\":[\"' + $spec + '\"]}'
python tools\scbridge.py raw '{\"op\":\"invoke\",\"target\":\"skylinebuilder\",\"member\":\"Undo\",\"action\":\"call\"}'
```

实测（AgentLab）：154 站 / 1386 格 → **写入 1258 格、0.5 ms、`skipNotLoaded=0`**；
沿曲线 5 个抽样点窗口内都有路面；`Undo()` 一次还原 1258 格（剩余 0）。
截图：`data/sessions/skyline-v005/builder/shots/bezier-road-on-road.png`。

已知限制：剖面须"沿路径厚 1 格"（更厚会自交）；不支持倾斜超高（banking）与变截面；
`groundFollow` 在陡坡处会台阶化；撤销栈只保留最近一次扫掠且不写存档。

## 6. 已知限制 / 后续路线

限制：

1. 每区块单元数 65536 → **524288**（-1024..1023，int 4B ≈ **2 MB/区块**，是原版 256KB 的 8 倍），视距大时注意；可降低 `settings.VisibilityRange`。
2. 命令方块 `place` 走 `SetCellValueFast`，不刷新 shaft/几何/光照 → 高处方块要用
   `ChangeCell`（桥的 `op:cell`）写，或建完触发 recalc。
3. 范围 **-1024..1023**（人物另有上下各 64 格生存余量，见 §5）；想更深/更高只需调 `TerrainChunk.MinHeight`（层数线性影响内存）；
   地下区域没有地形生成（天然空腔，适合创造模式挖建）；旧版序列化器（14/22/129）仍按 0 起点。
4. 取景模式（`FreeViewMode`）只有底层接口：伤害/缺氧豁免已生效，UI 按钮与相机自由飞行未做。
5. 区块是**按 XZ 整列**（16×16×2048）加载的：离玩家很远、尚未 Valid 的列里，脚本写入（`op:cell`）会被**静默丢弃**。
   脚本化建造请把场地放在玩家附近，或先用 `teleport`/`surface` 把那一列踩出来（v0.0.4 实测踩过的坑）。
6. 云层/雾的视觉参数仍是原版口径（云在 y≈256 附近），高空建筑会**位于云层之上**；`ComponentFlyAway/RunAway`
   的落脚点扫描从 256 次变成 2048 次（每次决策成本上升，实测无可见卡顿）。
7. 显卡自动选择：只在 **Windows** 有效；换卡需要重启（系统偏好的生效时机），日志里搜 `Skyline GPU:` 可看全流程。
   想要固定用核显，把 Windows"图形设置"里本游戏设为"省电"或在 `SkylineGpu.cfg` 里清掉目标（删文件即可）。

路线（草案）：

* 更深 / 更高：`MinHeight` 继续下探（索引与 shaft 都已是偏移式，改动面很小）。
* 建筑 API：区块级批量放置 + 自动几何/光照失效，供 agent 直接调用。
* 创造模式工具：区域选择/复制/镜像、蓝图导出。
* 与 AgentBridge / 命令方块 mod 的接口对齐（本分支是"游戏侧"，桥与 mod 在另一个仓库）。
