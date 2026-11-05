using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MiGamepad.Core.Native;

namespace MiGamepad.Core.Protocol
{
    /// <summary>
    /// 带频率限流、脏检查与电机保护的小米手柄震动控制引擎
    /// </summary>
    public class RumbleEngine : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly Thread _workerThread;
        private readonly AutoResetEvent _wakeEvent = new(false);
        private readonly CancellationTokenSource _cts = new();

        private byte _targetSmallMotor;
        private byte _targetLargeMotor;
        private byte _lastSentSmallMotor;
        private byte _lastSentLargeMotor;

        private DateTime _lastUpdateTime = DateTime.UtcNow;
        private bool _disposed;

        public RumbleEngine(SafeFileHandle handle)
        {
            _handle = handle;

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
        /// 更新目标震动强度（由 ViGEm XInput Feedback 回调触发）
        /// </summary>
        /// <param name="largeMotor">强震马达 (低频) 0~255</param>
        /// <param name="smallMotor">弱震马达 (高频) 0~255</param>
        public void SetVibration(byte largeMotor, byte smallMotor)
        {
            if (_disposed) return;

            // 比较是否有变动
            if (_targetLargeMotor != largeMotor || _targetSmallMotor != smallMotor)
            {
                _targetLargeMotor = largeMotor;
                _targetSmallMotor = smallMotor;
                _lastUpdateTime = DateTime.UtcNow;
                _wakeEvent.Set();
            }
        }

        private void WorkerLoop()
        {
            var token = _cts.Token;

            while (!token.IsCancellationRequested && !_handle.IsClosed && !_handle.IsInvalid)
            {
                // 等待通知，或者每 500ms 巡检一次
                _wakeEvent.WaitOne(500);

                if (token.IsCancellationRequested) break;

                byte small = _targetSmallMotor;
                byte large = _targetLargeMotor;

                // 超时保护：若马达持续震动超过 3 秒没有新指令更新，自动停止震动保护电机与手柄电量
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

        private void SendRumblePacket(byte smallMotor, byte largeMotor)
        {
            if (_handle.IsClosed || _handle.IsInvalid) return;

            try
            {
                byte[] packet = new byte[3]
                {
                    0x20,        // Feature Report ID: 0x20
                    smallMotor,  // 弱震强度
                    largeMotor   // 强震强度
                };

                Win32Hid.HidD_SetFeature(_handle, packet, packet.Length);
            }
            catch
            {
                // 忽略手柄拔出/断开时的通信异常
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
                if (_workerThread.IsAlive)
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
