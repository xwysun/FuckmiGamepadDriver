using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MiGamepad.Core.Native;

namespace MiGamepad.Diag
{
    /// <summary>
    /// 小米手柄通信诊断工具 v2 (时序验证):
    /// 全程后台监听物理输入流，同时按时间线发送震动特征包，
    /// 用于同时验证: 输入流是否存活 / 震动是否生效 / SetFeature 是否杀死输入流
    /// </summary>
    internal class Program
    {
        private static readonly Stopwatch _sw = Stopwatch.StartNew();
        private static int _inputTotal;

        private static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            bool auto = args.Contains("--auto");
            bool inputOnly = args.Contains("--input");

            // --hidhide: 通过 HidHide 官方 IOCTL 接口查询屏蔽配置 (需管理员)
            if (args.Contains("--hidhide"))
            {
                return RunHidHideReport();
            }

            // --guide: 查看 MI 键 Guide 映射配置与 Game Bar 检测结果
            if (args.Contains("--guide"))
            {
                Console.WriteLine("=== MI 键 Guide 映射 ===");
                Console.WriteLine($"配置文件        : {MiGamepad.Core.Configuration.GamepadSettings.SettingsFilePath}");
                Console.WriteLine($"配置值          : {MiGamepad.Core.Configuration.GamepadSettings.ConfiguredGuideMode}");
                Console.WriteLine($"检测到 Game Bar : {MiGamepad.Core.Configuration.GamepadSettings.DetectGameBarHandler()}");
                Console.WriteLine($"实际生效        : {(MiGamepad.Core.Configuration.GamepadSettings.MapGuideButton ? "映射 Guide" : "不映射")}");
                return 0;
            }

            // --list: 枚举候选设备并逐项显示打开结果与 Usage (排查误连接/多集合)
            if (args.Contains("--list"))
            {
                Console.WriteLine("=== 小米 HID 候选设备 (全部) ===");
                foreach (var candidatePath in MiGamepad.Core.Native.Win32Hid.EnumerateAllXiaomiCandidates())
                {
                    using var candidateHandle = MiGamepad.Core.Native.Win32Hid.OpenDevice(candidatePath);
                    string openState = candidateHandle.IsInvalid
                        ? $"打开失败 Win32={Marshal.GetLastWin32Error()}"
                        : "打开成功";

                    string usage = "-";
                    if (!candidateHandle.IsInvalid)
                    {
                        var candidateCaps = MiGamepad.Core.Native.Win32Hid.GetDeviceCaps(candidateHandle);
                        if (candidateCaps != null)
                        {
                            usage = $"UsagePage=0x{candidateCaps.Value.UsagePage:X2} Usage=0x{candidateCaps.Value.Usage:X2} " +
                                    $"In={candidateCaps.Value.InputReportByteLength} Out={candidateCaps.Value.OutputReportByteLength} " +
                                    $"Feat={candidateCaps.Value.FeatureReportByteLength}";
                        }
                    }

                    Console.WriteLine($"  {candidatePath}");
                    Console.WriteLine($"      {openState} | {usage}");
                }

                Console.WriteLine("\n=== 过滤后 (实际接管) ===");
                var acceptedPaths = MiGamepad.Core.Native.Win32Hid.EnumerateXiaomiGamepads();
                Console.WriteLine($"  共 {acceptedPaths.Count} 个");
                foreach (var acceptedPath in acceptedPaths)
                {
                    Console.WriteLine($"  {acceptedPath}");
                }
                return 0;
            }

            // --input 模式: 只监听按键原始报文并解码 (排查按键映射/触点问题)
            if (inputOnly)
            {
                return await RunInputCapture(20);
            }

            Console.WriteLine("==================================================================");
            Console.WriteLine("         小米手柄 HID 通信诊断工具 v2 (时序验证)                   ");
            Console.WriteLine("==================================================================");

            var paths = Win32Hid.EnumerateXiaomiGamepads();
            if (paths.Count == 0)
            {
                Console.WriteLine("\n[X] 未发现小米手柄 (VID 0x2717 / PID 0x3144)。请确认蓝牙已连接。");
                return 1;
            }

            string path = paths[0];
            Console.WriteLine($"\n[设备] {path}");

