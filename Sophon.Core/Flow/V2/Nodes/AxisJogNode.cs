#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 点动节点。dir=+1/-1，指定速度与运行时间（ms），若 durationMs&lt;=0 则持续运行直到收到取消/停止信号。
    /// </summary>
    public class AxisJogNode : FlowNodeBase
    {
        public override string NodeType => "AxisJog";
        public override string Name => "轴点动";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("axisId", "轴编号", "axis", 0, "目标点动轴 ID", isRequired: true),
            new ParameterSchema("dir", "方向(+1/-1)", "int", 1, "点动方向：+1 正向，-1 负向", isRequired: true),
            new ParameterSchema("speed", "点动速度(mm/s)", "double", 50.0, "点动速度", isRequired: true),
            new ParameterSchema("durationMs", "持续时间(ms)", "int", 0, "点动毫秒数，0表示持续点动直到外部停止或取消")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var motion = ctx.MotionController;
            if (motion == null)
            {
                return NodeExecutionResult.Failed("运动控制器 (IMotionController) 未注入或未就绪");
            }

            int axisId = ctx.GetParameter<int>("axisId", 0);
            int dir = ctx.GetParameter<int>("dir", 1);
            double speed = ctx.GetParameter<double>("speed", 50.0);
            int durationMs = ctx.GetParameter<int>("durationMs", 0);

            ctx.LogInfo($"轴 {axisId} 开始点动: 方向={dir}, 速度={speed}, 持续时间={durationMs}ms");

            try
            {
                motion.Jog(axisId, dir, speed);
            }
            catch (Exception ex)
            {
                return NodeExecutionResult.Failed($"下发 Jog 命令失败: {ex.Message}");
            }

            try
            {
                if (durationMs > 0)
                {
                    await Task.Delay(durationMs, ct);
                    motion.StopMotion(axisId);
                }
                else
                {
                    // 持续点动，等待取消信号
                    var tcs = new TaskCompletionSource<bool>();
                    using var reg = ct.Register(() => tcs.TrySetResult(true));
                    await tcs.Task;
                    motion.StopMotion(axisId);
                }

                ctx.LogInfo($"轴 {axisId} 点动停止完成");
                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                try { motion.StopMotion(axisId); } catch { }
                return NodeExecutionResult.Failed($"轴 {axisId} 点动被取消并停止");
            }
        }
    }
}
