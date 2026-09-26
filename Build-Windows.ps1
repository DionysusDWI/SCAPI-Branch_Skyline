<#
SCAPI Skyline Project —— Windows 基础构建 / 部署脚本

用法：
  powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1                 # 只构建
  powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1 -Deploy         # 构建并覆盖到游戏目录
  powershell -ExecutionPolicy Bypass -File .\Build-Windows.ps1 -Deploy -GameDir "D:\SCAPI\Windows-SCAPI_1.9.3.1"

说明：
  * 本分支只支持 Windows 目标；整个 SurvivalcraftApi.sln 还包含 Android/iOS/Browser 工程，
    它们需要 android / ios / wasm-tools 工作负载，与本分支的"创意建筑特化"无关，**不要**用 sln 构建。
  * 覆盖的是**游戏本体**（Survivalcraft.dll/Engine.dll/EntitySystem.dll + Content.zip），不是 mod。
    部署前建议备份原版这几个文件。
#>

param(
    [string]$Configuration = "Release",
    [switch]$Deploy,
    [string]$GameDir = "",
    [string]$NuGetPackages = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($NuGetPackages) { $env:NUGET_PACKAGES = $NuGetPackages }

Write-Host "[Skyline] build $Configuration ..." -ForegroundColor Cyan
dotnet build (Join-Path $root "Survivalcraft.Windows\Survivalcraft.Windows.csproj") -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$out = Join-Path $root "Survivalcraft.Windows\bin\$Configuration\net10.0-windows\win-x64"
Write-Host "[Skyline] output: $out" -ForegroundColor Green

if (-not $Deploy) { return }

if (-not $GameDir) { $GameDir = Join-Path $root "..\.game\v1.9.3\Windows-SCAPI_1.9.3.1" }
if (-not (Test-Path $GameDir)) { throw "game dir not found: $GameDir" }

# [Skyline v0.0.4] libEGL.dll / libGLESv2.dll 是"按 LUID 选显卡"的 ANGLE 后端，必须一起部署，
#                否则 SkylineGpu 只能退回到"写 Windows 显卡偏好 + 重启"那条兜底路径。
$files = @(
    "Survivalcraft.dll", "Engine.dll", "EntitySystem.dll", "Content.zip",
    "libEGL.dll", "libGLESv2.dll", "glfw3.dll"
)
foreach ($f in $files) {
    $src = Join-Path $out $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $GameDir $f) -Force
        Write-Host "[Skyline] deployed $f" -ForegroundColor Yellow
    } else {
        Write-Host "[Skyline] skip missing $f" -ForegroundColor DarkYellow
    }
}

Write-Host "[Skyline] done. 提示：Content.zip 必须与 dll 同一版本（本源码树的 Content 比部分随包内容新）。" -ForegroundColor Green
