using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using MiGamepad.Core.Configuration;

namespace MiGamepad.Core
{
    /// <summary>
    /// 抑制 Windows 11 在虚拟 Xbox 360 控制器接入/移除时自发触发的
    /// “需要新应用打开 ms-gamebar 链接”弹窗 (系统未安装 Xbox Game Bar 时必现, Win11 已知问题)。
    ///
    /// 两级抑制 (实测 24H2 上仅 NoOpenWith 不足以拦截该弹窗):
    ///   1. NoOpenWith: 在 ms-gamebar / ms-gamebarservices 协议键下写入空的 REG_SZ 值,
    ///      Explorer 在协议激活失败时不再弹出“查找应用”对话框 (社区通行做法);
    ///   2. 空操作处理器: 为协议注册 URL Protocol + shell\open\command 指向一个静默退出的
    ///      wscript 脚本 —— 协议激活成功落入空操作, 从源头避免“激活失败”弹窗。
    ///      键上写入 MiGamepadNoOp 标记值, 供 GamepadSettings.DetectGameBarHandler
    ///      区分“我们注册的空操作”与“真实安装的 Xbox Game Bar”。
    /// </summary>
    public static class GameBarSuppressor
    {
        private static readonly string[] ProtocolNames = { "ms-gamebar", "ms-gamebarservices" };

        private const string NoOpenWithValueName = "NoOpenWith";
        private const string NoOpMarkerValueName = "MiGamepadNoOp";
        private const string UrlProtocolValueName = "URL Protocol";

        private static string NoOpScriptPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiGamepad");
                return Path.Combine(dir, "gamebar-noop.vbs");
            }
        }

        /// <summary>
        /// 幂等写入抑制配置: HKLM (需管理员/服务, 覆盖全部用户) 与 HKCU (无特权交互运行时的兜底) 都尝试。
        /// auto 模式仅在该系统未安装真实 Xbox Game Bar 时执行 (LTSC/精简版场景), 已装 Game Bar 则跳过。
        /// 返回本次是否有写入动作; summary 描述执行结果供日志展示。
        /// </summary>
        public static bool EnsureSuppressed(out string summary)
        {
            summary = "已按配置禁用 (SuppressGameBarPopup=off)";

            if (!GamepadSettings.SuppressGameBarPopup)
            {
                return false;
            }

            // LTSC 优化按需生效: 已安装 Game Bar 的系统不会弹该窗, 不做任何注册表改动
            if (GamepadSettings.ConfiguredSuppressPopupMode.Trim().ToLowerInvariant() is "auto" or "" &&
                GamepadSettings.DetectGameBarHandler())
            {
                summary = "已安装 Xbox Game Bar, 无需抑制弹窗";
                return false;
            }

            bool wroteAnything = false;
            bool wroteHklm = false;
            bool deniedHklm = false;
            bool wroteHkcu = false;

            string? noOpScript = EnsureNoOpScript();

            foreach (string protocol in ProtocolNames)
            {
                var hklmResult = ConfigureProtocol(Registry.LocalMachine, $@"SOFTWARE\Classes\{protocol}", noOpScript);
                wroteHklm |= hklmResult != WriteResult.Denied;
                deniedHklm |= hklmResult == WriteResult.Denied;
                wroteAnything |= hklmResult == WriteResult.Written;

                var hkcuResult = ConfigureProtocol(Registry.CurrentUser, $@"Software\Classes\{protocol}", noOpScript);
                wroteHkcu |= hkcuResult == WriteResult.Written;
                wroteAnything |= hkcuResult == WriteResult.Written;
            }

            if (!wroteAnything)
            {
                summary = "ms-gamebar 弹窗此前已抑制, 无需重复写入";
                return false;
            }

            var parts = new List<string>();
            if (wroteHklm) parts.Add("HKLM");
            if (wroteHkcu) parts.Add("HKCU");
            summary = $"已写入弹窗抑制 ({string.Join("+", parts)}: NoOpenWith + 空操作处理器)";
            if (!wroteHklm && deniedHklm)
            {
                summary += " [HKLM 无权限, 仅当前用户生效; 安装服务后可全局生效]";
            }

            return true;
        }

        private enum WriteResult
        {
            AlreadyPresent,
            Written,
            Denied,
        }

        /// <summary>对单个协议根键配置两级抑制; 已完整配置时返回 AlreadyPresent</summary>
        private static WriteResult ConfigureProtocol(RegistryKey root, string subPath, string? noOpScript)
        {
            try
            {
                using var key = root.CreateSubKey(subPath, writable: true);
                if (key == null)
                {
                    return WriteResult.Denied;
                }

                bool changed = false;

                // 一级: NoOpenWith (抑制“查找应用”兜底对话框)
                var existing = key.GetValue(NoOpenWithValueName);
                if (existing is not string s || s.Length != 0)
                {
                    key.SetValue(NoOpenWithValueName, string.Empty, RegistryValueKind.String);
                    changed = true;
                }

                // 二级: 空操作处理器 (让协议激活成功, 不触发失败弹窗)
                if (noOpScript != null)
                {
                    if (key.GetValue(UrlProtocolValueName) == null)
                    {
                        key.SetValue(UrlProtocolValueName, string.Empty, RegistryValueKind.String);
                        changed = true;
                    }

                    using var openCommand = key.CreateSubKey(@"shell\open\command", writable: true);
                    if (openCommand != null)
                    {
                        string command = $"wscript.exe //B \"{noOpScript}\"";
                        var existingCommand = openCommand.GetValue(null) as string;
                        if (!string.Equals(existingCommand, command, StringComparison.OrdinalIgnoreCase))
                        {
                            openCommand.SetValue(null, command, RegistryValueKind.String);
                            changed = true;
                        }
                    }

                    if (key.GetValue(NoOpMarkerValueName) == null)
                    {
                        key.SetValue(NoOpMarkerValueName, 1, RegistryValueKind.DWord);
                        changed = true;
                    }
                }

                return changed ? WriteResult.Written : WriteResult.AlreadyPresent;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                        or System.Security.SecurityException
                                        or IOException)
            {
                return WriteResult.Denied;
            }
        }

        /// <summary>写入静默空操作脚本 (VBS 仅注释, wscript //B 执行无窗口); 失败返回 null 降级为仅 NoOpenWith</summary>
        private static string? EnsureNoOpScript()
        {
            try
            {
                string path = NoOpScriptPath;
                string? dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir))
                {
                    return null;
                }

                Directory.CreateDirectory(dir);
                const string content = "' MiGamepad: ms-gamebar protocol no-op handler\r\n";
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, content);
                }

                return path;
            }
            catch
            {
                // 无权限写入脚本文件时降级: 仅保留 NoOpenWith
                return null;
            }
        }
    }
}
