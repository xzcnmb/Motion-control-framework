#nullable enable
namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 节点在图形画布上的坐标位置。
    /// </summary>
    public class FlowPosition
    {
        /// <summary>
        /// X 轴坐标。
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Y 轴坐标。
        /// </summary>
        public double Y { get; set; }

        public FlowPosition() { }

        public FlowPosition(double x, double y)
        {
            X = x;
            Y = y;
        }
    }
}