            // 关键: 输入监听与 SetFeature 必须使用独立句柄,
            // 同一句柄上挂起的异步读会被 SetFeature 中止并互相破坏
            using var readHandle = Win32Hid.OpenDevice(path);
            using var writeHandle = Win32Hid.OpenDevice(path);
            if (readHandle.IsInvalid || writeHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"  [X] 打开失败: Win32 错误 {err} ({new Win32Exception(err).Message})");
                return 1;
            }

            var attr = Win32Hid.GetDeviceAttributes(writeHandle);
            var caps = Win32Hid.GetDeviceCaps(writeHandle);
            if (caps == null)
            {
                Console.WriteLine("  [X] HidP_GetCaps 失败");
                return 1;
            }

            Console.WriteLine($"  VID=0x{attr.VendorID:X4} PID=0x{attr.ProductID:X4}");
            Console.WriteLine($"  UsagePage=0x{caps.Value.UsagePage:X2} Usage=0x{caps.Value.Usage:X2}");
            Console.WriteLine($"  Input={caps.Value.InputReportByteLength}B Output={caps.Value.OutputReportByteLength}B Feature={caps.Value.FeatureReportByteLength}B");

            int featureLen = Math.Max(3, (int)caps.Value.FeatureReportByteLength);

            // 启动全程后台输入监听 (独立句柄)
            using var inputCts = new CancellationTokenSource();
            Task inputTask = Task.Run(() => InputLoop(readHandle, inputCts.Token));

            Console.WriteLine("\n==================================================================");
            Console.WriteLine(">>> 全程请握住手柄并在提示时按动按键，感受震动 <<<<");
            Console.WriteLine("==================================================================");

            // 阶段 1: 基线输入验证
            Console.WriteLine("\n[阶段 1/3] 请立刻反复按动手柄按键 (A/B/十字键/摇杆) 持续 6 秒...");
            await Task.Delay(6000);
            int baseline = _inputTotal;
            Console.WriteLine($"  基线输入: {baseline} 个报文 {(baseline > 0 ? "✓ 输入流存活" : "✗ 无输入 (设备空闲或输入流已死)")}");

            // 阶段 2: SetFeature 震动
            Console.WriteLine("\n[阶段 2/3] 现在发送震动: HidD_SetFeature (7字节缓冲) [0x20, C8, C8]...");
            byte[] rumble = new byte[featureLen];
            rumble[0] = 0x20;
            rumble[1] = 0xC8;
            rumble[2] = 0xC8;
            bool ok = Win32Hid.HidD_SetFeature(writeHandle, rumble, rumble.Length);
            int errR = Marshal.GetLastWin32Error();
            Console.WriteLine($"  强震发送: {(ok ? "调用成功" : $"失败 Win32 {errR} ({new Win32Exception(errR).Message})")}");
            await Task.Delay(1000);

            byte[] stop = new byte[featureLen];
            stop[0] = 0x20;
            ok = Win32Hid.HidD_SetFeature(writeHandle, stop, stop.Length);
            int errS = Marshal.GetLastWin32Error();
            Console.WriteLine($"  停止发送: {(ok ? "调用成功" : $"失败 Win32 {errS} ({new Win32Exception(errS).Message})")}");
            int afterRumble = _inputTotal;
            await Task.Delay(1000);

            // 阶段 3: SetFeature 后输入流复核
            Console.WriteLine("\n[阶段 3/3] 请再次反复按动手柄按键持续 6 秒 (验证震动后输入流是否存活)...");
            await Task.Delay(6000);
            int final = _inputTotal;
            Console.WriteLine($"  震动后输入: {final - afterRumble} 个报文 {(final - afterRumble > 0 ? "✓ 输入流存活" : "✗ 无输入 (SetFeature 可能杀死了输入流，需要重新连接手柄恢复)")}");

            inputCts.Cancel();
            try { await inputTask; } catch { }

