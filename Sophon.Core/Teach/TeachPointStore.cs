#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sophon.Core.Teach
{
    /// <summary>
    /// 示教点与点位组 JSON 单源持久化存储（SophonData\teach_points.json）。
    /// </summary>
    public class TeachPointStore
    {
        private class StoreData
        {
            public List<TeachPoint> Points { get; set; } = new();
            public List<TeachPointGroup> Groups { get; set; } = new();
        }

        private readonly string _filePath;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<string, TeachPoint> _points = new();
        private readonly ConcurrentDictionary<string, TeachPointGroup> _groups = new();

        public TeachPointStore(string? filePath = null)
        {
            _filePath = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "teach_points.json");
            Load();
        }

        public IReadOnlyList<TeachPoint> GetAllPoints() => _points.Values.OrderBy(p => p.Name).ToList();

        public TeachPoint? GetPoint(string id) => _points.TryGetValue(id, out var p) ? p : null;

        public TeachPoint? GetPointByName(string name) => _points.Values.FirstOrDefault(p => p.Name == name);

        public void SaveOrUpdatePoint(TeachPoint point)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));
            if (string.IsNullOrWhiteSpace(point.Id))
            {
                point.Id = Guid.NewGuid().ToString("N");
            }
            _points[point.Id] = point;
            Save();
        }

        public bool DeletePoint(string id)
        {
            if (_points.TryRemove(id, out _))
            {
                // 同时从所有组中移除该点
                foreach (var g in _groups.Values)
                {
                    g.PointIds.RemoveAll(pid => pid == id);
                }
                Save();
                return true;
            }
            return false;
        }

        public IReadOnlyList<TeachPointGroup> GetAllGroups() => _groups.Values.OrderBy(g => g.Name).ToList();

        public TeachPointGroup? GetGroup(string name) => _groups.TryGetValue(name, out var g) ? g : null;

        public void SaveOrUpdateGroup(TeachPointGroup group)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));
            if (string.IsNullOrWhiteSpace(group.Name)) throw new ArgumentException("Group name cannot be empty", nameof(group.Name));

            _groups[group.Name] = group;
            Save();
        }

        public bool DeleteGroup(string name)
        {
            if (_groups.TryRemove(name, out _))
            {
                Save();
                return true;
            }
            return false;
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var data = new StoreData
                    {
                        Points = _points.Values.ToList(),
                        Groups = _groups.Values.ToList()
                    };

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(data, options);
                    File.WriteAllText(_filePath, json);
                }
                catch { }
            }
        }

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_filePath))
                    {
                        string json = File.ReadAllText(_filePath);
                        var data = JsonSerializer.Deserialize<StoreData>(json);
                        if (data != null)
                        {
                            _points.Clear();
                            foreach (var p in data.Points)
                            {
                                _points[p.Id] = p;
                            }

                            _groups.Clear();
                            foreach (var g in data.Groups)
                            {
                                _groups[g.Name] = g;
                            }
                        }
                    }
                }
                catch { }
            }
        }
    }
}
