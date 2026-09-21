using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sophon.Vision.Calibration
{
    /// <summary>
    /// 标定文件持久化存储工具。
    /// 统一按单源管理存储至 SophonData\calibrations\{cameraId}.json。
    /// 采用 System.Text.Json 序列化，UTF-8 无 BOM 输出。
    /// </summary>
    public static class CalibrationStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// 获取标定文件保存的默认根目录 (默认为工作目录下的 SophonData\\calibrations)。
        /// </summary>
        public static string GetDefaultDirectory(string? baseDirectory = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "calibrations")
                : Path.Combine(baseDirectory, "SophonData", "calibrations");

            return Path.GetFullPath(root);
        }

        /// <summary>
        /// 获取指定相机的标定 JSON 文件路径。
        /// </summary>
        public static string GetFilePath(string cameraId, string? baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(cameraId))
            {
                throw new ArgumentException("相机 ID 不能为空", nameof(cameraId));
            }

            string dir = GetDefaultDirectory(baseDirectory);
            return Path.Combine(dir, $"{cameraId}.json");
        }

        /// <summary>
        /// 检查相机标定文件是否存在。
        /// </summary>
        public static bool Exists(string cameraId, string? baseDirectory = null)
        {
            return File.Exists(GetFilePath(cameraId, baseDirectory));
        }

        /// <summary>
        /// 保存标定数据到 JSON 文件。
        /// </summary>
        public static void Save(CalibrationData data, string? baseDirectory = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(data.CameraId))
            {
                throw new ArgumentException("标定数据中 CameraId 不能为空", nameof(data));
            }

            string filePath = GetFilePath(data.CameraId, baseDirectory);
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(filePath, json, Utf8NoBom);
        }

        /// <summary>
        /// 从 JSON 文件加载相机标定数据。若不存在则返回 null。
        /// </summary>
        public static CalibrationData? Load(string cameraId, string? baseDirectory = null)
        {
            string filePath = GetFilePath(cameraId, baseDirectory);
            if (!File.Exists(filePath))
            {
                return null;
            }

            string json = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<CalibrationData>(json, JsonOptions);
        }
    }
}
