# SurvivalCraft API 生存战争插件版

## 介绍

生存战争插件版是基于 Candy Rufus Game 开发的 [生存战争 Survivalcraft](https://kaalus.wordpress.com/) 二次开发的支持加载模组的版本

## SCAPI Skyline Project

> 基于**最新 SCAPI 游戏源码**的"建筑特化"分支：更高的世界 + 建筑辅助能力（面向 AI / agent 建造与创造模式工具）。

**当前状态：v0.1.3** —— 在 v0.0.4（竖直范围 **-1024..1023** 全链路对齐 + **显卡自动选择**）之上，
补上**大规模 / 高空建造链路**的能力，并落地三层渲染路线的第一轮（多层家具 LOD + 超视距 LOD）：

- **视觉球三档**（v0.1.3）：`skyline.VisualSphereEnabled` 开启后——**短球内**（视距×0.5）**
  强制全精度**（含被遮挡件）、**短球~完全球之间**按占替距 `d_box(E)`、**完全球外**一律方盒占位
  （地形由 LOD 接管）。这就是用户指示的"短球形视距内渲染 / 两球之间占位 / 球外 LOD 替代"。
- **超视距 LOD 精细度分层**（v0.1.2）：**8 m 精细层**覆盖近环（视距~视距×2）、**16 m 粗层**覆盖远环
  （到 1024 m）——用户"128 格视距下 LOD 非常粗糙"的反馈落地为双分辨率网格；旧存档自动兼容并补采细层。

- **性能 HUD 真实化**（v0.1.1）：原 `CPU x%` 只是"主线程占帧比"，现新增 **SYS（系统 CPU 总占用）**、
  **PROC x%/16 cores（进程占全机）**、**GPU x%（NVAPI 利用率）**——左上角一眼看清真实负载。
- **LOD 与视距边缘的雾统一**（v0.1.1）：视图雾跨度拉远到 `LOD 半径×0.9`，真实地形与 LOD
  **共用同一条雾曲线**——消除"视距边缘全雾消失 / LOD 无雾跳出"的交接跳变（A/B 差异 18 万像素）；
  `skyline.LodFogExtend` 可切换。

- **多层家具 LOD**（v0.1.0，`SkylineRender`）：按实测的**占替距**曲线 `d_box(E)=39.6·E^0.300`
  （E=暴露度，≈投影 ≤70 px²）把超出距离的家具渲染为**主材质方盒**；区块级 Full/Mixed/Boxed
  三态 + 5% 迟滞 + 每 tick 重建预算。高空俯视（水平近/垂直远）的失效已修复，双向对照：
  800 m 处整块占位（8,210 box / 0 full）、15 m 处全精度为主（20,478 full / 4,143 box）。
- **超视距 LOD**（v0.1.0，`SkylineLod`）：Distant Horizons 式**加载即采样**——16 m 粗网格记
  最低顶面高度+材质，每 60 s 落盘世界目录 `SkylineLod.bin`，半径 1024 m 内以低模（顶面+双面裙边）
  渲染。实测：关 LOD 远处只有雾、开 LOD 补出地形（on/off 差异 15,954 px、噪声 0）；
  重启后 `loaded 1050 / 989 cells` 两次持久化验证通过。
- **40 GB 硬指标**（v0.1.0）：单区块 16³ 填满 4096 件高复杂度家具（每件非空体素 >10,000）
  → 物理内存峰值 **21.9~26.1 GB**（两次会话实测；红线 56 GB）、GPU 几何 116→167 MB、fps ≥28.6。

- **修"高空写入有碰撞、无渲染"**（v0.0.9）：`TerrainUpdater` 的切片内容哈希区间钳位在高度扩展后没同步
  （`SliceHeight-1` 停留在上游 16 切片时代）→ 16 号以上切片永不重建几何。修复后高空方块立即渲染，
  空闲几何重建从 ≈3 万切片/10 s 降到 0~300/10 s。
- **高复杂度家具几何预算**（v0.0.9，`SkylineFurniture`）：家具几何按**实例**重复烘进区块顶点缓冲，
  实测一件 28³ 棋盘家具 ≈263k 顶点 ≈6.5 MB 显存；单区块塞满外推 26 GB 显存 + 86 GB 内存。
  现在每个几何阶段有顶点预算，超出部分**退化为方盒占位**（可见、保留碰撞），可用
  `SkylineRuntime.Furniture*` 调参/查询。

- **贝塞尔曲线铺设**（`SkylineBuilder`，v0.0.6）：把一段**横截面**沿**三次贝塞尔曲线**扫出去
  （弧长等距采样 + 平行传输坐标系），支持多段拼接、贴地形、偏移 / 镜像 / 材质替换 / `dryRun` / 一次撤销
  —— 就像 Axiom 那样铺弯道公路，而不是只能画圆弧。
- **区块列驻留**（`SkylineRuntime.ChunkResidencyMode`）：把目标区域"钉"在内存里，**远处建造不再静默失效**；
  写入接口也不再假装成功（列没加载就明确报错）。
- **蓝图 / 区域变换**（`SkylineBlueprint`）：把一块区域当对象**捕获 / 旋转 90°·180°·270° / XZ 镜像 / 贴回 / 导入导出**，另有 Fill / Replace。
- **分层云雾 / 天空高度**（`SkylineAtmosphere`）：云层高度可配、可逐层指定、可跟随相机高度，视图雾带随高度抬升
  —— 高空建筑不再"跑到云上面"（默认关闭时与原版逐位一致）。
  用户口径的三带（低层雾 / 底云 **+300~400** / 高云 **≈+900**）在 v0.0.7 起有 `LayeredPreset()` 一键预设。
- **NVIDIA 深化**（`SkylineNvidia`）：NVAPI 直连**只读**信息（驱动 / 型号 / 显存 / 核心 / 架构 / 温度 / 占用），
  DLSS / 光追是**独立开关（默认关闭）**，`-nvapi off` 可整体关闭；非 NVIDIA 机器零副作用。
- **修既有 bug**：y = 最底层（MinHeight）放方块的几何生成 `IndexOutOfRangeException`（实测修复前 30~40 条/分钟并会卡死主线程 → 修复后 0 条）。

| 版本 | 内容 | 日期 |
|---|---|---|
| v0.0.1 | 世界高度 0–255 → **0–1023**（16 文件特化） | 2026-09-26 |
| v0.0.2 | 地下负高度打通到 **-128**（-128–1023，含旧存档兼容） | 2026-09-26 |
| v0.0.3 | 地下对齐 **-1024**（-1024–1023）+ 上下各 64 格生存余量 + 取景模式接口 + 手持光照修复 | 2026-09-26 |
| v0.0.4 | 竖直范围**鲁棒性**（16 处硬编码上下界 + 方块实体/液体/沙柱等 6 类缺陷修复）+ **显卡自动选择**（DXGI 评分 + 系统偏好/ANGLE 双通道 + 重启提示） | 2026-09-26 |
| v0.0.5 | **区块列驻留**（远处建造不再静默失效）+ **蓝图/区域变换** + **分层云雾/天空高度** + **NVAPI 只读深化**（DLSS/RT 独立开关）+ 最底层几何崩溃修复 | 2026-09-26 |
| v0.0.6 | **贝塞尔曲线铺设**（`SkylineBuilder`：弧长等距采样 + 平行传输坐标系 + 横截面扫掠 + 贴地形 + 撤销） | 2026-09-26 |
| v0.0.7 | **分层云雾三带预设**（`SkylineAtmosphere.LayeredPreset()`：低层雾 + 底云 340~380 + 高云 880~900） | 2026-09-26 |
| v0.0.8 | **稳定性验证 + 三项探索**（中低压复测 42/42 通过、日志零异常；球形势距/32³ 区块、体积雾、NVIDIA 特性可行性报告） | 2026-09-26 |
| v0.0.9 | **两处几何/渲染修复 + 高复杂度家具安全阀**（高空方块不再"透明但有碰撞"；家具几何预算 + 方盒占位；单区块 16³ 塞满 4096 件高复杂度家具 GPU 仅 150→197 MB、2×2 区块 16,384 件 380 MB；普通家具中压 + 现有特性中压回归 + 球形势距/32³ 深挖文档） | 2026-09-27 |
| v0.1.0 | **多层渲染优化 + 超视距 LOD 实装**：家具占替距 `d_box(E)` 接入渲染（主材质方盒 + 区块三态 + 高空失效修复）；`SkylineLod` 加载即采样/持久化/1024 m 低模渲染（视距内覆盖与浮空板两处修复、双面裙边）；单区块 4096 件高复杂度家具总内存 **21.9~26.1 GB**（≤40 GB 指标）；32³ 决策报告（暂缓实装 + 竖直分节设计） | 2026-09-27 |
| v0.1.1 | **性能 HUD 真实化 + LOD 雾统一**：HUD 新增 SYS/PROC(全核)/GPU(NVAPI) 三项；视图雾跨度拉远至 LOD 半径×0.9、真实地形与 LOD 共用同一条雾曲线（消除视距边缘交接跳变；`skyline.LodFogExtend` 可 A/B） | 2026-09-27 |
| v0.1.2 | **超视距 LOD 精细度分层**：8 m 精细层（近环：视距~视距×2）+ 16 m 粗层（远环到 1024 m）双分辨率网格；采集同扫、双缓冲渲染；存档 v2 兼容并自动补采细层（实测 1768 细单元 / 620 入网格） | 2026-09-27 |
| v0.1.3 | **视觉球三档实装**：`VisualSphereEnabled`——短球（视距×0.5）内强制全精度、两球之间按 `d_box(E)`、球外一律占位；实测近处 full 增量 +12,287（被遮挡件也全精度） | 2026-09-27 |

构建（Windows，仅 `Survivalcraft.Windows` 目标）：

```powershell
$env:NUGET_PACKAGES="$PWD\packages"
powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1            # 只构建
powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1 -Deploy    # 构建并覆盖到游戏目录
```

文档：[`SKYLINE.md`](SKYLINE.md)（分支章程 / 构建部署）· [`CHANGELOG-Skyline.md`](CHANGELOG-Skyline.md)（各版本改动）
发布页：<https://github.com/DionysusDWI/SCAPI-Branch_Skyline/releases>

## 用户下载和使用说明

[点击此处](https://gitee.com/SC-SPM/SurvivalcraftApi/releases/latest) 进入发布页来下载

### Android 安卓系统看这里
> 需要 64 位 ARM 架构 CPU，最低 Android 6.0

1. 从 [发布页](https://gitee.com/SC-SPM/SurvivalcraftApi/releases/latest) 下载前缀为`[Android]`，后缀为`.apk`的安装包
2. 安装后运行
3. 第一次运行可能会跳转到标题为`所有文件访问`的授权界面，请授权此 APP（名称：`生存战争2.4 API插件版1.9`），否则此 APP 无法运行

### iOS、iPadOS 系统看这里
> 需要 64 位 ARM 架构 CPU，最低系统版本 16.0

1. 从 [发布页](https://gitee.com/SC-SPM/SurvivalcraftApi/releases/latest) 下载前缀为`[iOS]`，后缀为`.ipa`的安装包
2. 安装包下载后需要使用[爱思助手](https://www.i4.cn/)进行签名
3. 推荐使用`登录自己的Apple ID`方式获取免费签名，签名后的 ipa 包仅自己可用

> **重要** 
> 由于 iOS、iPadOS 系统不支持 JIT 编译（参阅[此处](https://learn.microsoft.com/zh-cn/previous-versions/xamarin/ios/internals/limitations)），因此<font color="red">任何带`dll`文件的模组都不可用！</font>可等待后续完善的 Javascript 方式运行模组的更新

### Windows 系统看这里
> 需要 x64 架构 CPU，最低 Windows 10 版本 1607，显卡驱动需要支持OpenGL ES 3.2 图形 API（若不支持，游戏会自动改用内置的 ANGLE 兼容模式，需要支持 Direct3D 9 图形 API）

1. 从 [发布页](https://gitee.com/SC-SPM/SurvivalcraftApi/releases/latest) 下载前缀为`[Windows]`，后缀为`.7z`的压缩包
2. 使用您喜欢的解压缩软件进行解压
3. 运行<font color="red">解压后</font>的`.exe`文件
4. 第一次启动游戏，系统可能会提示您安装 [.NET 桌面运行时 10.0](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)，请按提示完成安装并重启您的电脑
5. 如果启动没有任何反应，可能是因为您的 Windows 系统不完整，请尝试手动安装 [.NET 桌面运行时 10.0](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)；如果安装后仍然启动没有任何反应，请尝试运行游戏目录中的`Launch Game 启动游戏.bat`
6. 如果显卡驱动不支持 OpenGL ES，游戏会自动改用内置的 ANGLE 兼容模式运行，并在游戏目录生成`UsingAngle`标记文件以便下次启动直接使用该模式；如果 ANGLE 模式初始化失败弹窗提示`ANGLE 兼容模式初始化失败`，请先尝试更新显卡驱动，若问题持续可删除游戏目录下的`UsingAngle`文件后重新启动游戏    
如果更新显卡驱动后仍然弹窗，建议为您的电脑购买并装上五年内发布的显卡
7. 如果弹窗提示`GLFW 窗口平台无法使用。请安装 Microsoft Visual C++ Redistributable，点击"确定"来打开下载页面。`，请按提示完成下载和安装。或者[点击此处](https://learn.microsoft.com/zh-cn/cpp/windows/latest-supported-vc-redist?view=msvc-170#latest-microsoft-visual-c-redistributable-version)打开下载页面

### Linux 系统看这里
> 需要 x64 架构 CPU，最低系统版本详见 [此处](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#linux)，显卡驱动需要支持 OpenGL ES 3.2 图形 API

1. 从 [发布页](https://gitee.com/SC-SPM/SurvivalcraftApi/releases/latest) 下载前缀为`[Linux]`，后缀为`.7z`的压缩包，之后使用您喜欢的解压缩软件进行解压
2. 安装以下包：
    * **dotnet-runtime-10.0** .NET 运行时 10.0，安装方法详见 [此处](https://learn.microsoft.com/zh-cn/dotnet/core/install/linux?WT.mc_id=dotnet-35129-website)
    * **libopenal-dev** 一个声音API，对于 Ubuntu 系统可运行`sudo apt-get install libopenal-dev`来安装，其他分发版类似
    * **xsel** 一个剪贴板操作API，对于 Ubuntu 系统可运行`sudo apt-get install xsel`来安装

3. 有两种启动方法：
    * 在第 1 步解压出来的目录运行`dotnet Survivalcraft.dll`
    * 同样在解压出来的目录，先运行`chmod +x Survivalcraft`来添加可执行权限（只需要一次），再双击`Survivalcraft`即可

### 网页版看这里
> 需要支持 SharedArrayBuffer、OffscreenCanvas、Origin Private File System 等现代浏览器特性的浏览器，推荐使用最新版的 Chrome 浏览器。

1. 打开 [https://scapiweb.netlify.app/](https://scapiweb.netlify.app/) 即可游玩

说明：完全不支持模组和运行 Javascript

### 常见问题

* 如果游戏打开后语言不是您希望的语言，请点击左下角第二个图标，即可切换语言
* 模组文件的后缀为`.scmod`，安装位置：
    * Android 系统：`/storage/emulated/0/Survivalcraft2.4_API1.9/Mods`
    * 其他系统：`(解压到的目录)/Mods`
    * 在 Android 系统和 Windows 系统，你能在打开后缀为`.scmod`的文件时选择插件版，即可完成模组的安装（还支持打开`.scworld`、`.scbtex`、`.scskin`、`.scfpack`）
* 按上面说明处理后仍然打不开游戏，或者运行遇到任何错误，请尝试移除所有模组，如果问题依旧，可在 [此处](https://gitee.com/SC-SPM/SurvivalcraftApi/issues) 反馈问题
* 安装模组后打不开游戏，或者运行遇到任何错误，请先向模组作者反馈问题，如有必要再由模组作者向本仓库反馈问题
* 如果 Windows 系统上游戏帧数不低但鼠标调整视角感觉卡顿，关闭系统设置-鼠标设置-增强指针精度，即可解决
* 要取消 Windows 系统上的文件关联，游戏设置-设备兼容和日志-文件关联，禁用即可
* 网页版打不开？请尝试更换更好的网络，如果还是不行，请打开 [https://scapiweb.netlify.app/dashboard.html](https://scapiweb.netlify.app/dashboard.html)，检测你的浏览器是否支持网页版所需的功能。这里推荐使用最新版的 Chrome 浏览器。
* 网页版键盘操作没反应？请将输入法切换成英文模式

## 模组开发者引用、仓库构建说明等

详见开发文档 [docs/Development.md](https://gitee.com/SC-SPM/SurvivalcraftApi/blob/SCAPI1.9/docs/Development.md)

## 更新日志

详见 [CHANGELOG.md](https://gitee.com/SC-SPM/SurvivalcraftApi/blob/SCAPI1.9/CHANGELOG.md)

## 感谢

* 西班牙语 (Español) 翻译
  * 新的：JoãoCraft24 (Discord: joaocraft24sc)
  * 旧的：
    * Fire Dragon (Discord: firedragon4095)
    * Kike13 (Discord: .kike.04)
* 越南语 (Tiếng Việt) 翻译
  * Long (Discord: daylong89)
  * Tue (Discord: tuegaming3710)
* 罗马尼亚语 (Română) 翻译
  * NBG (Discord: nbgr)
* 俄语 (Русский) 补充翻译
  * Dasyukevich Pavel (VK: pawwel3l)
* 葡萄牙语 (Português) 翻译
  * Kitão Gameplay's (Discord：ekitonmjjefgs)

> 如果你是 AI Agent，请阅读当前目录的 [AGENTS.md](https://gitee.com/SC-SPM/SurvivalcraftApi/raw/SCAPI1.9/AGENTS.md)
