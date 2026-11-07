using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using MiGamepad.Core;
using MiGamepad.Core.Configuration;
using MiGamepad.Core.Devices;

namespace MiGamepad.App
{
    internal class Program
    {
        private const string SingleInstanceMutexName = @"Global\MiGamepad.SingleInstance";
        private static Mutex? _singleInstanceMutex;

        private static GamepadManager? _manager;
        private static readonly CancellationTokenSource _cts = new();

        static async Task<int> Main(string[] args)
        {
            // 服务安装/卸载管理最先处理 (需管理员);
            // 此时不能抢占单实例互斥锁, 否则服务运行中执行升级安装会被自己拦截
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                return ServiceInstaller.Install();
            }

            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                return ServiceInstaller.Uninstall();
            }

            // 仅安装底层驱动 (不注册服务)
            if (args.Contains("--install-drivers", StringComparer.OrdinalIgnoreCase))
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine("=== MiGamepad 底层驱动安装 (ViGEmBus / HidHide, 官方安装器内嵌于 exe) ===\n");
                bool ready = DriverInstaller.EnsureInstalled();
                Console.WriteLine(ready ? "\n驱动全部就绪 ✓" : "\n存在未就绪驱动, 请查看上方输出。");
                return ready ? 0 : 1;
            }

            // MI 键映射配置: --set-guide auto|on|off
            int guideArgIndex = Array.FindIndex(args, a => a.Equals("--set-guide", StringComparison.OrdinalIgnoreCase));
            if (guideArgIndex >= 0)
            {
                Console.OutputEncoding = Encoding.UTF8;
                if (guideArgIndex + 1 >= args.Length)
                {
                    Console.WriteLine("用法: MiGamepad.App.exe --set-guide auto|on|off");
                    return 1;
                }

                string mode = args[guideArgIndex + 1].Trim().ToLowerInvariant();
                if (mode is not ("auto" or "on" or "off"))
                {
                    Console.WriteLine("无效取值, 仅支持 auto / on / off");
                    return 1;
                }

                bool written = GamepadSettings.WriteValue("MapGuideButton", mode);
                if (!written)
                {
                    Console.WriteLine("[X] 写入配置失败, 请以管理员身份运行。");
                    return 1;
                }

                Console.WriteLine($"已设置 MapGuideButton={mode}");
                Console.WriteLine($"配置文件: {GamepadSettings.SettingsFilePath}");
                Console.WriteLine($"当前检测: {(GamepadSettings.MapGuideButton ? "映射 Guide" : "不映射 Guide")}");
                Console.WriteLine("重启服务后生效: sc stop MiGamepad && sc start MiGamepad");
                return 0;
            }

            // 无参数 (双击启动): 未完成安装时自动执行安装流程, 已安装则进入交互模式
            bool argsEmpty = args.Length == 0;
            if (argsEmpty && (!DriverInstaller.QueryDriversReady() || !ServiceInstaller.IsServiceInstalled()))
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine("检测到 MiGamepad 尚未完成安装, 正在自动安装底层驱动与常驻服务...\n");
                int installCode = ServiceInstaller.Install();
                Console.WriteLine("\n按任意键关闭窗口...");
                try { Console.ReadKey(intercept: true); } catch { }
                return installCode;
            }

            // 单实例保护: 常驻服务与交互模式互斥, 避免重复创建虚拟手柄
            _singleInstanceMutex = TryAcquireSingleInstanceMutex();
            if (_singleInstanceMutex == null)
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine("MiGamepad 常驻服务正在后台运行, 手柄已由服务接管。");
                Console.WriteLine();
                Console.WriteLine("  [S] 停止服务并进入手动测试模式 (V 键测震动)");
                Console.WriteLine("  [U] 卸载服务");
                Console.WriteLine("  [其他键] 直接退出");
                Console.Write("选择: ");

                var choice = Console.ReadKey(intercept: true);
                Console.WriteLine();

                if (choice.Key == ConsoleKey.S)
                {
                    ServiceInstaller.StopService();
                    _singleInstanceMutex = TryAcquireSingleInstanceMutex();
                    if (_singleInstanceMutex == null)
                    {
                        Console.WriteLine("服务仍在运行, 请稍后重试。按任意键退出...");
                        try { Console.ReadKey(intercept: true); } catch { }
                        return 2;
                    }

                    return await RunInteractiveAsync();
                }

                if (choice.Key == ConsoleKey.U)
                {
                    ServiceInstaller.Uninstall();
                    Console.WriteLine("\n按任意键关闭窗口...");
                    try { Console.ReadKey(intercept: true); } catch { }
                    return 0;
                }

                return 0;
            }

            // 由 Windows 服务管理器拉起 -> 常驻服务模式 (无控制台)
            if (WindowsServiceHelpers.IsWindowsService())
            {
                return await RunAsServiceAsync();
            }

            // 默认: 交互式控制台 (手动测试用)
            return await RunInteractiveAsync();
        }

        /// <summary>尝试获取全局单实例互斥体; 已有实例运行时返回 null</summary>
        private static Mutex? TryAcquireSingleInstanceMutex()
        {
            // 优先 Global (跨会话, 服务与桌面互斥); 无特权环境回退 Local
            foreach (var name in new[] { SingleInstanceMutexName, @"Local\MiGamepad.SingleInstance" })
            {
                try
                {
                    var mutex = new Mutex(initiallyOwned: true, name, out bool createdNew);
                    if (createdNew)
                    {
                        return mutex;
                    }

                    mutex.Dispose();
                    return null;
                }
                catch
                {
                    // 尝试下一个命名空间
                }
            }

            return null;
        }

        /// <summary>Windows 服务模式: 托管 GamepadManager 常驻运行</summary>
        private static async Task<int> RunAsServiceAsync()
        {
            // 服务进程无控制台, 屏蔽 Core 库中的 Console 输出 (会写文件日志代替)
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(options => options.ServiceName = "MiGamepad");
            builder.Services.AddHostedService<GamepadServiceWorker>();

            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }

        /// <summary>交互式控制台模式 (原有手动测试流程)</summary>
        private static async Task<int> RunInteractiveAsync()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "小米蓝牙手柄 Win11 驱动服务 (XInput / Xbox 360 映射)";

            PrintBanner();

            // 未安装 Game Bar 的系统上, 虚拟手柄接入会触发 “需要新应用打开 ms-gamebar” 弹窗;
            // 非管理员交互运行时写 HKCU 兜底
            if (GameBarSuppressor.EnsureSuppressed(out string gameBarSummary))
            {
                Console.WriteLine($"[GameBar] {gameBarSummary}");
            }

            _manager = new GamepadManager();
            _manager.DiagnosticLog = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [蓝牙自愈] {msg}");
                Console.ResetColor();
            };

            // 蓝牙自愈删除配对后直接打开系统“添加设备”页, 用户点一下手柄即可完成重配
            BluetoothRecovery.PairingUiLauncher = () =>
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            };

            // 先挂载事件监听器，避免首次扫描时遗漏连接事件
            _manager.GamepadConnected += OnGamepadConnected;
            _manager.GamepadDisconnected += OnGamepadDisconnected;

            // 检查依赖驱动状态
            PrintDriverStatus(_manager);

            // 启动后台扫描监听
            _manager.Start();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                _cts.Cancel();
            };

            Console.WriteLine("\n[操作指令] 按 [V] 测试当前手柄震动 | 按 [C] 清屏 | 按 [Ctrl+C] 安全退出并释放手柄\n");

            // 交互式按键监听任务
            _ = Task.Run(() => HandleKeyCommands(_cts.Token));

            try
            {
                await Task.Delay(-1, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常退出
            }

            Console.WriteLine("\n正在清理虚拟设备并退出...");
            _manager.Dispose();
            Console.WriteLine("已安全退出。");
            return 0;
        }

        private static void PrintBanner()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================================");
            Console.WriteLine("          小米蓝牙手柄 Windows 11 XInput 虚拟驱动服务             ");
            Console.WriteLine("  支持双线性马达震动 / HidHide 幽灵手柄隔离 / 4手柄共存与热插拔   ");
            Console.WriteLine("==================================================================");
            Console.ResetColor();
        }

        private static void PrintDriverStatus(GamepadManager manager)
        {
            Console.WriteLine("\n[系统驱动检查]");

            // ViGEmBus 检查
            if (manager.IsViGEmBusAvailable)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ ViGEmBus 驱动: 正常 (已连接虚拟手柄总线)");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ✗ ViGEmBus 驱动: 未检测到！");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("    以管理员运行: MiGamepad.App.exe --install-drivers (官方安装器已内嵌, 免联网)。");
            }
            Console.ResetColor();

            // HidHide 检查
            if (manager.IsHidHideAvailable)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  ✓ HidHide 隔离驱动: 正常 (已启用白名单与 DirectInput 幽灵设备屏蔽)");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("  ! HidHide 驱动: 未检测到 (可选)。建议安装以防游戏出现按键双重输入。");
            }
            Console.ResetColor();

            Console.WriteLine("\n[手柄监听状态] 正在后台扫描蓝牙设备 (VID: 0x2717, PID: 0x3144)...");
            Console.WriteLine($"  MI 键 Guide 映射: {(GamepadSettings.MapGuideButton ? "启用" : "禁用")} " +
                              $"(MapGuideButton={GamepadSettings.ConfiguredGuideMode})");
        }

        private static void OnGamepadConnected(object? sender, XiaomiGamepad gamepad)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[+] 小米手柄已连接 -> 已绑定虚拟 [Xbox 360 控制器 (Player {gamepad.PlayerIndex})]");
            Console.WriteLine($"    设备路径: {gamepad.DevicePath}");
            Console.ResetColor();

            // 监听输入状态打印示例
            gamepad.StateUpdated += (s, report) =>
            {
                // 若按下 MI 键，打印提示
                if (report.Guide)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine($"[Player {gamepad.PlayerIndex}] 按下 MI 徽标键 (Xbox Guide / Game Bar)");
                    Console.ResetColor();
                }
            };

            // 震动通路异常诊断日志
            gamepad.RumbleSendFailed += (s, message) =>
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [震动诊断] [Player {gamepad.PlayerIndex}] {message}");
                Console.ResetColor();
            };
        }

        private static void OnGamepadDisconnected(object? sender, XiaomiGamepad gamepad)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n[-] 小米手柄断开连接 / 进入休眠 -> 已释放 [Player {gamepad.PlayerIndex}] 虚拟设备");
            Console.ResetColor();
        }

        private static void HandleKeyCommands(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.V)
                    {
                        TestVibration();
                    }
                    else if (key.Key == ConsoleKey.C)
                    {
                        Console.Clear();
                        PrintBanner();
                        if (_manager != null) PrintDriverStatus(_manager);
                    }
                }
                Thread.Sleep(50);
            }
        }

        private static void TestVibration()
        {
            if (_manager == null) return;

            var gamepads = _manager.ConnectedGamepads;
            if (gamepads.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [测试震动] 当前没有已连接的小米手柄。正在等待蓝牙手柄数据包...");
                Console.ResetColor();
                return;
            }

            foreach (var pad in gamepads)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [测试震动] 正在向 Player {pad.PlayerIndex} 下发震动测试 (双马达 1秒)...");
                Console.ResetColor();

                pad.TestRumble();
            }
        }
    }
}
