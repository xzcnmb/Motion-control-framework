#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 条件分支节点。根据上下文变量判真或条件表达式计算结果，
    /// 分流输出至 "True" 或 "False" Out 端口。
    /// </summary>
    public class BranchNode : FlowNodeBase
    {
        public override string NodeType => "Branch";
        public override string Name => "条件分支";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("conditionKey", "条件变量名", "string", "", "上下文 Data 中的布尔或数值变量名", isRequired: true),
            new ParameterSchema("expectedValue", "期望值", "string", "true", "用于匹配的值（布尔 true/false 或数值/字符串）")
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            string? conditionKey = ctx.GetParameter<string>("conditionKey");
            string expectedValueStr = ctx.GetParameter<string>("expectedValue", "true") ?? "true";

            bool result = false;
            if (!string.IsNullOrWhiteSpace(conditionKey))
            {
                var val = ctx.GetVariable<object>(conditionKey);
                if (val is bool bVal)
                {
                    if (bool.TryParse(expectedValueStr, out var expB))
                    {
                        result = (bVal == expB);
                    }
                    else
                    {
                        result = bVal;
                    }
                }
                else if (val is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.True) result = bool.Parse(expectedValueStr);
                    else if (je.ValueKind == JsonValueKind.False) result = !bool.Parse(expectedValueStr);
                    else result = string.Equals(je.ToString(), expectedValueStr, StringComparison.OrdinalIgnoreCase);
                }
                else if (val != null)
                {
                    result = string.Equals(val.ToString(), expectedValueStr, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    result = string.Equals("null", expectedValueStr, StringComparison.OrdinalIgnoreCase);
                }
            }

            string selectedPort = result ? "True" : "False";
            ctx.LogInfo($"条件分支判定: 变量 '{conditionKey}' 结果为 {result} -> 选择端口 '{selectedPort}'");

            return Task.FromResult(NodeExecutionResult.Completed(selectedPort));
        }

        public override List<FlowPort> GetDefaultPorts()
        {
            return new List<FlowPort>
            {
                new("In", FlowPortDirection.In, "In"),
                new("True", FlowPortDirection.Out, "True"),
                new("False", FlowPortDirection.Out, "False")
            };
        }
    }
}
