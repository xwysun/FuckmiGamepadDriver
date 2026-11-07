using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace MiGamepad.App
{
    /// <summary>
    /// 通过 sc.exe 注册/卸载 Windows 常驻服务 (需管理员权限)。
    /// 安装流程模仿常规软件: 自动把自身部署到 %ProgramFiles%\MiGamepad\ 再从该路径注册服务,
    /// 重复执行即为升级 (停旧服务 → 覆盖文件 → 重建服务 → 启动)。
    /// </summary>
    internal static class ServiceInstaller
    {
        private const string ServiceName = "MiGamepad";
        private const string DisplayName = "MiGamepad 虚拟手柄服务";
        private const string Description =
            "小米蓝牙手柄 (MDZ-11-AA) → XInput 虚拟 Xbox 360 控制器常驻驱动服务 (ViGEm + HidHide)。开机自启, 崩溃自动重启。";

        private static string InstallDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MiGamepad");

        private static string InstalledExePath => Path.Combine(InstallDirectory, "MiGamepad.App.exe");

        private static string UninstallRegistryKeyPath => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MiGamepad";

        /// <summary>MiGamepad 服务是否已注册</summary>
        public static bool IsServiceInstalled()
        {
            try
            {
                using var controller = ServiceController.GetServices()
                    .FirstOrDefault(s => s.ServiceName.Equals(ServiceName, StringComparison.OrdinalIgnoreCase));
                return controller != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>停止常驻服务并等待其真正停止</summary>
        public static void StopService()
        {
            if (!ServiceExists())
            {
                return;
            }

            Console.WriteLine("正在停止常驻服务...");

            try
            {
                using var controller = new ServiceController(ServiceName);
                if (controller.Status != ServiceControllerStatus.Stopped)
                {
                    controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                }
            }
            catch (Exception)
            {
                // 设备/服务状态异常时回退到 sc.exe, 并给 SCM 一点时间
                Run("sc.exe", $"stop {ServiceName}", out _);
                Thread.Sleep(1500);
            }
        }

        /// <summary>查询服务是否仍注册在 SCM 中 (静默, 不打印 sc 输出)</summary>
        private static bool ServiceExists()
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                _ = controller.Status;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>等待服务从 SCM 中真正消失 (sc delete 后存在“标记为删除”过渡态)</summary>
        private static bool WaitServiceDeleted(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!ServiceExists())
                {
                    return true;
                }

                Thread.Sleep(400);
            }

            return !ServiceExists();
        }

        /// <summary>创建服务; 遇到 1072 (标记为删除) 时短暂等待并重试</summary>
        private static int CreateServiceWithRetry(string exePath)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int rc = Run("sc.exe",
                    $"create {ServiceName} binPath= \"{exePath}\" start= auto depend= HidHide/ViGEmBus DisplayName= \"{DisplayName}\"",
                    out _);

                if (rc == 0)
                {
                    return 0;
                }

                if (rc != 1072)
                {
                    return rc;
                }

                Thread.Sleep(1000);
            }

            return 1072;
        }

        public static int Install()
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine("                  MiGamepad 常驻服务安装                          ");
            Console.WriteLine("==================================================================");

            // 仅支持单文件发布产物安装: 普通构建无法靠复制一个 exe 完整部署
            if (!string.IsNullOrEmpty(typeof(ServiceInstaller).Assembly.Location))
            {
                Console.WriteLine("[X] 当前不是单文件发布版本, 无法自动安装。");
                Console.WriteLine("    请先运行 tools\\Publish-Portable.ps1 生成 dist\\MiGamepad.App.exe, 再从该 exe 执行 --install。");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine("[0/4] 检查底层驱动 (缺失项将使用内嵌的官方安装器自动静默安装)...");
            bool driversReady = DriverInstaller.EnsureInstalled();
            if (!driversReady)
            {
                Console.WriteLine("  [!] 驱动未全部就绪, 服务仍会注册 (重启电脑后重新执行 --install 可重试)。");
            }

            string sourceExe = Environment.ProcessPath!;
            string targetExe = InstalledExePath;
            bool alreadyInstalled = string.Equals(sourceExe, targetExe, StringComparison.OrdinalIgnoreCase);

            // 生成默认配置 (已存在则保留), 便于用户调整 MI 键 Guide 映射
            MiGamepad.Core.Configuration.GamepadSettings.EnsureDefaultFile();
            Console.WriteLine($"配置文件: {MiGamepad.Core.Configuration.GamepadSettings.SettingsFilePath} " +
                              $"(MI 键: {MiGamepad.Core.Configuration.GamepadSettings.ConfiguredGuideMode})");

            // 抑制 Win11 在虚拟手柄接入时自发弹出的 “需要新应用打开 ms-gamebar” 对话框
            if (MiGamepad.Core.GameBarSuppressor.EnsureSuppressed(out string gameBarSummary))
            {
                Console.WriteLine($"已抑制 ms-gamebar 弹窗 ({gameBarSummary})");
            }

            Console.WriteLine();
            if (!alreadyInstalled)
            {
                Console.WriteLine($"[1/4] 部署程序到 {InstallDirectory} ...");

                // 旧版本服务可能锁定文件, 先停掉并等待真正停止再覆盖
                StopService();

                try
                {
                    Directory.CreateDirectory(InstallDirectory);
                    File.Copy(sourceExe, targetExe, overwrite: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] 复制失败: {ex.Message}");
                    Console.WriteLine("    若文件被占用, 请先执行 --uninstall 再安装。");
                    return 1;
                }
            }
            else
            {
                Console.WriteLine("[1/4] 程序已位于安装目录, 跳过复制。");
            }

            // 注册服务: 已存在则就地重配 (升级), 不存在才新建。
            // 不走 delete+create: 服务删除是异步的, 会进入“标记为删除”过渡态,
            // 短暂窗口内 create 必然报 1072, 且该状态可能持续到重启。
            Console.WriteLine("[2/4] 注册服务...");
            int rc;
            if (ServiceExists())
            {
                Console.WriteLine("     检测到已有服务, 就地更新配置...");
                rc = Run("sc.exe",
                    $"config {ServiceName} binPath= \"{targetExe}\" start= auto depend= HidHide/ViGEmBus DisplayName= \"{DisplayName}\"",
                    out _);

                if (rc != 0)
                {
                    // 旧实例处于“标记为删除”等异常态, 只能等它消失或重启
                    Console.WriteLine($"     [!] 更新配置失败 (sc 退出码 {rc}), 尝试等待旧实例清除后重建...");
                    if (!WaitServiceDeleted(TimeSpan.FromSeconds(15)))
                    {
                        Console.WriteLine();
                        Console.WriteLine("[X] 系统中残留一个“标记为删除”的 MiGamepad 服务, Windows 会在重启后清除它。");
                        Console.WriteLine("    → 请重启电脑, 然后再次运行本安装命令即可完成。");
                        return 1072;
                    }

                    rc = CreateServiceWithRetry(targetExe);
                }
            }
            else
            {
                rc = CreateServiceWithRetry(targetExe);
            }

            if (rc != 0)
            {
                Console.WriteLine($"\n[X] 创建服务失败 (sc 退出码 {rc})。请确认以管理员身份运行。");
                return rc;
            }

            Run("sc.exe", $"description {ServiceName} \"{Description}\"", out _);

            // 崩溃自动重启: 5s / 10s / 60s 三级, 一天内计数不重置
            Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/60000", out _);

            Console.WriteLine("[3/4] 启动服务...");
            rc = Run("sc.exe", $"start {ServiceName}", out _);
            if (rc == 0)
            {
                Console.WriteLine("[4/4] 完成 ✓  服务已安装并启动, 开机将自动运行 (早于登录)。");
            }
            else
            {
                Console.WriteLine("[4/4] 服务已注册但启动失败, 可在 services.msc 中手动启动。");
            }

            WriteUninstallRegistry(targetExe);

            Console.WriteLine();
            Console.WriteLine($"安装位置 : {InstallDirectory}");
            Console.WriteLine($"运行日志 : %ProgramData%\\MiGamepad\\service.log");
            Console.WriteLine($"卸载方式 : Windows 设置 → 应用 → 安装的应用 → \"MiGamepad 虚拟手柄服务\"");
            Console.WriteLine($"           或命令行 \"{targetExe}\" --uninstall");
            Console.WriteLine("手动震动测试: sc stop MiGamepad 后直接运行本程序进入交互模式。");
            return rc;
        }

        public static int Uninstall()
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine("                  MiGamepad 常驻服务卸载                          ");
            Console.WriteLine("==================================================================");

            Run("sc.exe", $"stop {ServiceName}", out _);
            Thread.Sleep(1500);

            int rc = Run("sc.exe", $"delete {ServiceName}", out _);
            DeleteUninstallRegistry();
            if (rc == 0)
            {
                Console.WriteLine("\n服务已卸载 ✓ (ViGEmBus/HidHide 驱动不受影响, 如需清理请运行 Uninstall-Drivers.ps1)");
                if (Directory.Exists(InstallDirectory))
                {
                    Console.WriteLine($"程序文件保留在 {InstallDirectory} , 如不再使用可手动删除该目录。");
                }
            }
            else if (rc == 1072)
            {
                Console.WriteLine("\n服务已处于“标记为删除”状态, 重启后会自动清除 ✓");
            }
            else
            {
                Console.WriteLine($"\n[X] 卸载失败 (sc 退出码 {rc}), 请确认服务存在且以管理员身份运行。");
            }

            return rc;
        }

        /// <summary>
        /// 写入 Windows「应用管理」卸载入口 (设置 → 应用 → 安装的应用)
        /// </summary>
        private static void WriteUninstallRegistry(string exePath)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(UninstallRegistryKeyPath, writable: true);
                key.SetValue("DisplayName", DisplayName);
                key.SetValue("DisplayVersion", typeof(ServiceInstaller).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
                key.SetValue("Publisher", "MiGamepad Project");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("DisplayIcon", exePath);
                key.SetValue("UninstallString", $"\"{exePath}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{exePath}\" --uninstall");
                key.SetValue("EstimatedSize", (int)(new FileInfo(exePath).Length / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                Console.WriteLine("已注册系统卸载入口 (设置 → 应用 → 安装的应用) ✓");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] 注册卸载入口失败: {ex.Message}");
            }
        }

        private static void DeleteUninstallRegistry()
        {
            try
            {
                Registry.LocalMachine.DeleteSubKey(UninstallRegistryKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
                // 清理失败不影响服务卸载
            }
        }

        internal static int Run(string fileName, string arguments, out int exitCode)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                exitCode = -1;
                return exitCode;
            }

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            exitCode = process.ExitCode;

            if (!string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(output.TrimEnd());
            }

            return exitCode;
        }
    }
}
