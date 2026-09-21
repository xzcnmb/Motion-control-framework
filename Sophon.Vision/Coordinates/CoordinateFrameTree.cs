using System;
using System.Collections.Generic;
using Sophon.Vision.Calibration;

namespace Sophon.Vision.Coordinates
{
    /// <summary>
    /// 坐标系树（Coordinate Frame Tree）。
    ///
    /// 以「边」的形式注册任意两个坐标系之间的 2D 齐次变换，
    /// 并可查找同一连通分量内任意两个坐标系之间的复合变换（多跳自动复合）。
    ///
    /// 语义约定：<c>SetTransform(A, B, T)</c> 表示 T 把「A 坐标系下的点坐标」映射为
    /// 「B 坐标系下的点坐标」，即 p_B = T * p_A（记作 T_{B←A}）。
    /// 复合规则：T_{C←A} = T_{C←B} * T_{B←A}。
    ///
    /// 由于每条边均可逆，反向查找（如 World -&gt; Pixel）会自动沿逆变换进行：
    /// p_A = T_{B←A}.Inverse() * p_B。
    /// </summary>
    public sealed class CoordinateFrameTree
    {
        /// <summary>线性部分奇异判定容限（与求逆判据一致）。</summary>
        private const double SingularEpsilon = 1e-12;

        // 有向边表：(from, to) -> T_{to←from}
        private readonly Dictionary<(CoordinateFrame From, CoordinateFrame To), Transform2D> _edges = new();

        // 邻接表：frame -> [(neighbor, T_{neighbor←frame}), ...]，反向边以逆变换登记，供 BFS 双向遍历。
        private readonly Dictionary<CoordinateFrame, List<(CoordinateFrame Neighbor, Transform2D Transform)>> _adjacency = new();

        // 已注册过的坐标系集合。
        private readonly HashSet<CoordinateFrame> _frames = new();

        /// <summary>已注册的有向变换边数量。</summary>
        public int EdgeCount => _edges.Count;

        /// <summary>已注册的坐标系集合。</summary>
        public IReadOnlyCollection<CoordinateFrame> RegisteredFrames => _frames;

        /// <summary>
        /// 注册（或覆盖）一条坐标系变换边。
        /// </summary>
        /// <param name="fromFrame">源坐标系</param>
        /// <param name="toFrame">目标坐标系</param>
        /// <param name="transform">T_{to←from}：把 from 坐标映射为 to 坐标的变换</param>
        public void SetTransform(CoordinateFrame fromFrame, CoordinateFrame toFrame, Transform2D transform)
        {
            if (!transform.IsInvertible)
            {
                throw new ArgumentException(
                    "变换线性部分奇异（行列式接近0），无法作为坐标系树的边注册", nameof(transform));
            }

            var key = (fromFrame, toFrame);

            // 覆盖旧边时，先摘除旧邻接项，避免邻接表残留。
            if (_edges.TryGetValue(key, out var _))
            {
                RemoveAdjacencyEntry(fromFrame, toFrame);
            }

            _edges[key] = transform;
            AddAdjacencyEntry(fromFrame, toFrame, transform);
            AddAdjacencyEntry(toFrame, fromFrame, transform.Inverse());

            _frames.Add(fromFrame);
            _frames.Add(toFrame);
        }

        /// <summary>
        /// 移除一条已注册的有向变换边。
        /// </summary>
        /// <returns>边存在并成功移除时返回 true。</returns>
        public bool RemoveTransform(CoordinateFrame fromFrame, CoordinateFrame toFrame)
        {
            var key = (fromFrame, toFrame);
            if (!_edges.Remove(key))
            {
                return false;
            }

            RemoveAdjacencyEntry(fromFrame, toFrame);
            RemoveAdjacencyEntry(toFrame, fromFrame);
            return true;
        }

        /// <summary>
        /// 判断是否存在直接注册的边（不考虑多跳复合）。
        /// </summary>
        public bool HasEdge(CoordinateFrame fromFrame, CoordinateFrame toFrame) =>
            _edges.ContainsKey((fromFrame, toFrame));

