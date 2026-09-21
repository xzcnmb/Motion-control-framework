#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Core.Device;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 485/232 外设点位读取节点。
    /// 从外设服务读取指定设备点位的当前工程量数值，并写入流程上下文变量。
    /// </summary>
    public class DeviceReadNode : FlowNodeBase
    {
        public override string NodeType => "DeviceRead";
        public override string Name => "外设点位读取";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("deviceName", "设备名称", "string", "温控器1", "外设标识名称", isRequired: true),
            new ParameterSchema("tagName", "点位名称", "string", "PV", "点位标识（如 PV, Torque, Weight）", isRequired: true),
            new ParameterSchema("variableName", "输出变量名", "string", "CurrentTemperature", "写入上下文的目标变量名", isRequired: true)
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            string devName = ctx.GetParameter<string>("deviceName", "");
            string tagName = ctx.GetParameter<string>("tagName", "");
            string varName = ctx.GetParameter<string>("variableName", "DeviceValue");

            if (string.IsNullOrWhiteSpace(devName) || string.IsNullOrWhiteSpace(tagName))
            {
                return Task.FromResult(NodeExecutionResult.Failed("设备名称和点位名称不能为空"));
            }

            var service = ctx.Services?.GetService(typeof(PeripheralDeviceService)) as PeripheralDeviceService;
            if (service == null)
            {
                return Task.FromResult(NodeExecutionResult.Failed("外设通信服务 (PeripheralDeviceService) 未在容器中注入"));
            }

            if (service.TryGetTagValue(devName, tagName, out double val))
            {
                ctx.SetVariable(varName, val);
                ctx.LogInfo($"外设读取成功: 设备='{devName}', 点位='{tagName}', 值={val} -> 变量='{varName}'");
                return Task.FromResult(NodeExecutionResult.Completed("Out"));
            }

            return Task.FromResult(NodeExecutionResult.Failed($"未采集到设备 '{devName}' 的点位 '{tagName}' 数据"));
        }
    }
}
