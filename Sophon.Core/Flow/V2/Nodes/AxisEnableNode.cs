#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 轴使能/去使能节点（伺服上电/断电）。
    /// </summary>
    public class AxisEnableNode : FlowNodeBase
    {
        public override string NodeType => "AxisEnable";
        public override string Name => "轴使能/去使能";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("axisId", "轴编号", "axis", 0, "目标轴 ID", isRequired: true),
            new ParameterSchema("enable", "使能状态", "bool", true, "true=使能(伺服上电)，false=去使能(伺服断电)", isRequired: true),
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var motion = ctx.MotionController;
            if (motion == null)
            {
                return Task.FromResult(NodeExecutionResult.Failed("运动控制器 (IMotionController) 未注入或未就绪"));
            }

            int axisId = ctx.GetParameter<int>("axisId", 0);
            bool enable = ctx.GetParameter<bool>("enable", true);

            try
            {
                if (enable)
                {
                    motion.EnableAxis(axisId);
                }
                else
                {
                    motion.DisableAxis(axisId);
                }
                ctx.LogInfo($"轴 {axisId} {(enable ? "使能" : "去使能")}完成");
                return Task.FromResult(NodeExecutionResult.Completed("Out"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(NodeExecutionResult.Failed($"轴 {axisId} {(enable ? "使能" : "去使能")}失败: {ex.Message}"));
            }
        }
    }
}