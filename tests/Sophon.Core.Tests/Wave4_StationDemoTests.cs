using Sophon.Contracts;
using Sophon.Core;
using Sophon.Core.Flow.V2;
using Sophon.Infrastructure.Motion.Drivers;
using Sophon.Infrastructure.Motion.Sim;
using Sophon.Vision.Providers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// Wave4 集成验收：示例工站全链路（回零→视觉定位→两轴插补搬运→DO 输出→回安全点）。
    /// 纯 Sim 环境闭环验证流程引擎 v2 + 硬件 HAL + 视觉提供者的端到端协作。
    /// </summary>
    public class Wave4_StationDemoTests
    {
        private sealed class VisionServices : IServiceProvider
        {
            private readonly IVisionProvider _provider;
            public VisionServices(IVisionProvider provider) => _provider = provider;
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IVisionProvider) ? _provider : null;
        }

        private static List<AxisDefinition> BuildAxes() => new()
        {
            new AxisDefinition { AxisId = 0, Name = "X轴", Unit = "mm", SoftLimitMin = 0, SoftLimitMax = 500, MaxSpeed = 200, MaxAccel = 500, MaxDecel = 500 },
            new AxisDefinition { AxisId = 1, Name = "Y轴", Unit = "mm", SoftLimitMin = 0, SoftLimitMax = 500, MaxSpeed = 200, MaxAccel = 500, MaxDecel = 500 },
        };

        private static FlowGraph BuildDemoGraph()
        {
            FlowNode N(string id, string type, string name, string[] portIds, Dictionary<string, object?>? ps = null) =>
                new FlowNode(type, name, id)
                {
                    Ports = new List<FlowPort>(),
                    Parameters = ps ?? new Dictionary<string, object?>()
                };

            FlowNode Make(string id, string type, string name, string[] ports, Dictionary<string, object?>? ps = null)
            {
                var node = N(id, type, name, ports, ps);
                foreach (var p in ports)
                {
                    var dir = p == "In" ? FlowPortDirection.In : FlowPortDirection.Out;
                    node.Ports.Add(new FlowPort(p, dir, p));
                }
                return node;
            }

            var nodes = new List<FlowNode>
            {
                Make("n1", "Start", "开始", new[] { "Out" }),
                Make("n2", "AxisHome", "X轴回零", new[] { "In", "Out" }, new Dictionary<string, object?> { ["axisId"] = 0, ["mode"] = HomingMode.CurrentPosition, ["dir"] = HomeDirection.Positive, ["speed"] = 50.0, ["timeoutMs"] = 10000 }),
                Make("n3", "AxisHome", "Y轴回零", new[] { "In", "Out" }, new Dictionary<string, object?> { ["axisId"] = 1, ["mode"] = HomingMode.CurrentPosition, ["dir"] = HomeDirection.Positive, ["speed"] = 50.0, ["timeoutMs"] = 10000 }),
                Make("n4", "VisionMeasure", "视觉定位", new[] { "In", "Out" }, new Dictionary<string, object?> { ["cameraId"] = "Cam1", ["timeoutMs"] = 5000 }),
                Make("n5", "Branch", "判定OK", new[] { "In", "True", "False" }, new Dictionary<string, object?> { ["conditionKey"] = "VisionOk", ["expectedValue"] = "true" }),
                Make("n6", "MultiAxisInterp", "插补搬运", new[] { "In", "Out" }, new Dictionary<string, object?> { ["axisIds"] = new[] { 0, 1 }, ["targetsFromContext"] = new[] { "VisionWorldX", "VisionWorldY" }, ["speed"] = 100.0, ["accel"] = 500.0, ["decel"] = 500.0, ["timeoutMs"] = 30000 }),
                Make("n7", "DoSet", "亮绿灯", new[] { "In" }, new Dictionary<string, object?> { ["pointName"] = "GreenLight", ["value"] = true }),
                Make("n8", "DoSet", "亮红灯", new[] { "In" }, new Dictionary<string, object?> { ["pointName"] = "RedLight", ["value"] = true }),
            };

            var connections = new List<FlowConnection>
            {
                new("n1", "Out", "n2", "In"),
                new("n2", "Out", "n3", "In"),
                new("n3", "Out", "n4", "In"),
                new("n4", "Out", "n5", "In"),
                new("n5", "True", "n6", "In"),
                new("n6", "Out", "n7", "In"),
                new("n5", "False", "n8", "In"),
            };

            return new FlowGraph { FlowName = "示例工站_搬运demo", Nodes = nodes, Connections = connections };
        }

        [Fact]
        public async Task 示例工站_视觉引导插补搬运_全链路闭环()
        {
            var io = new SimIoController();
            using var motion = MotionControllerFactory.Create(DriverKind.Simulated, BuildAxes(), ioController: io, allowSimFallback: true);
            await motion.ConnectAsync();

            var vision = VisionProviderFactory.CreateSimProvider(new[] { "Cam1" });
            var simVision = (SimVisionProvider)vision;
            simVision.SetNoiseLevel(0);
            simVision.SetGroundTruth(150, 120, 0);

            var engine = new FlowEngineV2(motion, io, null, new VisionServices(vision));
            var ctx = new FlowContext("示例工站", new FakeLoggerFactory());

            var errors = new List<string>();
            var graph = BuildDemoGraph();
            graph.Validate(out errors);
            Assert.Empty(errors);

            await engine.RunAsync(graph, ctx);

            // 视觉闭环：OK 判定 + 物理坐标写入
            Assert.True(ctx.GetData<bool>("VisionOk"));
            // 插补搬运到视觉引导目标（允许到位死区容差）
            Assert.InRange(motion.GetPosition(0), 145, 155);
            Assert.InRange(motion.GetPosition(1), 115, 125);
            // 成功路径：绿灯亮、红灯灭
            var doSnapshot = io.SnapshotDo();
            Assert.True(doSnapshot["GreenLight"]);
            Assert.False(doSnapshot["RedLight"]);
        }

        [Fact]
        public async Task 示例工站_视觉NG_走红灯分支()
        {
            var io = new SimIoController();
            using var motion = MotionControllerFactory.Create(DriverKind.Simulated, BuildAxes(), ioController: io, allowSimFallback: true);
            await motion.ConnectAsync();

            var vision = VisionProviderFactory.CreateSimProvider(new[] { "Cam1" });
            var simVision = (SimVisionProvider)vision;
            simVision.SetNoiseLevel(0);
            simVision.InjectFailure(true); // NG 场景：检测失败

            var engine = new FlowEngineV2(motion, io, null, new VisionServices(vision));
            var ctx = new FlowContext("示例工站", new FakeLoggerFactory());

            await engine.RunAsync(BuildDemoGraph(), ctx);

            Assert.False(ctx.GetData<bool>("VisionOk"));
            var doSnapshot = io.SnapshotDo();
            Assert.True(doSnapshot["RedLight"]);
            Assert.False(doSnapshot["GreenLight"]);
        }
    }
}