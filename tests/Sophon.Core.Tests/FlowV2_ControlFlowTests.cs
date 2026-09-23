#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    // 本类与 FlowV2_ParallelJoinLoopTests、FlowV2_HardwareNodeTests 都会改写进程级全局
    // FlowGraphStore.DefaultBaseDirectory，xUnit 默认按类并行，故纳入同一 Collection 串行执行，
    // 杜绝全局目录在别类测试窗口内被翻转导致读错目录。
    [Collection("FlowGraphStore")]
    public class FlowV2_ControlFlowTests
    {
        [Fact]
        public async Task BranchNode_分支流转_正确执行True分支并跳过False分支()
        {
            var executedNodes = new List<string>();

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var branch = new FlowNode("Branch", "条件判断")
            {
                Ports = new List<FlowPort>
                {
                    new("In", FlowPortDirection.In, "In"),
                    new("True", FlowPortDirection.Out, "True"),
                    new("False", FlowPortDirection.Out, "False")
                },
                Parameters = new Dictionary<string, object?>
                {
                    ["conditionKey"] = "IsOk",
                    ["expectedValue"] = "true"
                }
            };
            var trueAction = new FlowNode("Delay", "TrueAction")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 1 }
            };
            var falseAction = new FlowNode("Delay", "FalseAction")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 1 }
            };

            var graph = new FlowGraph
            {
                FlowName = "分支测试",
                Nodes = new List<FlowNode> { start, branch, trueAction, falseAction },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", branch.Id, "In"),
                    new(branch.Id, "True", trueAction.Id, "In"),
                    new(branch.Id, "False", falseAction.Id, "In")
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

            var ctx = new FlowContext("分支测试", new FakeLoggerFactory());
            ctx.SetData("IsOk", true);

            await engine.RunAsync(graph, ctx);

            Assert.Contains("TrueAction", executedNodes);
            Assert.DoesNotContain("FalseAction", executedNodes);
        }

        [Fact]
        public async Task LoopNode_指定次数循环执行_完成后流向Completed端口()
        {
            int loopBodyExecCount = 0;
            bool completedExecuted = false;

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var loop = new FlowNode("Loop", "循环节点")
            {
                Ports = new List<FlowPort>
                {
                    new("In", FlowPortDirection.In, "In"),
                    new("LoopBody", FlowPortDirection.Out, "LoopBody"),
                    new("Completed", FlowPortDirection.Out, "Completed")
                },
                Parameters = new Dictionary<string, object?>
                {
                    ["loopCount"] = 3,
                    ["counterKey"] = "TestLoopCounter"
                }
            };

            var bodyAction = new FlowNode("Delay", "BodyAction")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 1 }
            };

            var doneAction = new FlowNode("Delay", "DoneAction")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 1 }
            };

            // Start -> Loop
            // Loop(LoopBody) -> BodyAction -> Loop(In)
            // Loop(Completed) -> DoneAction
            var graph = new FlowGraph
            {
                FlowName = "循环测试",
                Nodes = new List<FlowNode> { start, loop, bodyAction, doneAction },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "LoopBody", bodyAction.Id, "In"),
                    new(bodyAction.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "Completed", doneAction.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            engine.NodeStateChanged += change =>
            {
                if (change.State == FlowNodeState.Completed)
                {
                    if (change.NodeName == "BodyAction") loopBodyExecCount++;
                    if (change.NodeName == "DoneAction") completedExecuted = true;
                }
            };

            var ctx = new FlowContext("循环测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Equal(3, loopBodyExecCount);
            Assert.True(completedExecuted);
        }

        [Fact]
        public async Task ParallelNode_多分支并发执行_全部完成()
        {
            var executedNodes = new HashSet<string>();

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var parallel = new FlowNode("Parallel", "并行分流")
            {
                Ports = new List<FlowPort>
                {
                    new("In", FlowPortDirection.In, "In"),
                    new("Branch1", FlowPortDirection.Out, "Branch1"),
                    new("Branch2", FlowPortDirection.Out, "Branch2")
                }
            };

            var b1 = new FlowNode("Delay", "Branch1Action")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 20 }
            };
            var b2 = new FlowNode("Delay", "Branch2Action")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?> { ["delayMs"] = 20 }
            };

            var graph = new FlowGraph
            {
                FlowName = "并行分流测试",
                Nodes = new List<FlowNode> { start, parallel, b1, b2 },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", parallel.Id, "In"),
                    new(parallel.Id, "Branch1", b1.Id, "In"),
                    new(parallel.Id, "Branch2", b2.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            engine.NodeStateChanged += change =>
            {
                if (change.State == FlowNodeState.Completed)
                {
                    lock (executedNodes)
                    {
                        executedNodes.Add(change.NodeName);
                    }
                }
            };

            var ctx = new FlowContext("并行分流测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Contains("Branch1Action", executedNodes);
            Assert.Contains("Branch2Action", executedNodes);
        }

        [Fact]
        public async Task VariableNode_写入与读取复制变量()
        {
            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var setVar = new FlowNode("Variable", "设置变量")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?>
                {
                    ["operation"] = "Set",
                    ["key"] = "TargetSpeed",
                    ["value"] = 123.45
                }
            };
            var copyVar = new FlowNode("Variable", "复制变量")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?>
                {
                    ["operation"] = "Get",
                    ["key"] = "TargetSpeed",
                    ["targetKey"] = "BackupSpeed"
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "变量测试",
                Nodes = new List<FlowNode> { start, setVar, copyVar },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", setVar.Id, "In"),
                    new(setVar.Id, "Out", copyVar.Id, "In")
                }
            };

            var engine = new FlowEngineV2();
            var ctx = new FlowContext("变量测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Equal(123.45, ctx.GetData<double>("TargetSpeed"));
            Assert.Equal(123.45, ctx.GetData<double>("BackupSpeed"));
        }

        [Fact]
        public async Task SubFlowNode_调用并执行子流程()
        {
            // 每测试独占临时目录，且从保存前到断言后全程持有 DefaultBaseDirectory：
            // 不写共享默认目录、不跨目录清理，从根上消除与其他测试类的同名/同目录争用
            var tempDir = Path.Combine(Path.GetTempPath(), "sophon_flowtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var origDir = FlowGraphStore.DefaultBaseDirectory;
            FlowGraphStore.DefaultBaseDirectory = tempDir;
            try
            {
                // 创建并保存子流程（落在本测试独占目录）
                var subStart = new FlowNode("Start", "子图起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
                var subSet = new FlowNode("Variable", "子图设变量")
                {
                    Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                    Parameters = new Dictionary<string, object?>
                    {
                        ["operation"] = "Set",
                        ["key"] = "SubFlowExecuted",
                        ["value"] = true
                    }
                };
                var subGraph = new FlowGraph
                {
                    FlowName = "ChildFlow",
                    Nodes = new List<FlowNode> { subStart, subSet },
                    Connections = new List<FlowConnection> { new(subStart.Id, "Out", subSet.Id, "In") }
                };

                FlowGraphStore.Save(subGraph);

                // 创建主流程
                var mainStart = new FlowNode("Start", "主图起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
                var subNode = new FlowNode("SubFlow", "调用子流程")
                {
                    Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                    Parameters = new Dictionary<string, object?>
                    {
                        ["subFlowName"] = "ChildFlow"
                    }
                };
                var mainGraph = new FlowGraph
                {
                    FlowName = "MainFlow",
                    Nodes = new List<FlowNode> { mainStart, subNode },
                    Connections = new List<FlowConnection> { new(mainStart.Id, "Out", subNode.Id, "In") }
                };

                var engine = new FlowEngineV2();
                var ctx = new FlowContext("MainFlow", new FakeLoggerFactory());
                await engine.RunAsync(mainGraph, ctx);

                Assert.True(ctx.GetData<bool>("SubFlowExecuted"));
            }
            finally
            {
                FlowGraphStore.DefaultBaseDirectory = origDir;

                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
