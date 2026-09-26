# Changelog —— SCAPI Skyline Project

本文件只记录 **Skyline 分支相对上游 SCAPI 源码**的特化改动。
格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

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
