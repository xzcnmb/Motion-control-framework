#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// Item ⑤ 执行模型验收：DAG + Loop/Jump/Parallel 执行模型。
    /// 覆盖：
    /// 1. Parallel Fork / Join 屏障（分支并发、汇聚节点只触发一次、分支变量无竞态、Fail-Fast 连锁取消、Any 竞速汇合）；
    /// 2. Loop 迭代状态与安全丝熔断（无限 until 循环强制切断、break/continue、计数循环重入）；
    /// 3. Jump / SubFlow 跳转执行（Id/名称解析、上下文保持、目标缺失明确失败、长跳转链不涨栈）；
    /// 4. AxisMove / MultiAxisInterp 完成状态与 PLCopen CommandCompletionStatus 精确映射。
    /// </summary>
    public class FlowV2_ParallelJoinLoopTests
    {
        #region 测试基建

        /// <summary>按完成剧本应答的运动控制器假实现（可精确控制 PLCopen 完成状态）。</summary>
        private sealed class ScriptedMotionController : IMotionController
        {
            public DriverKind Kind => DriverKind.Simulated;
            public ConnectionState State => ConnectionState.Ready;
            public event Action<ConnectionState>? StateChanged;
            public MotionCapability Capabilities => MotionCapability.None;
            public IReadOnlyList<AxisDefinition> Axes => Array.Empty<AxisDefinition>();

            public event Action<AxisDoneArgs>? AxisDone;
            public event Action<AxisFaultArgs>? AxisFault;
            public event Action<LimitTriggeredArgs>? LimitTriggered;

            /// <summary>每个轴的完成剧本：Success / PLCopen 状态 / 原因。</summary>
            public Dictionary<int, (bool Success, CommandCompletionStatus Status, string Reason)> Script { get; } = new();

            /// <summary>每个轴的完成延时（毫秒），缺省用 <see cref="CompleteDelayMs"/>。</summary>
            public Dictionary<int, int> AxisDelayMs { get; } = new();

            public List<int> MoveAbsCalls { get; } = new();
            public List<int> AbortCalls { get; } = new();
            public List<int> StopCalls { get; } = new();
            public int CompleteDelayMs { get; set; } = 5;

            public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DisconnectAsync() => Task.CompletedTask;
            public void Dispose() { }
            public void EnableAxis(int axisId) { }
            public void DisableAxis(int axisId) { }
            public bool IsAxisEnabled(int axisId) => true;
            public bool IsAxisHomed(int axisId) => true;
            public double GetPosition(int axisId) => 0.0;
            public double GetVelocity(int axisId) => 0.0;
            public Guid Jog(int axisId, int dir, double speed) => Guid.NewGuid();
            public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0) => Guid.NewGuid();
            public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed) => Guid.NewGuid();
            public void Halt(int axisId) => StopCalls.Add(axisId);
            public void Stop(int axisId) => StopCalls.Add(axisId);
            public void StopMotion(int axisId) => StopCalls.Add(axisId);
            public void EmergencyStop(int axisId) => AbortCalls.Add(axisId);
            public void Abort(int axisId, double decelRatio = 0) => AbortCalls.Add(axisId);
            public void EmergencyStopAll() { }
            public void AbortAll() { }
            public void ResetAxis(int axisId) { }

            public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0) =>
                Guid.NewGuid();

            public async Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default)
            {
                MoveAbsCalls.Add(axisId);
                var delayMs = AxisDelayMs.TryGetValue(axisId, out var d) ? d : CompleteDelayMs;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, ct);
                }
                (bool Success, CommandCompletionStatus Status, string Reason) script;
                if (Script.TryGetValue(axisId, out var s))
                {
                    script = s;
                }
                else
                {
                    script = (true, CommandCompletionStatus.Done, "到位成功");
                }
                return new AxisDoneArgs(Guid.NewGuid(), script.Success, script.Reason, script.Status, axisId);
            }

            public async Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default)
            {
                if (CompleteDelayMs > 0)
                {
                    await Task.Delay(CompleteDelayMs, ct);
                }
                return new AxisDoneArgs(Guid.NewGuid(), true, "回零完成", CommandCompletionStatus.Done, axisId);
            }
        }

        private static FlowNode Start(string name = "起始") =>
            new("Start", name) { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };

        private static FlowNode Delay(string name, int delayMs = 5, params string[] ports)
        {
            var node = new FlowNode("Delay", name)
            {
                Parameters = new Dictionary<string, object?> { ["delayMs"] = delayMs }
            };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            foreach (var p in ports.Length == 0 ? new[] { "Out" } : ports)
            {
                node.Ports.Add(new FlowPort(p, FlowPortDirection.Out, p));
            }
            return node;
        }

        private static FlowNode VariableSet(string name, string key, object value)
        {
            var node = new FlowNode("Variable", name)
            {
                Parameters = new Dictionary<string, object?>
                {
                    ["operation"] = "Set",
                    ["key"] = key,
                    ["value"] = value
                }
            };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        private static FlowNode VariableGet(string name, string key, string targetKey)
        {
            var node = new FlowNode("Variable", name)
            {
                Parameters = new Dictionary<string, object?>
                {
                    ["operation"] = "Get",
                    ["key"] = key,
                    ["targetKey"] = targetKey
                }
            };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        private static FlowNode LoopNode(string name, Dictionary<string, object?> parameters, params string[] ports)
        {
            var node = new FlowNode("Loop", name) { Parameters = parameters };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            foreach (var p in ports)
            {
                node.Ports.Add(new FlowPort(p, FlowPortDirection.Out, p));
            }
            return node;
        }

        private static FlowNode JumpNode(string name, string? targetNodeId = null, string? targetNodeName = null)
        {
            var node = new FlowNode("Jump", name);
            if (targetNodeId != null) node.Parameters["targetNodeId"] = targetNodeId;
            if (targetNodeName != null) node.Parameters["targetNodeName"] = targetNodeName;
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        private static FlowNode ParallelNode(string name, Dictionary<string, object?>? parameters = null)
        {
            var node = new FlowNode("Parallel", name) { Parameters = parameters ?? new Dictionary<string, object?>() };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Branch1", FlowPortDirection.Out, "Branch1"));
            node.Ports.Add(new FlowPort("Branch2", FlowPortDirection.Out, "Branch2"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        private static FlowNode AxisMoveNode(string name, int axisId, double target)
        {
            var node = new FlowNode("AxisMove", name)
            {
                Parameters = new Dictionary<string, object?>
                {
                    ["axisId"] = axisId,
                    ["target"] = target,
                    ["speed"] = 100.0,
                    ["timeoutMs"] = 5000
                }
            };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        private static FlowNode MultiAxisNode(string name, int[] axisIds, double[] targets)
        {
            var node = new FlowNode("MultiAxisInterp", name)
            {
                Parameters = new Dictionary<string, object?>
                {
                    ["axisIds"] = axisIds,
                    ["targets"] = targets,
                    ["speed"] = 100.0,
                    ["timeoutMs"] = 5000
                }
            };
            node.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            node.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
            return node;
        }

        /// <summary>记录节点完成顺序的引擎装饰（线程安全）。</summary>
        private sealed class ExecutionTracker
        {
            private readonly List<string> _completed = new();
            private readonly object _lock = new();

            public void OnCompleted(string nodeName)
            {
                lock (_lock) { _completed.Add(nodeName); }
            }

            public int CountOf(string nodeName)
            {
                lock (_lock) { return _completed.Count(n => n == nodeName); }
            }

            public List<string> Snapshot()
            {
                lock (_lock) { return _completed.ToList(); }
            }

            public void Attach(FlowEngineV2 engine)
            {
                engine.NodeStateChanged += change =>
                {
                    if (change.State == FlowNodeState.Completed)
                    {
                        OnCompleted(change.NodeName);
                    }
                };
            }
        }

        #endregion

        #region 1. Parallel Fork / Join 屏障

        [Fact]
        public async Task ParallelNode_结构化Fork_Join屏障后下游节点只执行一次()
        {
            // Start -> Parallel
            // Parallel.Branch1 -> A1(30ms)
            // Parallel.Branch2 -> A2(5ms)
            // Parallel.Out -> After          （Join 屏障后续流：只触发一次）
            var start = Start();
            var parallel = ParallelNode("并行分流");
            var a1 = Delay("A1", 30, "In", "Out");
            var a2 = Delay("A2", 5, "In", "Out");
            var after = Delay("After", 1);

            var graph = new FlowGraph
            {
                FlowName = "结构化Fork-Join",
                Nodes = new List<FlowNode> { start, parallel, a1, a2, after },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", parallel.Id, "In"),
                    new(parallel.Id, "Branch1", a1.Id, "In"),
                    new(parallel.Id, "Branch2", a2.Id, "In"),
                    new(parallel.Id, "Out", after.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var sw = Stopwatch.StartNew();
            await engine.RunAsync(graph, new FlowContext("ForkJoin", new FakeLoggerFactory()));
            sw.Stop();

            Assert.Equal(1, tracker.CountOf("After"));
            Assert.Equal(1, tracker.CountOf("A1"));
            Assert.Equal(1, tracker.CountOf("A2"));

            // After 必须晚于两个分支完成（屏障语义：先汇合后续流）
            var order = tracker.Snapshot();
            Assert.True(order.IndexOf("After") > order.IndexOf("A1"));
            Assert.True(order.IndexOf("After") > order.IndexOf("A2"));
        }

        [Fact]
        public async Task ParallelNode_分支直接汇聚_汇聚节点全局只执行一次()
        {
            // 分支不经过 Out 端口而是直接汇聚到同一节点（非结构化汇合）：
            // Start -> Parallel
            // Parallel.Branch1 -> A1 -> Join
            // Parallel.Branch2 -> A2 -> Join
            // Join -> After
            // 关键断言：Join 只执行一次（旧实现会每个分支各触发一次）。
            var start = Start();
            var parallel = ParallelNode("并行分流");
            var a1 = Delay("A1", 20, "In", "Out");
            var a2 = Delay("A2", 5, "In", "Out");
            var join = Delay("Join", 1);
            var after = Delay("After", 1);

            var graph = new FlowGraph
            {
                FlowName = "非结构化汇合",
                Nodes = new List<FlowNode> { start, parallel, a1, a2, join, after },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", parallel.Id, "In"),
                    new(parallel.Id, "Branch1", a1.Id, "In"),
                    new(parallel.Id, "Branch2", a2.Id, "In"),
                    new(a1.Id, "Out", join.Id, "In"),
                    new(a2.Id, "Out", join.Id, "In"),
                    new(join.Id, "Out", after.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            await engine.RunAsync(graph, new FlowContext("Converge", new FakeLoggerFactory()));

            Assert.Equal(1, tracker.CountOf("Join"));
            Assert.Equal(1, tracker.CountOf("After"));
            Assert.Equal(1, tracker.CountOf("A1"));
            Assert.Equal(1, tracker.CountOf("A2"));

            var order = tracker.Snapshot();
            Assert.True(order.IndexOf("Join") > order.IndexOf("A1"));
            Assert.True(order.IndexOf("Join") > order.IndexOf("A2"));
            Assert.True(order.IndexOf("After") > order.IndexOf("Join"));
        }

        [Fact]
        public async Task ParallelNode_分支故障_连锁取消其余分支并整体失败()
        {
            // 分支2 快速失败 -> 分支1（200ms 长任务）必须被连锁取消，整条流程快速失败。
            var start = Start();
            var parallel = ParallelNode("并行分流");
            var slow = Delay("SlowBranch", 3000, "In", "Out");   // 长任务分支
            var failBranch = new FlowNode("Variable", "FailBranch")
            {
                Parameters = new Dictionary<string, object?> { ["operation"] = "Set", ["value"] = 1 } // 缺 key -> Failed
            };
            failBranch.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
            failBranch.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));

            var graph = new FlowGraph
            {
                FlowName = "分支故障",
                Nodes = new List<FlowNode> { start, parallel, slow, failBranch },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", parallel.Id, "In"),
                    new(parallel.Id, "Branch1", slow.Id, "In"),
                    new(parallel.Id, "Branch2", failBranch.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("FailFast", new FakeLoggerFactory())));
            sw.Stop();

            Assert.Contains("FailBranch", ex.Message);
            // Fail-Fast：不应等待 3 秒的长分支
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Fail-Fast 连锁取消未生效，耗时 {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task ParallelNode_Any竞速汇合_首分支完成即放行()
        {
            // joinMode=Any：快分支（5ms）完成即放行，慢分支（3000ms）被取消。
            var start = Start();
            var parallel = ParallelNode("竞速分流", new Dictionary<string, object?> { ["joinMode"] = "Any" });
            var slow = Delay("SlowBranch", 3000, "In", "Out");
            var fast = Delay("FastBranch", 5, "In", "Out");
            var after = Delay("After", 1);

            var graph = new FlowGraph
            {
                FlowName = "竞速汇合",
                Nodes = new List<FlowNode> { start, parallel, slow, fast, after },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", parallel.Id, "In"),
                    new(parallel.Id, "Branch1", slow.Id, "In"),
                    new(parallel.Id, "Branch2", fast.Id, "In"),
                    new(parallel.Id, "Out", after.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var sw = Stopwatch.StartNew();
            await engine.RunAsync(graph, new FlowContext("AnyJoin", new FakeLoggerFactory()));
            sw.Stop();

            Assert.Equal(1, tracker.CountOf("After"));
            Assert.Equal(1, tracker.CountOf("FastBranch"));
            Assert.True(sw.ElapsedMilliseconds < 2000, $"Any 竞速汇合未生效，耗时 {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public async Task 并行分支变量读写_共享上下文无竞态()
        {
            // 8 个并发分支各自写入独立变量并汇合，全部可见即证明分支变量经锁串行化、无竞态丢失。
            var start = Start();
            var parallel = ParallelNode("并行分流");
            var branches = new List<FlowNode>();
            var connections = new List<FlowConnection>
            {
                new(start.Id, "Out", parallel.Id, "In")
            };

            const int branchCount = 8;
            for (int i = 0; i < branchCount; i++)
            {
                var setter = VariableSet($"BranchSetter{i}", $"BranchVar{i}", i * 10);
                branches.Add(setter);
                connections.Add(new FlowConnection(parallel.Id, i == 0 ? "Branch1" : "Branch2", setter.Id, "In"));
            }

            var join = Delay("Join", 1);
            foreach (var b in branches)
            {
                connections.Add(new FlowConnection(b.Id, "Out", join.Id, "In"));
            }

            var graph = new FlowGraph
            {
                FlowName = "分支变量",
                Nodes = new List<FlowNode> { start, parallel }.Concat(branches).Append(join).ToList(),
                Connections = connections
            };

            var ctx = new FlowContext("分支变量", new FakeLoggerFactory());
            var engine = new FlowEngineV2();
            await engine.RunAsync(graph, ctx);

            for (int i = 0; i < branchCount; i++)
            {
                Assert.Equal(i * 10, ctx.GetData<int>($"BranchVar{i}"));
            }
        }

        [Fact]
        public async Task FlowContext_并发读写_锁序列化无异常()
        {
            // 直接压测 FlowContext：多线程并发读写同一上下文不得抛异常、不得损坏字典。
            var ctx = new FlowContext("并发压测", new FakeLoggerFactory());

            var tasks = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
            {
                for (int j = 0; j < 200; j++)
                {
                    ctx.SetData($"K{i % 8}", j);
                    _ = ctx.GetData<int>($"K{i % 8}");
                }
            })).ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(8, ctx.Data.Count);
        }

        #endregion

        #region 2. Loop 迭代状态与安全丝熔断

        [Fact]
        public async Task LoopNode_安全丝熔断_until条件永假时强制失败()
        {
            // loopCount=0（until 模式）且 until 条件永不满足 -> 安全丝必须在硬上限处切断，
            // 工业执行线程不得被无限循环挂死。
            var start = Start();
            var loop = LoopNode("失控循环", new Dictionary<string, object?>
            {
                ["loopCount"] = 0,
                ["maxIterations"] = 50,
                ["counterKey"] = "FuseCounter"
            }, "LoopBody", "Completed");
            var body = Delay("LoopBody", 1, "In", "Out");

            var graph = new FlowGraph
            {
                FlowName = "安全丝测试",
                Nodes = new List<FlowNode> { start, loop, body },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "LoopBody", body.Id, "In"),
                    new(body.Id, "Out", loop.Id, "In")
                }
            };

            var ctx = new FlowContext("安全丝测试", new FakeLoggerFactory());
            var engine = new FlowEngineV2();

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<StepExecuteException>(() => engine.RunAsync(graph, ctx));
            sw.Stop();

            Assert.Contains("安全丝熔断", ex.Message);
            Assert.Contains("50", ex.Message);
            // 必须快速失败（50 次 1ms 迭代），不得挂死
            Assert.True(sw.ElapsedMilliseconds < 10000, $"安全丝熔断耗时异常: {sw.ElapsedMilliseconds}ms");
            // 熔断后计数器复位，便于人工干预后重跑
            Assert.Equal(0, ctx.GetData<int>("FuseCounter"));
        }

        [Fact]
        public async Task LoopNode_break条件_立即退出到Completed且不执行循环体()
        {
            var start = Start();
            var loop = LoopNode("带跳出循环", new Dictionary<string, object?>
            {
                ["loopCount"] = 0,
                ["breakConditionKey"] = "EmergencyAbort",
                ["counterKey"] = "BreakCounter"
            }, "LoopBody", "Completed");
            var body = Delay("LoopBody", 1, "In", "Out");
            var done = Delay("DoneAction", 1);

            var graph = new FlowGraph
            {
                FlowName = "break测试",
                Nodes = new List<FlowNode> { start, loop, body, done },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "LoopBody", body.Id, "In"),
                    new(body.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "Completed", done.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var ctx = new FlowContext("break测试", new FakeLoggerFactory());
            ctx.SetData("EmergencyAbort", true);

            await engine.RunAsync(graph, ctx);

            Assert.Equal(0, tracker.CountOf("LoopBody"));
            Assert.Equal(1, tracker.CountOf("DoneAction"));
        }

        [Fact]
        public async Task LoopNode_continue条件_跳过循环体但迭代计数照常推进()
        {
            // loopCount=4；循环体末尾把 SkipNext 置 true ->
            // 第 1 次迭代执行循环体，第 2~4 次迭代被 continue 跳过，最终走 Completed。
            var start = Start();
            var loop = LoopNode("带继续循环", new Dictionary<string, object?>
            {
                ["loopCount"] = 4,
                ["continueConditionKey"] = "SkipNext",
                ["counterKey"] = "ContinueCounter"
            }, "LoopBody", "Completed");
            var body = Delay("LoopBody", 1, "In", "Out");
            var setSkip = VariableSet("SetSkip", "SkipNext", true);
            var done = Delay("DoneAction", 1);

            var graph = new FlowGraph
            {
                FlowName = "continue测试",
                Nodes = new List<FlowNode> { start, loop, body, setSkip, done },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "LoopBody", body.Id, "In"),
                    new(body.Id, "Out", setSkip.Id, "In"),
                    new(setSkip.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "Completed", done.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var ctx = new FlowContext("continue测试", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Equal(1, tracker.CountOf("LoopBody"));   // 4 次迭代仅执行 1 次循环体
            Assert.Equal(1, tracker.CountOf("DoneAction"));  // 迭代完成后正常出口
        }

        [Fact]
        public async Task LoopNode_计数循环_预设计数器与重复执行语义正确()
        {
            // 预设计数器=2，loopCount=3 -> 仅再执行 1 次循环体即完成（迭代状态驻留上下文可复位）。
            var start = Start();
            var loop = LoopNode("计数循环", new Dictionary<string, object?>
            {
                ["loopCount"] = 3,
                ["counterKey"] = "PresetCounter"
            }, "LoopBody", "Completed");
            var body = Delay("LoopBody", 1, "In", "Out");
            var done = Delay("DoneAction", 1);

            FlowGraph BuildGraph() => new FlowGraph
            {
                FlowName = "计数循环",
                Nodes = new List<FlowNode> { start, loop, body, done },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "LoopBody", body.Id, "In"),
                    new(body.Id, "Out", loop.Id, "In"),
                    new(loop.Id, "Completed", done.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var ctx = new FlowContext("计数循环", new FakeLoggerFactory());
            ctx.SetData("PresetCounter", 2);
            await engine.RunAsync(BuildGraph(), ctx);

            Assert.Equal(1, tracker.CountOf("LoopBody"));
            Assert.Equal(1, tracker.CountOf("DoneAction"));
            // 完成后计数器复位，同一上下文可无残留重入
            Assert.Equal(0, ctx.GetData<int>("PresetCounter"));

            var tracker2 = new ExecutionTracker();
            var engine2 = new FlowEngineV2();
            tracker2.Attach(engine2);
            await engine2.RunAsync(BuildGraph(), ctx);

            Assert.Equal(3, tracker2.CountOf("LoopBody"));
            Assert.Equal(1, tracker2.CountOf("DoneAction"));
        }

        [Fact]
        public async Task LoopNode_负loopCount_参数校验直接失败()
        {
            var start = Start();
            var loop = LoopNode("非法循环", new Dictionary<string, object?>
            {
                ["loopCount"] = -5
            }, "LoopBody", "Completed");

            var graph = new FlowGraph
            {
                FlowName = "非法循环参数",
                Nodes = new List<FlowNode> { start, loop },
                Connections = new List<FlowConnection> { new(start.Id, "Out", loop.Id, "In") }
            };

            var engine = new FlowEngineV2();
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("非法循环", new FakeLoggerFactory())));

            Assert.Contains("loopCount", ex.Message);
        }

        #endregion

        #region 3. Jump / SubFlow 执行

        [Fact]
        public async Task JumpNode_按目标节点Id跳转_执行上下文保持且跳过拓扑后继()
        {
            // Start -> SetVar(Marker) -> Jump --(跳)--> Target -> GetVar(Marker->AfterMarker)
            //                        \--(拓扑)--> DeadEnd（不得执行）
            var start = Start();
            var setVar = VariableSet("SetMarker", "Marker", "before-jump");
            var jump = JumpNode("跳转", targetNodeId: null!); // 占位，稍后绑定目标 Id
            var target = Delay("JumpTarget", 1, "In", "Out");
            var getVar = VariableGet("ReadMarker", "Marker", "AfterMarker");
            var deadEnd = Delay("DeadEnd", 1);
            jump.Parameters["targetNodeId"] = target.Id;

            var graph = new FlowGraph
            {
                FlowName = "Jump执行",
                Nodes = new List<FlowNode> { start, setVar, jump, target, getVar, deadEnd },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", setVar.Id, "In"),
                    new(setVar.Id, "Out", jump.Id, "In"),
                    new(jump.Id, "Out", deadEnd.Id, "In"),   // 拓扑后继应被跳过
                    new(target.Id, "Out", getVar.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            var ctx = new FlowContext("Jump执行", new FakeLoggerFactory());
            await engine.RunAsync(graph, ctx);

            Assert.Equal(1, tracker.CountOf("JumpTarget"));
            Assert.Equal(0, tracker.CountOf("DeadEnd"));
            // 上下文保持：跳转前后数据字典原样传递
            Assert.Equal("before-jump", ctx.GetData<string>("AfterMarker"));
        }

        [Fact]
        public async Task JumpNode_按节点名称标签跳转()
        {
            var start = Start();
            var jump = JumpNode("标签跳转", targetNodeName: "汇聚点");
            var target = Delay("汇聚点", 1);

            var graph = new FlowGraph
            {
                FlowName = "标签跳转",
                Nodes = new List<FlowNode> { start, jump, target },
                Connections = new List<FlowConnection> { new(start.Id, "Out", jump.Id, "In") }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            await engine.RunAsync(graph, new FlowContext("标签跳转", new FakeLoggerFactory()));

            Assert.Equal(1, tracker.CountOf("汇聚点"));
        }

        [Fact]
        public async Task JumpNode_目标不存在_明确失败不静默()
        {
            var start = Start();
            var jump = JumpNode("无效跳转", targetNodeId: "NOT-EXIST-NODE");

            var graph = new FlowGraph
            {
                FlowName = "无效跳转",
                Nodes = new List<FlowNode> { start, jump },
                Connections = new List<FlowConnection> { new(start.Id, "Out", jump.Id, "In") }
            };

            var engine = new FlowEngineV2();
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("无效跳转", new FakeLoggerFactory())));

            Assert.Contains("Jump 目标节点不存在", ex.Message);
            Assert.Contains("NOT-EXIST-NODE", ex.Message);
        }

        [Fact]
        public async Task JumpNode_长跳转链_迭代调度不增长调用栈()
        {
            // 300 个 Jump 节点串联成链（旧递归调度会持续增长调用栈，有 StackOverflow 风险）。
            // 拓扑仅把执行流送到 J0，其余全部经 Jump 非拓扑直跳推进。
            const int chainLength = 300;
            var start = Start();
            var jumpers = Enumerable.Range(0, chainLength).Select(i => JumpNode($"J{i}")).ToList();
            var chainEnd = Delay("ChainEnd", 1);

            var nodes = new List<FlowNode> { start };
            nodes.AddRange(jumpers);
            nodes.Add(chainEnd);

            var connections = new List<FlowConnection>
            {
                new(start.Id, "Out", jumpers[0].Id, "In")
            };
            for (int i = 0; i < jumpers.Count; i++)
            {
                var target = i == jumpers.Count - 1 ? chainEnd : jumpers[i + 1];
                jumpers[i].Parameters["targetNodeId"] = target.Id;
            }

            var graph = new FlowGraph
            {
                FlowName = "长跳转链",
                Nodes = nodes,
                Connections = connections
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2();
            tracker.Attach(engine);

            await engine.RunAsync(graph, new FlowContext("长跳转链", new FakeLoggerFactory()));

            Assert.Equal(1, tracker.CountOf("ChainEnd"));
        }

        [Fact]
        public async Task SubFlowNode_调用子流程_共享上下文数据()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SophonJoinLoopSubFlow_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var subStart = Start("子图起始");
                var subSet = VariableSet("子图设变量", "SubFlowExecuted", true);
                var subGraph = new FlowGraph
                {
                    FlowName = "ChildFlow",
                    Nodes = new List<FlowNode> { subStart, subSet },
                    Connections = new List<FlowConnection> { new(subStart.Id, "Out", subSet.Id, "In") }
                };

                FlowGraphStore.Save(subGraph, tempDir);
                FlowGraphStore.Save(subGraph); // 默认目录同步一份，测试结束清理

                var mainStart = Start("主图起始");
                var subNode = new FlowNode("SubFlow", "调用子流程")
                {
                    Parameters = new Dictionary<string, object?> { ["subFlowName"] = "ChildFlow" }
                };
                subNode.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
                subNode.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
                var mainGraph = new FlowGraph
                {
                    FlowName = "MainFlow",
                    Nodes = new List<FlowNode> { mainStart, subNode },
                    Connections = new List<FlowConnection> { new(mainStart.Id, "Out", subNode.Id, "In") }
                };

                var origDir = FlowGraphStore.DefaultBaseDirectory;
                FlowGraphStore.DefaultBaseDirectory = tempDir;
                try
                {
                    var engine = new FlowEngineV2();
                    var ctx = new FlowContext("MainFlow", new FakeLoggerFactory());
                    await engine.RunAsync(mainGraph, ctx);

                    Assert.True(ctx.GetData<bool>("SubFlowExecuted"));
                }
                finally
                {
                    FlowGraphStore.DefaultBaseDirectory = origDir;
                }
            }
            finally
            {
                var defaultFile = FlowGraphStore.GetFilePath("ChildFlow");
                if (File.Exists(defaultFile)) { try { File.Delete(defaultFile); } catch { } }
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task SubFlowNode_子流程失败_向上传播明确错误()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "SophonJoinLoopSubFail_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var subStart = Start("子图起始");
                var subFail = new FlowNode("Variable", "子图失败点")
                {
                    Parameters = new Dictionary<string, object?> { ["operation"] = "Set", ["value"] = 1 } // 缺 key -> Failed
                };
                subFail.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
                subFail.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
                var subGraph = new FlowGraph
                {
                    FlowName = "FailChildFlow",
                    Nodes = new List<FlowNode> { subStart, subFail },
                    Connections = new List<FlowConnection> { new(subStart.Id, "Out", subFail.Id, "In") }
                };

                FlowGraphStore.Save(subGraph, tempDir);
                FlowGraphStore.Save(subGraph);

                var mainStart = Start("主图起始");
                var subNode = new FlowNode("SubFlow", "调用子流程")
                {
                    Parameters = new Dictionary<string, object?> { ["subFlowName"] = "FailChildFlow" }
                };
                subNode.Ports.Add(new FlowPort("In", FlowPortDirection.In, "In"));
                subNode.Ports.Add(new FlowPort("Out", FlowPortDirection.Out, "Out"));
                var mainGraph = new FlowGraph
                {
                    FlowName = "MainFailFlow",
                    Nodes = new List<FlowNode> { mainStart, subNode },
                    Connections = new List<FlowConnection> { new(mainStart.Id, "Out", subNode.Id, "In") }
                };

                var origDir = FlowGraphStore.DefaultBaseDirectory;
                FlowGraphStore.DefaultBaseDirectory = tempDir;
                try
                {
                    var engine = new FlowEngineV2();
                    var ex = await Assert.ThrowsAsync<StepExecuteException>(
                        () => engine.RunAsync(mainGraph, new FlowContext("MainFailFlow", new FakeLoggerFactory())));

                    Assert.Contains("子图失败点", ex.Message);
                }
                finally
                {
                    FlowGraphStore.DefaultBaseDirectory = origDir;
                }
            }
            finally
            {
                var defaultFile = FlowGraphStore.GetFilePath("FailChildFlow");
                if (File.Exists(defaultFile)) { try { File.Delete(defaultFile); } catch { } }
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        #endregion

        #region 4. PLCopen 完成状态映射

        [Fact]
        public async Task AxisMoveNode_到位Done_Completed放行()
        {
            var motion = new ScriptedMotionController
            {
                CompleteDelayMs = 5
                // 默认剧本：Done / Success
            };

            var start = Start();
            var move = AxisMoveNode("定位", 0, 100.0);
            var after = Delay("AfterMove", 1);

            var graph = new FlowGraph
            {
                FlowName = "Done映射",
                Nodes = new List<FlowNode> { start, move, after },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", move.Id, "In"),
                    new(move.Id, "Out", after.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2(motion);
            tracker.Attach(engine);

            await engine.RunAsync(graph, new FlowContext("Done映射", new FakeLoggerFactory()));

            Assert.Equal(1, tracker.CountOf("AfterMove"));
            Assert.Single(motion.MoveAbsCalls);
        }

        [Fact]
        public async Task AxisMoveNode_命令被中止CommandAborted_失败消息精确()
        {
            var motion = new ScriptedMotionController { CompleteDelayMs = 5 };
            motion.Script[0] = (false, CommandCompletionStatus.CommandAborted, "MC_Stop 叫听");

            var start = Start();
            var move = AxisMoveNode("定位", 0, 100.0);

            var graph = new FlowGraph
            {
                FlowName = "Aborted映射",
                Nodes = new List<FlowNode> { start, move },
                Connections = new List<FlowConnection> { new(start.Id, "Out", move.Id, "In") }
            };

            var engine = new FlowEngineV2(motion);
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("Aborted映射", new FakeLoggerFactory())));

            Assert.Contains("命令被中止 (CommandAborted)", ex.Message);
            Assert.Contains("MC_Stop 叫听", ex.Message);
        }

        [Fact]
        public async Task AxisMoveNode_硬件故障Error_失败消息精确()
        {
            var motion = new ScriptedMotionController { CompleteDelayMs = 5 };
            motion.Script[0] = (false, CommandCompletionStatus.Error, "正向硬限位触发");

            var start = Start();
            var move = AxisMoveNode("定位", 0, 100.0);

            var graph = new FlowGraph
            {
                FlowName = "Error映射",
                Nodes = new List<FlowNode> { start, move },
                Connections = new List<FlowConnection> { new(start.Id, "Out", move.Id, "In") }
            };

            var engine = new FlowEngineV2(motion);
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("Error映射", new FakeLoggerFactory())));

            Assert.Contains("硬件故障/限位错误 (Error)", ex.Message);
            Assert.Contains("正向硬限位触发", ex.Message);
        }

        [Fact]
        public async Task MultiAxisInterpNode_全部Done_协同到位放行()
        {
            var motion = new ScriptedMotionController { CompleteDelayMs = 5 };

            var start = Start();
            var interp = MultiAxisNode("双轴插补", new[] { 0, 1 }, new[] { 100.0, 200.0 });
            var after = Delay("AfterInterp", 1);

            var graph = new FlowGraph
            {
                FlowName = "多轴Done",
                Nodes = new List<FlowNode> { start, interp, after },
                Connections = new List<FlowConnection>
                {
                    new(start.Id, "Out", interp.Id, "In"),
                    new(interp.Id, "Out", after.Id, "In")
                }
            };

            var tracker = new ExecutionTracker();
            var engine = new FlowEngineV2(motion);
            tracker.Attach(engine);

            await engine.RunAsync(graph, new FlowContext("多轴Done", new FakeLoggerFactory()));

            Assert.Equal(1, tracker.CountOf("AfterInterp"));
            Assert.Equal(2, motion.MoveAbsCalls.Count);
        }

        [Fact]
        public async Task MultiAxisInterpNode_单轴Error_连锁急停且失败消息含PLCopen状态()
        {
            var motion = new ScriptedMotionController();
            // 轴1 快速失败（Error），轴0 慢速到位 -> 故障分支必先完成，Fail-Fast 路径确定触发
            motion.AxisDelayMs[0] = 200;
            motion.AxisDelayMs[1] = 5;
            motion.Script[1] = (false, CommandCompletionStatus.Error, "Y轴驱动器报警");

            var start = Start();
            var interp = MultiAxisNode("双轴插补", new[] { 0, 1 }, new[] { 100.0, 200.0 });

            var graph = new FlowGraph
            {
                FlowName = "多轴Error",
                Nodes = new List<FlowNode> { start, interp },
                Connections = new List<FlowConnection> { new(start.Id, "Out", interp.Id, "In") }
            };

            var engine = new FlowEngineV2(motion);
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("多轴Error", new FakeLoggerFactory())));

            // 精确 PLCopen 状态必须出现在失败消息中（不得被笼统的连锁急停消息掩盖）
            Assert.Contains("硬件故障/限位错误 (Error)", ex.Message);
            Assert.Contains("Y轴驱动器报警", ex.Message);
            // Fail-Fast：另一轴必须被 Abort
            Assert.Contains(0, motion.AbortCalls);
        }

        [Fact]
        public async Task MultiAxisInterpNode_单轴CommandAborted_失败消息含PLCopen状态()
        {
            var motion = new ScriptedMotionController();
            // 轴0 快速失败（CommandAborted），轴1 慢速到位 -> Fail-Fast 路径确定触发
            motion.AxisDelayMs[0] = 5;
            motion.AxisDelayMs[1] = 200;
            motion.Script[0] = (false, CommandCompletionStatus.CommandAborted, "被更高优先级指令抢占");

            var start = Start();
            var interp = MultiAxisNode("双轴插补", new[] { 0, 1 }, new[] { 100.0, 200.0 });

            var graph = new FlowGraph
            {
                FlowName = "多轴Aborted",
                Nodes = new List<FlowNode> { start, interp },
                Connections = new List<FlowConnection> { new(start.Id, "Out", interp.Id, "In") }
            };

            var engine = new FlowEngineV2(motion);
            var ex = await Assert.ThrowsAsync<StepExecuteException>(
                () => engine.RunAsync(graph, new FlowContext("多轴Aborted", new FakeLoggerFactory())));

            Assert.Contains("命令被中止 (CommandAborted)", ex.Message);
            Assert.Contains("被更高优先级指令抢占", ex.Message);
            Assert.Contains(1, motion.AbortCalls);
        }

        #endregion
    }
}
