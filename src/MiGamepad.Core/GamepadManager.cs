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
                Debug.WriteLine("[GamepadManager] 未检测到 ViGEmBus 驱动，请先安装驱动！");
                _vigemClient = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GamepadManager] 初始化 ViGEmClient 失败: {ex.Message}");
                _vigemClient = null;
            }

            // 3. 启动设备监听后台巡检任务
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

            // 检查新接入的设备
            foreach (var path in detectedPaths)
            {
                if (!_gamepads.ContainsKey(path))
                {
                    TryConnectGamepad(path);
                }
            }
        }

        private void TryConnectGamepad(string devicePath)
        {
            if (_vigemClient == null) return;

            string? instanceId = Win32Hid.DevicePathToInstanceId(devicePath);

            // 自动通过 HidHide 隐藏物理设备，防止 DirectInput 双重输入
            if (!string.IsNullOrEmpty(instanceId))
            {
                _hidHideManager.CloakDeviceInstance(instanceId);
            }

            // 分配空闲玩家槽位
            int slot = AllocateSlot();
            if (slot == -1)
            {
                Debug.WriteLine("[GamepadManager] 槽位已满 (最多支持 4 个手柄同时连接)");
                return;
            }

            try
            {
                var gamepad = new XiaomiGamepad(devicePath, instanceId, slot, _vigemClient);
                gamepad.Disconnected += (s, e) =>
                {
                    RemoveGamepad(devicePath);
                };

                _gamepads[devicePath] = gamepad;
                GamepadConnected?.Invoke(this, gamepad);

                Debug.WriteLine($"[GamepadManager] 成功连接小米手柄 [Player {slot}]: {devicePath}");
            }
            catch (Exception ex)
            {
                FreeSlot(slot);
                Debug.WriteLine($"[GamepadManager] 初始化手柄失败 {devicePath}: {ex.Message}");
            }
        }

        private void RemoveGamepad(string devicePath)
        {
            if (_gamepads.TryRemove(devicePath, out var gamepad))
            {
                FreeSlot(gamepad.PlayerIndex);
                gamepad.Dispose();
                GamepadDisconnected?.Invoke(this, gamepad);
                Debug.WriteLine($"[GamepadManager] 手柄断开 [Player {gamepad.PlayerIndex}]: {devicePath}");
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
