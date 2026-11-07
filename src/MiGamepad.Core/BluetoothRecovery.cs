using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MiGamepad.Core.Configuration;
using MiGamepad.Core.Native;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace MiGamepad.Core
{
    /// <summary>
    /// 蓝牙回连卡死的自动修复器。
    /// 背景: 部分老款蓝牙适配器的旧版驱动 (如 2013 年版) 在手柄断连后
    /// 无法重建 HID 数据通道, 表现为: 手柄呼吸灯常闪、系统显示“已连接”、
    /// HID 接口节点以 ~20s 周期反复出现/消失 (闪断) 但永远没有输入报文。
    /// 该状态下唯一恢复手段是“删除配对后重新配对” (主机主动发起的配对不受影响)。
    ///
    /// 修复策略 (以“闪断特征”精准触发, 避免把手柄正常关机/休眠误判为卡死):
    ///   1. 监测 HID 接口节点出现/消失跳变: 滚动窗口内跳变次数达标即判定回连卡死;
    ///   2. 自动删除该配对 (BluetoothRemoveDevice);
    ///   3. 尝试 WinRT 无界面重配 (Just Works 手柄不弹窗); 部分故障适配器上会失败,
    ///      此时通过 PairingUiLauncher 把用户带到系统“添加设备”页, 点击一下手柄即可完成;
    ///   4. 若手柄暂不可见 (已休眠), 转入待重配看护, 待其出现后自动补配。
    /// </summary>
    public static class BluetoothRecovery
    {
        /// <summary>闪断判定滚动窗口</summary>
        public static readonly TimeSpan ChurnWindow = TimeSpan.FromSeconds(90);

        /// <summary>窗口内 HID 接口出现/消失跳变达到该次数即判定回连卡死
        /// (正常断开重连只产生 2 次跳变, 不会误触发)</summary>
        private const int ChurnTransitionsRequired = 3;

        /// <summary>两次自动修复 (删配对) 之间的冷却时间</summary>
        public static readonly TimeSpan RecoveryCooldown = TimeSpan.FromSeconds(180);

        /// <summary>待重配看护中两次配对尝试的间隔</summary>
        private static readonly TimeSpan PairRetryInterval = TimeSpan.FromSeconds(5);

        /// <summary>删除配对的最大连续次数, 超过后暂停并输出人工建议</summary>
        private const int MaxRemovalAttempts = 3;

        /// <summary>待重配看护的最大配对尝试次数</summary>
        private const int MaxPairAttempts = 5;

        private static bool? _lastHidPresent;
        private static readonly List<DateTime> _hidTransitions = new();
        private static DateTime _lastRecoveryAt = DateTime.MinValue;
        private static DateTime _lastPairAttemptAt = DateTime.MinValue;
        private static int _removalAttempts;
        private static int _pairAttempts;
        private static bool _suspensionLogged;
        private static ulong? _pendingRepairAddress;
        private static bool _pairInProgress;

        /// <summary>
        /// 上层注入: 删除配对后把用户带到系统配对界面的动作
        /// (交互模式直接打开 ms-settings:bluetooth; 服务模式需注入到用户会话启动)
        /// </summary>
        public static Action? PairingUiLauncher { get; set; }

        public static string? LastActionLog { get; private set; }

        /// <summary>修复状态是否因连续失败而挂起 (供上层给出人工建议)</summary>
        public static bool IsSuspended => _removalAttempts >= MaxRemovalAttempts;

        /// <summary>
        /// 每次扫描调用一次。hidInterfacePresent 表示当前是否枚举到手柄 HID 接口;
        /// 返回是否执行了删除配对的修复动作。
        /// </summary>
        public static bool CheckAndRepair(bool hidInterfacePresent, Action<string>? log = null)
        {
            if (!GamepadSettings.AutoRepairBluetooth)
            {
                return false;
            }

            var now = DateTime.UtcNow;

            // 记录 HID 接口出现/消失跳变 (滚动窗口)
            if (_lastHidPresent.HasValue && _lastHidPresent.Value != hidInterfacePresent)
            {
                _hidTransitions.Add(now);
            }
            _hidTransitions.RemoveAll(t => now - t > ChurnWindow);
            _lastHidPresent = hidInterfacePresent;

            if (hidInterfacePresent)
            {
                // 手柄正常可见: 清空修复计数 (修复成功或状态正常)
                if (_removalAttempts > 0 || _pendingRepairAddress != null)
                {
                    _removalAttempts = 0;
                    _suspensionLogged = false;
                    _pendingRepairAddress = null;
                    LastActionLog = null;
                }
                return false;
            }

            // 闪断判定: 滚动窗口内跳变次数达标且当前缺席
            if (_hidTransitions.Count < ChurnTransitionsRequired)
            {
                return false;
            }

            if (now - _lastRecoveryAt < RecoveryCooldown)
            {
                return false;
            }

            if (_removalAttempts >= MaxRemovalAttempts)
            {
                if (!_suspensionLogged)
                {
                    _suspensionLogged = true;
                    log?.Invoke($"已连续 {MaxRemovalAttempts} 次自动修复未见效, 暂停自动修复。" +
                                "建议: 在系统蓝牙设置中删除手柄并重新添加; " +
                                "老款蓝牙适配器请升级到其产品线的最终版蓝牙驱动 (旧版驱动可能不支持断电回连)。");
                }
                return false;
            }

            var pairedNodes = Win32Hid.EnumeratePairedXiaomiBluetoothNodes();
            if (pairedNodes.Count == 0)
            {
                // 手柄未配对 (或已手动删除): 无需修复
                return false;
            }

            var (nodeId, addressValue) = pairedNodes[0];
            ulong address = addressValue;

            _lastRecoveryAt = now;
            log?.Invoke($"检测到手柄 HID 接口 {ChurnWindow.TotalSeconds:0}s 内闪断 {_hidTransitions.Count} 次 (回连通道损坏), " +
                        $"自动删除死配对以触发重新配对: {nodeId}");

            if (!RemovePairing(address))
            {
                _removalAttempts++;
                LastActionLog = $"删除配对失败 (错误码 {Marshal.GetLastWin32Error()})";
                log?.Invoke($"[X] {LastActionLog} (可能需要管理员权限)");
                return false;
            }

            _removalAttempts++;
            _pairAttempts = 0;
            _pendingRepairAddress = address;
            LastActionLog = "已删除死配对, 正在尝试无界面重新配对...";
            log?.Invoke($"[✓] {LastActionLog} (若无法自动完成, 请在弹出的蓝牙设置页点击手柄完成配对)");

            _ = TryPairOnceAsync(address, log);

            try
            {
                PairingUiLauncher?.Invoke();
            }
            catch (Exception ex)
            {
                log?.Invoke($"[!] 打开系统配对界面失败: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// 待重配看护: 手柄被删配对后未立即出现 (休眠/关机) 时,
        /// 每次扫描调用, 待手柄可见后自动补配。
        /// </summary>
        public static bool CheckPendingRepair(bool hidInterfacePresent, Action<string>? log = null)
        {
            if (_pendingRepairAddress == null || hidInterfacePresent)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (now - _lastPairAttemptAt < PairRetryInterval || _pairInProgress)
            {
                return false;
            }

            _ = TryPairOnceAsync(_pendingRepairAddress.Value, log);
            return false;
        }

        /// <summary>重置修复状态机 (服务重启/手柄手动重配后调用)</summary>
        public static void Reset()
        {
            _lastHidPresent = null;
            _hidTransitions.Clear();
            _lastRecoveryAt = DateTime.MinValue;
            _removalAttempts = 0;
            _pairAttempts = 0;
            _suspensionLogged = false;
            _pendingRepairAddress = null;
            LastActionLog = null;
        }

        /// <summary>通过 WinRT 无界面配对 (Just Works 设备不会弹出任何确认框)</summary>
        private static async Task TryPairOnceAsync(ulong address, Action<string>? log)
        {
            if (_pairInProgress)
            {
                return;
            }

            _pairInProgress = true;
            _lastPairAttemptAt = DateTime.UtcNow;

            try
            {
                var btDevice = await BluetoothDevice.FromBluetoothAddressAsync(address).AsTask();
                if (btDevice == null)
                {
                    // 手柄当前不可见 (休眠/关机): 不计入失败, 继续看护等待
                    return;
                }

                var pairing = btDevice.DeviceInformation.Pairing;
                if (pairing.IsPaired)
                {
                    // 已被系统或其他途径配对成功
                    _pendingRepairAddress = null;
                    _pairAttempts = 0;
                    log?.Invoke("[✓] 检测到手柄已完成配对, 等待 HID 接口重建...");
                    return;
                }

                var result = await pairing.PairAsync(DevicePairingProtectionLevel.None).AsTask();
                if (result.Status == DevicePairingResultStatus.Paired ||
                    result.Status == DevicePairingResultStatus.AlreadyPaired)
                {
                    _pendingRepairAddress = null;
                    _pairAttempts = 0;
                    LastActionLog = "重新配对完成, 手柄即将重新接入";
                    log?.Invoke($"[✓] {LastActionLog}");
                    return;
                }

                _pairAttempts++;
                if (_pairAttempts >= MaxPairAttempts)
                {
                    _pendingRepairAddress = null;
                    log?.Invoke($"自动配对未成功 (状态: {result.Status})。" +
                                "请在系统蓝牙设置的“添加设备”中点击手柄完成配对。");
                }
                else
                {
                    log?.Invoke($"配对未完成 (状态: {result.Status}), 将自动重试 ({_pairAttempts}/{MaxPairAttempts})...");
                }
            }
            catch (Exception ex)
            {
                _pairAttempts++;
                if (_pairAttempts >= MaxPairAttempts)
                {
                    _pendingRepairAddress = null;
                    log?.Invoke($"自动配对异常 ({ex.Message}), 已停止重试。请在系统蓝牙设置中手动添加设备。");
                }
                else
                {
                    log?.Invoke($"配对尝试异常: {ex.Message} (将自动重试 {_pairAttempts}/{MaxPairAttempts})...");
                }
            }
            finally
            {
                _pairInProgress = false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLUETOOTH_ADDRESS
        {
            public ulong ullLong;
        }

        [DllImport("BluetoothApis.dll", SetLastError = true)]
        private static extern bool BluetoothRemoveDevice(ref BLUETOOTH_ADDRESS pAddress);

        /// <summary>
        /// 删除与指定蓝牙地址的配对 (等价于设置中的“删除设备”)。
        /// 实测 Windows 11 上该 API 可能返回 false 但配对实际已删除 (错误码 0),
        /// 因此以重新枚举配对节点的结果为准。
        /// </summary>
        private static bool RemovePairing(ulong address)
        {
            var btAddress = new BLUETOOTH_ADDRESS { ullLong = address };
            bool apiResult = BluetoothRemoveDevice(ref btAddress);
            if (apiResult)
            {
                return true;
            }

            bool stillPaired = Win32Hid.EnumeratePairedXiaomiBluetoothNodes()
                .Exists(n => n.Address == address);
            return !stillPaired;
        }
    }
}
