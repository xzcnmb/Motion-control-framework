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
    /// 气缸/夹爪配置持久化存储。
    /// 存储至 SophonData\cylinder_configs.json。
    /// </summary>
    public class CylinderConfigStore
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

        public CylinderConfigStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "cylinder_configs.json")
                : Path.GetFullPath(filePath);
        }

        /// <summary>
        /// 加载所有气缸配置。若文件不存在或异常则返回空列表。
        /// </summary>
        public List<CylinderDefinition> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<CylinderDefinition>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<CylinderDefinition>();
                    }

                    return JsonSerializer.Deserialize<List<CylinderDefinition>>(json, JsonOptions) ?? new List<CylinderDefinition>();
                }
                catch
                {
                    return new List<CylinderDefinition>();
                }
            }
        }

        /// <summary>
        /// 保存气缸配置列表到 JSON 文件。
        /// </summary>
        public void Save(List<CylinderDefinition> cylinders)
        {
            if (cylinders == null) throw new ArgumentNullException(nameof(cylinders));

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(cylinders, JsonOptions);
                File.WriteAllText(_filePath, json, Utf8NoBom);
            }
        }

        /// <summary>
        /// 生成默认的初始气缸配置（1个双电控气缸示例）。
        /// </summary>
        public static List<CylinderDefinition> SeedDefaults()
        {
            return new List<CylinderDefinition>
            {
                new()
                {
                    Name = "夹爪气缸",
                    Valve = ValveType.DoubleCoil,
                    WorkDoName = "DO_Gripper_Work",
                    HomeDoName = "DO_Gripper_Home",
                    WorkSensorDiName = "DI_Gripper_WorkSensor",
                    HomeSensorDiName = "DI_Gripper_HomeSensor",
                    PulseWidthMs = 200,
                    ConfirmTimeoutMs = 1500,
                    InterlockGroup = null,
                    ResetToHomeOnStart = true,
                    EnableConditionDiNames = new List<string>()
                }
            };
        }
    }
}
