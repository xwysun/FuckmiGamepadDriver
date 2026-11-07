using System;
using System.Runtime.InteropServices;

namespace MiGamepad.App
{
    /// <summary>
    /// 从 SYSTEM 服务进程向交互式用户会话启动进程。
    /// 用于蓝牙自愈后把“添加设备”页面弹到当前登录用户的桌面上
    /// (服务自身在 session 0, 直接启动的窗口用户不可见)。
    /// </summary>
    internal static class InteractiveSessionLauncher
    {
        /// <summary>在活动控制台会话中启动 commandLine (如 explorer.exe ms-settings:bluetooth)</summary>
        public static bool StartInActiveConsoleSession(string applicationName, string commandLine)
        {
            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
            {
                return false;
            }

            try
            {
                var startupInfo = new STARTUPINFO();
                startupInfo.cb = Marshal.SizeOf<STARTUPINFO>();

                bool ok = CreateProcessAsUser(
                    userToken,
                    applicationName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    creationFlags: 0,
                    environment: IntPtr.Zero,
                    currentDirectory: null,
                    ref startupInfo,
                    out _);

                return ok;
            }
            finally
            {
                CloseHandle(userToken);
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? applicationName,
            string commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public uint dwProcessId, dwThreadId;
        }
    }
}
