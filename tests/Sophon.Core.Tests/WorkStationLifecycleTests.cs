using Sophon.Core;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 工站生命周期回归测试：覆盖原版全部已知缺陷场景（异常逃逸卡 Running/
    /// Idle 时 Stop 残留 _isStopped/启启并发/暂停停止/停止后重启）。
    /// 每个测试实例独立记录器，可安全并行。
    /// </summary>
    public class WorkStationLifecycleTests
    {
        private static WorkStation CreateStation(out ExecutionRecorder recorder, params IFlowStep[] stepList)
        {
            recorder = new ExecutionRecorder();
            return new WorkStation(
                "TestStation",
                new FakeFlowEngineFactory(stepList.ToList()),
                new FakeFlowContextFactory(),
                new StateMachine(),
                WorkStationOptions.SingleShot);
        }

        [Fact]
        public async Task 流程正常完成_回到空闲_全部步骤按序执行()
        {
            var recorder = new ExecutionRecorder();
            var station = CreateStation(
                out _,
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder),
                new TestSteps.CountingStep("C", recorder));

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B", "C" }, recorder.Names);
        }

        [Fact]
        public async Task 步骤失败_进入报警_复位后可以重新启动()
        {
            var recorder = new ExecutionRecorder();
            var station = CreateStation(
                out _,
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.FailStep("F"),
                new TestSteps.CountingStep("B", recorder));

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Alarm));
            // 失败后 B 不应执行；状态必须离开 Running（原版缺陷：永久卡 Running）
            Assert.DoesNotContain("B", recorder.Names);
            Assert.Equal(WorkStationState.Alarm, station.CurrentState);

            // 复位 → 重启可再次执行
            station.Reset();
            Assert.Equal(WorkStationState.Idle, station.CurrentState);
            recorder.Clear();
            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Alarm));
            Assert.Contains("A", recorder.Names);
        }

        [Fact]
        public async Task 重复启动_只运行一份流程_不抛异常()
        {
            var recorder = new ExecutionRecorder();
            var station = CreateStation(
                out _,
                new TestSteps.DelayStep("D1", 80),
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            station.Start();
            await Task.Delay(10);
            station.Start(); // 运行中重复启动：应被忽略

            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names); // 只执行一遍
        }

        [Fact]
        public async Task 空闲时停止_随后启动_流程仍完整执行()
        {
            // 原版缺陷：Idle 态 Stop 使 _isStopped 残留，导致下一次 Start 一步都不执行
            var recorder = new ExecutionRecorder();
            var station = CreateStation(
                out _,
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            station.Stop(); // Idle → 无操作
            Assert.Equal(WorkStationState.Idle, station.CurrentState);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
        }

        [Fact]
        public async Task 暂停_挂在步骤边界_恢复后继续执行()
        {
            var recorder = new ExecutionRecorder();
            using var gate = new System.Threading.ManualResetEventSlim(false);
            using var entered = new System.Threading.ManualResetEventSlim(false);
            var station = CreateStation(
                out _,
                new TestSteps.GateStep("G", gate, entered),
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            station.Start();
            Assert.True(entered.Wait(2000));           // 门控步骤已进入
            station.Pause();                            // 暂停请求
            Assert.Equal(WorkStationState.Paused, station.CurrentState);

            gate.Set();                                 // 放行门控步骤 → 引擎应在边界挂起
            await Task.Delay(200);
            Assert.True(recorder.IsEmpty);              // 暂停期间不执行后续步骤

            station.Resume();
            Assert.Equal(WorkStationState.Running, station.CurrentState);
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
        }

        [Fact]
        public async Task 暂停中停止_状态为已停止且流程快速退出()
        {
            var recorder = new ExecutionRecorder();
            using var gate = new System.Threading.ManualResetEventSlim(false);
            using var entered = new System.Threading.ManualResetEventSlim(false);
            var station = CreateStation(
                out _,
                new TestSteps.GateStep("G", gate, entered),
                new TestSteps.CountingStep("A", recorder));

            station.Start();
            Assert.True(entered.Wait(2000));
            station.Pause();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);
            gate.Set();

            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            Assert.True(await TestHelper.WaitUntilAsync(() => recorder.IsEmpty)); // A 未执行
        }
    }
}