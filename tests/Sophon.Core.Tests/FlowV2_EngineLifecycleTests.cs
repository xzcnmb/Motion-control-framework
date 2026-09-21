#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowV2_EngineLifecycleTests
    {
        [Fact]
        public async Task 线性链执行顺序正确()
        {
            var executedNodes = new List<string>();

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var d1 = new FlowNode("Delay", "D1")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };
            var d2 = new FlowNode("Delay", "D2")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };

            var graph = new FlowGraph
            {
                FlowName = "顺序测试",
                Nodes = new List<FlowNode> { start, d1, d2 },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", d1.Id, "In"),
                    new(d1.Id, "Out", d2.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            engine.NodeStateChanged += change =>
            {
                if (change.State == FlowNodeState.Completed)
                {
                    executedNodes.Add(change.NodeName);
                }
            };

            var ctx = new FlowContext("顺序测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Equal(new[] { "起始", "D1", "D2" }, executedNodes);
        }

        [Fact]
        public async Task Delay取消即停()
        {
            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var d1 = new FlowNode("Delay", "LongDelay")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10000 }
            };

            var graph = new FlowGraph
            {
                FlowName = "取消测试",
                Nodes = new List<FlowNode> { start, d1 },
                Connections = new List<FlowConnection> { new(start.Id, "Out", d1.Id, "In") }
            };

            var engine = new FlowEngineV2();
            var ctx = new FlowContext("取消测试", new FakeLoggerFactory());

            using var cts = new CancellationTokenSource();
            var runTask = engine.RunAsync(graph, ctx, cts.Token);

            await Task.Delay(50);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask);
        }

        [Fact]
        public async Task 节点状态事件发布正确()
        {
            var states = new List<FlowNodeState>();

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var d1 = new FlowNode("Delay", "D1")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 5 }
            };

            var graph = new FlowGraph
            {
                FlowName = "状态事件测试",
                Nodes = new List<FlowNode> { start, d1 },
                Connections = new List<FlowConnection> { new(start.Id, "Out", d1.Id, "In") }
            };

            var engine = new FlowEngineV2();
            engine.NodeStateChanged += change =>
            {
                if (change.NodeName == "D1")
                {
                    states.Add(change.State);
                }
            };

            var ctx = new FlowContext("状态事件测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Contains(FlowNodeState.Idle, states);
            Assert.Contains(FlowNodeState.Running, states);
            Assert.Contains(FlowNodeState.Completed, states);
        }

        [Fact]
        public async Task 暂停与恢复_正常挂起后继续完成()
        {
            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var d1 = new FlowNode("Delay", "D1")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };
            var d2 = new FlowNode("Delay", "D2")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };

            var graph = new FlowGraph
            {
                FlowName = "暂停恢复测试",
                Nodes = new List<FlowNode> { start, d1, d2 },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", d1.Id, "In"),
                    new(d1.Id, "Out", d2.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            var ctx = new FlowContext("暂停恢复测试", new FakeLoggerFactory());

            bool d2Started = false;
            engine.NodeStateChanged += change =>
            {
                if (change.NodeName == "D1" && change.State == FlowNodeState.Completed)
                {
                    _ = engine.PauseAsync();
                }
                if (change.NodeName == "D2" && change.State == FlowNodeState.Running)
                {
                    d2Started = true;
                }
            };

            var runTask = engine.RunAsync(graph, ctx);

            // 等待一段时间，确保暂停在 D1 完成之后、D2 调度之前
            await Task.Delay(100);
            Assert.True(engine.IsPaused);
            Assert.False(d2Started);

            // 恢复执行
            await engine.ResumeAsync();
            await runTask;

            Assert.True(d2Started);
            Assert.False(engine.IsPaused);
        }

        [Fact]
        public async Task 断点命中挂起_恢复后完成()
        {
            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var d1 = new FlowNode("Delay", "D1")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };
            var d2 = new FlowNode("Delay", "D2")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 10 }
            };

            var graph = new FlowGraph
            {
                FlowName = "断点测试",
                Nodes = new List<FlowNode> { start, d1, d2 },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", d1.Id, "In"),
                    new(d1.Id, "Out", d2.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            engine.SetBreakpoint(d2.Id);

            string? hitNodeId = null;
            engine.BreakpointHit += id => hitNodeId = id;

            var ctx = new FlowContext("断点测试", new FakeLoggerFactory());
            var runTask = engine.RunAsync(graph, ctx);

            await Task.Delay(100);
            Assert.Equal(d2.Id, hitNodeId);

            // 从断点继续
            engine.ContinueFromBreakpoint();
            await runTask;
        }

        [Fact]
        public async Task 动态环防御_低阈值超限触发异常()
        {
            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var jumpA = new FlowNode("Jump", "JumpA")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") }
            };

            // Jump 节点跳回自身
            jumpA.Parameters["targetNodeId"] = jumpA.Id;

            var graph = new FlowGraph
            {
                FlowName = "自环测试",
                Nodes = new List<FlowNode> { start, jumpA },
                Connections = new List<FlowConnection> { new(start.Id, "Out", jumpA.Id, "In") }
            };

            var engine = new FlowEngineV2
            {
                MaxExecutionCount = 5 // 注入低阈值
            };

            var ctx = new FlowContext("自环测试", new FakeLoggerFactory());

            var ex = await Assert.ThrowsAsync<StepExecuteException>(async () =>
            {
                await engine.RunAsync(graph, ctx);
            });

            Assert.Contains("动态环防御触发", ex.Message);
            Assert.Contains("5", ex.Message);
        }
    }
}
