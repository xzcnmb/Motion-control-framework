#nullable enable
namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 流程节点运行状态。
    /// </summary>
    public enum FlowNodeState
    {
        /// <summary>未执行 / 空闲</summary>
        Idle,
        /// <summary>正在执行</summary>
        Running,
        /// <summary>执行成功完成</summary>
        Completed,
        /// <summary>执行失败</summary>
        Failed,
        /// <summary>跳过执行</summary>
        Skipped
    }
}
