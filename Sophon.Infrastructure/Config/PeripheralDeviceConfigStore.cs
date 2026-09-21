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
    /// 外设通讯设备配置持久化存储。
    /// 存储至 SophonData\peripheral_devices.json。
    /// </summary>
    public class PeripheralDeviceConfigStore
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

        public PeripheralDeviceConfigStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "peripheral_devices.json")
                : Path.GetFullPath(filePath);
        }

        /// <summary>
        /// 加载所有外设配置。若文件不存在或异常则返回空列表。
        /// </summary>
        public List<PeripheralDeviceConfig> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<PeripheralDeviceConfig>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<PeripheralDeviceConfig>();
                    }

                    return JsonSerializer.Deserialize<List<PeripheralDeviceConfig>>(json, JsonOptions) ?? new List<PeripheralDeviceConfig>();
                }
                catch
                {
                    return new List<PeripheralDeviceConfig>();
                }
            }
        }

        /// <summary>
        /// 保存外设配置列表到 JSON 文件。
        /// </summary>
        public void Save(List<PeripheralDeviceConfig> devices)
        {
            if (devices == null) throw new ArgumentNullException(nameof(devices));

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(devices, JsonOptions);
                File.WriteAllText(_filePath, json, Utf8NoBom);
            }
        }

        /// <summary>
        /// 生成默认的初始外设配置（1个 Modbus RTU 温控器示例，含 PV 地址0 系数0.1 单位"℃"，SV 地址1 可写 系数0.1）。
        /// </summary>
        public static List<PeripheralDeviceConfig> SeedDefaults()
        {
            return new List<PeripheralDeviceConfig>
            {
                new()
                {
                    Name = "温控器1",
                    Category = "温控",
                    Transport = DeviceTransport.ModbusRtu,
                    PortName = "COM1",
                    BaudRate = 9600,
                    Parity = "None",
                    DataBits = 8,
                    StopBits = "One",
                    SlaveAddress = 1,
                    PollPeriodMs = 500,
                    TimeoutMs = 500,
                    Retries = 2,
                    Tags = new List<DeviceTag>
                    {
                        new()
                        {
                            Name = "PV",
                            Area = ModbusArea.HoldingRegister,
                            Address = 0,
                            DataType = RegisterDataType.UInt16,
                            Writable = false,
                            Scale = 0.1,
                            Offset = 0.0,
                            Unit = "℃"
                        },
                        new()
                        {
                            Name = "SV",
                            Area = ModbusArea.HoldingRegister,
                            Address = 1,
                            DataType = RegisterDataType.UInt16,
                            Writable = true,
                            Scale = 0.1,
                            Offset = 0.0,
                            Unit = "℃"
                        }
                    }
                }
            };
        }
    }
}
