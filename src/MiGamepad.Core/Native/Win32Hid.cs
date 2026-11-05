using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MiGamepad.Core.Native
{
    public static class Win32Hid
    {
        public const int HID_VID_XIAOMI = 0x2717;
        public const int HID_PID_XIAOMI = 0x3144;

        #region Win32 API Constants

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        private const uint DIGCF_PRESENT = 0x00000002;
        private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        #endregion

        #region Win32 Structs

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid interfaceClassGuid;
            public int flags;
            public IntPtr reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string devicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        #endregion

        #region P/Invoke Imports

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        #endregion

        /// <summary>
        /// 枚举系统中所有已连接的小米蓝牙手柄设备路径
        /// </summary>
        public static List<string> EnumerateXiaomiGamepads()
        {
            var devicePaths = new List<string>();

            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == (IntPtr)(-1))
            {
                return devicePaths;
            }

            try
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf(interfaceData);

                uint index = 0;
                while (SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index++, ref interfaceData))
                {
                    SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, IntPtr.Zero);

                    if (requiredSize <= 0)
                        continue;

                    IntPtr detailDataBuffer = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        // 在 64 位系统上，cbSize 结构体头部根据平台指针宽度为 8 (x64) 或 5 (x86)
                        Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);

                        if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                        {
                            IntPtr pDevicePath = IntPtr.Add(detailDataBuffer, 4);
                            string? devicePath = Marshal.PtrToStringAuto(pDevicePath);

                            if (!string.IsNullOrEmpty(devicePath))
                            {
                                // 快速字符串匹配，过滤 VID/PID，降低多次打开设备的开销
                                if (devicePath.Contains("vid_2717", StringComparison.OrdinalIgnoreCase) &&
                                    devicePath.Contains("pid_3144", StringComparison.OrdinalIgnoreCase))
                                {
                                    devicePaths.Add(devicePath);
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailDataBuffer);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return devicePaths;
        }

        /// <summary>
        /// 打开小米手柄的 HID 设备句柄（支持异步重叠 I/O）
        /// </summary>
        public static SafeFileHandle OpenDevice(string devicePath, bool exclusive = false)
        {
            uint shareMode = exclusive ? 0 : (FILE_SHARE_READ | FILE_SHARE_WRITE);
            uint access = GENERIC_READ | GENERIC_WRITE;

            SafeFileHandle handle = CreateFile(
                devicePath,
                access,
                shareMode,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                // 如果独占打开失败，回退到共享模式打开
                if (exclusive)
                {
                    handle = CreateFile(
                        devicePath,
                        access,
                        FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero,
                        OPEN_EXISTING,
                        FILE_FLAG_OVERLAPPED,
                        IntPtr.Zero);
                }
            }

            return handle;
        }

        /// <summary>
        /// 从设备路径中提取设备实例 ID (Instance ID)，用于 HidHide 隐藏
        /// 例如: \\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&00022717_pid&3144#9&30687352&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
        /// 转换为: HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&00022717_PID&3144\9&30687352&0&0000
        /// </summary>
        public static string? DevicePathToInstanceId(string devicePath)
        {
            if (string.IsNullOrEmpty(devicePath))
                return null;

            try
            {
                string[] parts = devicePath.Split('#');
                if (parts.Length >= 4)
                {
                    // parts[0] = "\\?\" or "\\?\hid"
                    // parts[1] = enumerator & device ID
                    // parts[2] = instance ID
                    string enumerator = parts[0].Replace(@"\\?\", "").Replace(@"\\.\", "").Trim('\\');
                    return $"{enumerator}\\{parts[1]}\\{parts[2]}".ToUpperInvariant();
                }
            }
            catch
            {
                // 忽略解析错误
            }

            return null;
        }
    }
}
