#nullable enable
namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 节点单步执行结果状态。
    /// </summary>
    public enum NodeStatus
    {
        Completed,
        Failed,
        Skipped
    }

    /// <summary>
    /// 并行分支汇聚（Join）模式。
    /// </summary>
    public enum ParallelJoinMode
    {
        /// <summary>等待全部分支完成（AND 汇合屏障，默认）。</summary>
        All,
        /// <summary>首个分支完成即放行（OR 竞速汇合），其余分支随即取消。</summary>
        Any
    }

    /// <summary>
    /// 节点执行返回值。包含后续分支选择、错误信息及跳转控制。
    /// </summary>
    public class NodeExecutionResult
    {
        /// <summary>执行状态。</summary>
        public NodeStatus Status { get; set; } = NodeStatus.Completed;

        /// <summary>
        /// 指定后续执行的 Out 端口名（如 Branch 节点的 "True" 或 "False"）。
        /// 若为 null，则默认按所有有效的 Out 连线继续流转。
        /// </summary>
        public string? SelectedPortName { get; set; }

        /// <summary>错误详情（若失败）。</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// 跳转目标节点 ID（用于 Jump 节点的非拓扑直跳，目标必须为汇合点语义）。
        /// </summary>
        public string? JumpTargetNodeId { get; set; }

        /// <summary>
        /// Fork-Join 语义标记。为 true 时引擎按结构化并行调度：
        /// 除汇合端口（约定为 "Out"）以外的全部 Out 连线作为并发分支执行，
        /// 分支全部到达 Join 屏障后，才沿汇合端口单次向下续流——
        /// 保证下游汇聚节点只被触发一次，不与分支竞态共享状态。
        /// </summary>
        public bool ForkWithJoinBarrier { get; set; }

        /// <summary>Fork-Join 汇聚模式（默认 All：等待全部分支）。</summary>
        public ParallelJoinMode JoinMode { get; set; } = ParallelJoinMode.All;

        public static NodeExecutionResult Completed(string? selectedPortName = null) =>
            new() { Status = NodeStatus.Completed, SelectedPortName = selectedPortName };

        public static NodeExecutionResult Failed(string errorMessage) =>
            new() { Status = NodeStatus.Failed, ErrorMessage = errorMessage };

        public static NodeExecutionResult Skipped(string? reason = null) =>
            new() { Status = NodeStatus.Skipped, ErrorMessage = reason };

        public static NodeExecutionResult Jump(string targetNodeId) =>
            new() { Status = NodeStatus.Completed, JumpTargetNodeId = targetNodeId };

        /// <summary>
        /// 结构化并行分流（Fork）：分支并发执行 + Join 屏障后经汇合端口单次续流。
        /// </summary>
        /// <param name="joinMode">汇聚模式：All=等全部分支（默认）；Any=首分支竞速放行。</param>
        public static NodeExecutionResult ParallelFork(ParallelJoinMode joinMode = ParallelJoinMode.All) =>
            new() { Status = NodeStatus.Completed, ForkWithJoinBarrier = true, JoinMode = joinMode };
    }
}
