#nullable enable
using System;

namespace Sophon.Infrastructure.Motion.Stream
{
    /// <summary>
    /// 插补执行器预留流式位置接收接口。
    /// 用于接收上位机软插补或周期规划器输出的周期型值点（设计周期为 1ms，Wave4 对接）。
    /// </summary>
    public interface IPositionStreamSink
    {
        /// <summary>
        /// 开始新的流式插补段。
        /// </summary>
        /// <param name="requestId">请求标识</param>
        /// <param name="axisCount">轴数量</param>
        /// <param name="cycleMs">点位下发周期（毫秒，通常为 1.0ms）</param>
        void Begin(Guid requestId, int axisCount, double cycleMs);

        /// <summary>
        /// 提交一个插补周期的各轴绝对位置目标（物理单位）。
        /// </summary>
        /// <param name="positions">各轴物理位置目标数组</param>
        void Submit(double[] positions);

        /// <summary>
        /// 完成当前流式插补下发（标记数据结束）。
        /// </summary>
        void Complete();

        /// <summary>
        /// 周期位置点被底层控制器消费并接受时触发。
        /// </summary>
        event Action<double[]> PositionAccepted;
    }
}
