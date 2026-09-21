#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 子流程节点。引用另一个 FlowGraph（按名或按 Id），在当前上下文（或隔离上下文）下执行其子图。
    /// </summary>
    public class SubFlowNode : FlowNodeBase
    {
        public override string NodeType => "SubFlow";
        public override string Name => "子流程调用";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("subFlowName", "子流程名称", "string", "", "目标子流程的名称（在 FlowGraphStore 中存储）", isRequired: true),
            new ParameterSchema("subFlowId", "子流程ID", "string", "", "目标子流程的唯一标识符（可选）")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            string? subFlowName = ctx.GetParameter<string>("subFlowName");
            if (string.IsNullOrWhiteSpace(subFlowName))
            {
                return NodeExecutionResult.Failed("子流程节点未配置 subFlowName 参数");
            }

            ctx.LogInfo($"调用子流程: '{subFlowName}'");

            FlowGraph? subGraph = FlowGraphStore.Load(subFlowName);
            if (subGraph == null)
            {
                return NodeExecutionResult.Failed($"无法加载子流程: '{subFlowName}'");
            }

            var subEngine = new FlowEngineV2(
                ctx.MotionController,
                ctx.IoController,
                ctx.EventBus,
                ctx.Services);

            try
            {
                await subEngine.RunAsync(subGraph, ctx.FlowContext, ct);
                ctx.LogInfo($"子流程 '{subFlowName}' 执行成功完成");
                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Failed($"子流程 '{subFlowName}' 被外部取消");
            }
            catch (Exception ex)
            {
                return NodeExecutionResult.Failed($"子流程 '{subFlowName}' 执行失败: {ex.Message}");
            }
        }
    }
}
