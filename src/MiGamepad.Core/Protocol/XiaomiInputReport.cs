using System;

namespace MiGamepad.Core.Protocol
{
    /// <summary>
    /// 小米蓝牙手柄 21 字节原始输入报文解码器
    /// </summary>
    public class XiaomiInputReport
    {
        // 数字按键
        public bool A { get; set; }
        public bool B { get; set; }
        public bool X { get; set; }
        public bool Y { get; set; }
        public bool LeftShoulder { get; set; }
        public bool RightShoulder { get; set; }
        public bool Back { get; set; }
        public bool Start { get; set; }
        public bool LeftThumb { get; set; }
        public bool RightThumb { get; set; }
        public bool Guide { get; set; } // MI 徽标键

        // 十字键 (D-Pad)
        public bool DPadUp { get; set; }
        public bool DPadDown { get; set; }
        public bool DPadLeft { get; set; }
        public bool DPadRight { get; set; }

        // 模拟摇杆轴 (范围: -32768 ~ 32767)
        public short LeftThumbX { get; set; }
        public short LeftThumbY { get; set; }
        public short RightThumbX { get; set; }
        public short RightThumbY { get; set; }

        // 线性扳机 (范围: 0 ~ 255)
        public byte LeftTrigger { get; set; }
        public byte RightTrigger { get; set; }

        // 传感器与状态
        public byte BatteryPercent { get; set; }
        public short AccelX { get; set; }
        public short AccelY { get; set; }
        public short AccelZ { get; set; }

        // 死区参数 (0.0 ~ 1.0, 默认 0.10)
        public static float Deadzone { get; set; } = 0.10f;

        /// <summary>
        /// 解析 21 字节的原始 HID 数据包
        /// </summary>
        public bool Parse(ReadOnlySpan<byte> raw)
        {
            if (raw.Length < 21 || raw[0] != 4)
            {
                return false;
            }

            // 按键 1 (Byte 1)
            byte b1 = raw[1];
            A = (b1 & 1) != 0;
            B = (b1 & 2) != 0;
            X = (b1 & 8) != 0;
            Y = (b1 & 16) != 0;
            LeftShoulder = (b1 & 64) != 0;
            RightShoulder = (b1 & 128) != 0;

            // 按键 2 (Byte 2)
            byte b2 = raw[2];
            Back = (b2 & 4) != 0;
            Start = (b2 & 8) != 0;
            LeftThumb = (b2 & 32) != 0;
            RightThumb = (b2 & 64) != 0;

            // 十字键 Hat Switch (Byte 4)
            byte dpad = raw[4];
            DPadUp = false;
            DPadDown = false;
            DPadLeft = false;
            DPadRight = false;

            if (dpad != 15)
            {
                DPadUp = (dpad == 0 || dpad == 1 || dpad == 7);
                DPadRight = (dpad == 1 || dpad == 2 || dpad == 3);
                DPadDown = (dpad == 3 || dpad == 4 || dpad == 5);
                DPadLeft = (dpad == 5 || dpad == 6 || dpad == 7);
            }

            // 模拟摇杆 (Byte 5~8)
            LeftThumbX = MapAxis(raw[5], false);
            LeftThumbY = MapAxis(raw[6], true); // Y 轴方向在 HID 中 0=向上，需反转为 XInput 标准
            RightThumbX = MapAxis(raw[7], false);
            RightThumbY = MapAxis(raw[8], true);

            // 线性扳机 (Byte 11~12)
            LeftTrigger = raw[11];
            RightTrigger = raw[12];

            // 3 轴加速度计 (Byte 13~18)
            AccelX = BitConverter.ToInt16(raw.Slice(13, 2));
            AccelY = BitConverter.ToInt16(raw.Slice(15, 2));
            AccelZ = BitConverter.ToInt16(raw.Slice(17, 2));

            // 电量与 MI 徽标键 (Byte 19~20)
            BatteryPercent = raw[19];
            Guide = (raw[20] & 1) != 0;

            return true;
        }

        private static short MapAxis(byte rawValue, bool invert)
        {
            // rawValue 0~255, 中心约为 128
            float normalized = (rawValue - 128f) / 127f;

            if (invert)
            {
                normalized = -normalized;
            }

            // 死区处理
            if (Math.Abs(normalized) < Deadzone)
            {
                return 0;
            }

            // 平滑映射回 [-1.0, 1.0]
            float sign = Math.Sign(normalized);
            float scaled = sign * ((Math.Abs(normalized) - Deadzone) / (1.0f - Deadzone));

            int result = (int)Math.Round(scaled * 32767);
            if (result <= -32767) return -32768;
            if (result >= 32767) return 32767;

            return (short)result;
        }
    }
}
