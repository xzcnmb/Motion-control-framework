#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 延时节点。挂起指定毫秒数，支持 CancellationToken 随时取消。
    /// </summary>
    public class DelayNode : FlowNodeBase
    {
        public override string NodeType => "Delay";
        public override string Name => "延时";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("delayMs", "延时时间(ms)", "int", 1000, "等待毫秒数，支持随时取消", isRequired: true)
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            int delayMs = ctx.GetParameter<int>("delayMs", -1);
            if (delayMs < 0)
            {
                delayMs = ctx.GetParameter<int>("DelayTimeMs", 1000);
            }

            ctx.LogInfo($"开始延时 {delayMs} ms");
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct);
            }
            ctx.LogInfo($"延时 {delayMs} ms 完成");

            return NodeExecutionResult.Completed("Out");
        }
    }
}
