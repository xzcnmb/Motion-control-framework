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
    /// 工站必须跑流程编辑器同一套 v2 节点图，不能再走空的 v1 线性步骤。
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
        public async Task 工站启动_执行v2流程图_完成后回到空闲()
        {
            const string name = "搬运工站";
            SaveLinear(name, 20, 20);
            var station = CreateStation(name);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
        }

        [Fact]
        public async Task 没有v2流程图_启动进入报警_不假装跑完()
        {
            var station = CreateStation("空工站");
            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Alarm));
        }

        [Fact]
        public async Task 工站暂停恢复_v2节点边界挂起后能继续完成()
        {
            const string name = "暂停工站";
            SaveLinear(name, 80, 80);
            var station = CreateStation(name);

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
            var station = CreateStation(name);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Running));
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            await Task.Delay(80);
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
        }

        private WorkStation CreateStation(string name) =>
            new WorkStation(
                name,
                new V2GraphFlowEngineFactory(_dir),
                new FakeFlowContextFactory(),
                new StateMachine());

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
