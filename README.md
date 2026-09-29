# SurvivalCraft API 生存战争插件版

## 介绍

生存战争插件版是基于 Candy Rufus Game 开发的 [生存战争 Survivalcraft](https://kaalus.wordpress.com/) 二次开发的支持加载模组的版本


## SCAPI Skyline Project

> 基于**最新 SCAPI 游戏源码**的"建筑特化"分支：更高的世界 + 建筑辅助能力 + 超视距渲染，
> 面向超大规模创意建筑与 **AI Agent 辅助建造**。

**当前状态：v0.1.153**。**逐版变更与历史**见 [CHANGELOG-Skyline.md](CHANGELOG-Skyline.md)，
发布页见 <https://github.com/DionysusDWI/SCAPI-Branch_Skyline/releases>。

### 能力清单（只列当前状态）

**世界与建造**

* 世界竖直范围 **-1024 .. 1023**（上游 0..255）：存储/几何/光照/碰撞/存活/存档全链路对齐，
  旧存档透明兼容，上下各留 64 格余量。
* **[v0.1.128] 基岩下移 −1024（里程碑 5.1）**：生成器的基岩、密度网格、列扫描、材料阈值、矿脉、
  穴块、**地下湖**、洞穴、树、坟的高度全按世界全高换算（`SkylineTerrainBaseline`），支持
  **低基线自然生成**；新世界基岩 **7/7**、地表材质 **11/11**（修掉负 y 下的**裸岩**）。
* **[v0.1.137] 区块预加载（5.2 第一段，Chunky 式）**：`skyline.ChunkPreloadStart(x,z,r)` 用**虚拟加载点**
  把目标区域交给引擎既有加载线程（只做排队/进度·ETA/帧开销/可取消）；实测 r=6（**169 区块**）4.1 s
  全部到 `Valid`、目标列真地形 y=64、可取消。
* **[v0.1.138] 5.2 第二段前置（让"并行哪一段"有依据）**：逐 pass 耗时账本
  （`skyline.TerrainUpdateStats()`，**light 5705 ms 是绝对大头**）、区块内容指纹
  `ChunkContentHash` / `ChunkForceRegenerate`，以及**确定性验收**（预加载→释放→引擎确认卸载 3/3→
  再预加载后哈希**逐位相同**）；顺带修掉预加载点的球窗判据（改 2D 重载后 81/81 `Valid`）。
* **[v0.1.139/142] 第二段：并行白名单** —— 只有 `InvalidLight` 可并行；`lightSources/propagate` 必须串行；
  新区域生成时 **contents 占 ≈50%** 只写本区块 ⇒ 下一段目标。**[v0.1.150/152]** 补"光照位"比对与
  门禁 `shell-*` 的**壳仓锚点**；**[v0.1.153]** 另加**地形形状指纹** `skyline.ChunkShapeHash`
  （不含随日照变化的 `SunlightHeight`），用于跨轮比对邻块是否被污染。
* **[v0.1.144~151] 失败路径验收 + 按审计裁定修复**：取消/异常/坏档都不掉 tick；**上报**"非 IO 异常后
  引擎 `return true` ⇒ 坏档被当成已加载"（实测抹掉地形 281,844→**1** 格）；**v0.1.149 起修法 A 默认生效**
  （实测 **278,462 → 278,464**）；**v0.1.151** 起写回判定改**区块级**计数。
* 建筑工具：区域复制 / 镜像 / 旋转、蓝图导入导出、笔画式构建；
  `SkylineBuilder` 剖面扫掠——**三次贝塞尔曲线**（弧长等距）。
  **[v0.1.140] 曲线几何误差已量化**：`skyline.BezierSample(spec)` 与扫掠同源；步距 4 m 时间距偏差
  **0.01%**、到理想贝塞尔最大 **5.42 cm**。
* 大规模建筑压力测试：单区块 4096 件高复杂度家具；家具**逐级降分辨率 LOD**（`d_box(E)` 占替距）、
  几何预算与方盒占位、高复杂度家具安全阀。**[v0.1.125] 标定完成（2.2）**：出厂分界 L1=47.4 m
  掩膜内 **0 px**。
* **[v0.1.123] 外部高度场驱动地形**：`TerrainContentsGeneratorHeightmap`（默认不生效）证明管线能吃外部
  高度图（27/27 点 `|Δ| ≤ 1`）。

**超视距 LOD（里程碑 2；当前参考 Distant Horizons）**

* 加载距离之外的远景统一 **32³** 采样并持久化，1024 m 低模渲染 + 双面裙边；
* 与真地形**共用同一条雾曲线**；`ShellHoleFill` 让"地形已卸载但壳还在"的近处不留洞；
* **按 DH 规格的两项观感**（`SkylineLodLook`，默认全开）：**抖动淡出**（用 Iris 的 IGN，GLES 不收 const 数组）
  与**噪声补细节**（DH 默认 `steps=4 / intensity=5 / dropoff=1024`）；
* 分级边界按**屏幕像素**核算（`CubeShellTierPixelTable`），开关 `CubeShellPixelTiers`（默认关）。
* **[v0.1.127] LOD 分级以 DH 源码经验为主**：档位由 `level = floor(log_base(d / (unit×16)))` 给出
  （预设默认 **MEDIUM**：边界 **192/384/768 m**），带内三级 = **0.5/1/2 m 体素**（旧阶梯在 384 m
  就用 4 m，正是"分级过于明显"的根因）。另有**单位兼容层** `skyline.CubeShellDhCompat()`：
  MC 16× 区块/视距按区块/平面窗，本分支按方块/球形窗 —— 逐条写明，禁止公式互相套用。
  **[v0.1.128] 视距换算收口（2.4）**：MC 的 16× 区块半径 ↔ 我们的方块视距，规则 5 → **7 条**；
  可证伪对照：误把 32 当区块宽 ⇒ L0 由 192 m 变 384 m。验收 **10/10 PASS**。
* **[v0.1.129] 里程碑 3.2 收口**：脏队列曾被"永远取不到"的单元占住；现在取不到即销账 + 卸载预扫补采
  ⇒ 静止与走 1.44 km 后 `dirty` 都回 **0**。
* **[v0.1.130/134] 里程碑 2.6 合并阶梯**：按 DH 公式分 **32 / 64 / 128 m** 三档（边界 384 / 768 m，合并
  2³/4³/8³ 个 16 m 单元）；半径 2048 m 时外环用 14 个 128 m 块（等精度 32 m 要 224 个，**省 16×**）；默认**关**。
* **[v0.1.141] 距离契约（B-03）**：只读探针 `skyline.LodDistanceContract()` 并排"中心 vs 最短距离"
  —— 换口径只把边界外推半个块，120 步里仅 **6 行**改档。
  新增两条只读探针：`LodBlockProvenance(x,z)` 列出合并块内全部源 16 m 格（LOD 层是 2.5D 高度场）、
  `ShellVoxelProbe(cx,cy,cz)` 回读壳立方体的占用/材质（**壳层是 16³、格距 1 m**，非 32³/0.5 m）；
  **编辑现在会失效化壳层**（旧快照不再冒充新地形，`ShellInvalidateOnEdit` 默认开）。
* DH 的 `overdrawPrevention` 开关 `SkylineLod.OverdrawPrevention`（默认 **0**）：
  代价 −0.83%，但重叠带会盖掉真地形细节 ⇒ 默认关。

**光影（里程碑 4；主要参考 Iris 光影包，Dawnlight 作接口参考）**

* **太阳阴影（默认开）**：软阴影 PCF，核为**双同心环 16 抽样**（`GpuShadowKernelSelfCheck()` 可量化）；
  太阳追踪 + 远/近两级 GPU 阴影图，强度**跟随昼光**；`GpuShadowSampleEnabled=false` 可整条关掉；
  **[v0.1.122] Iris「镜像螺旋核」**（`GpuShadowKernel=3`）直边严格无偏但各向异性更差 ⇒ 默认 ring16。
* **远处阴影距离淡出**（Iris 的 `smoothstep(far*0.4, far*0.9, dist)`），
  消掉阴影图边界硬切（`GpuShadowFadeScale=0` 可回退）；
* **体积云 / 体积雾 / 体积神光**：雾里逐步做太阳遮挡 × 前向散射，**同时覆盖远景 LOD 层**；
  **[v0.1.119] 神光跟随昼光**；**[v0.1.118] 体积雾基础密度** `FogBaseDensity=0.10`（0.19% → **71%**）；
* **固定光源**：复用引擎扫描建光源列表、逐帧取 K 近邻做距离衰减（`skyline.PointLights(true)`，默认关、零代价）；
* **相机空间深度预通道**（`SkylineScreenDepth`）：半分辨率、16 bit 线性视距、只画 112 m 内真地形（默认关）；
* **屏幕空间体积光**（Iris `volumetricLight.glsl` 迁移）：全屏**加法** pass、12 步 + IGN 抖动、每步查阴影图，
  **天空片元也走满 256 m**；默认关、强度 **0.22**、代价 **0.138 ms**（实测）。
* **屏幕空间 AO**（`SkylineScreenAo`）：horizon-based、法线由深度重建、8 方向 × 3 步、旋转用**屏幕像素 IGN**
  （v0.1.116 修掉同心环）+ 24~56 m 淡出；
  实测默认档只影响 **0.95%** 帧面积，多机位 0.83%~9.05% 全 ≤10%，代价在噪声内；
  `skyline.ScreenAoEnabled=true` 一条打开（默认关）。
* LOD 参与云层阴影（烘进顶点色）与固定光源亮斑（顶面取**上方空气格**的光）。
* **LOD 体素参与光影**（2.3）：太阳阴影 = 实时深度图（`LodShadowReceive`，默认关）；云影与光源亮斑 = **烘焙**。
* **关雾（为了看清光影本身）**：`FogDisabled`（默认开）覆盖**全部**吃雾参数的 pass（地形三 pass /
  LOD 两层 / 体素壳 / 模型与粒子 / 移动方块 / 挖掘裂纹 / 选中框 / 天空地平线 / 掉落物褪色），
  加自研体积雾、神光、远景 LOD 雾、彩光雾 —— **一条 `FogAll(true)` 全关**（`false` 还原），
  `FogStatus()` 给逐 pass 账本。
* **[v0.1.131/133] 原生气氛可移除（2.5）+ 审计整改**：`AmbienceNeutralSky` 换**中性昼光渐变**、
  `AmbiencePrecipitation=false` 移除雨雪柱（差 **330,286 px**）；v0.1.133 按独立审计整改：聚合吃
  **全部样本**（排列不变性自检常驻门禁）、区域仓 **v2**（`LightAir`/第二层表面不再丢，v1 可读）、
  卸载预扫三态、覆盖判据改**空间面积**。

**参考环境（工作区内）**

* **Dawnlight v3.1**（SCAPI 1.9.2.1）已安装可运行，着色器实现已抽取对照；
* **Minecraft 1.21.11 + Fabric**：Distant Horizons / Iris / Sodium / Axiom / terrain-diffusion 与环境已就绪，
  另有两个成品光影包（Complementary Reimagined、Bliss）供实现对照。

### 构建 / 部署 / 运行

```powershell
$env:NUGET_PACKAGES="$PWD\packages"
dotnet build .\SCAPI-Branch_Skyline-Project\Survivalcraft.Windows\Survivalcraft.Windows.csproj -c Release
pwsh -NoProfile -File heightlab\deploy-skyline-v004.ps1 -KeepState
powershell -ExecutionPolicy Bypass -File scripts\run-game.ps1 -Port 8765
```

* 控制与观测走 **AgentBridge**（裸 TCP 行 JSON）：`python tools\scbridge.py state | act ... | shot`；
* 回归门禁：`python heightlab\regression-skyline.py`；全量巡检：`python heightlab\acceptance-sweep.py`
  （**[v0.1.126] 代价按毫秒/帧判**：百分比会被基线帧率放大，七次实测 −7.91%~+3.12%；
  预算 **1.0 ms = 60 fps 的 6%**。收口 **PASS 25 / FAIL 0 / SKIP 4**）。

### 文档与证据

* `notes/`：编号递增的实现笔记（每条都带实测数据与踩坑记录）；`docs/`：AgentBridge 协议与游戏机制；
* `data/sessions/`：每次实验的原始证据（状态 JSONL + 截图）；`heightlab/release-v*/`：发版说明与补丁；
* 分支章程 `SKYLINE.md`；逐版变更 `CHANGELOG-Skyline.md`。

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

### 常见问题

* 如果游戏打开后语言不是您希望的语言，请点击左下角第二个图标，即可切换语言
* 模组文件的后缀为`.scmod`，安装位置：
    * Android 系统：`/storage/emulated/0/Survivalcraft2.4_API1.9/Mods`
    * 其他系统：`(解压到的目录)/Mods`
    * 在 Android 系统和 Windows 系统，你能在打开后缀为`.scmod`的文件时选择插件版，即可完成模组的安装（还支持打开`.scworld`、`.scbtex`、`.scskin`、`.scfpack`）
* 按上面说明处理后仍然打不开游戏，或者运行遇到任何错误，请尝试移除所有模组，如果问题依旧，可在 [此处](https://gitee.com/SC-SPM/SurvivalcraftApi/issues) 反馈问题
* 安装模组后打不开游戏，或者运行遇到任何错误，请先向模组作者反馈问题，如有必要再由模组作者向本仓库反馈问题
* 如果 Windows 系统上游戏帧数不低但鼠标调整视角感觉卡顿，关闭系统设置-鼠标设置-增强指针精度，即可解决

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
