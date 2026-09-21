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
    /// 485/232 外设点位写入节点。
    /// 向指定外设点位写入目标工程量（如设定温度 SV、目标扭矩、复位指令），执行范围白名单拦截。
    /// </summary>
    public class DeviceWriteNode : FlowNodeBase
    {
        public override string NodeType => "DeviceWrite";
        public override string Name => "外设点位写入";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("deviceName", "设备名称", "string", "温控器1", "外设标识名称", isRequired: true),
            new ParameterSchema("tagName", "点位名称", "string", "SV", "点位标识（如 SV, TargetTorque）", isRequired: true),
            new ParameterSchema("value", "写入数值", "double", 50.0, "目标工程量数值"),
            new ParameterSchema("valueFromContext", "值取自变量", "string", "", "上下文变量名（存在时优先从该变量读取）")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            string devName = ctx.GetParameter<string>("deviceName", "");
            string tagName = ctx.GetParameter<string>("tagName", "");
            double targetVal = ctx.GetParameter<double>("value", 0.0);
            string varName = ctx.GetParameter<string>("valueFromContext", "");

            if (string.IsNullOrWhiteSpace(devName) || string.IsNullOrWhiteSpace(tagName))
            {
                return NodeExecutionResult.Failed("设备名称和点位名称不能为空");
            }

            if (!string.IsNullOrWhiteSpace(varName))
            {
                var dynVal = ctx.GetVariable<object>(varName);
                if (dynVal != null)
                {
                    targetVal = Convert.ToDouble(dynVal, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            var service = ctx.Services?.GetService(typeof(PeripheralDeviceService)) as PeripheralDeviceService;
            if (service == null)
            {
                return NodeExecutionResult.Failed("外设通信服务 (PeripheralDeviceService) 未在容器中注入");
            }

            // 从服务获取已注册的真实设备档案（包含真实的寄存器地址、缩放与 WriteMin/WriteMax 白名单），严禁捏造虚假配置
            if (!service.TryGetDeviceConfig(devName, out var cfg) || cfg == null)
            {
                return NodeExecutionResult.Failed($"未找到外设 '{devName}' 的配置档案，请先在配置面板中添加该设备");
            }

            ctx.LogInfo($"开始写入外设: 设备='{devName}', 点位='{tagName}', 值={targetVal}");

            var (ok, msg) = await service.WriteTagAsync(cfg, tagName, targetVal, ct);
            if (!ok)
            {
                return NodeExecutionResult.Failed($"外设写入失败: {msg}");
            }

            ctx.LogInfo($"外设写入成功: 设备='{devName}', 点位='{tagName}' -> {targetVal}");
            return NodeExecutionResult.Completed("Out");
        }
    }
}
