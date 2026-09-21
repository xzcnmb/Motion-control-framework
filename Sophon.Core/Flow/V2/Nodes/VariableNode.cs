#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 变量操作节点。支持将值写入流程上下文全局 Data 字典，或在上下文变量之间读取/转移。
    /// </summary>
    public class VariableNode : FlowNodeBase
    {
        public override string NodeType => "Variable";
        public override string Name => "变量操作";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("operation", "操作类型", "enum", "Set", "Set (写入变量) 或 Get (读取/复制变量)", isRequired: true, options: new[] { "Set", "Get" }),
            new ParameterSchema("key", "变量名", "string", "", "上下文 Data 字典中的键名", isRequired: true),
            new ParameterSchema("value", "变量值", "json", null, "写入的值（支持字符串、数值、布尔或 JSON 对象，仅 Set 操作使用）"),
            new ParameterSchema("targetKey", "目标变量名", "string", null, "目标变量名（仅 Get 操作用于变量传递复制时有效）")
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            string operation = ctx.GetParameter<string>("operation", "Set") ?? "Set";
            string? key = ctx.GetParameter<string>("key");

            if (string.IsNullOrWhiteSpace(key))
            {
                return Task.FromResult(NodeExecutionResult.Failed("变量操作节点未配置 key 参数"));
            }

            if (string.Equals(operation, "Set", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Node.Parameters.TryGetValue("value", out var rawValue);
                ctx.SetVariable(key, rawValue);
                ctx.LogInfo($"变量写入: {key} = {rawValue}");
            }
            else if (string.Equals(operation, "Get", StringComparison.OrdinalIgnoreCase))
            {
                var val = ctx.GetVariable<object>(key);
                string? targetKey = ctx.GetParameter<string>("targetKey");
                if (!string.IsNullOrWhiteSpace(targetKey))
                {
                    ctx.SetVariable(targetKey, val);
                    ctx.LogInfo($"变量读取并复制: {key} -> {targetKey} = {val}");
                }
                else
                {
                    ctx.LogInfo($"变量读取: {key} = {val}");
                }
            }
            else
            {
                return Task.FromResult(NodeExecutionResult.Failed($"未知的变量操作类型: {operation}"));
            }

            return Task.FromResult(NodeExecutionResult.Completed("Out"));
        }
    }
}
