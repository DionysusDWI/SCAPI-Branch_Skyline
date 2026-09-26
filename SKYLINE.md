# SCAPI Skyline Project

> 基于 **SCAPI（Survivalcraft API）最新游戏源码** 的分支构建版本。
> 主仓库：<https://github.com/DionysusDWI/SCAPI-Branch_Skyline>
> 本地源文件夹：`SCAPI-Branch_Skyline-Project`

## 1. 定位

Skyline 分支**只为一件事服务**：让《生存战争》成为可以大规模、自动化、由 AI agent 参与的
**创意建筑平台**。上游 SCAPI 源码保持原样可编译，本分支在其上做"建筑特化"的源码修改与功能追加。

具体目标：

* **更高的世界**：竖直可建造空间从 0–255 扩到 **-1024 – 1023**（v0.0.1：0–1023；v0.0.2：-128–1023；v0.0.3：-1024–1023）。
* **生存余量与取景模式**：建筑范围之外上下各留 **64 格生存余量**（人物安全范围 -1088..1087）；
  `SkylineRuntime.FreeViewMode`（取景模式）打开后不受高度/深度伤害与缺氧限制（v0.0.3 只做底层接口，暂无 UI 按钮）。
* **建筑辅助**：与外部操作桥（AgentBridge）/ 命令方块 mod 配合的批量建造、几何与光照失效自动化。
* **创造模式工具**：面向大体量建筑的放置、选择、复制、验证能力。

## 2. 与上游的关系

| 项目 | 值 |
|---|---|
| 上游 | SCAPI 游戏源码（`.resource/SurvivalcraftApi`，分支 `SCAPI1.9`，SCAPI 1.9.3.1） |
| 目标框架 | `net10.0`（Windows 桌面构建） |
| 版本 | **v0.0.1**（0–1023）、**v0.0.2**（-128–1023，含旧存档兼容）、**v0.0.3**（-1024–1023 + 上下各 64 格生存余量 + 取景模式接口 + 手持光照修复）——见 `CHANGELOG-Skyline.md` |
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

把上面 4 个产物覆盖到游戏目录（例如 `Windows-SCAPI_1.9.3.1\`）。**Content.zip 必须与 dll 同源**——
本源码树的 Content 比部分随包发布版新，混用会出现"缺少控件"之类的启动错误。
部署前请备份原版 `Survivalcraft.dll` / `Engine.dll` / `EntitySystem.dll` / `Content.zip`。

## 5. v0.0.3 特性：世界竖直范围 → **-1024 – 1023** + 64 格生存余量 / 取景模式

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

## 5b. v0.0.2 特性：世界竖直范围 → **-128 – 1023**（地下 128 层）

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

## 5c. v0.0.1 特性：世界高度 0–255 → 0–1023

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

## 6. 已知限制 / 后续路线

限制：

1. 每区块单元数 65536 → **524288**（-1024..1023，int 4B ≈ **2 MB/区块**，是原版 256KB 的 8 倍），视距大时注意；可降低 `settings.VisibilityRange`。
2. 命令方块 `place` 走 `SetCellValueFast`，不刷新 shaft/几何/光照 → 高处方块要用
   `ChangeCell`（桥的 `op:cell`）写，或建完触发 recalc。
3. 范围 **-1024..1023**（人物另有上下各 64 格生存余量，见 §5）；想更深/更高只需调 `TerrainChunk.MinHeight`（层数线性影响内存）；
   地下区域没有地形生成（天然空腔，适合创造模式挖建）；旧版序列化器（14/22/129）仍按 0 起点。
4. 取景模式（`FreeViewMode`）只有底层接口：伤害/缺氧豁免已生效，UI 按钮与相机自由飞行未做。

路线（草案）：

* 更深 / 更高：`MinHeight` 继续下探（索引与 shaft 都已是偏移式，改动面很小）。
* 建筑 API：区块级批量放置 + 自动几何/光照失效，供 agent 直接调用。
* 创造模式工具：区域选择/复制/镜像、蓝图导出。
* 与 AgentBridge / 命令方块 mod 的接口对齐（本分支是"游戏侧"，桥与 mod 在另一个仓库）。
