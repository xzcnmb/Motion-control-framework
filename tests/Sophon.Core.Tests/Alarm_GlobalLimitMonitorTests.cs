using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sophon.Contracts;
using Sophon.Core.Alarm;
using Sophon.Infrastructure.Motion.Axis;
using Xunit;

namespace Sophon.Core.Tests
{
    public class Alarm_GlobalLimitMonitorTests : IDisposable
    {
        private readonly string _tempFile;

        public Alarm_GlobalLimitMonitorTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"alarm_limit_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
        }

        [Fact]
        public void 软限位越界_触发SOFT_LIMIT_ERROR与StopAxis联动()
        {
            var axisDef = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                SoftLimitEnabled = true,
                SoftLimitMin = -10.0,
                SoftLimitMax = 100.0
            };

            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            using var monitor = new GlobalLimitMonitor(axisMgr, center, new[] { axisDef }, intervalMs: 20);

            // 模拟快照更新：正常位置 50.0
            var normalSnapshots = new List<AxisSnapshot>
            {
                new AxisSnapshot(0, 50.0, 0, true, true, AxisState.Idle)
            };
            monitor.OnSnapshotsUpdated(normalSnapshots);
            Assert.Empty(center.ActiveAlarms);

            // 模拟快照更新：超出最大软限位 105.0
            var overSnapshots = new List<AxisSnapshot>
            {
                new AxisSnapshot(0, 105.0, 0, true, true, AxisState.Moving)
            };
            monitor.OnSnapshotsUpdated(overSnapshots);

            Assert.Single(center.ActiveAlarms);
            var alarm = center.ActiveAlarms[0];
            Assert.Equal("SOFT_LIMIT_ERROR#0", alarm.Code); // 按轴实例化：轴 0
            Assert.Equal(AlarmSeverity.Error, alarm.Severity);
            Assert.Equal(LinkageMode.StopAxis, alarm.LinkageMode);

            // 模拟回到安全范围 80.0，软限位自动清除
            var backSnapshots = new List<AxisSnapshot>
            {
                new AxisSnapshot(0, 80.0, 0, true, true, AxisState.Idle)
            };
            monitor.OnSnapshotsUpdated(backSnapshots);
            Assert.Empty(center.ActiveAlarms);
        }

        [Fact]
        public void 硬限位事件_触发HARD_LIMIT_ESTOP并置位瞬态协议禁止同向运动()
        {
            var axisDef = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                HardLimitEnabled = true,
                LimitPositiveIoName = "DI_LIMIT_X_POS"
            };

            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            var fakeIo = new FakeIoControllerForAlarmTeach();
            fakeIo.SetDi("DI_LIMIT_X_POS", true); // 当前正处于硬限位电平上

            using var monitor = new GlobalLimitMonitor(axisMgr, center, new[] { axisDef }, ioController: fakeIo);

            // 触发硬限位正向报警
            monitor.OnGlobalLimitAlarm(new GlobalLimitAlarmArgs(0, "DI_LIMIT_X_POS", IsPositiveDirection: true, IsHardLimit: true));

            Assert.Single(center.ActiveAlarms);
            var alarm = center.ActiveAlarms[0];
            Assert.Equal("HARD_LIMIT_ESTOP#0", alarm.Code); // 按轴实例化：轴 0
            Assert.Equal(AlarmSeverity.EStop, alarm.Severity);
            Assert.Equal(LinkageMode.EStopAll, alarm.LinkageMode);

            // 瞬态协议：禁止同方向运动
            Assert.True(monitor.IsDirectionProhibited(0, +1)); // 正向被禁止
            Assert.False(monitor.IsDirectionProhibited(0, -1)); // 允许反向退离

            // 尝试复位：此时限位电平依然为 true，复位应被拒绝
            bool resetResult = monitor.TryResetLimit(0);
            Assert.False(resetResult);
            Assert.Single(center.ActiveAlarms);

            // 限位电平离开
            fakeIo.SetDi("DI_LIMIT_X_POS", false);

            // 再次尝试复位，应成功
            resetResult = monitor.TryResetLimit(0);
            Assert.True(resetResult);
            Assert.Empty(center.ActiveAlarms);
            Assert.False(monitor.IsDirectionProhibited(0, +1));
        }

        [Fact]
        public void 硬限位复位_读DI失败必须拒绝不能放行()
        {
            var axisDef = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                HardLimitEnabled = true,
                LimitPositiveIoName = "DI_LIMIT_X_POS"
            };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);
            var throwingIo = new ThrowingIoController();
            using var monitor = new GlobalLimitMonitor(axisMgr, center, new[] { axisDef }, ioController: throwingIo);

            monitor.OnGlobalLimitAlarm(new GlobalLimitAlarmArgs(0, "DI_LIMIT_X_POS", IsPositiveDirection: true, IsHardLimit: true));
            Assert.False(monitor.TryResetLimit(0));
            Assert.Single(center.ActiveAlarms);
            Assert.True(monitor.IsDirectionProhibited(0, +1));
        }

        private sealed class ThrowingIoController : IIoController
        {
            public bool ReadDi(string pointName) => throw new InvalidOperationException("io down");
            public void WriteDo(string pointName, bool value) { }
            public IReadOnlyDictionary<string, bool> SnapshotDi() => throw new InvalidOperationException("io down");
            public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>();
            public event Action<string, bool>? DiChanged { add { } remove { } }
            public IReadOnlyList<string> DiPointNames => Array.Empty<string>();
            public IReadOnlyList<string> DoPointNames => Array.Empty<string>();
        }

        [Fact]
        public void 看门狗超时_快照停止更新超过阈值触发报警()
        {
            var axisDef = new AxisDefinition { AxisId = 0 };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            // 将 AxisManager 的刷新周期设大（如 10000ms），避免其后台刷新线程自动推送快照干扰测试
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            // 设置看门狗阈值为 80ms，检测周期 15ms
            using var monitor = new GlobalLimitMonitor(
                axisMgr,
                center,
                new[] { axisDef },
                intervalMs: 15,
                watchdogTimeout: TimeSpan.FromMilliseconds(80));

            // 一开始给一次心跳
            monitor.OnSnapshotsUpdated(new List<AxisSnapshot>());
            Assert.Empty(center.ActiveAlarms);

            // 等待超过 160ms 模拟快照停止更新
            Thread.Sleep(200);

            Assert.Contains(center.ActiveAlarms, a => a.Code == "WATCHDOG_TIMEOUT");

            // 恢复快照推送，看门狗报警应自动消除
            monitor.OnSnapshotsUpdated(new List<AxisSnapshot>());
            Assert.DoesNotContain(center.ActiveAlarms, a => a.Code == "WATCHDOG_TIMEOUT");
        }
    }
}
