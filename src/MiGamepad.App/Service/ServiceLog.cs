using System;
using System.IO;
using System.Text;

namespace MiGamepad.App
{
    /// <summary>
    /// 轻量服务日志: 写入 %ProgramData%\MiGamepad\service.log, 超限轮转为 .old。
    /// 文件首次创建时写入 UTF-8 BOM, 保证记事本与 PS5.1 正确显示中文。
    /// </summary>
    internal static class ServiceLog
    {
        private const long MaxFileBytes = 512 * 1024;
        private static readonly object Lock = new();

        private static string LogDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MiGamepad");

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    string filePath = Path.Combine(LogDirectory, "service.log");

                    if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxFileBytes)
                    {
                        string oldPath = Path.Combine(LogDirectory, "service.old.log");
                        File.Delete(oldPath);
                        File.Move(filePath, oldPath);
                    }

                    bool isNew = !File.Exists(filePath);
                    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew);
                    File.AppendAllText(filePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", encoding);
                }
            }
            catch
            {
                // 日志失败不影响服务运行
            }
        }
    }
}
