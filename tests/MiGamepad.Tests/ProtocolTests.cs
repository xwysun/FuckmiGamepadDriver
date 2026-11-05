using System;
using MiGamepad.Core.Native;
using MiGamepad.Core.Protocol;
using Xunit;

namespace MiGamepad.Tests
{
    public class ProtocolTests
    {
        [Fact]
        public void Parse_InvalidReport_ReturnsFalse()
        {
            var report = new XiaomiInputReport();
            byte[] tooShort = new byte[10];
            Assert.False(report.Parse(tooShort));

            byte[] wrongId = new byte[21];
            wrongId[0] = 0x01; // Should be 0x04
            Assert.False(report.Parse(wrongId));
        }

        [Fact]
        public void Parse_ButtonsAndTriggers_Success()
        {
            var report = new XiaomiInputReport();
            byte[] raw = new byte[21];
            raw[0] = 0x04; // Report ID

            // Set buttons: A (bit 0), Y (bit 4), RB (bit 7)
            raw[1] = (byte)(1 | 16 | 128);

            // Set buttons: Back (bit 2), RightThumb (bit 6)
            raw[2] = (byte)(4 | 64);

            // D-Pad Hat Switch: Up (0)
            raw[4] = 0;

            // Sticks: Center ~128
            raw[5] = 128;
            raw[6] = 128;
            raw[7] = 128;
            raw[8] = 128;

            // Triggers: LT=100, RT=255
            raw[11] = 100;
            raw[12] = 255;

            // Battery: 88%
            raw[19] = 88;

            // MI Logo Button (Guide): Bit 0
            raw[20] = 1;

            bool success = report.Parse(raw);

            Assert.True(success);
            Assert.True(report.A);
            Assert.False(report.B);
            Assert.False(report.X);
            Assert.True(report.Y);
            Assert.False(report.LeftShoulder);
            Assert.True(report.RightShoulder);

            Assert.True(report.Back);
            Assert.False(report.Start);
            Assert.False(report.LeftThumb);
            Assert.True(report.RightThumb);

            Assert.True(report.DPadUp);
            Assert.False(report.DPadDown);
            Assert.False(report.DPadLeft);
            Assert.False(report.DPadRight);

            Assert.Equal(100, report.LeftTrigger);
            Assert.Equal(255, report.RightTrigger);

            Assert.Equal(88, report.BatteryPercent);
            Assert.True(report.Guide);
        }

        [Theory]
        [InlineData(0, true, false, false, false)]  // Up
        [InlineData(1, true, false, false, true)]   // Up-Right
        [InlineData(2, false, false, false, true)]  // Right
        [InlineData(3, false, true, false, true)]   // Down-Right
        [InlineData(4, false, true, false, false)]  // Down
        [InlineData(5, false, true, true, false)]   // Down-Left
        [InlineData(6, false, false, true, false)]  // Left
        [InlineData(7, true, false, true, false)]   // Up-Left
        [InlineData(15, false, false, false, false)]// Released
        public void Parse_DPadHatSwitch_CorrectDirections(byte hatValue, bool up, bool down, bool left, bool right)
        {
            var report = new XiaomiInputReport();
            byte[] raw = new byte[21];
            raw[0] = 0x04;
            raw[4] = hatValue;
            raw[5] = 128; raw[6] = 128; raw[7] = 128; raw[8] = 128;

            Assert.True(report.Parse(raw));
            Assert.Equal(up, report.DPadUp);
            Assert.Equal(down, report.DPadDown);
            Assert.Equal(left, report.DPadLeft);
            Assert.Equal(right, report.DPadRight);
        }

        [Fact]
        public void Parse_StickAxes_DeadzoneAndInversion()
        {
            var report = new XiaomiInputReport();
            byte[] raw = new byte[21];
            raw[0] = 0x04;
            raw[4] = 15; // DPad neutral

            // Test Center within deadzone (128 -> should be 0)
            raw[5] = 128;
            raw[6] = 128;
            raw[7] = 132; // Small drift within deadzone
            raw[8] = 124; // Small drift within deadzone

            Assert.True(report.Parse(raw));
            Assert.Equal(0, report.LeftThumbX);
            Assert.Equal(0, report.LeftThumbY);
            Assert.Equal(0, report.RightThumbX);
            Assert.Equal(0, report.RightThumbY);

            // Test Left Thumb Max Right (255) and Max Up (0 in HID -> +32767 in XInput)
            raw[5] = 255;
            raw[6] = 0; // Pushed all the way UP
            Assert.True(report.Parse(raw));
            Assert.Equal(32767, report.LeftThumbX);
            Assert.Equal(32767, report.LeftThumbY);

            // Test Left Thumb Max Left (0) and Max Down (255 in HID -> -32768 in XInput)
            raw[5] = 0;
            raw[6] = 255; // Pushed all the way DOWN
            Assert.True(report.Parse(raw));
            Assert.Equal(-32768, report.LeftThumbX);
            Assert.Equal(-32768, report.LeftThumbY);
        }

        [Fact]
        public void DevicePathToInstanceId_Extraction_Success()
        {
            string samplePath = @"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&00022717_pid&3144#9&30687352&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
            string? instanceId = Win32Hid.DevicePathToInstanceId(samplePath);

            Assert.NotNull(instanceId);
            Assert.StartsWith("HID\\", instanceId);
            Assert.Contains("VID&00022717_PID&3144", instanceId);
        }
    }
}
