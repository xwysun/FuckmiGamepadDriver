using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;

namespace MiGamepad.App
{
    /// <summary>
    /// 内嵌官方驱动安装器 (ViGEmBus / HidHide)。
    /// 安装时检测系统缺失情况, 从 exe 内释放官方安装器并静默执行, 装完即生效, 全程无需联网。
    /// </summary>
    internal static class DriverInstaller
    {
        private sealed record DriverPackage(string ResourceName, string FileName, string ServiceName, string Title);

        private static readonly DriverPackage[] Packages =
        {
            new(
                "MiGamepad.App.Drivers.ViGEmBus_1.22.0_x64_x86_arm64.exe",
                "ViGEmBus_1.22.0_x64_x86_arm64.exe",
                "ViGEmBus",
                "ViGEmBus 虚拟手柄总线驱动"),
            new(
                "MiGamepad.App.Drivers.HidHide_1.5.230_x64.exe",
                "HidHide_1.5.230_x64.exe",
                "HidHide",
                "HidHide 设备隐藏过滤驱动"),
        };

        /// <summary>仅查询驱动就绪状态: 已安装且正在运行 (不安装/不修复)</summary>
        public static bool QueryDriversReady() => Packages.All(p => IsRunning(p.ServiceName));

        /// <summary>
        /// 逐一检查驱动: 缺失则静默安装; 已安装但被禁用/未运行则修复; 返回是否全部在运行。
        /// 内核驱动被设为 DISABLED 时, 依赖它的 MiGamepad 服务启动会报 1068 (依赖服务无法启动),
        /// 因此这里必须检查并修复运行状态, 而不只是检查是否安装。
        /// </summary>
        public static bool EnsureInstalled()
        {
            foreach (var package in Packages)
            {
                bool installed = IsServicePresent(package.ServiceName);
                Console.WriteLine($"  {(installed ? "✓" : "!")} {package.Title}: {(installed ? "已安装" : "未安装, 正在静默安装 (官方安装器内嵌于 exe)...")}");

                if (!installed)
                {
                    if (TryInstallPackage(package, out int exitCode))
                    {
                        Console.WriteLine($"    ✓ {package.Title} 安装完成" + (exitCode == 3010 ? " (安装器提示需重启)" : ""));
                    }
                    else
                    {
                        Console.WriteLine($"    [X] {package.Title} 安装失败 (退出码 {exitCode}), 可手动运行包内 Install-Drivers.ps1 重试");
                        continue;
                    }
                }

                EnsureRunning(package);
            }

            return Packages.All(p => IsRunning(p.ServiceName));
        }

        /// <summary>驱动已安装但被禁用或未运行时自动修复 (改启动类型 + 立即启动)</summary>
        private static void EnsureRunning(DriverPackage package)
        {
            try
            {
                using var controller = FindController(package.ServiceName);
                if (controller == null)
                {
                    Console.WriteLine($"    [!] 找到服务记录但无法打开 {package.ServiceName}, 可能需要重启后重试");
                    return;
                }

                if (controller.StartType == ServiceStartMode.Disabled)
                {
                    Console.WriteLine($"    ! {package.Title} 启动类型为“禁用”(会导致 MiGamepad 报 1068), 正在改为系统启动...");
                    ServiceInstaller.Run("sc.exe", $"config {package.ServiceName} start= system", out _);
                }

                if (controller.Status == ServiceControllerStatus.Running)
                {
                    Console.WriteLine($"    ✓ {package.Title} 运行中");
                    return;
                }

                Console.WriteLine($"    ! {package.Title} 未运行 (当前: {controller.Status}), 正在启动...");
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                Console.WriteLine($"    ✓ {package.Title} 已启动");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [!] 启动 {package.Title} 失败: {ex.Message}");
                Console.WriteLine("        驱动刚安装时常见于未重启的系统, 重启后将自动生效。");
            }
        }

        private static ServiceController? FindController(string serviceName)
        {
            try
            {
                // 内核驱动 (ViGEmBus/HidHide) 只出现在 GetDevices() 中,
                // GetServices() 仅枚举 Win32 服务, 两者都查以免误判
                return ServiceController.GetDevices()
                           .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase))
                       ?? ServiceController.GetServices()
                           .FirstOrDefault(s => s.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        private static bool IsServicePresent(string serviceName)
        {
            using var controller = FindController(serviceName);
            return controller != null;
        }

        private static bool IsRunning(string serviceName)
        {
            try
            {
                using var controller = FindController(serviceName);
                return controller?.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInstallPackage(DriverPackage package, out int exitCode)
        {
            exitCode = -1;

            try
            {
                // 释放内嵌安装器到临时目录
                string extractDir = Path.Combine(Path.GetTempPath(), "MiGamepad", "drivers");
                Directory.CreateDirectory(extractDir);
                string installerPath = Path.Combine(extractDir, package.FileName);

                using (var resource = typeof(DriverInstaller).Assembly.GetManifestResourceStream(package.ResourceName))
                {
                    if (resource == null)
                    {
                        Console.WriteLine($"    [X] 内嵌资源缺失: {package.ResourceName}");
                        return false;
                    }

                    using var file = File.Create(installerPath);
                    resource.CopyTo(file);
                }

                // 与官方脚本一致的静默参数; 3010 = 成功但需重启
                var psi = new ProcessStartInfo(installerPath, "/passive /norestart")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return false;
                }

                process.WaitForExit();
                exitCode = process.ExitCode;
                return exitCode is 0 or 3010;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    [X] 安装异常: {ex.Message}");
                return false;
            }
        }
    }
}
