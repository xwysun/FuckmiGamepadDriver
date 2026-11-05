using System;
using MiGamepad.Core.Native;
using MiGamepad.Core.Protocol;
using Nefarius.Drivers.HidHide;
using Xunit;
using Xunit.Abstractions;

namespace MiGamepad.Tests
{
    public class ProtocolTests
    {
        private readonly ITestOutputHelper _output;

        public ProtocolTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TestHidHideService()
        {
            try
            {
                var service = new HidHideControlService();
                _output.WriteLine($"IsInstalled: {service.IsInstalled}");
                _output.WriteLine($"IsOperational: {service.IsOperational}");
                _output.WriteLine($"IsActive: {service.IsActive}");
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Exception: {ex.GetType().Name} - {ex.Message}");
            }
        }

        [Fact]
        public void Parse_InvalidReport_ReturnsFalse()
        {
            var report = new XiaomiInputReport();
            byte[] tooShort = new byte[10];
            Assert.False(report.Parse(tooShort));

            byte[] wrongId = new byte[21];
            wrongId[0] = 0x01;
            Assert.False(report.Parse(wrongId));
        }

        [Fact]
        public void Parse_ButtonsAndTriggers_Success()
        {
            var report = new XiaomiInputReport();
            byte[] raw = new byte[21];
            raw[0] = 0x04;
            raw[1] = (byte)(1 | 16 | 128);
            raw[2] = (byte)(4 | 64);
            raw[4] = 0;
            raw[5] = 128; raw[6] = 128; raw[7] = 128; raw[8] = 128;
            raw[11] = 100;
            raw[12] = 255;
            raw[19] = 88;
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
        [InlineData(0, true, false, false, false)]
        [InlineData(1, true, false, false, true)]
        [InlineData(2, false, false, false, true)]
        [InlineData(3, false, true, false, true)]
        [InlineData(4, false, true, false, false)]
        [InlineData(5, false, true, true, false)]
        [InlineData(6, false, false, true, false)]
        [InlineData(7, true, false, true, false)]
        [InlineData(15, false, false, false, false)]
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
            raw[4] = 15;

            raw[5] = 128; raw[6] = 128; raw[7] = 132; raw[8] = 124;

            Assert.True(report.Parse(raw));
            Assert.Equal(0, report.LeftThumbX);
            Assert.Equal(0, report.LeftThumbY);
            Assert.Equal(0, report.RightThumbX);
            Assert.Equal(0, report.RightThumbY);

            raw[5] = 255;
            raw[6] = 0;
            Assert.True(report.Parse(raw));
            Assert.Equal(32767, report.LeftThumbX);
            Assert.Equal(32767, report.LeftThumbY);

            raw[5] = 0;
            raw[6] = 255;
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
