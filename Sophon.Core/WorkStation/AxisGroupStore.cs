#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sophon.Common;
using Sophon.Contracts;

namespace Sophon.Core
{
    /// <summary>轴组档案：SophonData/axis_groups.json。</summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public sealed class AxisGroupStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private readonly string _filePath;
        private readonly object _lock = new();

        public AxisGroupStore()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "axis_groups.json"))
        {
        }

        public AxisGroupStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("档案路径不能为空", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
        }

        public string FilePath => _filePath;

        public List<AxisGroupDefinition> Load()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    return new List<AxisGroupDefinition>();
                }

                try
                {
                    string json = File.ReadAllText(_filePath, Utf8NoBom);
                    var file = JsonSerializer.Deserialize<AxisGroupFile>(json, JsonOptions);
                    return file?.Groups ?? new List<AxisGroupDefinition>();
                }
                catch (JsonException)
                {
                    try
                    {
                        File.Copy(_filePath, _filePath + ".bad", overwrite: true);
                    }
                    catch
                    {
                    }

                    return new List<AxisGroupDefinition>();
                }
            }
        }

        public void Save(IReadOnlyList<AxisGroupDefinition> groups)
        {
            if (groups == null) throw new ArgumentNullException(nameof(groups));
            lock (_lock)
            {
                string? dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var file = new AxisGroupFile { Groups = groups.ToList() };
                File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOptions), Utf8NoBom);
            }
        }

        public AxisGroupDefinition? Find(string? groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                return null;
            }

            return Load().FirstOrDefault(g =>
                string.Equals(g.GroupName, groupName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public void Upsert(AxisGroupDefinition group)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (string.IsNullOrWhiteSpace(group.GroupName))
            {
                throw new ArgumentException("轴组名不能为空", nameof(group));
            }

            lock (_lock)
            {
                var list = LoadUnlocked();
                int idx = list.FindIndex(g =>
                    string.Equals(g.GroupName, group.GroupName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    list[idx] = group;
                }
                else
                {
                    list.Add(group);
                }
                SaveUnlocked(list);
            }
        }

        private List<AxisGroupDefinition> LoadUnlocked()
        {
            if (!File.Exists(_filePath))
            {
                return new List<AxisGroupDefinition>();
            }

            try
            {
                string json = File.ReadAllText(_filePath, Utf8NoBom);
                var file = JsonSerializer.Deserialize<AxisGroupFile>(json, JsonOptions);
                return file?.Groups ?? new List<AxisGroupDefinition>();
            }
            catch (JsonException)
            {
                return new List<AxisGroupDefinition>();
            }
        }

        private void SaveUnlocked(IReadOnlyList<AxisGroupDefinition> groups)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = new AxisGroupFile { Groups = groups.ToList() };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOptions), Utf8NoBom);
        }
    }
}