        /// <summary>
        /// 清空所有已注册的坐标系与变换边。
        /// </summary>
        public void Clear()
        {
            _edges.Clear();
            _adjacency.Clear();
            _frames.Clear();
        }

        /// <summary>
        /// 注册九点仿射标定结果：T_{World←Pixel}（像素坐标 -&gt; 物理世界坐标）。
        /// 注册后即可通过 <see cref="GetTransform(CoordinateFrame, CoordinateFrame)"/>
        /// 在 PixelFrame 与 WorldFrame 之间双向查找变换。
        /// </summary>
        public void SetCalibration(NinePointCalibration calibration)
        {
            if (calibration == null) throw new ArgumentNullException(nameof(calibration));

            // 九点标定矩阵即 T_{World←Pixel}
            SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame, calibration.ToTransform2D());
        }

        /// <summary>
        /// 查找从 <paramref name="fromFrame"/> 到 <paramref name="toFrame"/> 的复合变换（支持多跳）。
        /// </summary>
        /// <returns>找不到连通路径时返回 false。</returns>
        public bool TryGetTransform(CoordinateFrame fromFrame, CoordinateFrame toFrame, out Transform2D transform)
        {
            // 同一坐标系到自身恒为单位变换（即使尚未注册任何边）。
            if (fromFrame == toFrame)
            {
                transform = Transform2D.Identity;
                return true;
            }

            // BFS：composed[node] = T_{node←source}
            var composed = new Dictionary<CoordinateFrame, Transform2D>
            {
                [fromFrame] = Transform2D.Identity
            };
            var queue = new Queue<CoordinateFrame>();
            queue.Enqueue(fromFrame);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var toCurrent = composed[current];

                if (!_adjacency.TryGetValue(current, out var neighbors))
                {
                    continue;
                }

                foreach (var (neighbor, hop) in neighbors)
                {
                    if (composed.ContainsKey(neighbor))
                    {
                        continue; // 已访问（天然防环）
                    }

                    var toNeighbor = hop * toCurrent;
                    if (neighbor == toFrame)
                    {
                        transform = toNeighbor;
                        return true;
                    }

                    composed[neighbor] = toNeighbor;
                    queue.Enqueue(neighbor);
                }
            }

            transform = default;
            return false;
        }

        /// <summary>
        /// 查找从 <paramref name="fromFrame"/> 到 <paramref name="toFrame"/> 的复合变换（支持多跳）。
        /// </summary>
        /// <exception cref="InvalidOperationException">两个坐标系不在同一连通分量内。</exception>
        public Transform2D GetTransform(CoordinateFrame fromFrame, CoordinateFrame toFrame)
        {
            if (!TryGetTransform(fromFrame, toFrame, out var transform))
            {
                throw new InvalidOperationException(
                    $"坐标系树中不存在从「{CoordinateFrameInfo.GetDescription(fromFrame)}」" +
                    $"到「{CoordinateFrameInfo.GetDescription(toFrame)}」的连通路径，" +
                    "请先用 SetTransform / SetCalibration 注册相应的坐标系变换");
            }

            return transform;
        }

        private void AddAdjacencyEntry(CoordinateFrame owner, CoordinateFrame neighbor, Transform2D transform)
        {
            if (!_adjacency.TryGetValue(owner, out var list))
            {
                list = new List<(CoordinateFrame, Transform2D)>();
                _adjacency[owner] = list;
            }

            list.Add((neighbor, transform));
        }

        private void RemoveAdjacencyEntry(CoordinateFrame owner, CoordinateFrame neighbor)
        {
            if (_adjacency.TryGetValue(owner, out var list))
            {
                list.RemoveAll(e => e.Neighbor == neighbor);
                if (list.Count == 0)
                {
                    _adjacency.Remove(owner);
                }
            }
        }
    }
}
