#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 事件等待节点。通过 IEventBus 等待指定名称的事件触发。
    /// 【契约铁律】必须带超时上限，禁止无限阻塞。
    /// </summary>
    public class EventWaitNode : FlowNodeBase
    {
        public override string NodeType => "EventWait";
        public override string Name => "等待事件";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("eventName", "事件名称", "string", "", "等待的事件主题或事件名称", isRequired: true),
            new ParameterSchema("timeoutMs", "等待超时(ms)", "int", 10000, "等待事件触发的超时上限（毫秒），必须带超时", isRequired: true),
            new ParameterSchema("outputKey", "输出变量名", "string", "", "将接收到的事件 Payload 存储至上下文 Data 的变量键名")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var bus = ctx.EventBus;
            if (bus == null)
            {
                return NodeExecutionResult.Failed("事件总线 (IEventBus) 未注入或未就绪");
            }

            string? eventName = ctx.GetParameter<string>("eventName");
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return NodeExecutionResult.Failed("事件等待节点未配置 eventName 参数");
            }

            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 10000);
            if (timeoutMs <= 0)
            {
                timeoutMs = 10000;
            }

            string? outputKey = ctx.GetParameter<string>("outputKey");

            ctx.LogInfo($"开始等待事件: '{eventName}' (超时上限: {timeoutMs}ms)");

            var tcs = new TaskCompletionSource<FlowCustomEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(FlowCustomEvent ev)
            {
                if (string.Equals(ev.EventName, eventName, StringComparison.OrdinalIgnoreCase))
                {
                    tcs.TrySetResult(ev);
                }
            }

            bus.Subscribe((Action<FlowCustomEvent>)Handler);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                }
                else if (timeoutCts.IsCancellationRequested)
                {
                    tcs.TrySetException(new TimeoutException($"等待事件 '{eventName}' 超时({timeoutMs}ms)"));
                }
            });

            try
            {
                var receivedEvent = await tcs.Task;
                ctx.LogInfo($"收到事件 '{eventName}', 载荷: {receivedEvent.Payload}");

                if (!string.IsNullOrWhiteSpace(outputKey))
                {
                    ctx.SetVariable(outputKey, receivedEvent.Payload);
                }

                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Failed($"等待事件 '{eventName}' 被外部取消");
            }
            catch (TimeoutException tex)
            {
                return NodeExecutionResult.Failed(tex.Message);
            }
            finally
            {
                bus.Unsubscribe((Action<FlowCustomEvent>)Handler);
            }
        }
    }
}
