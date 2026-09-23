#nullable enable
using System;
using System.IO;
using System.Text;

namespace Common
{
    /// <summary>
    /// 小型 JSON/文本配置的崩溃安全写入与损坏隔离辅助类。
    /// </summary>
    public static class AtomicFileStore
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public static void WriteAllText(string path, string content, Encoding? encoding = null)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("文件路径不能为空", nameof(path));
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string fullPath = Path.GetFullPath(path);
            string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, encoding ?? Utf8NoBom);
                if (File.Exists(fullPath))
                {
                    string backupPath = fullPath + ".bak";
                    File.Replace(tempPath, fullPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }

        public static string QuarantineCorruptFile(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            string quarantined = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bad";
            File.Move(path, quarantined);
            return quarantined;
        }
    }
}
