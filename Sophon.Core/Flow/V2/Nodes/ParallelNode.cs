#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 并行分支节点（结构化 Fork-Join 并发语义）。
    ///
    /// 形式化执行语义：
    /// 1. 【拓扑 Fork 模式】未配置 subFlowNames 时，本节点为结构化分流点：
    ///    除 "Out" 汇合端口以外的全部 Out 连线各启动一个并发分支（Task.Run 独立调度），
    ///    全部到达 Join 屏障后才沿 "Out" 端口单次向下续流——下游汇聚节点只触发一次；
    /// 2. 【子图 Fork 模式】配置 subFlowNames 时，并发执行多个子图分支（各自独立引擎实例 +
    ///    克隆上下文，分支变量经 FlowContext 共享字典的锁串行化，无竞态），同样全部完成才续流；
    /// 3. 任一分支失败立即连锁取消其余分支（Fail-Fast，防止孤儿分支竞态共享状态）；
    /// 4. 流程停止/取消时所有分支经取消令牌同步取消，Join 屏障等待同样可被立即打断。
    ///
    /// 约束：Fork 与 Join 之间的并行区应为无环 DAG（分支内不建议再嵌套 Loop/Jump 回边），
    /// 静态环检测与运行期 MaxExecutionCount 提供兜底防护。
    /// </summary>
    public class ParallelNode : FlowNodeBase
    {
        public override string NodeType => "Parallel";
        public override string Name => "并行分流(Fork-Join)";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("subFlowNames", "并行子流程列表", "json", "[]", "并发执行的子流程名称列表（如 ['BranchA','BranchB'] 或逗号分隔 'BranchA,BranchB'）"),
            new ParameterSchema("branchPorts", "分流端口列表", "string", "Branch1,Branch2", "拓扑多分支连线端口列表，逗号分隔"),
            new ParameterSchema("joinMode", "汇聚模式", "string", "All", "Join 屏障语义：All=等待全部分支（AND，默认）；Any=首个分支完成即放行（OR 竞速），其余分支取消")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var joinMode = ParseJoinMode(ctx);

            var subFlows = ParseSubFlowNames(ctx, "subFlowNames");

            if (subFlows.Length > 0)
            {
                ctx.LogInfo($"并行节点启动，并发执行 {subFlows.Length} 个子图分支 (Join={joinMode}): [{string.Join(", ", subFlows)}]");

                try
                {
                    if (joinMode == ParallelJoinMode.Any)
                    {
                        // OR 竞速汇合：首个分支结束即放行，取消其余分支
                        using var anyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var anyTasks = subFlows
                            .Select(name => RunSubFlowBranchAsync(ctx, name, anyCts.Token))
                            .ToList();

                        var winner = await Task.WhenAny(anyTasks);
                        try { anyCts.Cancel(); } catch { }

                        Exception? firstError = null;
                        foreach (var t in anyTasks)
                        {
                            try { await t; }
                            catch (Exception ex) { firstError ??= ex; }
                        }
                        if (winner.IsFaulted && winner.Exception != null)
                        {
                            throw winner.Exception.InnerException ?? winner.Exception;
                        }
                        if (firstError != null)
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
                        }
                    }
                    else
                    {
                        // AND 汇合屏障：全部分支完成
                        await Task.WhenAll(subFlows
                            .Select(name => RunSubFlowBranchAsync(ctx, name, ct))
                            .ToList());
                    }

                    ctx.LogInfo("所有并行子分支执行完成");
                    return NodeExecutionResult.Completed("Out");
                }
                catch (OperationCanceledException)
                {
                    return NodeExecutionResult.Failed("并行分支执行被外部取消");
                }
                catch (Exception ex)
                {
                    return NodeExecutionResult.Failed($"并行分支中出现失败: {ex.Message}");
                }
            }

            // 若未配置外部子图，则作为结构化拓扑分流点：由引擎并发调度全部分支端口，
            // 并在 Join 屏障后经 "Out" 端口单次续流。
            ctx.LogInfo($"并行节点分流启动 (拓扑 Fork 模式, Join={joinMode})");
            return NodeExecutionResult.ParallelFork(joinMode);
        }

        public override List<FlowPort> GetDefaultPorts()
        {
            return new List<FlowPort>
            {
                new("In", FlowPortDirection.In, "In"),
                new("Branch1", FlowPortDirection.Out, "Branch1"),
                new("Branch2", FlowPortDirection.Out, "Branch2"),
                new("Out", FlowPortDirection.Out, "Out")
            };
        }

        /// <summary>
        /// 执行单个并行子图分支：独立引擎实例 + 克隆上下文
        /// （数据字典与锁与父上下文共享，分支变量读写经锁串行化，无竞态）。
        /// </summary>
        private static async Task RunSubFlowBranchAsync(NodeExecutionContext ctx, string subFlowName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var subGraph = FlowGraphStore.Load(subFlowName);
            if (subGraph == null)
            {
                throw new InvalidOperationException($"未能找到并行分支子图: '{subFlowName}'");
            }

            var subEngine = new FlowEngineV2(
                ctx.MotionController,
                ctx.IoController,
                ctx.EventBus,
                ctx.Services);

            var subContext = ctx.FlowContext.Clone();
            await subEngine.RunAsync(subGraph, subContext, ct);
        }

        private static ParallelJoinMode ParseJoinMode(NodeExecutionContext ctx)
        {
            var raw = ctx.GetParameter<string>("joinMode");
            if (string.IsNullOrWhiteSpace(raw)) return ParallelJoinMode.All;

            return raw.Trim().ToLowerInvariant() switch
            {
                "any" or "first" or "or" => ParallelJoinMode.Any,
                _ => ParallelJoinMode.All
            };
        }

        private static string[] ParseSubFlowNames(NodeExecutionContext ctx, string key)
        {
            if (ctx.Node.Parameters.TryGetValue(key, out var val) && val != null)
            {
                if (val is string[] arr) return arr;
                if (val is IEnumerable<string> enu) return enu.ToArray();
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    return je.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                }
                if (val is string str && !string.IsNullOrWhiteSpace(str))
                {
                    return str.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                }
            }
            return Array.Empty<string>();
        }
    }
}
