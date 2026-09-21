#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 单轴回零节点。下发 Home 命令后订阅并 await AxisDone 事件，完成后 IsAxisHomed 置位。
    /// </summary>
    public class AxisHomeNode : FlowNodeBase
    {
        public override string NodeType => "AxisHome";
        public override string Name => "单轴回零";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("axisId", "轴编号", "axis", 0, "回零轴 ID", isRequired: true),
            new ParameterSchema("mode", "回零模式", "enum", (int)HomingMode.OriginSignal, "0:限位开关, 1:原点信号, 2:Z相, 3:当前位置定零", options: new[] { "LimitSwitch", "OriginSignal", "ZPhase", "CurrentPosition" }),
            new ParameterSchema("dir", "回零方向", "enum", (int)HomeDirection.Negative, "回零搜寻方向：0 正向，1 负向", options: new[] { "Positive", "Negative" }),
            new ParameterSchema("speed", "回零速度(mm/s)", "double", 20.0, "回零搜寻速度", isRequired: true),
            new ParameterSchema("timeoutMs", "回零超时(ms)", "int", 60000, "回零等待超时时间，默认60秒")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var motion = ctx.MotionController;
            if (motion == null)
            {
                return NodeExecutionResult.Failed("运动控制器 (IMotionController) 未注入或未就绪");
            }

            int axisId = ctx.GetParameter<int>("axisId", 0);
            int modeInt = ctx.GetParameter<int>("mode", (int)HomingMode.OriginSignal);
            int dirInt = ctx.GetParameter<int>("dir", (int)HomeDirection.Negative);
            double speed = ctx.GetParameter<double>("speed", 20.0);
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 60000);

            var mode = (HomingMode)modeInt;
            var dir = (HomeDirection)dirInt;

            ctx.LogInfo($"轴 {axisId} 开始回零: 模式={mode}, 方向={dir}, 速度={speed}, 超时={timeoutMs}ms");

            // 工业惯例：回零隐含使能。未使能时先自动使能，避免流程因忘写使能节点而中断。
            if (!motion.IsAxisEnabled(axisId))
            {
                ctx.LogInfo($"轴 {axisId} 未使能，自动使能后回零");
                try { motion.EnableAxis(axisId); }
                catch (Exception ex) { return NodeExecutionResult.Failed($"轴 {axisId} 自动使能失败: {ex.Message}"); }
            }

            try
            {
                var doneArgs = await motion
                    .HomeAsync(axisId, mode, dir, speed, ct)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);

                if (!doneArgs.Success)
                {
                    return NodeExecutionResult.Failed($"轴 {axisId} 回零失败: {doneArgs.Reason}");
                }

                ctx.LogInfo($"轴 {axisId} 回零完成");
                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                try { motion.Abort(axisId); } catch { }
                return NodeExecutionResult.Failed($"轴 {axisId} 回零被外部取消");
            }
            catch (TimeoutException)
            {
                try { motion.StopMotion(axisId); } catch { }
                return NodeExecutionResult.Failed($"轴 {axisId} 回零未在 {timeoutMs}ms 内完成");
            }
        }
    }
}
