#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sophon.Core.Teach
{
    /// <summary>
    /// 流程节点按示教点名解析坐标。业界写法是 MOVL P1，不是手填毫米数。
    /// </summary>
    public static class TeachPointLookup
    {
        private static readonly object DefaultLock = new();
        private static TeachPointStore? _defaultStore;

        public static TeachPointStore DefaultStore
        {
            get
            {
                lock (DefaultLock)
                {
                    return _defaultStore ??= new TeachPointStore();
                }
            }
        }

        public static TeachPoint? Find(string? nameOrId, TeachPointStore? store = null)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return null;
            }

            store ??= DefaultStore;
            if (ReferenceEquals(store, DefaultStore))
            {
                store.Reload();
            }
            string key = nameOrId.Trim();
            return store.GetPointByName(key) ?? store.GetPoint(key);
        }

        public static bool TryGetAxisPosition(string? nameOrId, int axisId, out double position, TeachPointStore? store = null)
        {
            position = 0;
            var point = Find(nameOrId, store);
            if (point?.AxisPositions == null)
            {
                return false;
            }

            return point.AxisPositions.TryGetValue(axisId, out position);
        }

        public static double[] ResolveAxisTargets(string? nameOrId, IReadOnlyList<int> axisIds, TeachPointStore? store = null)
        {
            if (axisIds == null || axisIds.Count == 0)
            {
                throw new InvalidOperationException("轴列表为空，无法从示教点解析目标。");
            }

            var point = Find(nameOrId, store);
            if (point == null)
            {
                throw new InvalidOperationException($"找不到示教点「{nameOrId}」。请在示教页保存该点后再选。");
            }

            var targets = new double[axisIds.Count];
            for (int i = 0; i < axisIds.Count; i++)
            {
                int axisId = axisIds[i];
                if (!point.AxisPositions.TryGetValue(axisId, out var pos))
                {
                    throw new InvalidOperationException(
                        $"示教点「{point.Name}」没有轴 {axisId} 的坐标。");
                }
                targets[i] = pos;
            }

            return targets;
        }

        public static IReadOnlyList<string> ListNames(TeachPointStore? store = null)
        {
            store ??= DefaultStore;
            if (ReferenceEquals(store, DefaultStore))
            {
                store.Reload();
            }
            return store.GetAllPoints()
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
    }
}
