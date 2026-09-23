#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Config
{
    /// <summary>
    /// 数字量 IO 配置持久化存储工具。
    /// 统一按单源存储至 SophonData\io_points.json。
    /// </summary>
    public class IoPointConfigStore
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

        public IoPointConfigStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "io_points.json")
                : Path.GetFullPath(filePath);
        }

        /// <summary>
        /// 加载所有已配置的 IO 点位。若文件不存在则返回空列表。
        /// </summary>
        public List<IoPointDefinition> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<IoPointDefinition>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<IoPointDefinition>();
                    }

                    return JsonSerializer.Deserialize<List<IoPointDefinition>>(json, JsonOptions) ?? new List<IoPointDefinition>();
                }
                catch (JsonException)
                {
                    try { AtomicFileStore.QuarantineCorruptFile(_filePath); } catch { }
                    throw new InvalidDataException($"IO 配置文件损坏，已隔离：{_filePath}");
                }
            }
        }

        /// <summary>
        /// 保存 IO 点位列表到 JSON 文件。
        /// </summary>
        public void Save(List<IoPointDefinition> points)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(points, JsonOptions);
                AtomicFileStore.WriteAllText(_filePath, json, Utf8NoBom);
            }
        }

        /// <summary>
        /// 生成标准工业机台默认预置的 IO 映射点位表（涵盖安全门、气压开关、夹爪电磁阀与双到位、工装气缸）。
        /// </summary>
        public static List<IoPointDefinition> SeedDefaults()
        {
            return new List<IoPointDefinition>
            {
                // ===== 输入点 (DI) =====
                new()
                {
                    LogicalName = "DI_EMG_STOP",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 0,
                    Invert = false,
                    Switch = SwitchType.NormallyClose, // 常闭安全急停
                    FilterMs = 10,
                    Category = "安全回路",
                    Description = "操作台外部急停按钮 (常闭NC)"
                },
                new()
                {
                    LogicalName = "DI_SAFETY_DOOR",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 1,
                    Invert = false,
                    Switch = SwitchType.NormallyClose,
                    FilterMs = 20,
                    Category = "安全回路",
                    Description = "安全光幕 / 保护门互锁信号"
                },
                new()
                {
                    LogicalName = "DI_AIR_PRESSURE_OK",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 2,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 50,
                    Category = "气动系统",
                    Description = "总气源主气压传感器 (>=0.45MPa 正常)"
                },
                new()
                {
                    LogicalName = "DI_GRIP_OPENED",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 3,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 30,
                    Category = "气缸机构",
                    Description = "夹爪1 张开/工作到位磁性开关"
                },
                new()
                {
                    LogicalName = "DI_GRIP_CLOSED",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 4,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 30,
                    Category = "气缸机构",
                    Description = "夹爪1 夹紧/复位到位磁性开关"
                },
                new()
                {
                    LogicalName = "DI_CYL_UP_REACHED",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 5,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 30,
                    Category = "气缸机构",
                    Description = "顶升气缸 伸出/上位磁性开关"
                },
                new()
                {
                    LogicalName = "DI_CYL_DOWN_REACHED",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 6,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 30,
                    Category = "气缸机构",
                    Description = "顶升气缸 缩回/原位磁性开关"
                },

                // ===== 输出点 (DO) =====
                new()
                {
                    LogicalName = "DO_GRIP_OPEN",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 0,
                    Invert = false,
                    Category = "气缸机构",
                    Description = "夹爪1 张开电磁阀线圈 (双电控脉冲)"
                },
                new()
                {
                    LogicalName = "DO_GRIP_CLOSE",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 1,
                    Invert = false,
                    Category = "气缸机构",
                    Description = "夹爪1 夹紧电磁阀线圈 (双电控脉冲)"
                },
                new()
                {
                    LogicalName = "DO_CYL_EXTEND",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 2,
                    Invert = false,
                    Category = "气缸机构",
                    Description = "顶升气缸 伸出控制线圈"
                },
                new()
                {
                    LogicalName = "DO_CYL_RETRACT",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 3,
                    Invert = false,
                    Category = "气缸机构",
                    Description = "顶升气缸 缩回控制线圈"
                },
                new()
                {
                    LogicalName = "DO_TOWER_LIGHT_RED",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 4,
                    Invert = false,
                    Category = "系统指示",
                    Description = "三色警示灯 - 红色(报警)"
                },
                new()
                {
                    LogicalName = "DO_TOWER_LIGHT_GREEN",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 5,
                    Invert = false,
                    Category = "系统指示",
                    Description = "三色警示灯 - 绿色(运行)"
                },
                new()
                {
                    LogicalName = "DO_BUZZER",
                    Direction = IoDirection.DO,
                    CardNo = 0,
                    ChannelBit = 6,
                    Invert = false,
                    Category = "系统指示",
                    Description = "现场蜂鸣器报警输出"
                }
            };
        }
    }
}
