using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiGamepad.Core;
using MiGamepad.Core.Devices;
using MiGamepad.Core.Protocol;

namespace MiGamepad.App
{
    internal class Program
    {
        private static GamepadManager? _manager;
        private static readonly CancellationTokenSource _cts = new();

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Title = "小米蓝牙手柄 Win11 驱动服务 (XInput / Xbox 360 映射)";

            PrintBanner();

            _manager = new GamepadManager();

            // 检查依赖驱动状态
            PrintDriverStatus(_manager);

            _manager.GamepadConnected += OnGamepadConnected;
            _manager.GamepadDisconnected += OnGamepadDisconnected;

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                _cts.Cancel();
            };

            Console.WriteLine("\n[提示] 按 [V] 测试 1 号手柄震动 | 按 [C] 清屏 | 按 [Ctrl+C] 安全退出并释放手柄\n");

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
                Console.WriteLine("    请在 tools 目录下运行 powershell -File .\\tools\\Install-Drivers.ps1 安装。");
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
                Console.WriteLine("[测试震动] 当前没有已连接的小米手柄。");
                Console.ResetColor();
                return;
            }

            foreach (var pad in gamepads)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[测试震动] 触发 Player {pad.PlayerIndex} 震动 1 秒 (强弱双马达)...");
                Console.ResetColor();

                // 找到对应手柄的私有句柄触发震动测试
                var rumbleEngineField = typeof(XiaomiGamepad).GetField("_rumbleEngine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (rumbleEngineField?.GetValue(pad) is RumbleEngine engine)
                {
                    Task.Run(() =>
                    {
                        engine.SetVibration(200, 200);
                        Thread.Sleep(1000);
                        engine.SetVibration(0, 0);
                    });
                }
            }
        }
    }
}
