#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Config
{
    /// <summary>
    /// 运动控制卡配置档案持久化存储。
    /// 存储至 SophonData\motion_card_profiles.json，支持当前激活配置持久化。
    /// </summary>
    public class MotionCardProfileStore
    {
        private class ActiveProfileRecord
        {
            public string? ActiveProfileName { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _filePath;
        private readonly string _activeFilePath;
        private readonly object _lock = new();
        private string? _activeProfileName;

        public MotionCardProfileStore(string? filePath = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "motion_card_profiles.json")
                : Path.GetFullPath(filePath);

            string? dir = Path.GetDirectoryName(_filePath);
            string fileName = Path.GetFileNameWithoutExtension(_filePath) + ".active.json";
            _activeFilePath = string.IsNullOrEmpty(dir) ? fileName : Path.Combine(dir, fileName);
        }

        /// <summary>
        /// 加载所有控制卡配置档案。若文件不存在或异常则返回空列表。
        /// </summary>
        public List<MotionCardProfile> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<MotionCardProfile>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Encoding.UTF8);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return new List<MotionCardProfile>();
                    }

                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        if (doc.RootElement.TryGetProperty("activeProfileName", out var aProp) ||
                            doc.RootElement.TryGetProperty("ActiveProfileName", out aProp))
                        {
                            _activeProfileName = aProp.GetString();
                        }

                        if (doc.RootElement.TryGetProperty("profiles", out var pProp) ||
                            doc.RootElement.TryGetProperty("Profiles", out pProp))
                        {
                            return JsonSerializer.Deserialize<List<MotionCardProfile>>(pProp.GetRawText(), JsonOptions) ?? new List<MotionCardProfile>();
                        }
                    }
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        return JsonSerializer.Deserialize<List<MotionCardProfile>>(json, JsonOptions) ?? new List<MotionCardProfile>();
                    }

                    return new List<MotionCardProfile>();
                }
                catch
                {
                    return new List<MotionCardProfile>();
                }
            }
        }

        /// <summary>
        /// 保存控制卡配置档案列表到 JSON 文件。
        /// </summary>
        public void Save(List<MotionCardProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));

            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(profiles, JsonOptions);
                File.WriteAllText(_filePath, json, Utf8NoBom);
            }
        }

        /// <summary>
        /// 获取当前激活的控制卡档案。若未设定或未匹配则返回 null。
        /// </summary>
        public MotionCardProfile? GetActive()
        {
            lock (_lock)
            {
                var profiles = Load();
                if (profiles.Count == 0)
                {
                    return null;
                }

                string? activeName = _activeProfileName;
                if (string.IsNullOrWhiteSpace(activeName) && File.Exists(_activeFilePath))
                {
                    try
                    {
                        string json = File.ReadAllText(_activeFilePath, Encoding.UTF8);
                        var record = JsonSerializer.Deserialize<ActiveProfileRecord>(json, JsonOptions);
                        activeName = record?.ActiveProfileName;
                        _activeProfileName = activeName;
                    }
                    catch
                    {
                    }
                }

                if (!string.IsNullOrWhiteSpace(activeName))
                {
                    var match = profiles.FirstOrDefault(p => string.Equals(p.ProfileName, activeName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        return match;
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// 设置并持久化当前激活的配置档案名称。
        /// </summary>
        public void SetActive(string profileName)
        {
            lock (_lock)
            {
                _activeProfileName = profileName;
                try
                {
                    string? dir = Path.GetDirectoryName(_activeFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var record = new ActiveProfileRecord { ActiveProfileName = profileName };
                    string json = JsonSerializer.Serialize(record, JsonOptions);
                    File.WriteAllText(_activeFilePath, json, Utf8NoBom);
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 生成默认现场档案（固高 GTS 四轴 X/Y/Z/R）。不含仿真驱动。
        /// </summary>
        public static List<MotionCardProfile> SeedDefaults()
        {
            var profile = new MotionCardProfile
            {
                ProfileName = "默认配置",
                CardNo = 0,
                ConnectionString = null,
                ConfigFilePath = null,
            };
            var gts400 = MotionCardCatalog.Find("GTS-400")
                         ?? throw new InvalidOperationException("选型目录缺少 GTS-400。");
            profile.ApplyModel(gts400);
            profile.Axes = new List<AxisDefinition>
            {
                    new()
                    {
                        AxisId = 0,
                        Name = "X",
                        Unit = "mm",
                        PulsePerUnit = 1000,
                        DirectionInvert = false,
                        SoftLimitEnabled = true,
                        SoftLimitMin = -500,
                        SoftLimitMax = 500,
                        HardLimitEnabled = false,
                        LimitPositiveIoName = "LimitX+",
                        LimitNegativeIoName = "LimitX-",
                        HomeIoName = "HomeX",
                        MaxSpeed = 200,
                        MaxAccel = 1000,
                        MaxDecel = 1000,
                        MaxJerk = 5000,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Negative,
                        HomeSpeed = 20
                    },
                    new()
                    {
                        AxisId = 1,
                        Name = "Y",
                        Unit = "mm",
                        PulsePerUnit = 1000,
                        DirectionInvert = false,
                        SoftLimitEnabled = true,
                        SoftLimitMin = -500,
                        SoftLimitMax = 500,
                        HardLimitEnabled = false,
                        LimitPositiveIoName = "LimitY+",
                        LimitNegativeIoName = "LimitY-",
                        HomeIoName = "HomeY",
                        MaxSpeed = 200,
                        MaxAccel = 1000,
                        MaxDecel = 1000,
                        MaxJerk = 5000,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Negative,
                        HomeSpeed = 20
                    },
                    new()
                    {
                        AxisId = 2,
                        Name = "Z",
                        Unit = "mm",
                        PulsePerUnit = 1000,
                        DirectionInvert = false,
                        SoftLimitEnabled = true,
                        SoftLimitMin = 0,
                        SoftLimitMax = 300,
                        HardLimitEnabled = false,
                        LimitPositiveIoName = "LimitZ+",
                        LimitNegativeIoName = "LimitZ-",
                        HomeIoName = "HomeZ",
                        MaxSpeed = 100,
                        MaxAccel = 500,
                        MaxDecel = 500,
                        MaxJerk = 2500,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Positive,
                        HomeSpeed = 10
                    },
                    new()
                    {
                        AxisId = 3,
                        Name = "R",
                        Unit = "deg",
                        PulsePerUnit = 1000,
                        DirectionInvert = false,
                        SoftLimitEnabled = true,
                        SoftLimitMin = -360,
                        SoftLimitMax = 360,
                        HardLimitEnabled = false,
                        LimitPositiveIoName = "LimitR+",
                        LimitNegativeIoName = "LimitR-",
                        HomeIoName = "HomeR",
                        MaxSpeed = 360,
                        MaxAccel = 1800,
                        MaxDecel = 1800,
                        MaxJerk = 9000,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Negative,
                        HomeSpeed = 36
                    }
            };

            return new List<MotionCardProfile> { profile };
        }
    }
}
