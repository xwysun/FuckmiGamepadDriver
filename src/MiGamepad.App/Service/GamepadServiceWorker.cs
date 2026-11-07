using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using MiGamepad.Core;
using MiGamepad.Core.Configuration;

namespace MiGamepad.App
{
    /// <summary>
    /// Windows 服务模式的手柄管理宿主：开机即扫、断电自愈、崩溃由服务恢复策略自动拉起
    /// </summary>
    internal class GamepadServiceWorker : BackgroundService
    {
        private GamepadManager? _manager;

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ServiceLog.Write("服务启动, 初始化驱动管理器...");

            try
            {
                _manager = new GamepadManager();
                _manager.DiagnosticLog = ServiceLog.Write;

                // 蓝牙自愈删除配对后, 把“添加设备”页弹到当前登录用户的桌面
                MiGamepad.Core.BluetoothRecovery.PairingUiLauncher = () =>
                {
                    if (!InteractiveSessionLauncher.StartInActiveConsoleSession(
                            @"C:\Windows\explorer.exe", "explorer.exe ms-settings:bluetooth"))
                    {
                        ServiceLog.Write("[蓝牙自愈] 未找到活动用户会话, 请手动打开系统蓝牙设置完成配对。");
                    }
                };

                _manager.GamepadConnected += (s, pad) =>
                    ServiceLog.Write($"[+] 手柄已连接 -> 虚拟 Xbox 360 (Player {pad.PlayerIndex}), " +
                                     $"MI 键 Guide 映射: {(GamepadSettings.MapGuideButton ? "启用" : "禁用 (未检测到 Xbox Game Bar)")}");

                _manager.GamepadDisconnected += (s, pad) =>
                    ServiceLog.Write($"[-] 手柄断开/休眠 -> 释放 Player {pad.PlayerIndex}");

                _manager.Start();

                ServiceLog.Write(
                    $"后台扫描已启动 (ViGEmBus={(_manager.IsViGEmBusAvailable ? "OK" : "缺失")}, " +
                    $"HidHide={(_manager.IsHidHideAvailable ? "OK" : "缺失")})");

                // 服务以 SYSTEM 运行: 写 HKLM 全局抑制 Win11 ms-gamebar 弹窗
                if (GameBarSuppressor.EnsureSuppressed(out string gameBarSummary))
                {
                    ServiceLog.Write($"[GameBar] {gameBarSummary}");
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Write($"[X] 初始化失败: {ex.Message}");
                // 抛出让宿主停止服务, 由 sc failure 配置的自动重启策略拉起
                throw;
            }

            return Task.CompletedTask;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            ServiceLog.Write("服务停止, 释放虚拟设备...");
            _manager?.Dispose();
            return base.StopAsync(cancellationToken);
        }
    }
}
