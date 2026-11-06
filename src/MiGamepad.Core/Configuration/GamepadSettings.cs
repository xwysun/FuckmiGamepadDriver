using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace MiGamepad.Core.Configuration
{
    /// <summary>
    /// MiGamepad 配置 (settings.ini, 位于 %ProgramData%\MiGamepad)。
    /// 目前支持 MapGuideButton: auto / on / off。
    /// </summary>
    public static class GamepadSettings
    {
        private const string MapGuideKey = "MapGuideButton";
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

        public static void Reload() => _mapGuideButton = null;

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

        /// <summary>
        /// 检测系统是否存在 ms-gamebar 协议处理器。
        /// 该协议由 Xbox Game Bar 按用户注册 (HKCU\Software\Classes),
        /// 服务以 SYSTEM 运行, 因此需要枚举 HKU 下各用户配置单元。
        /// </summary>
        public static bool DetectGameBarHandler()
        {
            if (KeyExists(Registry.ClassesRoot, "ms-gamebar")) return true;
            if (KeyExists(Registry.LocalMachine, @"SOFTWARE\Classes\ms-gamebar")) return true;

            try
            {
                foreach (string sid in Registry.Users.GetSubKeyNames())
                {
                    if (KeyExists(Registry.Users, $@"{sid}\Software\Classes\ms-gamebar"))
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

        private static bool KeyExists(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                return key != null;
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
