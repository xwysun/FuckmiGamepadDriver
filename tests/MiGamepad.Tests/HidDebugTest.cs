using System;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;

namespace MiGamepad.Tests
{
    public class HidDebugTest
    {
        private readonly ITestOutputHelper _output;

        public HidDebugTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }

        [Fact]
        public void DebugHidDevices()
        {
            HidD_GetHidGuid(out Guid hidGuid);
            _output.WriteLine($"HID GUID: {hidGuid}");

            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            _output.WriteLine($"deviceInfoSet: {deviceInfoSet}");

            var ifData = new SP_DEVICE_INTERFACE_DATA();
            ifData.cbSize = Marshal.SizeOf(ifData);
            _output.WriteLine($"ifData.cbSize: {ifData.cbSize}");

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index++, ref ifData))
            {
                SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, IntPtr.Zero, 0, out int reqSize, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(reqSize);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    bool ok = SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, buf, reqSize, out _, IntPtr.Zero);
                    int err = Marshal.GetLastWin32Error();
                    string path = Marshal.PtrToStringAuto(IntPtr.Add(buf, 4)) ?? "";
                    if (path.Contains("2717") || path.Contains("3144") || path.Contains("bth") || path.Contains("hid"))
                    {
                        _output.WriteLine($"Device #{index}: ok={ok}, err={err}, path={path}");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }

            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }
}
