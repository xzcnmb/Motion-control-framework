#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowV2_HardwareNodeTests
    {
        #region Fake 控制器实现

        private class FakeMotionController : IMotionController
        {
            public DriverKind Kind => DriverKind.Simulated;
            public ConnectionState State => ConnectionState.Ready;
            public event Action<ConnectionState>? StateChanged;
            public MotionCapability Capabilities => MotionCapability.None;
            public IReadOnlyList<AxisDefinition> Axes => Array.Empty<AxisDefinition>();

            public event Action<AxisDoneArgs>? AxisDone;
            public event Action<AxisFaultArgs>? AxisFault;
            public event Action<LimitTriggeredArgs>? LimitTriggered;

            public List<(int axisId, double target)> MoveAbsCalls { get; } = new();
            public List<int> StopCalls { get; } = new();
            public List<int> AbortCalls { get; } = new();
            public List<(int axisId, int dir, double speed)> JogCalls { get; } = new();
            public List<(int axisId, HomingMode mode, HomeDirection dir, double speed)> HomeCalls { get; } = new();

            public bool AutoCompleteOnMoveAbs { get; set; } = true;
            public bool CompleteSuccess { get; set; } = true;
            public string CompleteReason { get; set; } = "到位成功";
            public int AutoCompleteDelayMs { get; set; } = 10;

            public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DisconnectAsync() => Task.CompletedTask;
            public void Dispose() { }
            public void EnableAxis(int axisId) { }
            public void DisableAxis(int axisId) { }
            public bool IsAxisEnabled(int axisId) => true;
            public bool IsAxisHomed(int axisId) => true;
            public double GetPosition(int axisId) => 0.0;
            public double GetVelocity(int axisId) => 0.0;

            public Guid Jog(int axisId, int dir, double speed)
            {
                JogCalls.Add((axisId, dir, speed));
                return Guid.NewGuid();
            }

            public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0)
            {
                var reqId = Guid.NewGuid();
                MoveAbsCalls.Add((axisId, target));

                if (AutoCompleteOnMoveAbs)
                {
                    Task.Run(async () =>
                    {
                        if (AutoCompleteDelayMs > 0)
                        {
                            await Task.Delay(AutoCompleteDelayMs);
                        }
                        AxisDone?.Invoke(new AxisDoneArgs(reqId, CompleteSuccess, CompleteReason));
                    });
                }

                return reqId;
            }

            public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0) =>
                Guid.NewGuid();

            public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed)
            {
                var reqId = Guid.NewGuid();
                HomeCalls.Add((axisId, mode, dir, speed));

                if (AutoCompleteOnMoveAbs)
                {
                    Task.Run(async () =>
                    {
                        if (AutoCompleteDelayMs > 0)
                        {
                            await Task.Delay(AutoCompleteDelayMs);
                        }
                        AxisDone?.Invoke(new AxisDoneArgs(reqId, CompleteSuccess, CompleteReason));
                    });
                }

                return reqId;
            }

            public async Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default)
            {
                var reqId = Guid.NewGuid();
                MoveAbsCalls.Add((axisId, target));
                if (!AutoCompleteOnMoveAbs)
                {
                    // 模拟永不到位：仅在被取消时结束（供上层 WaitAsync 超时路径命中）
                    await Task.Delay(Timeout.Infinite, ct);
                }
                if (AutoCompleteDelayMs > 0)
                {
                    await Task.Delay(AutoCompleteDelayMs, ct);
                }
                return new AxisDoneArgs(reqId, CompleteSuccess, CompleteReason);
            }

            public async Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default)
            {
                var reqId = Guid.NewGuid();
                HomeCalls.Add((axisId, mode, dir, speed));
                if (!AutoCompleteOnMoveAbs)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                if (AutoCompleteDelayMs > 0)
                {
                    await Task.Delay(AutoCompleteDelayMs, ct);
                }
                return new AxisDoneArgs(reqId, CompleteSuccess, CompleteReason);
            }

            public void Halt(int axisId) => StopCalls.Add(axisId);
            public void Stop(int axisId) => StopCalls.Add(axisId);
            public void StopMotion(int axisId) => StopCalls.Add(axisId);
            public void EmergencyStop(int axisId) => AbortCalls.Add(axisId);
            public void Abort(int axisId, double decelRatio = 0) => AbortCalls.Add(axisId);
            public void EmergencyStopAll() { }
            public void AbortAll() { }
            public void ResetAxis(int axisId) { }

            public void FireAxisDone(Guid reqId, bool success, string reason)
            {
                AxisDone?.Invoke(new AxisDoneArgs(reqId, success, reason));
            }
        }

        private class FakeIoController : IIoController
        {
            public ConcurrentDictionary<string, bool> Di { get; } = new(StringComparer.OrdinalIgnoreCase);
            public ConcurrentDictionary<string, bool> Do { get; } = new(StringComparer.OrdinalIgnoreCase);

            public event Action<string, bool>? DiChanged;
            public IReadOnlyList<string> DiPointNames => new List<string>(Di.Keys);
            public IReadOnlyList<string> DoPointNames => new List<string>(Do.Keys);

            public bool ReadDi(string pointName) => Di.TryGetValue(pointName, out var v) && v;
            public void WriteDo(string pointName, bool value) => Do[pointName] = value;
            public IReadOnlyDictionary<string, bool> SnapshotDi() => new Dictionary<string, bool>(Di);
            public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>(Do);

            public void TriggerDi(string pointName, bool value)
            {
                Di[pointName] = value;
                DiChanged?.Invoke(pointName, value);
            }
        }

        private class FakeEventBus : IEventBus
        {
            private readonly List<object> _subscribers = new();

            public void Subscribe<T>(Action<T> handler)
            {
                lock (_subscribers)
                {
                    _subscribers.Add(handler);
                }
            }

            public void Unsubscribe<T>(Action<T> handler)
            {
                lock (_subscribers)
                {
                    _subscribers.Remove(handler);
                }
            }

            public void Publish<T>(T @event)
            {
                List<Action<T>> targets;
                lock (_subscribers)
                {
                    targets = _subscribers.FindAll(s => s is Action<T>).ConvertAll(s => (Action<T>)s);
                }
                foreach (var t in targets)
                {
                    t(@event);
                }
            }
        }

        #endregion

        [Fact]
        public async Task AxisMoveNode_等待AxisDone成功到位()
        {
            var fakeMotion = new FakeMotionController
            {
                AutoCompleteOnMoveAbs = true,
                AutoCompleteDelayMs = 20
            };

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var move = new FlowNode("AxisMove", "X轴定位")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                Parameters = new Dictionary<string, object?>
                {
                    ["axisId"] = 0,
                    ["target"] = 150.5,
                    ["speed"] = 100.0,
                    ["accel"] = 500.0,
                    ["decel"] = 500.0,
                    ["timeoutMs"] = 1000
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "定位测试",
                Nodes = new List<FlowNode> { start, move },
                Connections = new List<FlowConnection> { new(start.Id, "Out", move.Id, "In") }
            };

            var engine = new FlowEngineV2(motionController: fakeMotion);
            var ctx = new FlowContext("定位测试", new FakeLoggerFactory());

            await engine.RunAsync(graph, ctx);

            Assert.Single(fakeMotion.MoveAbsCalls);
            Assert.Equal(0, fakeMotion.MoveAbsCalls[0].axisId);
            Assert.Equal(150.5, fakeMotion.MoveAbsCalls[0].target);
        }

        [Fact]
        public async Task AxisMoveNode_超时未到位_抛出StepExecuteException()
        {
            var fakeMotion = new FakeMotionController
            {
                AutoCompleteOnMoveAbs = false // 不自动回复 AxisDone，模拟超时
            };

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var move = new FlowNode("AxisMove", "X轴超时定位")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["axisId"] = 1,
                    ["target"] = 50.0,
                    ["timeoutMs"] = 50 // 50ms 超时
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "超时定位测试",
                Nodes = new List<FlowNode> { start, move },
                Connections = new List<FlowConnection> { new(start.Id, "Out", move.Id, "In") }
            };

            var engine = new FlowEngineV2(motionController: fakeMotion);
            var ctx = new FlowContext("超时定位测试", new FakeLoggerFactory());

            var ex = await Assert.ThrowsAsync<StepExecuteException>(async () =>
            {
                await engine.RunAsync(graph, ctx);
            });

            Assert.Contains("超时", ex.Message);
            Assert.Contains(1, fakeMotion.StopCalls);
        }

        [Fact]
        public async Task MultiAxisInterpNode_多轴并发MoveAbs并WhenAll到位()
        {
            var fakeMotion = new FakeMotionController
            {
                AutoCompleteOnMoveAbs = true,
                AutoCompleteDelayMs = 20
            };

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var interp = new FlowNode("MultiAxisInterp", "XY两轴插补")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["axisIds"] = new[] { 0, 1 },
                    ["targets"] = new[] { 100.0, 200.0 },
                    ["speed"] = 80.0,
                    ["accel"] = 400.0,
                    ["decel"] = 400.0,
                    ["timeoutMs"] = 1000
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "多轴测试",
                Nodes = new List<FlowNode> { start, interp },
                Connections = new List<FlowConnection> { new(start.Id, "Out", interp.Id, "In") }
            };

            var engine = new FlowEngineV2(motionController: fakeMotion);
            var ctx = new FlowContext("多轴测试", new FakeLoggerFactory());

            await engine.RunAsync(graph, ctx);

            Assert.Equal(2, fakeMotion.MoveAbsCalls.Count);
            Assert.Equal(0, fakeMotion.MoveAbsCalls[0].axisId);
            Assert.Equal(100.0, fakeMotion.MoveAbsCalls[0].target);
            Assert.Equal(1, fakeMotion.MoveAbsCalls[1].axisId);
            Assert.Equal(200.0, fakeMotion.MoveAbsCalls[1].target);
        }

        [Fact]
        public async Task AxisHomeNode_回零下发并成功到位()
        {
            var fakeMotion = new FakeMotionController
            {
                AutoCompleteOnMoveAbs = true,
                AutoCompleteDelayMs = 10
            };

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var home = new FlowNode("AxisHome", "X轴回零")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["axisId"] = 0,
                    ["mode"] = (int)HomingMode.OriginSignal,
                    ["dir"] = (int)HomeDirection.Negative,
                    ["speed"] = 30.0,
                    ["timeoutMs"] = 500
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "回零测试",
                Nodes = new List<FlowNode> { start, home },
                Connections = new List<FlowConnection> { new(start.Id, "Out", home.Id, "In") }
            };

            var engine = new FlowEngineV2(motionController: fakeMotion);
            var ctx = new FlowContext("回零测试", new FakeLoggerFactory());

            await engine.RunAsync(graph, ctx);

            Assert.Single(fakeMotion.HomeCalls);
            Assert.Equal(0, fakeMotion.HomeCalls[0].axisId);
            Assert.Equal(HomingMode.OriginSignal, fakeMotion.HomeCalls[0].mode);
            Assert.Equal(HomeDirection.Negative, fakeMotion.HomeCalls[0].dir);
        }

        [Fact]
        public async Task DiWaitNode_通过DiChanged事件完成等待()
        {
            var fakeIo = new FakeIoController();
            fakeIo.Di["CylinderSensor"] = false;

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var diWait = new FlowNode("DiWait", "等待气缸到位")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["pointName"] = "CylinderSensor",
                    ["expectedValue"] = true,
                    ["timeoutMs"] = 1000
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "DI测试",
                Nodes = new List<FlowNode> { start, diWait },
                Connections = new List<FlowConnection> { new(start.Id, "Out", diWait.Id, "In") }
            };

            var engine = new FlowEngineV2(ioController: fakeIo);
            var ctx = new FlowContext("DI测试", new FakeLoggerFactory());

            var runTask = engine.RunAsync(graph, ctx);

            // 50ms 后触发 DI 变更
            await Task.Delay(50);
            fakeIo.TriggerDi("CylinderSensor", true);

            await runTask;
            Assert.True(fakeIo.ReadDi("CylinderSensor"));
        }

        [Fact]
        public async Task DiWaitNode_超时失败_必须带超时()
        {
            var fakeIo = new FakeIoController();
            fakeIo.Di["AlwaysLow"] = false;

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var diWait = new FlowNode("DiWait", "超时等待")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["pointName"] = "AlwaysLow",
                    ["expectedValue"] = true,
                    ["timeoutMs"] = 50 // 50ms 超时
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "DI超时测试",
                Nodes = new List<FlowNode> { start, diWait },
                Connections = new List<FlowConnection> { new(start.Id, "Out", diWait.Id, "In") }
            };

            var engine = new FlowEngineV2(ioController: fakeIo);
            var ctx = new FlowContext("DI超时测试", new FakeLoggerFactory());

            var ex = await Assert.ThrowsAsync<StepExecuteException>(async () =>
            {
                await engine.RunAsync(graph, ctx);
            });

            Assert.Contains("超时", ex.Message);
        }

        [Fact]
        public async Task DoSetNode_写入DO点()
        {
            var fakeIo = new FakeIoController();

            var start = new FlowNode("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } };
            var doSet = new FlowNode("DoSet", "吸真空")
            {
                Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                Parameters = new Dictionary<string, object?>
                {
                    ["pointName"] = "VacuumValve",
                    ["value"] = true
                }
            };

            var graph = new FlowGraph
            {
                FlowName = "DO测试",
                Nodes = new List<FlowNode> { start, doSet },
                Connections = new List<FlowConnection> { new(start.Id, "Out", doSet.Id, "In") }
            };

            var engine = new FlowEngineV2(ioController: fakeIo);
            var ctx = new FlowContext("DO测试", new FakeLoggerFactory());

            await engine.RunAsync(graph, ctx);

            Assert.True(fakeIo.Do.TryGetValue("VacuumValve", out var val) && val);
        }

        [Fact]
        public async Task EventPublish_和_EventWait_通过IEventBus通信()
        {
            var fakeBus = new FakeEventBus();

            // 并行分支A：延时后发布事件（保证分支B先完成订阅）
            var branchA = new FlowGraph
            {
                FlowName = "EventBranchA",
                Nodes = new List<FlowNode>
                {
                    new FlowNode("Start", "分支A起始", "a1") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } },
                    new FlowNode("Delay", "延时80ms", "a2")
                    {
                        Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                        Parameters = new Dictionary<string, object?> { ["delayMs"] = 80 }
                    },
                    new FlowNode("EventPublish", "发布完成事件", "a3")
                    {
                        Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                        Parameters = new Dictionary<string, object?> { ["eventName"] = "AssemblyReady", ["payload"] = "Part_1001" }
                    }
                },
                Connections = new List<FlowConnection>
                {
                    new("a1", "Out", "a2", "In"),
                    new("a2", "Out", "a3", "In")
                }
            };

            // 并行分支B：立即订阅并等待事件
            var branchB = new FlowGraph
            {
                FlowName = "EventBranchB",
                Nodes = new List<FlowNode>
                {
                    new FlowNode("Start", "分支B起始", "b1") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } },
                    new FlowNode("EventWait", "等待处理事件", "b2")
                    {
                        Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In") },
                        Parameters = new Dictionary<string, object?>
                        {
                            ["eventName"] = "AssemblyReady",
                            ["timeoutMs"] = 2000,
                            ["outputKey"] = "ReceivedPart"
                        }
                    }
                },
                Connections = new List<FlowConnection>
                {
                    new("b1", "Out", "b2", "In")
                }
            };

            var tempDir = Path.Combine(Path.GetTempPath(), "sophon_flowv2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            FlowGraphStore.Save(branchA, tempDir);
            FlowGraphStore.Save(branchB, tempDir);

            var mainGraph = new FlowGraph
            {
                FlowName = "总线测试",
                Nodes = new List<FlowNode>
                {
                    new FlowNode("Start", "起始", "m1") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } },
                    new FlowNode("Parallel", "并行分流", "m2")
                    {
                        Ports = new List<FlowPort> { new("In", FlowPortDirection.In, "In"), new("Out", FlowPortDirection.Out, "Out") },
                        Parameters = new Dictionary<string, object?> { ["subFlowNames"] = "EventBranchA,EventBranchB" }
                    }
                },
                Connections = new List<FlowConnection> { new("m1", "Out", "m2", "In") }
            };

            var origDir = FlowGraphStore.DefaultBaseDirectory;
            FlowGraphStore.DefaultBaseDirectory = tempDir;
            try
            {
                var engine = new FlowEngineV2(eventBus: fakeBus);
                var ctx = new FlowContext("总线测试", new FakeLoggerFactory());

                await engine.RunAsync(mainGraph, ctx);

                Assert.Equal("Part_1001", ctx.GetData<object>("ReceivedPart")?.ToString());
            }
            finally
            {
                FlowGraphStore.DefaultBaseDirectory = origDir;
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
