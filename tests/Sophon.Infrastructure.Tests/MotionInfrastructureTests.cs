using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;
using Sophon.Infrastructure.Motion.Drivers;
using Sophon.Infrastructure.Motion.Sim;
using Sophon.Infrastructure.Motion.Stream;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    public class SimMotionControllerTests
    {
        private SimMotionController CreateController(out List<AxisDefinition> axes)
        {
            axes = new List<AxisDefinition>
            {
                new()
                {
                    AxisId = 0,
                    Name = "AxisX",
                    Unit = "mm",
                    PulsePerUnit = 1000,
                    SoftLimitEnabled = true,
                    SoftLimitMin = 0,
                    SoftLimitMax = 500,
                    MaxSpeed = 200,
                    MaxAccel = 1000,
                    MaxDecel = 1000,
                    HomeSpeed = 50,
                    HomeMode = HomingMode.OriginSignal,
                    HomeDir = HomeDirection.Negative,
                    HomeIoName = "HomeX",
                    LimitPositiveIoName = "LimitX+",
                    LimitNegativeIoName = "LimitX-"
                }
            };
            return new SimMotionController(axes);
        }

        [Fact]
        public async Task MoveAbs_ShouldReachTarget_AndTriggerAxisDone_WithinTimeout()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);
            Assert.True(controller.IsAxisEnabled(0));

            var tcs = new TaskCompletionSource<AxisDoneArgs>();
            controller.AxisDone += args =>
            {
                tcs.TrySetResult(args);
            };

            var reqId = controller.MoveAbs(0, 10.0, 100.0, 500.0, 500.0);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            Assert.Same(tcs.Task, completedTask);

            var doneArgs = await tcs.Task;
            Assert.Equal(reqId, doneArgs.RequestId);
            Assert.True(doneArgs.Success);
            Assert.InRange(controller.GetPosition(0), 9.99, 10.01);
        }

        [Fact]
        public async Task MoveAbs_SpeedShouldBeClamped_ToMaxSpeed()
        {
            using var controller = CreateController(out var axes);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            // 轴 MaxSpeed 为 200，请求 500
            controller.MoveAbs(0, 400.0, 500.0, 1000.0, 1000.0);

            // 运行一段时间观察最大速度
            double maxObservedSpeed = 0;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(20);
                double v = Math.Abs(controller.GetVelocity(0));
                if (v > maxObservedSpeed) maxObservedSpeed = v;
            }

            Assert.True(maxObservedSpeed <= axes[0].MaxSpeed + 1.0);
        }

        [Fact]
        public async Task MoveAbs_BeyondSoftLimit_ShouldRejectImmediately_AndTriggerLimit()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>();
            var limitTcs = new TaskCompletionSource<LimitTriggeredArgs>();

            controller.AxisDone += args => doneTcs.TrySetResult(args);
            controller.LimitTriggered += args => limitTcs.TrySetResult(args);

            // 轴 SoftLimitMax 为 500，请求 600
            var reqId = controller.MoveAbs(0, 600.0, 100.0, 500.0, 500.0);

            var completedDone = await Task.WhenAny(doneTcs.Task, Task.Delay(1000));
            var completedLimit = await Task.WhenAny(limitTcs.Task, Task.Delay(1000));

            Assert.Same(doneTcs.Task, completedDone);
            Assert.Same(limitTcs.Task, completedLimit);

            var doneArgs = await doneTcs.Task;
            Assert.Equal(reqId, doneArgs.RequestId);
            Assert.False(doneArgs.Success);

            var limitArgs = await limitTcs.Task;
            Assert.Equal(0, limitArgs.AxisId);
            Assert.False(limitArgs.IsHardLimit); // 软限位
            Assert.True(limitArgs.IsPositiveDirection);
        }

        [Fact]
        public async Task StopMotion_ShouldDecelerateToZero()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            controller.MoveAbs(0, 300.0, 100.0, 200.0, 200.0);
            await Task.Delay(100);

            Assert.True(Math.Abs(controller.GetVelocity(0)) > 0);

            controller.StopMotion(0);
            await Task.Delay(400);

            Assert.Equal(0.0, controller.GetVelocity(0), 2);
        }

        [Fact]
        public async Task Abort_ShouldStopImmediately()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var abortTcs = new TaskCompletionSource<AxisDoneArgs>();
            controller.AxisDone += args => abortTcs.TrySetResult(args);

            var reqId = controller.MoveAbs(0, 300.0, 100.0, 500.0, 500.0);
            await Task.Delay(80);

            controller.Abort(0);

            var completed = await Task.WhenAny(abortTcs.Task, Task.Delay(1000));
            Assert.Same(abortTcs.Task, completed);

            var doneArgs = await abortTcs.Task;
            Assert.Equal(reqId, doneArgs.RequestId);
            Assert.False(doneArgs.Success);
            Assert.Equal(0.0, controller.GetVelocity(0));
        }

        [Fact]
        public async Task Home_ShouldComplete_AndSetIsAxisHomedTrue()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var homeTcs = new TaskCompletionSource<AxisDoneArgs>();
            controller.AxisDone += args => homeTcs.TrySetResult(args);

            var reqId = controller.Home(0, HomingMode.OriginSignal, HomeDirection.Negative, 100.0);

            var completed = await Task.WhenAny(homeTcs.Task, Task.Delay(2500));
            Assert.Same(homeTcs.Task, completed);

            var doneArgs = await homeTcs.Task;
            Assert.Equal(reqId, doneArgs.RequestId);
            Assert.True(doneArgs.Success);
            Assert.True(controller.IsAxisHomed(0));
            Assert.InRange(controller.GetPosition(0), -0.01, 0.01);
        }

        [Fact]
        public async Task FaultInjection_HardLimit_ShouldTriggerEvents_AndStopAxis()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var limitTcs = new TaskCompletionSource<LimitTriggeredArgs>();
            var faultTcs = new TaskCompletionSource<AxisFaultArgs>();

            controller.LimitTriggered += args => limitTcs.TrySetResult(args);
            controller.AxisFault += args => faultTcs.TrySetResult(args);

            controller.MoveAbs(0, 200.0, 100.0, 500.0, 500.0);
            await Task.Delay(50);

            // 注入正向硬限位
            controller.InjectFault(FaultKind.PositiveHardLimit, 0, true);

            var completedLimit = await Task.WhenAny(limitTcs.Task, Task.Delay(1000));
            var completedFault = await Task.WhenAny(faultTcs.Task, Task.Delay(1000));

            Assert.Same(limitTcs.Task, completedLimit);
            Assert.Same(faultTcs.Task, completedFault);

            var limit = await limitTcs.Task;
            Assert.True(limit.IsHardLimit);
            Assert.True(limit.IsPositiveDirection);

            var fault = await faultTcs.Task;
            Assert.Equal("HARD_LIMIT_POS", fault.FaultCode);
            Assert.Equal(0.0, controller.GetVelocity(0));
        }

        [Fact]
        public async Task FaultInjection_Disconnected_ShouldSetFaultState_AndRejectCommands()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            // 注入掉线
            controller.InjectFault(FaultKind.Disconnected, 0, true);

            Assert.Equal(ConnectionState.Fault, controller.State);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>();
            controller.AxisDone += args => doneTcs.TrySetResult(args);

            var reqId = controller.MoveAbs(0, 50.0, 50.0, 200.0, 200.0);

            var completed = await Task.WhenAny(doneTcs.Task, Task.Delay(1000));
            Assert.Same(doneTcs.Task, completed);

            var done = await doneTcs.Task;
            Assert.Equal(reqId, done.RequestId);
            Assert.False(done.Success);
        }

        [Fact]
        public async Task PositionStreamSink_ShouldAcceptPositions_InSimController()
        {
            using var controller = CreateController(out _);
            var sink = controller as IPositionStreamSink;
            Assert.NotNull(sink);

            controller.EnableAxis(0);
            double[] received = null!;
            var autoReset = new AutoResetEvent(false);

            sink!.PositionAccepted += pos =>
            {
                received = pos;
                autoReset.Set();
            };

            sink.Begin(Guid.NewGuid(), 1, 1.0);
            // 提交时控制器需要处于 Ready 状态
            await controller.ConnectAsync();
            sink.Submit(new double[] { 15.5 });

            bool ok = autoReset.WaitOne(1000);
            sink.Complete();

            Assert.True(ok);
            Assert.NotNull(received);
            Assert.Equal(15.5, received[0]);
            Assert.Equal(15.5, controller.GetPosition(0));
        }
    }

    public class SimIoControllerTests
    {
        [Fact]
        public void SetDi_ShouldUpdateReadDi_AndTriggerDiChanged()
        {
            var io = new SimIoController();
            string changedName = null!;
            bool changedVal = false;
            var resetEvent = new AutoResetEvent(false);

            io.DiChanged += (name, val) =>
            {
                changedName = name;
                changedVal = val;
                resetEvent.Set();
            };

            Assert.False(io.ReadDi("LimitX+"));
            io.SetDi("LimitX+", true);

            bool triggered = resetEvent.WaitOne(1000);
            Assert.True(triggered);
            Assert.Equal("LimitX+", changedName);
            Assert.True(changedVal);
            Assert.True(io.ReadDi("LimitX+"));
        }

        [Fact]
        public void WriteDo_ShouldUpdateSnapshotDo()
        {
            var io = new SimIoController();
            io.WriteDo("GreenLight", true);

            var snapshot = io.SnapshotDo();
            Assert.True(snapshot["GreenLight"]);
        }
    }

    public class MotionControllerFactoryTests
    {
        [Fact]
        public void Create_Simulated_ShouldSucceed()
        {
            using var controller = MotionControllerFactory.Create(DriverKind.Simulated);
            Assert.NotNull(controller);
            Assert.Equal(DriverKind.Simulated, controller.Kind);
            var verified = controller as IVerifiedDriver;
            Assert.NotNull(verified);
            Assert.True(verified!.IsFieldVerified);
        }

        [Fact]
        public void Create_GoogolGts_ShouldReturnUnverifiedDriver()
        {
            using var controller = MotionControllerFactory.Create(DriverKind.GoogolGts);
            Assert.NotNull(controller);
            Assert.Equal(DriverKind.GoogolGts, controller.Kind);
            var verified = controller as IVerifiedDriver;
            Assert.NotNull(verified);
            Assert.False(verified!.IsFieldVerified); // 真卡骨架返回 false
        }

        [Fact]
        public void Create_LeadShineDmc_ShouldReturnUnverifiedDriver()
        {
            using var controller = MotionControllerFactory.Create(DriverKind.LeadShineDmc);
            Assert.NotNull(controller);
            Assert.Equal(DriverKind.LeadShineDmc, controller.Kind);
            var verified = controller as IVerifiedDriver;
            Assert.NotNull(verified);
            Assert.False(verified!.IsFieldVerified);
        }

        [Fact]
        public void Create_RealDriver_FailureWithoutFallback_ShouldThrowException()
        {
            // 模拟构造失败（例如缺少硬件环境或底层库报错）且未允许降级
            Assert.Throws<InvalidOperationException>(() =>
            {
                MotionControllerFactory.Create(
                    DriverKind.GoogolGts,
                    allowSimFallback: false,
                    customInstantiator: _ => throw new DllNotFoundException("gts.dll missing in test"));
            });
        }

        [Fact]
        public void Create_RealDriver_FailureWithFallback_ShouldFallbackToSim_WithWarning()
        {
            string? warningLog = null;
            using var controller = MotionControllerFactory.Create(
                DriverKind.GoogolGts,
                allowSimFallback: true,
                logger: msg => warningLog = msg,
                customInstantiator: _ => throw new DllNotFoundException("gts.dll missing in test"));

            Assert.NotNull(controller);
            Assert.Equal(DriverKind.Simulated, controller.Kind);
            Assert.NotNull(warningLog);
            Assert.Contains("降级到仿真控制器", warningLog);
        }
    }

    public class AxisManagerTests
    {
        [Fact]
        public async Task AxisManager_LifecycleAndSnapshot_ShouldWorkCorrectly()
        {
            using var sim = new SimMotionController();
            await sim.ConnectAsync();

            using var manager = new AxisManager(sim, refreshIntervalMs: 10);
            Assert.NotNull(manager.Controller);

            var snapshots = manager.GetSnapshots();
            Assert.NotEmpty(snapshots);
            Assert.Equal(AxisState.Disabled, snapshots[0].State);

            manager.EnableAll();
            await Task.Delay(50);

            var s1 = manager.GetSnapshot(0);
            Assert.NotNull(s1);
            Assert.True(s1!.Enabled);
            Assert.Equal(AxisState.Idle, s1.State);

            // 发起运动
            var tcs = new TaskCompletionSource<AxisDoneArgs>();
            manager.AxisDone += args => tcs.TrySetResult(args);

            var req = manager.MoveAbs(0, 5.0, 100.0, 500.0, 500.0);
            await Task.Delay(20);

            var movingSnap = manager.GetSnapshot(0);
            Assert.NotNull(movingSnap);
            Assert.True(movingSnap!.State == AxisState.Moving || movingSnap.State == AxisState.Idle);

            var done = await tcs.Task;
            Assert.Equal(req, done.RequestId);
            Assert.True(done.Success);

            await Task.Delay(50);
            var idleSnap = manager.GetSnapshot(0);
            Assert.Equal(AxisState.Idle, idleSnap!.State);
            Assert.InRange(idleSnap.Position, 4.99, 5.01);
        }

        [Fact]
        public async Task AxisManager_ShouldAggregateLimitAlarm()
        {
            using var sim = new SimMotionController();
            await sim.ConnectAsync();

            using var manager = new AxisManager(sim, refreshIntervalMs: 10);
            manager.EnableAll();

            var alarmTcs = new TaskCompletionSource<GlobalLimitAlarmArgs>();
            manager.GlobalLimitAlarm += alarm => alarmTcs.TrySetResult(alarm);

            // 触发硬限位故障
            sim.InjectFault(FaultKind.PositiveHardLimit, 0, true);

            var completed = await Task.WhenAny(alarmTcs.Task, Task.Delay(1000));
            Assert.Same(alarmTcs.Task, completed);

            var alarm = await alarmTcs.Task;
            Assert.Equal(0, alarm.AxisId);
            Assert.True(alarm.IsHardLimit);
            Assert.True(alarm.IsPositiveDirection);

            await Task.Delay(50);
            var snap = manager.GetSnapshot(0);
            Assert.Equal(AxisState.Error, snap!.State);
        }

        [Fact]
        public void AxisManager_ConfigSaveAndLoad_ShouldWork()
        {
            using var sim = new SimMotionController();
            using var manager = new AxisManager(sim);

            string tempPath = Path.Combine(Path.GetTempPath(), $"sophon_axis_test_{Guid.NewGuid():N}.json");
            try
            {
                manager.SaveConfiguration(tempPath);
                Assert.True(File.Exists(tempPath));

                var loaded = AxisManager.LoadConfiguration(tempPath);
                Assert.NotEmpty(loaded);
                Assert.Equal(sim.Axes.Count, loaded.Count);
                Assert.Equal(sim.Axes[0].Name, loaded[0].Name);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
    }
}
