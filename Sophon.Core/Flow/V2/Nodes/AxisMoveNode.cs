#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Teach;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 单轴绝对定位节点。经 IMotionController.MoveAbs 下发命令，
    /// 严格订阅并 await 匹配 RequestId 的 AxisDone 事件判定到位（严禁轮询 GetPosition），支持配置超时与取消联动 Abort。
    /// 完成语义严格映射 PLCopen <see cref="CommandCompletionStatus"/>：
    /// Done -> Completed("Out")；CommandAborted -> Failed("命令被中止 (CommandAborted)")；Error -> Failed("硬件故障/限位错误 (Error)")。
    /// </summary>
    public class AxisMoveNode : FlowNodeBase
    {
        public override string NodeType => "AxisMove";
        public override string Name => "单轴绝对定位";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("axisId", "轴编号", "axis", 0, "目标运动轴 ID", isRequired: true),
            new ParameterSchema("target", "目标物理位置(mm/deg)", "double", 0.0, "目标物理坐标"),
            new ParameterSchema("pointName", "点位名称", "point", "", "示教点名称（可选，若提供且 target 未设定时优先从点位表加载）"),
            new ParameterSchema("speed", "运行速度(mm/s)", "double", 100.0, "定位最大速度", isRequired: true),
            new ParameterSchema("accel", "加速度(mm/s²)", "double", 500.0, "加速度", isRequired: true),
            new ParameterSchema("decel", "减速度(mm/s²)", "double", 500.0, "减速度", isRequired: true),
            new ParameterSchema("jerk", "加加速度(mm/s³)", "double", 0.0, "S曲线平滑参数，默认0为梯形"),
            new ParameterSchema("timeoutMs", "到位超时(ms)", "int", 30000, "等待 AxisDone 到位事件的超时上限，默认30秒")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var motion = ctx.MotionController;
            if (motion == null)
            {
                return NodeExecutionResult.Failed("运动控制器 (IMotionController) 未注入或未就绪");
            }

            int axisId = ctx.GetParameter<int>("axisId", 0);
            double target = ctx.GetParameter<double>("target", 0.0);
            string? pointName = ctx.GetParameter<string>("pointName");
            double speed = ctx.GetParameter<double>("speed", 100.0);
            double accel = ctx.GetParameter<double>("accel", 500.0);
            double decel = ctx.GetParameter<double>("decel", 500.0);
            double jerk = ctx.GetParameter<double>("jerk", 0.0);
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 30000);

            if (!string.IsNullOrWhiteSpace(pointName))
            {
                if (!TeachPointLookup.TryGetAxisPosition(pointName, axisId, out var taught))
                {
                    return NodeExecutionResult.Failed($"找不到示教点「{pointName}」或其中没有轴 {axisId} 的坐标");
                }
                target = taught;
                ctx.LogInfo($"轴 {axisId} 目标取自示教点「{pointName}」= {target}");
            }

            ctx.LogInfo($"轴 {axisId} 开始绝对运动 -> 目标: {target}, 速度: {speed}, 超时: {timeoutMs}ms");

            try
            {
                // 异步完成回报 API：快速失败也会以 Success=false 完成任务，无订阅竞态。
                // WaitAsync 的时限独占超时判定；外部取消经 ct 传播为 OperationCanceledException，两者可区分。
                var doneArgs = await motion
                    .MoveAbsAsync(axisId, target, speed, accel, decel, jerk, ct)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);

                // PLCopen 完成状态精确映射：Done=正常到位；CommandAborted=被叫停/抢占；Error=故障/限位
                if (doneArgs.Status == CommandCompletionStatus.Done && doneArgs.Success)
                {
                    ctx.LogInfo($"轴 {axisId} 绝对运动到位成功 (Done)");
                    return NodeExecutionResult.Completed("Out");
                }

                ctx.LogError($"轴 {axisId} 绝对运动未成功完成: {DescribeCompletionStatus(doneArgs)}");
                return NodeExecutionResult.Failed($"轴 {axisId} 运动未成功完成: {DescribeCompletionStatus(doneArgs)}");
            }
            catch (OperationCanceledException)
            {
                try { motion.Abort(axisId); } catch { }
                var msg = $"轴 {axisId} 运动被取消/中止 (CommandAborted)";
                ctx.LogError(msg);
                return NodeExecutionResult.Failed(msg);
            }
            catch (TimeoutException)
            {
                try { motion.StopMotion(axisId); } catch { }
                var msg = $"轴 {axisId} 绝对运动未在 {timeoutMs}ms 内到位 (到位超时)";
                ctx.LogError(msg);
                return NodeExecutionResult.Failed(msg);
            }
        }

        /// <summary>
        /// PLCopen 完成状态文本化（供日志与失败消息使用，与 <see cref="CommandCompletionStatus"/> 一一对应）。
        /// </summary>
        internal static string DescribeCompletionStatus(AxisDoneArgs doneArgs) => doneArgs.Status switch
        {
            CommandCompletionStatus.Done => $"命令被中止 (CommandAborted): {doneArgs.Reason}",
            CommandCompletionStatus.CommandAborted => $"命令被中止 (CommandAborted): {doneArgs.Reason}",
            CommandCompletionStatus.Error => $"硬件故障/限位错误 (Error): {doneArgs.Reason}",
            _ => $"未知完成状态 ({doneArgs.Status}): {doneArgs.Reason}"
        };
    }
}
