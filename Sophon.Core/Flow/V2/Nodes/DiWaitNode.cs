#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 数字输入等待节点。按虚拟点名等待 DI 达到期望状态。
    /// 【契约铁律】必须带超时上限，禁止无限阻塞；先检查即时状态，未满足则监听 DiChanged 事件。
    /// </summary>
    public class DiWaitNode : FlowNodeBase
    {
        public override string NodeType => "DiWait";
        public override string Name => "等待输入(DI)";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("pointName", "DI点位名称", "point", "", "虚拟输入点名称（如 'VacuumSuck', 'CylinderPush'）", isRequired: true),
            new ParameterSchema("expectedValue", "期望状态", "bool", true, "期望的输入电平 (True/False)", isRequired: true),
            new ParameterSchema("timeoutMs", "等待超时(ms)", "int", 10000, "必须带超时上限，禁止无限阻塞", isRequired: true)
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var io = ctx.IoController;
            if (io == null)
            {
                return NodeExecutionResult.Failed("IO 控制器 (IIoController) 未注入或未就绪");
            }

            string? pointName = ctx.GetParameter<string>("pointName");
            if (string.IsNullOrWhiteSpace(pointName))
            {
                return NodeExecutionResult.Failed("DI 等待节点未配置 pointName 参数");
            }

            bool expectedValue = ctx.GetParameter<bool>("expectedValue", true);
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 10000);
            if (timeoutMs <= 0)
            {
                timeoutMs = 10000; // 强制最低超时保护
            }

            // 1. 先做即时检查
            if (io.ReadDi(pointName) == expectedValue)
            {
                ctx.LogInfo($"DI 点 '{pointName}' 当前已满足期望值 {expectedValue}");
                return NodeExecutionResult.Completed("Out");
            }

            ctx.LogInfo($"等待 DI 点 '{pointName}' -> {expectedValue} (超时上限: {timeoutMs}ms)");

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnDiChanged(string changedPoint, bool newValue)
            {
                if (string.Equals(changedPoint, pointName, StringComparison.OrdinalIgnoreCase) && newValue == expectedValue)
                {
                    tcs.TrySetResult(true);
                }
            }

            io.DiChanged += OnDiChanged;

            // 再次检查防止订阅间隙漏事件
            if (io.ReadDi(pointName) == expectedValue)
            {
                io.DiChanged -= OnDiChanged;
                return NodeExecutionResult.Completed("Out");
            }

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
                    tcs.TrySetException(new TimeoutException($"等待 DI 点 '{pointName}' 达到 {expectedValue} 超时({timeoutMs}ms)"));
                }
            });

            try
            {
                await tcs.Task;
                ctx.LogInfo($"DI 点 '{pointName}' 成功达到期望值 {expectedValue}");
                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Failed($"等待 DI 点 '{pointName}' 被外部取消");
            }
            catch (TimeoutException tex)
            {
                return NodeExecutionResult.Failed(tex.Message);
            }
            finally
            {
                io.DiChanged -= OnDiChanged;
            }
        }
    }
}
