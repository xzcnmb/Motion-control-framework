#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 流程起始节点。流程唯一入口，无输入端口，无条件从 Out 端口连出。
    /// </summary>
    public class StartNode : FlowNodeBase
    {
        public override string NodeType => "Start";
        public override string Name => "起始";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => Array.Empty<ParameterSchema>();

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            ctx.LogInfo("流程启动");
            return Task.FromResult(NodeExecutionResult.Completed("Out"));
        }

        public override List<FlowPort> GetDefaultPorts()
        {
            return new List<FlowPort>
            {
                new("Out", FlowPortDirection.Out, "Out")
            };
        }
    }
}
