using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Sim;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    /// <summary>
    /// P0-3 / P1-1 / P1-2 仿真控制器可验证最小闭环：
    /// 急停锁定与复位、普通取消 CommandAborted、Halt/Stop/Abort 可观察区分、回零状态机。
    /// </summary>
    public class SimMotionSafetyTests
    {
        private const int DefaultTimeoutMs = 1500;

        /// <summary>构造测试轴集合。<paramref name="span"/> 为软限位跨度（影响回零搜索行程，用于快速触发熔断）。</summary>
        private static List<AxisDefinition> BuildAxes(int count = 1, string? homeIo = "HomeX", double span = 500)
        {
            var axes = new List<AxisDefinition>();
            for (int i = 0; i < count; i++)
            {
                axes.Add(new AxisDefinition
                {
                    AxisId = i,
                    Name = $"Axis{i}",
                    Unit = "mm",
                    PulsePerUnit = 1000,
                    SoftLimitEnabled = true,
                    SoftLimitMin = 0,
                    SoftLimitMax = span,
                    MaxSpeed = 200,
                    MaxAccel = 1000,
                    MaxDecel = 1000,
                    HomeSpeed = 50,
                    HomeMode = HomingMode.OriginSignal,
                    HomeDir = HomeDirection.Negative,
                    HomeIoName = homeIo == null ? null : $"Home{i}",
                    LimitPositiveIoName = $"Limit{i}+",
                    LimitNegativeIoName = $"Limit{i}-"
                });
            }
            return axes;
        }

        /// <summary>无 Thread.Sleep 的异步等待：只轮询可观测状态，避免睡固定时间带来的抖动。</summary>
        private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (predicate()) return true;
                await Task.Delay(10);
            }
            return predicate();
        }

        private static async Task<T> CompleteWithinAsync<T>(Task<T> task, int timeoutMs = DefaultTimeoutMs)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
            Assert.Same(task, completed);
            return await task;
        }

        private SimMotionController CreateController(out SimIoController io, int axisCount = 1, string? homeIo = "HomeX", double span = 500)
        {
            io = new SimIoController();
            return new SimMotionController(BuildAxes(axisCount, homeIo, span), ioController: io);
        }

        // ---------------- P0-3: 急停/Abort 完成在途请求 + ErrorStop 锁定 + ResetAxis 恢复 ----------------

        [Fact]
        public async Task EmergencyStop_ShouldCompleteInFlight_AsError_AndBlockMotion_UntilResetAxis()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.AxisDone += a => doneTcs.TrySetResult(a);

            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(0)) > 0), "运动应已启动");

            // 1) 急停：在途请求必须以 Error 完结，不能悬挂
            controller.EmergencyStop(0);
            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.Equal(0.0, controller.GetVelocity(0));
            var eventArgs = await CompleteWithinAsync(doneTcs.Task);
            Assert.Equal(doneArgs.RequestId, eventArgs.RequestId);

            // 2) ErrorStop 锁定：新运动（Jog/Move/Home）必须明确失败，不得静默启动
            var moved = await controller.MoveAbsAsync(0, 100, 100, 1000, 1000);
            Assert.False(moved.Success);
            Assert.Equal(CommandCompletionStatus.Error, moved.Status);

            var jogTcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.AxisDone += a => jogTcs.TrySetResult(a);

            var jogId = controller.Jog(0, 1, 50);
            var jogArgs = await CompleteWithinAsync(jogTcs.Task);
            Assert.Equal(jogId, jogArgs.RequestId);
            Assert.False(jogArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, jogArgs.Status);

            var homed = await controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.False(homed.Success);
            Assert.Equal(CommandCompletionStatus.Error, homed.Status);
            Assert.Equal(0.0, controller.GetVelocity(0));

            // 3) 复位后恢复：清除锁定，重新运动成功
            controller.ResetAxis(0);
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0));
            var recovered = await controller.MoveAbsAsync(0, 100, 100, 1000, 1000);
            Assert.True(recovered.Success);
            Assert.Equal(CommandCompletionStatus.Done, recovered.Status);
        }

        [Fact]
        public async Task Abort_ShouldLatchErrorStop_WithAbortedNotDoneStatus()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(0)) > 0));

            controller.Abort(0);

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.Equal(0.0, controller.GetVelocity(0));

            // Abort 与 EmergencyStop 同为锁定位：必须与 Stop/Halt（CommandAborted、不锁定）可区分
            var after = await controller.MoveAbsAsync(0, 10, 100, 1000, 1000);
            Assert.False(after.Success);
            Assert.Equal(CommandCompletionStatus.Error, after.Status);
        }

        [Fact]
        public async Task AbortAll_ShouldCompleteAllAxes_AsError_AndEmergencyStopAllMatchIt()
        {
            using var controller = CreateController(out _, axisCount: 3);
            await controller.ConnectAsync();
            controller.EnableAxis(0);
            controller.EnableAxis(1);
            controller.EnableAxis(2);

            var tasks = new[] { 0, 1, 2 }
                .Select(i => controller.MoveAbsAsync(i, 400, 100, 1000, 1000))
                .ToArray();
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(1)) > 0));

            controller.AbortAll();

            foreach (var t in tasks)
            {
                var args = await CompleteWithinAsync(t);
                Assert.False(args.Success);
                Assert.Equal(CommandCompletionStatus.Error, args.Status);
            }
            foreach (var i in new[] { 0, 1, 2 })
            {
                Assert.Equal(0.0, controller.GetVelocity(i), 3);
            }

            controller.ResetAxis(1);
            var recovered = await controller.MoveAbsAsync(1, 10, 100, 1000, 1000);
            Assert.True(recovered.Success);
        }

        [Fact]
        public async Task EmergencyStopAll_ShouldCompleteInFlight_AsError()
        {
            using var controller = CreateController(out _, axisCount: 2);
            await controller.ConnectAsync();
            controller.EnableAxis(0);
            controller.EnableAxis(1);

            var t0 = controller.MoveAbsAsync(0, 400, 100, 1000, 1000);
            var t1 = controller.MoveAbsAsync(1, 400, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(1)) > 0));

            controller.EmergencyStopAll();

            foreach (var t in new[] { t0, t1 })
            {
                var args = await CompleteWithinAsync(t);
                Assert.False(args.Success);
                Assert.Equal(CommandCompletionStatus.Error, args.Status);
            }
        }

        // ---------------- P1-1: 普通取消必须是 CommandAborted，不得悬挂、不得升级 ErrorStop ----------------

        [Fact]
        public async Task Halt_ShouldCompleteInFlight_AsCommandAborted_WithoutErrorStop_AndAllowNextMotion()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(0)) > 0));

            controller.Halt(0);

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.CommandAborted, doneArgs.Status);
            Assert.Equal(0.0, controller.GetVelocity(0), 3);

            // Halt 是工艺暂停，不进入 ErrorStop：应允许立即再次运动
            var after = await CompleteWithinAsync(controller.MoveAbsAsync(0, 50, 100, 1000, 1000));
            Assert.True(after.Success);
            Assert.Equal(CommandCompletionStatus.Done, after.Status);
        }

        [Fact]
        public async Task Stop_ShouldCompleteInFlight_AsCommandAborted_AndDecelerateToZero()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(0)) > 10));

            controller.StopMotion(0);

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.CommandAborted, doneArgs.Status);

            // 可观察差异：Stop(Cat1) 应受控减速收敛到零，而不是骤停
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0), "Stop 后应减速到零");
            Assert.Equal(0.0, controller.GetVelocity(0));

            // Stop 不是急停：不进入 ErrorStop，可继续运动
            var after = await CompleteWithinAsync(controller.MoveAbsAsync(0, 50, 100, 1000, 1000));
            Assert.True(after.Success);
        }

        [Fact]
        public async Task MoveAbsAsync_ExternalCancel_ShouldCompleteAsCommandAborted_NotThrownCancellation()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            using var cts = new CancellationTokenSource();
            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000, 0, cts.Token);
            Assert.True(await WaitUntilAsync(() => Math.Abs(controller.GetVelocity(0)) > 0));

            cts.Cancel();

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.CommandAborted, doneArgs.Status);
            Assert.False(task.IsCanceled);
            Assert.False(task.IsFaulted);

            // 普通取消必须平滑停下且不锁定
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0));
            var after = await CompleteWithinAsync(controller.MoveAbsAsync(0, 50, 100, 1000, 1000));
            Assert.True(after.Success);
        }

        [Fact]
        public async Task MoveAbsAsync_CancelBeforeStart_ShouldCompleteAsCommandAborted_AndNeverMove()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var task = controller.MoveAbsAsync(0, 400, 100, 1000, 1000, 0, cts.Token);

            var doneArgs = await CompleteWithinAsync(task, 1000);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.CommandAborted, doneArgs.Status);
            Assert.False(task.IsCanceled);
            Assert.Equal(0.0, controller.GetVelocity(0));
            Assert.Equal(0.0, controller.GetPosition(0));
        }

        [Fact]
        public async Task HomeAsync_ExternalCancel_ShouldCompleteAsCommandAborted_AndNotLatch()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            using var cts = new CancellationTokenSource();
            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20, cts.Token);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) < -1), "回零应已开始向负向搜索");

            cts.Cancel();

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.CommandAborted, doneArgs.Status);
            Assert.False(task.IsCanceled);
            Assert.False(controller.IsAxisHomed(0));
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0));

            // 普通回零取消不进入 ErrorStop
            var after = await CompleteWithinAsync(controller.MoveAbsAsync(0, 50, 100, 1000, 1000));
            Assert.True(after.Success);
        }

        // ---------------- P1-2: 回零有阶段/事件驱动状态机 + 超时 + 失败可见 ----------------

        [Fact]
        public async Task Home_OriginSignalTriggered_ShouldCompleteThroughStateMachine_AndZeroPosition()
        {
            using var controller = CreateController(out var io);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.AxisDone += a => doneTcs.TrySetResult(a);

            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) < -1), "应先向负向搜索原点");

            // 事件驱动：原点输入触发后应完成（不是固定距离成功）
            io.SetDi("Home0", true);

            var doneArgs = await CompleteWithinAsync(task, 3000);
            Assert.True(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Done, doneArgs.Status);
            Assert.True(controller.IsAxisHomed(0));
            Assert.Equal(0.0, controller.GetPosition(0), 3);
            Assert.Equal(0.0, controller.GetVelocity(0), 3);

            // Sync Home 也必须通过 AxisDone 事件回报完成
            var syncDone = await CompleteWithinAsync(doneTcs.Task);
            Assert.Equal(doneArgs.RequestId, syncDone.RequestId);
        }

        [Fact]
        public async Task Home_SyncApi_ShouldCompleteViaAxisDoneEvent_AndZeroPosition()
        {
            using var controller = CreateController(out var io);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.AxisDone += a => doneTcs.TrySetResult(a);

            var reqId = controller.Home(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) < -1));
            io.SetDi("Home0", true);

            var completed = await Task.WhenAny(doneTcs.Task, Task.Delay(3000));
            Assert.Same(doneTcs.Task, completed);
            var doneArgs = await doneTcs.Task;
            Assert.Equal(reqId, doneArgs.RequestId);
            Assert.True(doneArgs.Success);
            Assert.True(controller.IsAxisHomed(0));
            Assert.InRange(controller.GetPosition(0), -0.01, 0.01);
        }

        [Fact]
        public async Task Home_OriginNeverTriggered_ShouldFailVisible_NotHangForever()
        {
            // 小跨度使搜索行程很短，验证"走满行程仍未碰原点"的熔断
            using var controller = CreateController(out _, span: 20);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 100);

            var doneArgs = await CompleteWithinAsync(task, 5000);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.False(controller.IsAxisHomed(0));
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0), "失败后必须停住");
            Assert.Contains("原点", doneArgs.Reason);
        }

        [Fact]
        public async Task Home_StageTimeout_ShouldFailVisible_EvenIfAxisKeepsMoving()
        {
            // 极慢回零速度：行程上限远未走满，但总超时必须兜底熔断，不得永久挂起
            using var controller = CreateController(out _, span: 500);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 1);

            var doneArgs = await CompleteWithinAsync(task, 10000);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.False(controller.IsAxisHomed(0));
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0));
        }

        [Fact]
        public async Task Home_EmergencyStop_ShouldFailVisible_AndLatch()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) < -1));

            controller.EmergencyStop(0);

            var doneArgs = await CompleteWithinAsync(task);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.False(controller.IsAxisHomed(0));
            Assert.Equal(0.0, controller.GetVelocity(0), 3);

            // 急停锁定：复位前不能重新回零
            var again = await controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.False(again.Success);
            Assert.Equal(CommandCompletionStatus.Error, again.Status);
        }

        [Fact]
        public async Task Home_HardLimitDuringSearch_ShouldFailVisible()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var task = controller.HomeAsync(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) < -1));

            // 注入硬限位故障：回零必须失败可见
            controller.InjectFault(FaultKind.NegativeHardLimit, 0, true);

            var doneArgs = await CompleteWithinAsync(task, 3000);
            Assert.False(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, doneArgs.Status);
            Assert.False(controller.IsAxisHomed(0));
            Assert.Equal(0.0, controller.GetVelocity(0), 3);
        }

        [Fact]
        public async Task Home_WhenHomeIoNotConfigured_ShouldFailWithExplicitError()
        {
            using var controller = CreateController(out _, homeIo: null);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            var doneTcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.AxisDone += a => doneTcs.TrySetResult(a);

            var reqId = controller.Home(0, HomingMode.OriginSignal, HomeDirection.Negative, 20);

            var eventArgs = await CompleteWithinAsync(doneTcs.Task, 1000);
            Assert.Equal(reqId, eventArgs.RequestId);
            Assert.False(eventArgs.Success);
            Assert.Equal(CommandCompletionStatus.Error, eventArgs.Status);
            Assert.Contains("HomeIoName", eventArgs.Reason);
            Assert.False(controller.IsAxisHomed(0));
        }

        [Fact]
        public async Task Home_CurrentPositionMode_ShouldZeroPosition_AndSucceedWithoutOriginInput()
        {
            using var controller = CreateController(out _);
            await controller.ConnectAsync();
            controller.EnableAxis(0);

            controller.MoveAbs(0, 120, 100, 1000, 1000);
            Assert.True(await WaitUntilAsync(() => controller.GetPosition(0) > 100));
            Assert.True(await WaitUntilAsync(() => controller.GetVelocity(0) == 0));

            var task = controller.HomeAsync(0, HomingMode.CurrentPosition, HomeDirection.Negative, 20);

            var doneArgs = await CompleteWithinAsync(task);
            Assert.True(doneArgs.Success);
            Assert.Equal(CommandCompletionStatus.Done, doneArgs.Status);
            Assert.True(controller.IsAxisHomed(0));
            Assert.Equal(0.0, controller.GetPosition(0), 3);
        }
    }
}
