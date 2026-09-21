#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowV2_ModelTests
    {
        [Fact]
        public void Validate_缺Start节点_检出错误()
        {
            var graph = new FlowGraph
            {
                FlowName = "缺Start测试",
                Nodes = new List<FlowNode>
                {
                    new FlowNode("Delay", "延时1")
                    {
                        Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") }
                    }
                }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("缺失 Start 节点"));
        }

        [Fact]
        public void Validate_多个Start节点_检出错误()
        {
            var graph = new FlowGraph
            {
                FlowName = "多Start测试",
                Nodes = new List<FlowNode>
                {
                    new FlowNode("Start", "起始1"),
                    new FlowNode("Start", "起始2")
                }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("存在多个 Start 节点"));
        }

        [Fact]
        public void Validate_悬空连接_源或目标节点不存在_检出错误()
        {
            var start = new FlowNode("Start", "起始");
            start.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var graph = new FlowGraph
            {
                FlowName = "悬空测试",
                Nodes = new List<FlowNode> { start },
                Connections = new List<FlowConnection>
                {
                    new FlowConnection(start.Id, "Out", "NonExistentNodeId", "In")
                }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("目标节点不存在"));
        }

        [Fact]
        public void Validate_悬空连接_端口不存在_检出错误()
        {
            var start = new FlowNode("Start", "起始");
            start.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var delay = new FlowNode("Delay", "延时1");
            delay.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));

            var graph = new FlowGraph
            {
                FlowName = "端口不存在测试",
                Nodes = new List<FlowNode> { start, delay },
                Connections = new List<FlowConnection>
                {
                    new FlowConnection(start.Id, "NonExistentPort", delay.Id, "In")
                }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("无端口 'NonExistentPort'"));
        }

        [Fact]
        public void Validate_非Start节点无入边_检出错误()
        {
            var start = new FlowNode("Start", "起始");
            start.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var delay = new FlowNode("Delay", "孤立节点");
            delay.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));

            var graph = new FlowGraph
            {
                FlowName = "孤立节点测试",
                Nodes = new List<FlowNode> { start, delay }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("无任何入边"));
        }

        [Fact]
        public void Validate_静态环检测_检出回路路径()
        {
            var start = new FlowNode("Start", "起始");
            start.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var nodeA = new FlowNode("Delay", "NodeA");
            nodeA.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            nodeA.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var nodeB = new FlowNode("Delay", "NodeB");
            nodeB.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            nodeB.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            // 构造拓扑: Start -> A -> B -> A (回路)
            var graph = new FlowGraph
            {
                FlowName = "环测试",
                Nodes = new List<FlowNode> { start, nodeA, nodeB },
                Connections = new List<FlowConnection>
                {
                    new FlowConnection(start.Id, "Out", nodeA.Id, "In"),
                    new FlowConnection(nodeA.Id, "Out", nodeB.Id, "In"),
                    new FlowConnection(nodeB.Id, "Out", nodeA.Id, "In")
                }
            };

            bool valid = graph.Validate(out var errors);
            Assert.False(valid);
            Assert.Contains(errors, e => e.Contains("静态环检测失败") && e.Contains("NodeA") && e.Contains("NodeB"));

            var cycles = graph.DetectCycles();
            Assert.NotEmpty(cycles);
        }

        [Fact]
        public void ToJson_FromJson_序列化反序列化无损()
        {
            var start = new FlowNode("Start", "起始")
            {
                Position = new FlowPosition(100, 200),
                Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") }
            };

            var delay = new FlowNode("Delay", "延时500ms")
            {
                Position = new FlowPosition(300, 200),
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 500 }
            };

            var graph = new FlowGraph
            {
                FlowName = "序列化测试",
                Version = 2,
                Nodes = new List<FlowNode> { start, delay },
                Connections = new List<FlowConnection>
                {
                    new FlowConnection(start.Id, "Out", delay.Id, "In")
                }
            };

            string json = graph.ToJson();
            Assert.False(string.IsNullOrWhiteSpace(json));

            var restored = FlowGraph.FromJson(json);
            Assert.NotNull(restored);
            Assert.Equal(2, restored!.Version);
            Assert.Equal("序列化测试", restored.FlowName);
            Assert.Equal(2, restored.Nodes.Count);
            Assert.Single(restored.Connections);

            var restoredStart = restored.Nodes.First(n => n.NodeType == "Start");
            Assert.Equal(100, restoredStart.Position.X);
            Assert.Equal(200, restoredStart.Position.Y);

            var restoredDelay = restored.Nodes.First(n => n.NodeType == "Delay");
            Assert.True(restoredDelay.Parameters.ContainsKey("delayMs"));
        }
    }
}
