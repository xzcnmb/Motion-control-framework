using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sophon.Core;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 工站跑流程编辑器同一套 v2 节点图；生产默认循环，测试单次模式仍回 Idle。
    /// </summary>
    public class WorkStationV2GraphTests : IDisposable
    {
        private readonly string _dir;

        public WorkStationV2GraphTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sophon-ws-v2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    Directory.Delete(_dir, true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public async Task 工站启动_单次模式_执行v2图后回到空闲()
        {
            const string name = "搬运工站";
            SaveLinear(name, 20, 20);
            var station = CreateStation(name, loop: false);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(1, station.CycleCount);
        }

        [Fact]
        public async Task 没有v2流程图_启动进入报警_不假装跑完()
        {
            var station = CreateStation("空工站", loop: false);
            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Alarm));
        }

        [Fact]
        public async Task 启动后立刻暂停_配方必须挂住不能空跑完()
        {
            const string name = "即停工站";
            SaveLinear(name, 80, 80, 80);
            var station = CreateStation(name, loop: false);

            station.Start();
            station.Pause();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);
            await Task.Delay(250);
            Assert.Equal(WorkStationState.Paused, station.CurrentState);
            Assert.Equal(0, station.CycleCount);
            station.Resume();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle, timeoutMs: 5000));
            Assert.Equal(1, station.CycleCount);
        }

        [Fact]
        public async Task 工站暂停恢复_v2节点边界挂起后能继续完成()
        {
            const string name = "暂停工站";
            SaveLinear(name, 80, 80);
            var station = CreateStation(name, loop: false);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Running));
            await Task.Delay(30);
            station.Pause();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);

            station.Resume();
            Assert.Equal(WorkStationState.Running, station.CurrentState);
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle, timeoutMs: 5000));
        }

        [Fact]
        public async Task 工站停止_取消正在执行的v2延时节点()
        {
            const string name = "停止工站";
            SaveLinear(name, 10000);
            var station = CreateStation(name, loop: true);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Running));
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            await Task.Delay(80);
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
        }

        [Fact]
        public async Task 工站循环模式_跑完配方继续下一圈_直到停止()
        {
            const string flow = "循环配方";
            SaveLinear(flow, 15);
            var station = new WorkStation(
                "循环工站",
                new V2GraphFlowEngineFactory(_dir),
                new FakeFlowContextFactory(),
                new StateMachine(),
                WorkStationOptions.Cyclic(flow));

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CycleCount >= 2, timeoutMs: 5000));
            Assert.Equal(WorkStationState.Running, station.CurrentState);
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            Assert.True(station.CycleCount >= 2);
        }

        [Fact]
        public async Task 工站绑定另一张流程图_启动跑的是绑定的配方()
        {
            SaveLinear("配方A", 10);
            SaveLinear("配方B", 10);
            var station = new WorkStation(
                "装配工站",
                new V2GraphFlowEngineFactory(_dir),
                new FakeFlowContextFactory(),
                new StateMachine(),
                new WorkStationOptions { BoundFlowName = "配方A", LoopRecipe = false });

            Assert.Equal("配方A", station.BoundFlowName);
            station.BindRecipe("配方B");
            Assert.Equal("配方B", station.BoundFlowName);
            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(1, station.CycleCount);
        }

        [Fact]
        public void 运行中禁止换配方()
        {
            SaveLinear("配方A", 10000);
            var station = CreateStation("配方A", loop: true);
            station.Start();
            Assert.Throws<InvalidOperationException>(() => station.BindRecipe("别的"));
            station.Stop();
        }

        private WorkStation CreateStation(string name, bool loop) =>
            new WorkStation(
                name,
                new V2GraphFlowEngineFactory(_dir),
                new FakeFlowContextFactory(),
                new StateMachine(),
                loop ? WorkStationOptions.Cyclic(name) : WorkStationOptions.SingleShot);

        private void SaveLinear(string flowName, params int[] delayMs)
        {
            var start = new FlowNode("Start", "起始")
            {
                Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") }
            };
            var nodes = new List<FlowNode> { start };
            var connections = new List<FlowConnection>();
            FlowNode prev = start;
            for (int i = 0; i < delayMs.Length; i++)
            {
                var d = new FlowNode("Delay", "D" + (i + 1))
                {
                    Ports = new List<FlowPort>
                    {
                        new("In", FlowPortDirection.In, "In"),
                        new("Out", FlowPortDirection.Out, "Out")
                    },
                    Parameters = new Dictionary<string, object?> { ["delayMs"] = delayMs[i] }
                };
                nodes.Add(d);
                connections.Add(new FlowConnection(prev.Id, "Out", d.Id, "In"));
                prev = d;
            }

            var graph = new FlowGraph
            {
                FlowName = flowName,
                Nodes = nodes,
                Connections = connections
            };
            FlowGraphStore.Save(graph, _dir);
        }

        private sealed class V2GraphFlowEngineFactory : IFlowEngineFactory
        {
            private readonly string _dir;
            public V2GraphFlowEngineFactory(string dir) => _dir = dir;
            public IFlowEngine CreateFlowEngine(string flowName) =>
                new FlowEngineV2Host(flowName, graphDirectory: _dir);
        }
    }
}
