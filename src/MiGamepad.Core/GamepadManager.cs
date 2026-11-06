using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MiGamepad.Core.Cloaking;
using MiGamepad.Core.Devices;
using MiGamepad.Core.Native;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;

namespace MiGamepad.Core
{
    /// <summary>
    /// 全局手柄管理器：负责多手柄发现、自动重连、XInput 槽位分配以及 HidHide 物理隔离
    /// </summary>
    public class GamepadManager : IDisposable
    {
        public bool IsViGEmBusAvailable => _vigemClient != null;
        public bool IsHidHideAvailable => _hidHideManager.IsAvailable;
        public bool IsHidHideActive => _hidHideManager.IsActive;

        public IReadOnlyCollection<XiaomiGamepad> ConnectedGamepads => _gamepads.Values.ToList();

        public event EventHandler<XiaomiGamepad>? GamepadConnected;
        public event EventHandler<XiaomiGamepad>? GamepadDisconnected;

        private ViGEmClient? _vigemClient;
        private readonly HidHideManager _hidHideManager;
        private readonly ConcurrentDictionary<string, XiaomiGamepad> _gamepads = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool[] _slots = new bool[4]; // 槽位 1~4
        private readonly object _slotLock = new();

        // 去抖与冷却: 蓝牙链路抖动时避免反复创建/销毁虚拟手柄
        private readonly Dictionary<string, DateTime> _pendingDevices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _cooldownDevices = new(StringComparer.OrdinalIgnoreCase);
        private const double StableSecondsBeforeConnect = 2.0;
        private const double ReconnectCooldownSeconds = 5.0;

        private readonly CancellationTokenSource _cts = new();
        private Task? _monitorTask;
        private bool _disposed;

        public GamepadManager()
        {
            // 1. 初始化 HidHide 屏蔽驱动
            _hidHideManager = new HidHideManager();

            // 2. 初始化 ViGEm 虚拟总线客户端
            try
            {
                _vigemClient = new ViGEmClient();
            }
            catch (VigemBusNotFoundException)
            {
                _vigemClient = null;
            }
            catch (Exception)
            {
                _vigemClient = null;
            }
        }

        /// <summary>
        /// 启动手柄后台监听扫描任务
        /// </summary>
        public void Start()
        {
            if (_monitorTask != null) return;
            _monitorTask = Task.Run(MonitorLoopAsync);
        }

        private async Task MonitorLoopAsync()
        {
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_vigemClient != null)
                    {
                        ScanDevices();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GamepadManager] 扫描设备异常: {ex.Message}");
                }

                try
                {
                    // 每 1.5 秒轮询一次手柄接入状态（支持蓝牙休眠唤醒）
                    await Task.Delay(1500, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void ScanDevices()
        {
            if (_vigemClient == null) return;

            var detectedPaths = Win32Hid.EnumerateXiaomiGamepads();
            var now = DateTime.UtcNow;

            // 清理已消失设备的去抖记录
            foreach (var stalePath in _pendingDevices.Keys.Where(p => !detectedPaths.Contains(p)).ToList())
            {
                _pendingDevices.Remove(stalePath);
            }

            // 检查新接入的设备
            foreach (var path in detectedPaths)
            {
                if (_gamepads.ContainsKey(path)) continue;

                // 断连冷却期内不重连, 避免蓝牙抖动导致的重连风暴
                if (_cooldownDevices.TryGetValue(path, out var until))
                {
                    if (now < until) continue;
                    _cooldownDevices.Remove(path);
                }

                // 去抖: 设备需连续存在约 2 秒才视为稳定接入
                if (!_pendingDevices.TryGetValue(path, out var firstSeen))
                {
                    _pendingDevices[path] = now;
                    continue;
                }

                if ((now - firstSeen).TotalSeconds < StableSecondsBeforeConnect)
                {
                    continue;
                }

                // 链路探活: 已配对但未连接的手柄虽然节点存在, 读取会报 1167,
                // 此时不创建虚拟手柄 (否则会出现“日志说已连接, 指示灯却还在闪”)
                if (!Win32Hid.IsDeviceLinkUp(path))
                {
                    // 保持等待状态, 等蓝牙链路真正建立后再接入
                    _pendingDevices[path] = now;
                    continue;
                }

                _pendingDevices.Remove(path);
                TryConnectGamepad(path);
            }
        }

        private void TryConnectGamepad(string devicePath)
        {
            if (_vigemClient == null) return;

            string? instanceId = Win32Hid.DevicePathToInstanceId(devicePath);

            // 分配空闲玩家槽位
            int slot = AllocateSlot();
            if (slot == -1)
            {
                Console.WriteLine("[GamepadManager] 提示: 4个手柄槽位已满。");
                return;
            }

            try
            {
                // 先打开设备再隐藏: 若白名单配置失败，先隐藏会把本进程也锁在门外
                var gamepad = new XiaomiGamepad(devicePath, instanceId, slot, _vigemClient);

                _gamepads[devicePath] = gamepad;

                // 自动通过 HidHide 隐藏物理设备，防止 DirectInput 双重输入
                if (!string.IsNullOrEmpty(instanceId))
                {
                    _hidHideManager.CloakDeviceInstance(instanceId);
                }

                gamepad.Disconnected += (s, e) =>
                {
                    RemoveGamepad(devicePath);
                };

                GamepadConnected?.Invoke(this, gamepad);
            }
            catch (Exception ex)
            {
                FreeSlot(slot);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[!] 初始化手柄失败 ({devicePath}): {ex.Message}");
                Console.ResetColor();
            }
        }

        private void RemoveGamepad(string devicePath)
        {
            if (_gamepads.TryRemove(devicePath, out var gamepad))
            {
                // 断开后进入冷却期, 防止链路抖动导致立刻重连
                _cooldownDevices[devicePath] = DateTime.UtcNow.AddSeconds(ReconnectCooldownSeconds);

                // 解除 HidHide 隐藏，避免遗留规则导致游戏侧再也看不到物理设备
                if (!string.IsNullOrEmpty(gamepad.InstanceId))
                {
                    _hidHideManager.UncloakDeviceInstance(gamepad.InstanceId);
                }

                FreeSlot(gamepad.PlayerIndex);
                gamepad.Dispose();
                GamepadDisconnected?.Invoke(this, gamepad);
            }
        }

        private int AllocateSlot()
        {
            lock (_slotLock)
            {
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (!_slots[i])
                    {
                        _slots[i] = true;
                        return i + 1; // Player 1..4
                    }
                }
                return -1;
            }
        }

        private void FreeSlot(int slot)
        {
            if (slot < 1 || slot > 4) return;
            lock (_slotLock)
            {
                _slots[slot - 1] = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();

            foreach (var kvp in _gamepads)
            {
                try
                {
                    if (!string.IsNullOrEmpty(kvp.Value.InstanceId))
                    {
                        _hidHideManager.UncloakDeviceInstance(kvp.Value.InstanceId);
                    }
                }
                catch { }

                try
                {
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _gamepads.Clear();

            _vigemClient?.Dispose();
            _cts.Dispose();
        }
    }
}
