#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Sophon.Common;
using Sophon.Contracts;

namespace Sophon.Core
{
    public sealed class AxisGroupFile
    {
        public List<AxisGroupDefinition> Groups { get; set; } = new();
    }

    public static class AxisGroupExtensions
    {
        public static IReadOnlyList<int> DistinctAxisIds(this AxisGroupDefinition group) =>
            (group?.AxisIds ?? new List<int>()).Distinct().OrderBy(id => id).ToList();

        public static string AxisSummary(this AxisGroupDefinition group) =>
            group?.AxisIds == null || group.AxisIds.Count == 0
                ? "未绑定轴"
                : string.Join(",", group.AxisIds);
    }

    /// <summary>
    /// 运行期轴占用：同一物理轴不能同时属于两个启用中的工站组。
    /// </summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public sealed class AxisGroupLease
    {
        private readonly object _lock = new();
        private readonly Dictionary<int, string> _ownerByAxis = new();

        public IReadOnlyCollection<int> TryAcquire(string stationName, IReadOnlyList<int> axisIds, out string? conflict)
        {
            if (string.IsNullOrWhiteSpace(stationName))
            {
                throw new ArgumentException("工站名不能为空", nameof(stationName));
            }

            lock (_lock)
            {
                var ids = (axisIds ?? Array.Empty<int>()).Distinct().ToList();
                foreach (int id in ids)
                {
                    if (_ownerByAxis.TryGetValue(id, out var owner)
                        && !string.Equals(owner, stationName, StringComparison.Ordinal))
                    {
                        conflict = $"轴 {id} 已被工站「{owner}」占用";
                        return Array.Empty<int>();
                    }
                }

                foreach (int id in ids)
                {
                    _ownerByAxis[id] = stationName;
                }

                conflict = null;
                return ids;
            }
        }

        public void Release(string stationName)
        {
            if (string.IsNullOrWhiteSpace(stationName))
            {
                return;
            }

            lock (_lock)
            {
                var drop = _ownerByAxis.Where(kv => string.Equals(kv.Value, stationName, StringComparison.Ordinal))
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (int id in drop)
                {
                    _ownerByAxis.Remove(id);
                }
            }
        }
    }
}
