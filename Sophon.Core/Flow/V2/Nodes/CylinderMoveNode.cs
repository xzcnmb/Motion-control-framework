#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Device;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 气缸 / 夹爪动作节点。
    /// 驱动气缸伸出/缩回或夹爪夹紧/张开，支持单/双电控防烧脉冲、组互锁与磁性开关到位超时确认。
    /// </summary>
    public class CylinderMoveNode : FlowNodeBase
    {
        public override string NodeType => "CylinderMove";
        public override string Name => "气缸/夹爪控制";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("cylinderName", "气缸名称", "string", "夹爪1", "气缸或夹爪标识名称", isRequired: true),
            new ParameterSchema("target", "目标动作", "enum", 2, "1: Home(缩回/夹紧), 2: Work(伸出/张开)", options: new[] { "Unknown", "Home", "Work" }, isRequired: true),
            new ParameterSchema("valveType", "阀体类型", "enum", 1, "0: 单电控(弹簧自复位), 1: 双电控(双线圈脉冲)", options: new[] { "SingleCoil", "DoubleCoil" }),
            new ParameterSchema("workDo", "工作DO点名", "string", "DO_GRIP_OPEN", "伸出/张开线圈输出点名", isRequired: true),
            new ParameterSchema("homeDo", "复位DO点名", "string", "DO_GRIP_CLOSE", "缩回/夹紧线圈输出点名（双电控必填）"),
            new ParameterSchema("workSensor", "工作到位DI", "string", "DI_GRIP_OPENED", "伸出/张开到位磁性开关"),
            new ParameterSchema("homeSensor", "复位到位DI", "string", "DI_GRIP_CLOSED", "缩回/夹紧到位磁性开关"),
            new ParameterSchema("timeoutMs", "确认超时(ms)", "int", 1500, "磁性开关到位超时阈值"),
            new ParameterSchema("interlockGroup", "互锁组", "string", "", "防机械干涉互锁组名（可选）")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var io = ctx.IoController;
            if (io == null)
            {
                return NodeExecutionResult.Failed("IO 控制器 (IIoController) 未注入或未就绪");
            }

            string cylName = ctx.GetParameter<string>("cylinderName", "气缸1");
            int targetInt = ctx.GetParameter<int>("target", 2);
            var target = (CylinderPosition)targetInt;

            int valveInt = ctx.GetParameter<int>("valveType", 1);
            var valve = (ValveType)valveInt;

            string workDo = ctx.GetParameter<string>("workDo", "");
            string homeDo = ctx.GetParameter<string>("homeDo", "");
            string workSensor = ctx.GetParameter<string>("workSensor", "");
            string homeSensor = ctx.GetParameter<string>("homeSensor", "");
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 1500);
            string interlockGroup = ctx.GetParameter<string>("interlockGroup", "");

            var def = new CylinderDefinition
            {
                Name = cylName,
                Valve = valve,
                WorkDoName = workDo,
                HomeDoName = string.IsNullOrWhiteSpace(homeDo) ? null : homeDo,
                WorkSensorDiName = string.IsNullOrWhiteSpace(workSensor) ? null : workSensor,
                HomeSensorDiName = string.IsNullOrWhiteSpace(homeSensor) ? null : homeSensor,
                ConfirmTimeoutMs = timeoutMs,
                InterlockGroup = string.IsNullOrWhiteSpace(interlockGroup) ? null : interlockGroup
            };

            // 优先从容器解析已注册的单例服务，若无则基于 ctx.IoController 本地构造
            var service = ctx.Services?.GetService(typeof(CylinderService)) as CylinderService
                          ?? new CylinderService(io);

            ctx.LogInfo($"气缸 '{cylName}' 开始执行动作 -> 目标: {target}, 阀型: {valve}, 超时: {timeoutMs}ms");

            var result = await service.MoveToAsync(def, target, ct);

            if (!result.Success)
            {
                return NodeExecutionResult.Failed(result.Message);
            }

            ctx.LogInfo($"气缸 '{cylName}' 动作完成: {result.Message}");
            return NodeExecutionResult.Completed("Out");
        }
    }
}
