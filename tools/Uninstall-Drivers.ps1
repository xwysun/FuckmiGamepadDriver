#Requires -RunAsAdministrator
<#
.SYNOPSIS
    一键卸载小米手柄相关的底层驱动 (ViGEmBus 与 HidHide)。
#>

$ErrorActionPreference = "SilentlyContinue"

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "         小米蓝牙手柄 Win11 依赖驱动一键卸载脚本                  " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# 1. 查找并卸载 HidHide
Write-Host "`n[1/2] 正在检测并卸载 HidHide..." -ForegroundColor Yellow
$hidhideEntries = Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object { $_.DisplayName -match "HidHide" }

if ($hidhideEntries) {
    foreach ($entry in $hidhideEntries) {
        Write-Host "  正在卸载: $($entry.DisplayName) ($($entry.PSChildName))..." -ForegroundColor Cyan
        $p = Start-Process "msiexec.exe" -ArgumentList "/x", "$($entry.PSChildName)", "/passive", "/norestart" -PassThru -Wait
        Write-Host "  [OK] HidHide 卸载完成。" -ForegroundColor Green
    }
} else {
    Write-Host "  [-] 系统中未找到 HidHide 驱动。" -ForegroundColor DarkGray
}

# 2. 查找并卸载 ViGEmBus
Write-Host "`n[2/2] 正在检测并卸载 ViGEmBus..." -ForegroundColor Yellow
$vigemEntries = Get-ItemProperty HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* |
    Where-Object { $_.DisplayName -match "ViGEm" }

if ($vigemEntries) {
    foreach ($entry in $vigemEntries) {
        Write-Host "  正在卸载: $($entry.DisplayName) ($($entry.PSChildName))..." -ForegroundColor Cyan
        $p = Start-Process "msiexec.exe" -ArgumentList "/x", "$($entry.PSChildName)", "/passive", "/norestart" -PassThru -Wait
        Write-Host "  [OK] ViGEmBus 卸载完成。" -ForegroundColor Green
    }
} else {
    Write-Host "  [-] 系统中未找到 ViGEmBus 驱动。" -ForegroundColor DarkGray
}

Write-Host "`n==================================================================" -ForegroundColor Green
Write-Host "驱动清理完毕！" -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green