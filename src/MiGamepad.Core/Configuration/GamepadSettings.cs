using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MiGamepad.Core.Configuration
{
    /// <summary>
    /// MiGamepad 配置 (settings.ini, 位于 %ProgramData%\MiGamepad)。
    /// 支持 MapGuideButton (auto/on/off) 与 SuppressGameBarPopup (auto/on/off)。
    /// </summary>
    public static class GamepadSettings
    {
        private const string MapGuideKey = "MapGuideButton";
        private const string SuppressPopupKey = "SuppressGameBarPopup";
        private const string AutoRepairKey = "AutoRepairBluetooth";
        private static bool? _mapGuideButton;

        public static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiGamepad", "settings.ini");

        /// <summary>
        /// MI 徽标键是否映射为 Xbox Guide。
        /// 默认 auto: 系统存在 ms-gamebar 协议处理器 (Xbox Game Bar) 时才启用。
        /// Windows 11 LTSC 等未安装 Game Bar 的系统上, 映射 Guide 会触发
        /// “无法打开此 ms-gamebar 链接”弹窗, 因此自动关闭。
        /// </summary>
        public static bool MapGuideButton => _mapGuideButton ??= ResolveMapGuideButton();

        /// <summary>配置文件中写的原始模式 (auto / on / off)</summary>
        public static string ConfiguredGuideMode => ReadValue(MapGuideKey) ?? "auto";

        /// <summary>
        /// 是否写入注册表抑制 “需要新应用打开 ms-gamebar” 弹窗 (见 GameBarSuppressor)。
        /// 默认 auto=启用: 弹窗由 Win11 在虚拟手柄接入时自发触发, 与是否按 MI 键无关,
        /// 抑制动作无害, 因此 auto 与 on 等价, 仅提供 off 逃生口。
        /// </summary>
        public static bool SuppressGameBarPopup => ResolveSuppressGameBarPopup();

        /// <summary>SuppressGameBarPopup 配置文件中的原始模式</summary>
        public static string ConfiguredSuppressPopupMode => ReadValue(SuppressPopupKey) ?? "auto";

        /// <summary>
        /// 是否在“已配对但 HID 接口长时间不出现”(蓝牙回连认证卡死) 时自动
        /// 删除配对并无 UI 重新配对 (见 BluetoothRecovery)。默认 on。
        /// </summary>
        public static bool AutoRepairBluetooth => ResolveAutoRepair();

        /// <summary>AutoRepairBluetooth 配置文件中的原始模式</summary>
        public static string ConfiguredAutoRepairMode => ReadValue(AutoRepairKey) ?? "on";

        public static void Reload() => _mapGuideButton = null;

        private static bool ResolveAutoRepair()
        {
            string mode = (ReadValue(AutoRepairKey) ?? "on").Trim().ToLowerInvariant();
            return mode is not ("off" or "false" or "0");
        }

        private static bool ResolveMapGuideButton()
        {
            string mode = (ReadValue(MapGuideKey) ?? "auto").Trim().ToLowerInvariant();
            switch (mode)
            {
                case "off":
                case "false":
                case "0":
                    return false;
                case "on":
                case "true":
                case "1":
                    return true;
                default:
                    return DetectGameBarHandler();
            }
        }

        private static bool ResolveSuppressGameBarPopup()
        {
            string mode = (ReadValue(SuppressPopupKey) ?? "auto").Trim().ToLowerInvariant();
            return mode is not ("off" or "false" or "0");
        }

        /// <summary>
        /// 检测系统是否真正注册了 ms-gamebar 协议处理器 (Xbox Game Bar)。
        /// 该协议由 Xbox Game Bar 按用户注册 (HKCU\Software\Classes),
        /// 服务以 SYSTEM 运行, 因此需要枚举 HKU 下各用户配置单元。
        /// 注意: 不能只判断键是否存在 —— MiGamepad 自身会写入 NoOpenWith 抑制弹窗
        /// (见 GameBarSuppressor), 仅含 NoOpenWith 的键不代表 Game Bar 可用。
        /// </summary>
        public static bool DetectGameBarHandler()
        {
            if (HasProtocolRegistration(Registry.ClassesRoot, "ms-gamebar")) return true;
            if (HasProtocolRegistration(Registry.LocalMachine, @"SOFTWARE\Classes\ms-gamebar")) return true;

            try
            {
                foreach (string sid in Registry.Users.GetSubKeyNames())
                {
                    if (HasProtocolRegistration(Registry.Users, $@"{sid}\Software\Classes\ms-gamebar"))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // HKU 枚举失败时按“无处理器”处理
            }

            return false;
        }

        private static bool HasProtocolRegistration(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) return false;

                // MiGamepad 自身注册的空操作处理器 (见 GameBarSuppressor) 不代表 Game Bar 可用
                if (key.GetValue("MiGamepadNoOp") != null) return false;

                // 标准协议注册的标志是 "URL Protocol" 字符串值
                return key.GetValue("URL Protocol") != null;
            }
            catch
            {
                return false;
            }
        }

        private static string? ReadValue(string key)
        {
            try
            {
                if (!File.Exists(SettingsFilePath)) return null;

                foreach (string raw in File.ReadAllLines(SettingsFilePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return line[(eq + 1)..].Trim();
                    }
                }
            }
            catch
            {
                // 配置读取失败时使用默认值
            }

            return null;
        }

        /// <summary>写入/更新配置项 (供 --set-guide 使用)</summary>
        public static bool WriteValue(string key, string value)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var lines = File.Exists(SettingsFilePath)
                    ? new List<string>(File.ReadAllLines(SettingsFilePath))
                    : new List<string>();

                if (lines.Count == 0)
                {
                    lines.Add("# MiGamepad 配置文件");
                    lines.Add("# MapGuideButton: auto = 自动检测 (系统存在 Xbox Game Bar 时才把 MI 键映射为 Guide)");
                    lines.Add("#                 on   = 始终映射 MI 键为 Xbox Guide");
                    lines.Add("#                 off  = 不映射 (Windows 11 LTSC 等没有 Game Bar 的系统推荐)");
                    lines.Add("# SuppressGameBarPopup: auto = 仅在未安装 Game Bar 的系统 (LTSC/精简版) 抑制 ms-gamebar 弹窗");
                    lines.Add("#                       on   = 无论是否安装都写入抑制");
                    lines.Add("#                       off  = 不做任何抑制");
                    lines.Add("# AutoRepairBluetooth:  on   = 手柄回连通道损坏 (HID 闪断) 时自动删配对重配 (off 关闭)");
                    lines.Add("# 修改后需重启服务: sc stop MiGamepad && sc start MiGamepad");
                }

                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                    int eq = line.IndexOf('=');
                    if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    lines.Add($"{key}={value}");
                }

                File.WriteAllLines(SettingsFilePath, lines);
                Reload();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>首次安装时生成带注释的默认配置文件 (已存在则跳过)</summary>
        public static void EnsureDefaultFile()
        {
            if (!File.Exists(SettingsFilePath))
            {
                WriteValue(MapGuideKey, "auto");
            }
        }
    }
}
