using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MiGamepad.Core.Native;

namespace MiGamepad.Core.Protocol
{
    /// <summary>
    /// 带频率限流、脏检查与电机保护的小米手柄震动控制引擎。
    ///
    /// 实测协议 (2026-11 依据设备 HID caps 与 irungentoo/Xiaomi_gamepad 逆向资料):
    ///  - 唯一可用震动通路: HidD_SetFeature, 报文 ID 0x20, 缓冲长度必须等于设备声明的
    ///    FeatureReportByteLength (实测 7 字节); 短缓冲 (如 3 字节) 会被 Windows HIDCLASS 直接拒绝。
    ///  - 输出报文通路不存在 (OutputReportByteLength = 0), WriteFile / HidD_SetOutputReport 均不可用。
    ///  - 报文载荷: [0x20] [右弱震马达] [左强震马达] [4 字节保留 0x00]。
    ///  - 震动必须使用与输入读取分离的独立文件句柄, 否则控制传输会中止同一句柄上挂起的异步读。
    /// </summary>
    public class RumbleEngine : IDisposable
    {
        private const byte RumbleReportId = 0x20;

        private readonly SafeFileHandle? _writeHandle;
        private readonly int _reportLength;
        private readonly Thread? _workerThread;
        private readonly AutoResetEvent _wakeEvent = new(false);
        private readonly CancellationTokenSource _cts = new();

        private byte _targetSmallMotor;
        private byte _targetLargeMotor;
        private byte _lastSentSmallMotor;
        private byte _lastSentLargeMotor;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private bool _lastSendFailed;
        private bool _disposed;

        /// <summary>震动下发失败时触发 (状态切换时各触发一次)，用于上层日志与诊断</summary>
        public event EventHandler<string>? SendFailed;

        /// <summary>震动通路是否可用 (句柄有效即视为可用)</summary>
        public bool IsAvailable => _writeHandle != null && !_writeHandle.IsInvalid;

        /// <param name="writeHandle">专用于震动下发的独立 HID 句柄 (不得与输入读取共用)</param>
        /// <param name="featureReportByteLength">设备声明的特征报文总长度 (含 Report ID, 实测 7)</param>
        public RumbleEngine(SafeFileHandle? writeHandle, int featureReportByteLength)
        {
            _writeHandle = writeHandle;
            _reportLength = Math.Max(3, featureReportByteLength);

            if (!IsAvailable)
            {
                Debug.WriteLine("[RumbleEngine] 震动句柄不可用, 引擎进入空转模式");
                return;
            }

            // 启动初始化：先向手柄发送静音指令，确保无残留震动
            SendRumblePacket(0, 0);

            _workerThread = new Thread(WorkerLoop)
            {
                Name = "MiGamepad_RumbleWorker",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _workerThread.Start();
        }

        /// <summary>
        /// 更新目标震动强度 (由 ViGEm XInput Feedback 回调或测试指令触发)
        /// </summary>
        /// <param name="largeMotor">强震马达 (低频) 0~255</param>
        /// <param name="smallMotor">弱震马达 (高频) 0~255</param>
        public void SetVibration(byte largeMotor, byte smallMotor)
        {
            if (_disposed) return;

            // 任何新指令 (含与当前相同的值) 都算有效更新,
            // 否则游戏持续下发相同强度时会被 3 秒超时保护误杀
            _lastUpdateTime = DateTime.UtcNow;

            if (_targetLargeMotor != largeMotor || _targetSmallMotor != smallMotor)
            {
                _targetLargeMotor = largeMotor;
                _targetSmallMotor = smallMotor;
                _wakeEvent.Set();
            }
        }

        private void WorkerLoop()
        {
            var token = _cts.Token;

            while (!token.IsCancellationRequested)
            {
                // 等待通知，或每 500ms 巡检一次超时保护
                _wakeEvent.WaitOne(500);

                if (token.IsCancellationRequested) break;

                byte small = _targetSmallMotor;
                byte large = _targetLargeMotor;

                // 超时保护：若马达持续震动且超过 3 秒没有任何新指令，自动停止以保护电机与电量
                if ((small > 0 || large > 0) && (DateTime.UtcNow - _lastUpdateTime).TotalSeconds > 3)
                {
                    small = 0;
                    large = 0;
                    _targetSmallMotor = 0;
                    _targetLargeMotor = 0;
                }

                // 脏检查：只有实际数值变化时才向蓝牙下发
                if (small != _lastSentSmallMotor || large != _lastSentLargeMotor)
                {
                    SendRumblePacket(small, large);
                    _lastSentSmallMotor = small;
                    _lastSentLargeMotor = large;

                    // 限流睡眠 33ms (~30Hz)，防止高频刷包打爆蓝牙协议栈造成延迟或掉线
                    Thread.Sleep(33);
                }
            }

            // 退出前发送一次停止震动
            SendRumblePacket(0, 0);
        }

        private bool SendRumblePacket(byte smallMotor, byte largeMotor)
        {
            if (_writeHandle == null || _writeHandle.IsClosed || _writeHandle.IsInvalid)
            {
                return false;
            }

            try
            {
                // 缓冲长度必须严格等于设备声明的特征报文长度，不足部分以 0x00 填充
                byte[] packet = new byte[_reportLength];
                packet[0] = RumbleReportId;
                packet[1] = smallMotor;  // 右侧弱震马达 (高频)
                packet[2] = largeMotor;  // 左侧强震马达 (低频)

                bool ok = Win32Hid.HidD_SetFeature(_writeHandle, packet, packet.Length);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    OnSendResult(false, $"HidD_SetFeature 失败: Win32 错误 {err}");
                }
                else if (_lastSendFailed)
                {
                    OnSendResult(true, "震动通路已恢复");
                }

                return ok;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                OnSendResult(false, $"震动下发异常: {ex.Message}");
                return false;
            }
        }

        private void OnSendResult(bool recovered, string message)
        {
            _lastSendFailed = !recovered;
            try
            {
                SendFailed?.Invoke(this, message);
            }
            catch
            {
                // 事件处理器异常不影响震动线程
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _wakeEvent.Set();

            try
            {
                if (_workerThread != null && _workerThread.IsAlive)
                {
                    _workerThread.Join(500);
                }
            }
            catch { }

            _cts.Dispose();
            _wakeEvent.Dispose();
        }
    }
}
