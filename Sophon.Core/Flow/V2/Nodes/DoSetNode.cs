#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 数字输出设置节点。按虚拟点名写入 DO 输出状态。
    /// </summary>
    public class DoSetNode : FlowNodeBase
    {
        public override string NodeType => "DoSet";
        public override string Name => "设置输出(DO)";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("pointName", "DO点位名称", "point", "", "虚拟输出点名称（如 'GreenLight', 'VacuumSuck'）", isRequired: true),
            new ParameterSchema("value", "输出电平值", "bool", true, "输出电平状态 (True/False)", isRequired: true)
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var io = ctx.IoController;
            if (io == null)
            {
                return Task.FromResult(NodeExecutionResult.Failed("IO 控制器 (IIoController) 未注入或未就绪"));
            }

            string? pointName = ctx.GetParameter<string>("pointName");
            if (string.IsNullOrWhiteSpace(pointName))
            {
                return Task.FromResult(NodeExecutionResult.Failed("DO 设置节点未配置 pointName 参数"));
            }

            bool value = ctx.GetParameter<bool>("value", true);

            ctx.LogInfo($"设置 DO 点 '{pointName}' -> {value}");
            try
            {
                io.WriteDo(pointName, value);
                return Task.FromResult(NodeExecutionResult.Completed("Out"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(NodeExecutionResult.Failed($"写入 DO 点 '{pointName}' 失败: {ex.Message}"));
            }
        }
    }
}
