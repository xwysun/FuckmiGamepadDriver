using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using MiGamepad.Core.Native;
using MiGamepad.Core.Protocol;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace MiGamepad.Core.Devices
{
    /// <summary>
    /// 代表单个接入的小米蓝牙手柄，负责数据流读取、按键映射与虚拟 Xbox 360 控制器管理
    /// </summary>
    public class XiaomiGamepad : IDisposable
    {
        public string DevicePath { get; }
        public string? InstanceId { get; }
        public int PlayerIndex { get; set; }
        public byte BatteryPercent => _currentReport.BatteryPercent;
        public bool IsConnected => !_cts.IsCancellationRequested && !_disposed;

        public event EventHandler<XiaomiInputReport>? StateUpdated;
        public event EventHandler? Disconnected;

        private readonly ViGEmClient _vigemClient;
        private readonly SafeFileHandle _deviceHandle;
        private readonly FileStream _deviceStream;
        private readonly RumbleEngine _rumbleEngine;
        private readonly IXbox360Controller _virtualController;
        private readonly CancellationTokenSource _cts = new();
        private readonly XiaomiInputReport _currentReport = new();

        private Task? _readLoopTask;
        private bool _disposed;

        public XiaomiGamepad(string devicePath, string? instanceId, int playerIndex, ViGEmClient vigemClient)
        {
            DevicePath = devicePath;
            InstanceId = instanceId;
            PlayerIndex = playerIndex;
            _vigemClient = vigemClient;

            // 打开物理手柄 HID 句柄
            _deviceHandle = Win32Hid.OpenDevice(devicePath, exclusive: false);
            if (_deviceHandle.IsInvalid)
            {
                throw new IOException($"无法打开小米手柄设备: {devicePath}");
            }

            // 初始化异步文件流（重叠 I/O）
            _deviceStream = new FileStream(_deviceHandle, FileAccess.ReadWrite, 64, isAsync: true);

            // 初始化震动引擎
            _rumbleEngine = new RumbleEngine(_deviceHandle);

            // 创建并连接虚拟 Xbox 360 控制器
            _virtualController = _vigemClient.CreateXbox360Controller();
            _virtualController.AutoSubmitReport = false;
            _virtualController.FeedbackReceived += OnVibrationFeedback;

            try
            {
                _virtualController.Connect();
            }
            catch (Exception ex)
            {
                Dispose();
                throw new InvalidOperationException($"无法连接虚拟 Xbox 360 控制器: {ex.Message}", ex);
            }

            // 启动异步输入读取循环
            _readLoopTask = Task.Run(ReadLoopAsync);
        }

        private void OnVibrationFeedback(object sender, Xbox360FeedbackReceivedEventArgs e)
        {
            // ViGEm 反馈的马达强度为 0~255 (LargeMotor / SmallMotor)
            _rumbleEngine.SetVibration(e.LargeMotor, e.SmallMotor);
        }

        private async Task ReadLoopAsync()
        {
            byte[] buffer = new byte[64];
            var token = _cts.Token;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await _deviceStream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    if (_currentReport.Parse(buffer.AsSpan(0, bytesRead)))
                    {
                        // 映射按键至虚拟 Xbox 控制器
                        UpdateVirtualController(_currentReport);

                        // 触发事件通知 UI 或上层状态展示
                        StateUpdated?.Invoke(this, _currentReport);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception)
            {
                // 蓝牙断连、设备断电或休眠时抛出 IOException
            }
            finally
            {
                // 触发断开连接
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateVirtualController(XiaomiInputReport report)
        {
            lock (_virtualController)
            {
                // ABXY
                _virtualController.SetButtonState(Xbox360Button.A, report.A);
                _virtualController.SetButtonState(Xbox360Button.B, report.B);
                _virtualController.SetButtonState(Xbox360Button.X, report.X);
                _virtualController.SetButtonState(Xbox360Button.Y, report.Y);

                // 肩键与摇杆下压
                _virtualController.SetButtonState(Xbox360Button.LeftShoulder, report.LeftShoulder);
                _virtualController.SetButtonState(Xbox360Button.RightShoulder, report.RightShoulder);
                _virtualController.SetButtonState(Xbox360Button.LeftThumb, report.LeftThumb);
                _virtualController.SetButtonState(Xbox360Button.RightThumb, report.RightThumb);

                // 功能键
                _virtualController.SetButtonState(Xbox360Button.Back, report.Back);
                _virtualController.SetButtonState(Xbox360Button.Start, report.Start);
                _virtualController.SetButtonState(Xbox360Button.Guide, report.Guide);

                // 十字键
                _virtualController.SetButtonState(Xbox360Button.Up, report.DPadUp);
                _virtualController.SetButtonState(Xbox360Button.Down, report.DPadDown);
                _virtualController.SetButtonState(Xbox360Button.Left, report.DPadLeft);
                _virtualController.SetButtonState(Xbox360Button.Right, report.DPadRight);

                // 模拟摇杆轴
                _virtualController.SetAxisValue(Xbox360Axis.LeftThumbX, report.LeftThumbX);
                _virtualController.SetAxisValue(Xbox360Axis.LeftThumbY, report.LeftThumbY);
                _virtualController.SetAxisValue(Xbox360Axis.RightThumbX, report.RightThumbX);
                _virtualController.SetAxisValue(Xbox360Axis.RightThumbY, report.RightThumbY);

                // 线性扳机
                _virtualController.SetSliderValue(Xbox360Slider.LeftTrigger, report.LeftTrigger);
                _virtualController.SetSliderValue(Xbox360Slider.RightTrigger, report.RightTrigger);

                _virtualController.SubmitReport();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();

            try
            {
                _virtualController.FeedbackReceived -= OnVibrationFeedback;
                _virtualController.Disconnect();
            }
            catch { }

            _rumbleEngine.Dispose();

            try
            {
                _deviceStream.Dispose();
            }
            catch { }

            try
            {
                _deviceHandle.Dispose();
            }
            catch { }

            _cts.Dispose();
        }
    }
}
