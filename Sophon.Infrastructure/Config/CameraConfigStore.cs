#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Config
{
    /// <summary>
    /// 相机配置持久化存储。
    /// 存储至 SophonData\camera_configs.json。
    /// </summary>
    public class CameraConfigStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _filePath;
        private readonly object _lock = new();

        public CameraConfigStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "camera_configs.json")
                : Path.GetFullPath(filePath);
        }

        /// <summary>
        /// 加载所有相机配置。若文件不存在或异常则返回空列表。
        /// </summary>
        public List<CameraConfig> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<CameraConfig>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<CameraConfig>();
                    }

                    return JsonSerializer.Deserialize<List<CameraConfig>>(json, JsonOptions) ?? new List<CameraConfig>();
                }
                catch
                {
                    return new List<CameraConfig>();
                }
            }
        }

        /// <summary>
        /// 保存相机配置列表到 JSON 文件。
        /// </summary>
        public void Save(List<CameraConfig> configs)
        {
            if (configs == null) throw new ArgumentNullException(nameof(configs));

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(configs, JsonOptions);
                File.WriteAllText(_filePath, json, Utf8NoBom);
            }
        }

        /// <summary>
        /// 生成默认现场相机档案（海康，需填写序列号后才能连接）。
        /// </summary>
        public static List<CameraConfig> SeedDefaults()
        {
            return new List<CameraConfig>
            {
                new()
                {
                    Name = "相机1",
                    Vendor = CameraVendor.HikvisionMvs,
                    DeviceKey = string.Empty,
                    TriggerMode = CameraTriggerMode.Software,
                    ExposureUs = 20000,
                    Gain = 0,
                    OptimizePacketSize = false,
                    GrabTimeoutMs = 1000,
                    FlipY = false
                }
            };
        }
    }
}
