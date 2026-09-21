#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 跳转节点。根据目标节点 Id（或节点名称标签）直接跳转，目标节点必须具有汇合点语义。
    ///
    /// 执行上下文保持性：跳转由引擎以显式工作栈压栈实现——
    /// 流程上下文（IFlowContext 及其数据字典）原样传递，调用栈零增长，
    /// 循环变量 / 断点 / 暂停状态均不受影响；跳转引发的环由运行期
    /// MaxExecutionCount 动态环防御兜底（不会耗尽线程栈）。
    /// </summary>
    public class JumpNode : FlowNodeBase
    {
        public override string NodeType => "Jump";
        public override string Name => "跳转目标";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("targetNodeId", "目标节点ID", "string", "", "需要跳转的目标节点 ID（优先按 ID 解析）", isRequired: true),
            new ParameterSchema("targetNodeName", "目标节点名称", "string", "", "备用：按节点名称（标签）解析目标，targetNodeId 缺失时使用")
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            string? targetNodeId = ctx.GetParameter<string>("targetNodeId");
            if (string.IsNullOrWhiteSpace(targetNodeId))
            {
                // 回退：按节点名称（标签）跳转
                targetNodeId = ctx.GetParameter<string>("targetNodeName");
            }

            if (string.IsNullOrWhiteSpace(targetNodeId))
            {
                return Task.FromResult(NodeExecutionResult.Failed(
                    "跳转节点未配置 targetNodeId / targetNodeName 参数"));
            }

            ctx.LogInfo($"执行非拓扑跳转 -> 目标节点: {targetNodeId}");
            return Task.FromResult(NodeExecutionResult.Jump(targetNodeId));
        }
    }
}
