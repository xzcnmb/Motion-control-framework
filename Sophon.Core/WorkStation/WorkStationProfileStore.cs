#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sophon.Common;
using Sophon.Core.Flow.V2;

namespace Sophon.Core
{
    /// <summary>
    /// 工站档案：SophonData/workstations.json。损坏文件另存 .bad，禁止静默覆盖绑定。
    /// </summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public sealed class WorkStationProfileStore
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

        public WorkStationProfileStore()
            : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "workstations.json"))
        {
        }

        public WorkStationProfileStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("档案路径不能为空", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
        }

        public string FilePath => _filePath;

        public List<WorkStationProfile> Load()
        {
            lock (_lock)
            {
                return LoadUnlocked();
            }
        }

        public void Save(IReadOnlyList<WorkStationProfile> stations)
        {
            if (stations == null) throw new ArgumentNullException(nameof(stations));
            lock (_lock)
            {
                SaveUnlocked(stations);
            }
        }

        /// <summary>
        /// 没有档案时，用已保存的流程图各建一个同名工站（首次迁移）。解析失败不覆盖原文件。
        /// </summary>
        public List<WorkStationProfile> LoadOrMigrateFromFlows()
        {
            lock (_lock)
            {
                var list = LoadUnlocked();
                if (list.Count > 0)
                {
                    return list;
                }

                if (File.Exists(_filePath))
                {
                    return list;
                }

                foreach (var flow in FlowGraphStore.ListFlowNames())
                {
                    list.Add(new WorkStationProfile
                    {
                        StationName = flow,
                        BoundFlowName = flow,
                        LoopRecipe = true
                    });
                }

                if (list.Count > 0)
                {
                    SaveUnlocked(list);
                }

                return list;
            }
        }

        public void Upsert(WorkStationProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(profile.StationName))
            {
                throw new ArgumentException("工站名不能为空", nameof(profile));
            }

            lock (_lock)
            {
                var list = LoadUnlocked();
                int idx = list.FindIndex(p => string.Equals(p.StationName, profile.StationName, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    list[idx] = profile;
                }
                else
                {
                    list.Add(profile);
                }
                SaveUnlocked(list);
            }
        }

        private List<WorkStationProfile> LoadUnlocked()
        {
            if (!File.Exists(_filePath))
            {
                return new List<WorkStationProfile>();
            }

            try
            {
                string json = File.ReadAllText(_filePath, Utf8NoBom);
                var file = JsonSerializer.Deserialize<WorkStationProfileFile>(json, JsonOptions);
                return file?.Stations ?? new List<WorkStationProfile>();
            }
            catch (JsonException)
            {
                try
                {
                    string bad = _filePath + ".bad";
                    File.Copy(_filePath, bad, overwrite: true);
                }
                catch
                {
                }

                return new List<WorkStationProfile>();
            }
        }

        private void SaveUnlocked(IReadOnlyList<WorkStationProfile> stations)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var file = new WorkStationProfileFile { Stations = stations.ToList() };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(file, JsonOptions), Utf8NoBom);
        }
    }
}