            Console.WriteLine("\n==================================================================");
            Console.WriteLine($"[汇总] 总输入报文: {_inputTotal} | 请回忆: 阶段2手柄是否震动了?");
            Console.WriteLine("==================================================================");
            return 0;
        }

        /// <summary>通过 HidHide 官方 IOCTL 接口输出屏蔽配置 (需管理员权限)</summary>
        private static int RunHidHideReport()
        {
            Console.WriteLine("=== HidHide 屏蔽状态 (官方 IOCTL 查询) ===");
            try
            {
                var service = new Nefarius.Drivers.HidHide.HidHideControlService();
                Console.WriteLine($"IsInstalled       : {service.IsInstalled}");
                Console.WriteLine($"IsOperational     : {service.IsOperational}");
                Console.WriteLine($"IsActive          : {service.IsActive}");
                Console.WriteLine($"IsAppListInverted : {service.IsAppListInverted}  (false = 白名单模式)");

                Console.WriteLine("ApplicationPaths (白名单进程):");
                foreach (var path in service.ApplicationPaths)
                {
                    Console.WriteLine($"  {path}");
                }

                Console.WriteLine("BlockedInstanceIds (被屏蔽设备):");
                foreach (var id in service.BlockedInstanceIds)
                {
                    Console.WriteLine($"  {id}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] 查询失败: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine("    请以管理员身份运行。");
                return 1;
            }

            return 0;
        }

        /// <summary>纯输入监听: 打印每个报文的按键位解码, 用于排查按键映射与触点老化</summary>
        private static async Task<int> RunInputCapture(int seconds)
        {
            var paths = Win32Hid.EnumerateXiaomiGamepads();
            if (paths.Count == 0)
            {
                Console.WriteLine("[X] 未发现小米手柄。");
                return 1;
            }

            Console.WriteLine($"[输入捕获] {seconds} 秒, 请依次轻按/重按 X 与 Y (也欢迎按其他键对比)...");
            using var handle = Win32Hid.OpenDevice(paths[0]);
            if (handle.IsInvalid)
            {
                Console.WriteLine($"[X] 打开失败: Win32 错误 {Marshal.GetLastWin32Error()}");
                return 1;
            }

            using var stream = new FileStream(handle, FileAccess.Read, 64, isAsync: true);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            byte[] buffer = new byte[64];
            var sw = Stopwatch.StartNew();

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
                    if (n <= 0) break;
                    if (n < 21 || buffer[0] != 0x04) continue;

                    byte b1 = buffer[1], b2 = buffer[2];
                    var sb = new StringBuilder();
                    if ((b1 & 0x01) != 0) sb.Append("A ");
                    if ((b1 & 0x02) != 0) sb.Append("B ");
                    if ((b1 & 0x04) != 0) sb.Append("?(b1:04) ");
                    if ((b1 & 0x08) != 0) sb.Append("X ");
                    if ((b1 & 0x10) != 0) sb.Append("Y ");
                    if ((b1 & 0x20) != 0) sb.Append("?(b1:20) ");
                    if ((b1 & 0x40) != 0) sb.Append("LB ");
                    if ((b1 & 0x80) != 0) sb.Append("RB ");
                    if ((b2 & 0x01) != 0) sb.Append("?(b2:01) ");
                    if ((b2 & 0x02) != 0) sb.Append("?(b2:02) ");
                    if ((b2 & 0x04) != 0) sb.Append("Back ");
                    if ((b2 & 0x08) != 0) sb.Append("Start ");
                    if ((b2 & 0x10) != 0) sb.Append("?(b2:10) ");
                    if ((b2 & 0x20) != 0) sb.Append("LS ");
                    if ((b2 & 0x40) != 0) sb.Append("RS ");
                    if ((b2 & 0x80) != 0) sb.Append("?(b2:80) ");
                    if (sb.Length == 0) sb.Append("-");

                    Console.WriteLine(
                        $"[{sw.Elapsed.TotalSeconds,6:F2}s] b1={b1:X2} b2={b2:X2} dpad={buffer[4]:X} " +
                        $"LX={buffer[5]:X2} LY={buffer[6]:X2} RX={buffer[7]:X2} RY={buffer[8]:X2} " +
                        $"LT={buffer[11]:X2} RT={buffer[12]:X2} bat={buffer[19]} => {sb}");
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }

            Console.WriteLine("[输入捕获] 结束。");
            return 0;
        }

        private static async Task InputLoop(SafeFileHandle handle, CancellationToken token)
        {
            try
            {
                using var stream = new FileStream(handle, FileAccess.Read, 64, isAsync: true);
                byte[] buffer = new byte[64];

                while (!token.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                    if (n <= 0) break;
                    _inputTotal++;

                    var sb = new StringBuilder(n * 3);
                    for (int i = 0; i < Math.Min(n, 21); i++) sb.Append($"{buffer[i]:X2} ");
                    Console.WriteLine($"  [{_sw.Elapsed.TotalSeconds,7:F2}s] IN len={n}: {sb}");
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
}
