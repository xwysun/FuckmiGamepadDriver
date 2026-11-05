#Requires -RunAsAdministrator
<#
.SYNOPSIS
    一键检查并下载安装小米手柄虚拟驱动所需的底层总线与隔离驱动 (ViGEmBus & HidHide)。
#>

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
$ErrorActionPreference = "Stop"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "         小米蓝牙手柄 Win11 驱动前置依赖一键安装脚本              " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

$tempDir = Join-Path $env:TEMP "MiGamepad_Drivers"
if (!(Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}

# 1. 检查 ViGEmBus
Write-Host "`n[1/2] 正在检查 ViGEmBus (虚拟 Xbox/DS4 总线驱动)..." -ForegroundColor Yellow
$vigemService = Get-Service -Name "ViGEmBus" -ErrorAction SilentlyContinue

if ($vigemService -and $vigemService.Status -eq "Running") {
    Write-Host "  [OK] ViGEmBus 驱动已安装且正在运行。" -ForegroundColor Green
} else {
    Write-Host "  [!] 未检测到运行中的 ViGEmBus，准备下载安装..." -ForegroundColor DarkYellow
    $vigemUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe"
    $vigemInstaller = Join-Path $tempDir "ViGEmBus_Setup.exe"

    if (!(Test-Path $vigemInstaller)) {
        Write-Host "  正在从 GitHub 下载 ViGEmBus 安装包..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $vigemUrl -OutFile $vigemInstaller -UseBasicParsing
    }

    Write-Host "  正在启动 ViGEmBus 安装程序..." -ForegroundColor Cyan
    $p1 = Start-Process -FilePath $vigemInstaller -ArgumentList "/passive", "/norestart" -PassThru -Wait
    if ($p1.ExitCode -eq 0) {
        Write-Host "  [OK] ViGEmBus 安装成功！" -ForegroundColor Green
    } else {
        Write-Host "  [X] ViGEmBus 安装退出码: $($p1.ExitCode)" -ForegroundColor Red
    }
}

# 2. 检查 HidHide
Write-Host "`n[2/2] 正在检查 HidHide (DirectInput 幽灵设备屏蔽驱动)..." -ForegroundColor Yellow
$hidhideService = Get-Service -Name "HidHide" -ErrorAction SilentlyContinue

if ($hidhideService) {
    Write-Host "  [OK] HidHide 驱动已安装在系统中。" -ForegroundColor Green
} else {
    Write-Host "  [!] 未检测到 HidHide，准备下载安装..." -ForegroundColor DarkYellow
    $hidhideUrl = "https://github.com/nefarius/HidHide/releases/download/v1.5.230.0/HidHide_1.5.230_x64.exe"
    $hidhideInstaller = Join-Path $tempDir "HidHide_Setup.exe"

    if (!(Test-Path $hidhideInstaller)) {
        Write-Host "  正在从 GitHub 下载 HidHide 安装包..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $hidhideUrl -OutFile $hidhideInstaller -UseBasicParsing
    }

    Write-Host "  正在启动 HidHide 安装程序..." -ForegroundColor Cyan
    $p2 = Start-Process -FilePath $hidhideInstaller -ArgumentList "/passive", "/norestart" -PassThru -Wait
    if ($p2.ExitCode -eq 0) {
        Write-Host "  [OK] HidHide 安装成功！" -ForegroundColor Green
    } else {
        Write-Host "  [X] HidHide 安装退出码: $($p2.ExitCode)" -ForegroundColor Red
    }
}

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "驱动环境配置完毕！现在可以直接运行 MiGamepad 应用程序。" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green