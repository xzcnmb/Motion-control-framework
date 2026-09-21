#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 自定义流程事件载荷。
    /// </summary>
    public record FlowCustomEvent(string EventName, object? Payload);

    /// <summary>
    /// 事件发布节点。通过 IEventBus 向总线发布自定义事件。
    /// </summary>
    public class EventPublishNode : FlowNodeBase
    {
        public override string NodeType => "EventPublish";
        public override string Name => "发布事件";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("eventName", "事件名称", "string", "", "发布的事件主题或事件名称", isRequired: true),
            new ParameterSchema("payload", "事件载荷", "json", null, "事件附加数据（字符串、对象或 JSON）")
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var bus = ctx.EventBus;
            if (bus == null)
            {
                return Task.FromResult(NodeExecutionResult.Failed("事件总线 (IEventBus) 未注入或未就绪"));
            }

            string? eventName = ctx.GetParameter<string>("eventName");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return Task.FromResult(NodeExecutionResult.Failed("事件发布节点未配置 eventName 参数"));
            }

            ctx.Node.Parameters.TryGetValue("payload", out var payload);

            ctx.LogInfo($"发布事件: '{eventName}', 载荷: {payload}");
            try
            {
                bus.Publish(new FlowCustomEvent(eventName, payload));
                return Task.FromResult(NodeExecutionResult.Completed("Out"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(NodeExecutionResult.Failed($"发布事件失败: {ex.Message}"));
            }
        }
    }
}
