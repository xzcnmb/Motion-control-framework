#nullable enable
using System;
using System.Collections.Generic;

namespace Sophon.Core.Teach
{
    /// <summary>
    /// 示教点定义。
    /// </summary>
    public class TeachPoint
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Group { get; set; } = "Default";

        /// <summary>
        /// 各轴物理目标位置：Dictionary&lt;int, double&gt; 轴编号 → 目标物理坐标(mm/deg)。
        /// </summary>
        public Dictionary<int, double> AxisPositions { get; set; } = new();

        /// <summary>
        /// 运行速度 (mm/s 或 deg/s)。
        /// </summary>
        public double Speed { get; set; } = 20.0;

        /// <summary>
        /// 点位描述说明。
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// 创建时间。
        /// </summary>
        public DateTime CreateTime { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 示教点组（构成多点序列/轨迹）。
    /// </summary>
    public class TeachPointGroup
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 点位 ID 有序列表。
        /// </summary>
        public List<string> PointIds { get; set; } = new();

        /// <summary>
        /// 组描述。
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }
}
