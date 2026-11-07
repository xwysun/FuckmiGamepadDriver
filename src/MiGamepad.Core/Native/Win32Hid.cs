using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

        #endregion

        #region P/Invoke Imports

        [DllImport("hid.dll", SetLastError = true)]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetOutputReport(SafeFileHandle hidDeviceObject, byte[] lpReportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid classGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        private const uint DIGCF_ALLCLASSES = 0x00000004;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid classGuid;
            public int devInst;
            public IntPtr reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        /// <summary>对应 Win32 HIDP_CAPS，描述顶层集合的报文能力</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct HIDP_CAPS
        {
            public ushort UsagePage;
            public ushort Usage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        #endregion

        /// <summary>
        /// 查询设备的 HID 属性 (VID/PID/版本号)
        /// </summary>
        public static HIDD_ATTRIBUTES GetDeviceAttributes(SafeFileHandle handle)
        {
            var attributes = new HIDD_ATTRIBUTES();
            attributes.Size = Marshal.SizeOf(attributes);

            if (HidD_GetAttributes(handle, ref attributes))
            {
                return attributes;
            }

            return default;
        }

        /// <summary>
        /// 查询设备顶层集合的报文能力 (输入/输出/特征报文长度等)，失败返回 null
        /// </summary>
        public static HIDP_CAPS? GetDeviceCaps(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsedData) || preparsedData == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var caps = default(HIDP_CAPS);
                if (HidP_GetCaps(preparsedData, ref caps) != HIDP_STATUS_SUCCESS)
                {
                    return null;
                }

                return caps;
            }
            finally
            {
                HidD_FreePreparsedData(preparsedData);
            }
        }

        /// <summary>
        /// 枚举系统中所有已连接的小米蓝牙手柄设备路径 (仅手柄集合)
        /// </summary>
        public static List<string> EnumerateXiaomiGamepads()
        {
            return EnumerateAllXiaomiCandidates()
                .Where(IsGamepadCollection)
                .ToList();
        }

        /// <summary>
        /// 枚举所有匹配 VID 0x2717 / PID 0x3144 的 HID 接口 (未按 Usage 过滤, 供诊断使用)
        /// </summary>
        public static List<string> EnumerateAllXiaomiCandidates()
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
                        // 在 64 位系统上，cbSize 结构体头部根据平台指针宽度为 8 (x64) 或 6/4 (x86)
                        Marshal.WriteInt32(detailDataBuffer, IntPtr.Size == 8 ? 8 : (4 + Marshal.SystemDefaultCharSize));

                        if (SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, IntPtr.Zero))
                        {
                            IntPtr pDevicePath = IntPtr.Add(detailDataBuffer, 4);
                            string? devicePath = Marshal.PtrToStringAuto(pDevicePath);

                            if (!string.IsNullOrEmpty(devicePath))
                            {
                                // 蓝牙 HID 路径格式为 vid&00022717_pid&3144，USB 格式为 vid_2717&pid_3144
                                // 因此直接匹配十六进制特征 "2717" 与 "3144"
                                if (devicePath.Contains("2717", StringComparison.OrdinalIgnoreCase) &&
                                    devicePath.Contains("3144", StringComparison.OrdinalIgnoreCase))
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
        /// 枚举蓝牙层 (BTHENUM) 上已配对小米手柄的服务节点。
        /// 配对存在时这些节点持续可见 (与手柄是否在线无关), 用于识别
        /// “已配对但 HID 接口层始终不出现”的回连卡死状态。
        /// 返回 (实例 ID, 蓝牙 MAC) 列表。
        /// </summary>
        public static List<(string InstanceId, ulong Address)> EnumeratePairedXiaomiBluetoothNodes()
        {
            var results = new List<(string, ulong)>();
            const string bthEnumerator = "BTHENUM";

            IntPtr deviceInfoSet = SetupDiGetClassDevs(IntPtr.Zero, bthEnumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == (IntPtr)(-1))
            {
                return results;
            }

            try
            {
                var infoData = new SP_DEVINFO_DATA();
                infoData.cbSize = Marshal.SizeOf(infoData);

                uint index = 0;
                while (SetupDiEnumDeviceInfo(deviceInfoSet, index++, ref infoData))
                {
                    var buffer = new StringBuilder(512);
                    if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref infoData, buffer, buffer.Capacity, out _))
                    {
                        continue;
                    }

                    string instanceId = buffer.ToString();
                    // 服务节点形如 BTHENUM\{00001124-...}_VID&00022717_PID&3144\8&00000000&0&001122334455_C00000000;
                    // 根节点 BTHENUM\DEV_<MAC>\... 不携带 VID&PID, 故从服务节点实例串提取 MAC
                    if (!instanceId.Contains("VID&00022717_PID&3144", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryExtractBluetoothAddress(instanceId, out ulong address))
                    {
                        results.Add((instanceId, address));
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return results;
        }

        /// <summary>
        /// 从 BTHENUM 服务节点实例 ID 提取蓝牙 MAC 地址。
        /// 实例后缀形如 8&amp;00000000&amp;0&amp;001122334455_C00000000:
        /// 最后一个 &amp; 与 _ 之间的 12 位十六进制即为 MAC (直观字节序 00:11:22:33:44:55)。
        /// 蓝牙地址结构按小端 ulong 存储, 因此低位字节在前。
        /// </summary>
        private static bool TryExtractBluetoothAddress(string instanceId, out ulong address)
        {
            address = 0;
            var match = Regex.Match(instanceId, @"&([0-9A-Fa-f]{12})_");
            if (!match.Success)
            {
                return false;
            }

            string hex = match.Groups[1].Value;
            try
            {
                ulong parsed = 0;
                for (int i = 0; i < 6; i++)
                {
                    parsed |= (ulong)Convert.ToByte(hex.Substring(10 - i * 2, 2), 16) << (i * 8);
                }
                address = parsed;
                return true;
            }
            catch
            {
                return false;
            }
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
        /// 判断该 HID 接口是否为"游戏手柄"集合 (UsagePage=0x01 Generic Desktop, Usage=0x05 Game Pad / 0x04 Joystick)。
        /// 同一物理手柄可能暴露多个 HID 集合 (如 &0/&1 两个实例), 只接管手柄集合,
        /// 否则会为同一手柄创建多个虚拟控制器 (占用多个 XInput 槽位)。
        /// 句柄打不开时需区分原因: 拒绝访问(5) 说明被 HidHide 屏蔽, 设备仍在 (本程序服务已加白名单),
        /// 不能据此判定掉线; 只有 ERROR_DEVICE_NOT_CONNECTED(1167)/找不到设备(2) 才视为未接入。
        /// </summary>
        private static bool IsGamepadCollection(string devicePath)
        {
            SafeFileHandle probe = OpenDevice(devicePath);
            if (probe.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                // 5=拒绝访问(HidHide 屏蔽), 0=未提供错误码: 保持候选
                return err == 5 || err == 0;
            }

            try
            {
                var caps = GetDeviceCaps(probe);
                if (caps == null)
                {
                    // 无法读取能力时保守放行, 由后续读取循环决定可用性
                    return true;
                }

                // 实测本手柄为 UsagePage=0x05 (Game Controls) / Usage=0x01 (Game Pad);
                // 同时兼容标准桌面页 0x01 / 0x04(Joystick)、0x05(Game Pad)
                bool isGamepadUsage = (caps.Value.UsagePage == 0x05 && caps.Value.Usage == 0x01)
                                      || (caps.Value.UsagePage == 0x01 && (caps.Value.Usage == 0x04 || caps.Value.Usage == 0x05));

                return isGamepadUsage;
            }
            finally
            {
                probe.Dispose();
            }
        }

        /// <summary>
        /// 从设备路径中提取设备实例 ID (Instance ID)，用于 HidHide 隐藏
        /// 例如: \\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&00022717_pid&3144#9&00000000&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
        /// 转换为: HID\{00001124-0000-1000-8000-00805f9b34fb}_VID&00022717_PID&3144\9&00000000&0&0000
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
