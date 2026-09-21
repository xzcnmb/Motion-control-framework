#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowV2_MigratorTests
    {
        private const string V1Sample = @"[
            { ""StepName"": ""延时1"", ""__type"": ""FlowStep_Delay"", ""_delayTime_ms"": 20 },
            { ""StepName"": ""回零X轴"", ""__type"": ""FlowStep_AxisHome"" },
            { ""StepName"": ""吸真空"", ""__type"": ""FlowStep_DoSet"", ""pointName"": ""Vacuum"", ""value"": true }
        ]";

        private const string V2Sample = @"{ ""version"": 2, ""flowName"": ""demo"", ""nodes"": [] }";

        [Fact]
        public void MigrateV1ToFlowGraph_转换链式结构与NodeType映射()
        {
            var graph = FlowJsonMigrator.MigrateV1ToFlowGraph(V1Sample, "完整迁移测试");

            Assert.NotNull(graph);
            Assert.Equal(2, graph!.Version);
            Assert.Equal("完整迁移测试", graph.FlowName);

            // 包含 1 个 Start 节点 + 3 个业务节点 = 4 个节点
            Assert.Equal(4, graph.Nodes.Count);
            Assert.Equal("Start", graph.Nodes[0].NodeType);
            Assert.Equal("Delay", graph.Nodes[1].NodeType);
            Assert.Equal("AxisHome", graph.Nodes[2].NodeType);
            Assert.Equal("DoSet", graph.Nodes[3].NodeType);

            Assert.Equal(new[] { "起始", "延时1", "回零X轴", "吸真空" }, graph.Nodes.Select(n => n.Name).ToArray());

            // 线性连接：3 条连线
            Assert.Equal(3, graph.Connections.Count);
            Assert.Equal(graph.Nodes[0].Id, graph.Connections[0].FromNodeId);
            Assert.Equal(graph.Nodes[1].Id, graph.Connections[0].ToNodeId);

            Assert.Equal(graph.Nodes[1].Id, graph.Connections[1].FromNodeId);
            Assert.Equal(graph.Nodes[2].Id, graph.Connections[1].ToNodeId);

            Assert.Equal(graph.Nodes[2].Id, graph.Connections[2].FromNodeId);
            Assert.Equal(graph.Nodes[3].Id, graph.Connections[2].ToNodeId);

            // 属性映射：_delayTime_ms -> delayMs
            Assert.True(graph.Nodes[1].Parameters.ContainsKey("delayMs"));
            Assert.Equal(20, Convert.ToInt32(graph.Nodes[1].Parameters["delayMs"]));

            // 通过图模型合法性校验
            bool valid = graph.Validate(out var errors);
            Assert.True(valid, string.Join("; ", errors));
        }

        [Fact]
        public async Task MigrateV1ToFlowGraph_迁移后的流程可在引擎中正常执行()
        {
            const string simpleV1 = @"[
                { ""StepName"": ""延时1"", ""__type"": ""FlowStep_Delay"", ""_delayTime_ms"": 10 },
                { ""StepName"": ""延时2"", ""__type"": ""FlowStep_Delay"", ""_delayTime_ms"": 10 }
            ]";

            var graph = FlowJsonMigrator.MigrateV1ToFlowGraph(simpleV1, "执行迁移流程测试");
            Assert.NotNull(graph);

            var engine = new FlowEngineV2();
            var ctx = new FlowContext("执行迁移流程测试", new FakeLoggerFactory());

            int completedCount = 0;
            engine.NodeStateChanged += change =>
            {
                if (change.State == FlowNodeState.Completed)
                {
                    completedCount++;
                }
            };

            await engine.RunAsync(graph!, ctx);

            // Start + Delay1 + Delay2 = 3 个节点完成
            Assert.Equal(3, completedCount);
        }

        [Fact]
        public void MigrateV1ToFlowGraph_非V1返回空()
        {
            Assert.Null(FlowJsonMigrator.MigrateV1ToFlowGraph(V2Sample, "test"));
            Assert.Null(FlowJsonMigrator.MigrateV1ToFlowGraph("{}", "test"));
        }

        [Fact]
        public void MigrateV1ToFlowGraph_缺失StepName抛异常()
        {
            const string broken = @"[ { ""foo"": 1 } ]";
            Assert.Throws<InvalidOperationException>(() =>
                FlowJsonMigrator.MigrateV1ToFlowGraph(broken, "坏样本"));
        }
    }
}
