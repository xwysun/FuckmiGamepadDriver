using System;
using System.IO;
using Microsoft.Win32.SafeHandles;
using MiGamepad.Core.Native;
using Nefarius.ViGEm.Client;
using Xunit;
using Xunit.Abstractions;

namespace MiGamepad.Tests
{
    public class GamepadConnectTest
    {
        private readonly ITestOutputHelper _output;

        public GamepadConnectTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TestOpenGamepadDevice()
        {
            var paths = Win32Hid.EnumerateXiaomiGamepads();
            if (paths.Count == 0)
            {
                // 集成测试: 需要真实手柄在线。离线时跳过而不是失败,
                // 否则 CI/无手柄环境会误报。
                _output.WriteLine("未检测到小米手柄 (离线或未配对), 跳过集成测试。");
                return;
            }

            string path = paths[0];
            _output.WriteLine($"Target device: {path}");

            using SafeFileHandle handle = Win32Hid.OpenDevice(path, exclusive: false);
            _output.WriteLine($"Handle isInvalid: {handle.IsInvalid}, isClosed: {handle.IsClosed}");
            Assert.False(handle.IsInvalid);

            // Test reading a report with timeout
            using var stream = new FileStream(handle, FileAccess.ReadWrite, 64, isAsync: true);
            byte[] buf = new byte[64];
            _output.WriteLine("Stream opened successfully!");

            // Test ViGEm connection
            using var client = new ViGEmClient();
            var x360 = client.CreateXbox360Controller();
            x360.Connect();
            _output.WriteLine("ViGEm Virtual Xbox 360 controller connected!");
            x360.Disconnect();
        }
    }
}
