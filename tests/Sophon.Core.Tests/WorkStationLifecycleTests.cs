using Sophon.Contracts;
using Sophon.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 工站生命周期回归：异常不逃逸、Alarm 须复位、暂停不另开任务、停止发 Cat1 受控停。
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
            Assert.DoesNotContain("B", recorder.Names);
            Assert.Equal(WorkStationState.Alarm, station.CurrentState);

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
            station.Start();

            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
        }

        [Fact]
        public async Task 空闲时停止_随后启动_流程仍完整执行()
        {
            var recorder = new ExecutionRecorder();
            var station = CreateStation(
                out _,
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            station.Stop();
            Assert.Equal(WorkStationState.Idle, station.CurrentState);

            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
        }

        [Fact]
        public async Task 暂停_挂在步骤边界_恢复后继续执行()
        {
            var recorder = new ExecutionRecorder();
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var station = CreateStation(
                out _,
                new TestSteps.GateStep("G", gate, entered),
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            station.Start();
            Assert.True(entered.Wait(2000));
            station.Pause();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);

            gate.Set();
            await Task.Delay(200);
            Assert.True(recorder.IsEmpty);

            station.Resume();
            Assert.Equal(WorkStationState.Running, station.CurrentState);
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Idle));
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
        }

        [Fact]
        public async Task 暂停中停止_状态为已停止且流程快速退出()
        {
            var recorder = new ExecutionRecorder();
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
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
            Assert.True(await TestHelper.WaitUntilAsync(() => recorder.IsEmpty));
        }

        [Fact]
        public async Task 报警后不复位不能启动()
        {
            var station = CreateStation(out _, new TestSteps.FailStep("A"));
            station.Start();
            Assert.True(await TestHelper.WaitUntilAsync(() => station.CurrentState == WorkStationState.Alarm));
            var ex = Assert.Throws<InvalidOperationException>(() => station.Start());
            Assert.Contains("复位", ex.Message);
            station.Reset();
            Assert.Equal(WorkStationState.Idle, station.CurrentState);
        }

        [Fact]
        public async Task 暂停时Start不另开循环任务()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var station = CreateStation(out _, new TestSteps.GateStep("G", gate, entered));
            station.Start();
            Assert.True(entered.Wait(2000));
            station.Pause();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);
            station.Start();
            Assert.Equal(WorkStationState.Paused, station.CurrentState);
            gate.Set();
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
        }

        [Fact]
        public void 停止对运动发受控停不发急停()
        {
            var motion = new FakeMotionControllerForAlarmTeach(
                new AxisDefinition { AxisId = 0, Name = "X" },
                new AxisDefinition { AxisId = 1, Name = "Y" });
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var station = new WorkStation(
                "TestStation",
                new FakeFlowEngineFactory(new IFlowStep[] { new TestSteps.GateStep("G", gate, entered) }),
                new FakeFlowContextFactory(),
                new StateMachine(),
                new WorkStationOptions { LoopRecipe = false, AxisIds = new[] { 0, 1 } },
                motion);

            station.Start();
            Assert.True(entered.Wait(2000));
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            Assert.Equal(new[] { 0, 1 }, motion.StoppedAxes.ToArray());
            Assert.Empty(motion.AbortedAxes);
            Assert.False(motion.AbortAllCalled);
            gate.Set();
        }

        [Fact]
        public void 停止只停轴组内的轴_不影响其他轴()
        {
            var motion = new FakeMotionControllerForAlarmTeach(
                new AxisDefinition { AxisId = 0, Name = "X" },
                new AxisDefinition { AxisId = 1, Name = "Y" },
                new AxisDefinition { AxisId = 2, Name = "Z" });
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var station = new WorkStation(
                "搬运",
                new FakeFlowEngineFactory(new IFlowStep[] { new TestSteps.GateStep("G", gate, entered) }),
                new FakeFlowContextFactory(),
                new StateMachine(),
                new WorkStationOptions { LoopRecipe = false, AxisIds = new[] { 0, 1 } },
                motion);

            station.Start();
            Assert.True(entered.Wait(2000));
            station.Stop();
            Assert.Equal(new[] { 0, 1 }, motion.StoppedAxes.ToArray());
            Assert.DoesNotContain(2, motion.StoppedAxes);
            gate.Set();
        }

        [Fact]
        public void 两个工站不能同时占用同一根轴()
        {
            var lease = new AxisGroupLease();
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var a = new WorkStation(
                "A",
                new FakeFlowEngineFactory(new IFlowStep[] { new TestSteps.GateStep("G", gate, entered) }),
                new FakeFlowContextFactory(),
                new StateMachine(),
                new WorkStationOptions { LoopRecipe = true, AxisIds = new[] { 0, 1 } },
                axisLease: lease);
            var b = new WorkStation(
                "B",
                new FakeFlowEngineFactory(new IFlowStep[] { new TestSteps.CountingStep("X", new ExecutionRecorder()) }),
                new FakeFlowContextFactory(),
                new StateMachine(),
                new WorkStationOptions { LoopRecipe = false, AxisIds = new[] { 1, 2 } },
                axisLease: lease);

            a.Start();
            Assert.True(entered.Wait(2000));
            var ex = Assert.Throws<InvalidOperationException>(() => b.Start());
            Assert.Contains("占用", ex.Message);
            a.Stop();
            gate.Set();
            b.Start();
            Assert.Equal(WorkStationState.Running, b.CurrentState);
            b.Stop();
        }

        [Fact]
        public async Task 停止后立刻启动_上一轮未退出则拒绝叠任务()
        {
            using var gate = new ManualResetEventSlim(false);
            using var entered = new ManualResetEventSlim(false);
            var station = CreateStation(out _, new IgnoreCancelUntilGate(gate, entered));

            station.Start();
            Assert.True(entered.Wait(2000));
            station.Stop();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);
            station.Start();
            Assert.Equal(WorkStationState.Stopped, station.CurrentState);

            gate.Set();
            Assert.True(await TestHelper.WaitUntilAsync(() =>
            {
                station.Start();
                return station.CurrentState == WorkStationState.Running;
            }));
            station.Stop();
        }

        private sealed class IgnoreCancelUntilGate : IFlowStep
        {
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _entered;

            public IgnoreCancelUntilGate(ManualResetEventSlim gate, ManualResetEventSlim entered)
            {
                _gate = gate;
                _entered = entered;
            }

            public string StepName => "IgnoreCancel";

            public Task<StepResult> AsyncExecuteStep(IFlowContext context, CancellationToken token)
            {
                _entered.Set();
                return Task.Run(() =>
                {
                    _gate.Wait();
                    context.NextStepIndex++;
                    return StepResult.Success();
                });
            }
        }
    }
}
