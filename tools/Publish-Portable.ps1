﻿<#
.SYNOPSIS
    发布便携单文件 exe 并打包为可直接拷贝分发的 zip。
#>

$ErrorActionPreference = "Stop"

$projectDir = Join-Path $PSScriptRoot "..\src\MiGamepad.App"
$distDir = Join-Path $PSScriptRoot "..\dist"
$stageDir = Join-Path $distDir "package"
$zipPath = Join-Path $distDir "MiGamepad-Portable.zip"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "              MiGamepad 便携包发布 (单文件 exe + zip)              " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

dotnet publish $projectDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $distDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[X] 发布失败。" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 清理发布目录中的多余文件, 只保留 exe
Remove-Item (Join-Path $distDir "*.pdb") -Force -ErrorAction SilentlyContinue

# 组装分发包 (exe 已内嵌 ViGEmBus/HidHide 官方安装器, 免联网)
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

Copy-Item (Join-Path $distDir "MiGamepad.App.exe") $stageDir -Force
Copy-Item (Join-Path $PSScriptRoot "Deploy-Guide.txt") $stageDir -Force
Copy-Item (Join-Path $PSScriptRoot "Uninstall-Drivers.ps1") $stageDir -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item $stageDir -Recurse -Force

$exeSize = [math]::Round((Get-Item (Join-Path $distDir "MiGamepad.App.exe")).Length / 1MB, 1)
$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "发布完成" -ForegroundColor Green
Write-Host "  单文件 exe : $distDir\MiGamepad.App.exe  ($exeSize MB)" -ForegroundColor Green
Write-Host "  分发包 zip : $zipPath  ($zipSize MB)" -ForegroundColor Green
Write-Host ""
Write-Host "新电脑部署: 解压后以管理员运行 MiGamepad.App.exe --install (或直接双击 exe)" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
