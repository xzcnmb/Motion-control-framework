using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Alarm;
using Sophon.Core.Device;
using Sophon.Infrastructure.Motion.Axis;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// Item ⑧ 安全语义测试：气缸故障自动愈合安全边界、安全等级聚合、看门狗 Fail-Safe DO 复位。
    /// </summary>
    public class SafetySemanticsTests : IDisposable
    {
        private readonly string _tempFile;

        public SafetySemanticsTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"alarm_safety_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
        }

        /// <summary>测试用假 IO 控制器（记录 DO 写入序列，可回放磁性开关）。</summary>
        private sealed class SafetyFakeIo : IIoController
        {
            public Dictionary<string, bool> Di { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Do { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string name, bool val)> DoWrites { get; } = new();

            public event Action<string, bool> DiChanged;
            public IReadOnlyList<string> DiPointNames => new List<string>(Di.Keys);
            public IReadOnlyList<string> DoPointNames => new List<string>(Do.Keys);

            public bool ReadDi(string pointName) => Di.TryGetValue(pointName, out var v) && v;
            public void WriteDo(string pointName, bool value)
            {
                Do[pointName] = value;
                DoWrites.Add((pointName, value));
            }
            public IReadOnlyDictionary<string, bool> SnapshotDi() => new Dictionary<string, bool>(Di);
            public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>(Do);

            public void SetDi(string pointName, bool value)
            {
                Di[pointName] = value;
                DiChanged?.Invoke(pointName, value);
            }
        }

        private static CylinderDefinition MakeDoubleCoilCylinder(string name, string group, string workDo, string homeDo, string workSensor)
        {
            return new CylinderDefinition
            {
                Name = name,
                Valve = ValveType.DoubleCoil,
                WorkDoName = workDo,
                HomeDoName = homeDo,
                WorkSensorDiName = workSensor,
                HomeSensorDiName = null,
                InterlockGroup = group,
                PulseWidthMs = 10,
                ConfirmTimeoutMs = 60
            };
        }

        #region 气缸故障自动愈合安全边界

        [Fact]
        public async Task 气缸到位超时_释放互锁且严禁盲动反向线圈()
        {
            var io = new SafetyFakeIo();
            var center = new AlarmCenter(historyFilePath: _tempFile);
            var service = new CylinderService(io, center);

            var cylA = MakeDoubleCoilCylinder("CylA", "G1", "DO_A_WORK", "DO_A_HOME", "DI_A_WORK");

            // 磁性开关始终不触发 → 到位超时
            var res = await service.MoveToAsync(cylA, CylinderPosition.Work);

            Assert.False(res.Success);
            Assert.Contains("超时", res.Message);

            // 安全边界 1：互锁组占用必须被释放，同组其他气缸不得被永久锁死
            Assert.Null(service.GetInterlockHolder("G1"));

            // 安全边界 2：严禁盲目驱动反向线圈（Home 线圈从未被置 TRUE）
            Assert.DoesNotContain(io.DoWrites, w => w.name == "DO_A_HOME" && w.val == true);
            // 双线圈脉冲结束后保持双断电（阀机械保位，气缸维持当前物理状态）
            Assert.False(io.Do["DO_A_WORK"]);
            Assert.False(io.Do["DO_A_HOME"]);

            // 安全边界 3：登记正式安全故障报警（Error / StopFlow / 按气缸实例化）
            var alarm = center.ActiveAlarms.FirstOrDefault(a => a.Code == "CYLINDER_TIMEOUT#CylA");
            Assert.NotNull(alarm);
            Assert.Equal(AlarmSeverity.Error, alarm.Severity);
            Assert.Equal(LinkageMode.StopFlow, alarm.LinkageMode);
            Assert.Equal("CylinderService", alarm.Source);

            // 互锁已释放：同组另一气缸可正常占用工作位（证明未死锁）
            var cylB = new CylinderDefinition
            {
                Name = "CylB",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_B",
                InterlockGroup = "G1",
                PulseWidthMs = 10
            };
            var resB = await service.MoveToAsync(cylB, CylinderPosition.Work);
            Assert.True(resB.Success);
            Assert.Equal("CylB", service.GetInterlockHolder("G1"));
        }

        [Fact]
        public async Task 单电控气缸超时_撤除驱动电平并释放互锁()
        {
            var io = new SafetyFakeIo();
            var center = new AlarmCenter(historyFilePath: _tempFile);
            var service = new CylinderService(io, center);

            var cyl = new CylinderDefinition
            {
                Name = "CylS",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_S",
                WorkSensorDiName = "DI_S",
                InterlockGroup = "G2",
                PulseWidthMs = 10,
                ConfirmTimeoutMs = 60
            };

            var res = await service.MoveToAsync(cyl, CylinderPosition.Work);

            Assert.False(res.Success);
            // Fail-Safe：单电控撤除驱动电平（失电回到弹簧复位的设计安全位），防止无监控继续运动
            Assert.True(io.DoWrites.Exists(w => w.name == "DO_S" && w.val == true));
            Assert.False(io.Do["DO_S"]);
            // 互锁释放，不锁死同组
            Assert.Null(service.GetInterlockHolder("G2"));
            Assert.Contains(center.ActiveAlarms, a => a.Code == "CYLINDER_TIMEOUT#CylS");
        }

        [Fact]
        public async Task 动作取消且未确认到位_释放互锁并登记故障()
        {
            var io = new SafetyFakeIo();
            var center = new AlarmCenter(historyFilePath: _tempFile);
            var service = new CylinderService(io, center);

            var cyl = MakeDoubleCoilCylinder("CylC", "G4", "DO_C_WORK", "DO_C_HOME", "DI_C_WORK");
            cyl.ConfirmTimeoutMs = 5000; // 确保在取消前一直轮询

            using var cts = new CancellationTokenSource(100);

            // TaskCanceledException 派生自 OperationCanceledException，用 ThrowsAnyAsync 接受派生类型
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.MoveToAsync(cyl, CylinderPosition.Work, cts.Token));

            // 取消后互锁必须释放（否则同组气缸永久锁死）
            Assert.Null(service.GetInterlockHolder("G4"));
            // 驱动全部撤除
            Assert.False(io.Do["DO_C_WORK"]);
            Assert.False(io.Do["DO_C_HOME"]);
            // 未确认到位必须登记安全故障
            Assert.Contains(center.ActiveAlarms, a => a.Code == "CYLINDER_FAULT#CylC");
        }

        [Fact]
        public async Task 人工干预_强制释放互锁与占用查询()
        {
            var io = new SafetyFakeIo();
            var service = new CylinderService(io);

            var cylA = new CylinderDefinition
            {
                Name = "CylA",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_A",
                WorkSensorDiName = "DI_A",
                InterlockGroup = "G3",
                PulseWidthMs = 10,
                ConfirmTimeoutMs = 500
            };

            // 异步触发到位信号，使动作成功并占用互锁组
            _ = Task.Run(async () =>
            {
                await Task.Delay(30);
                io.SetDi("DI_A", true);
            });

            var resA = await service.MoveToAsync(cylA, CylinderPosition.Work);
            Assert.True(resA.Success);

            // 占用查询：CylA 持有 G3
            Assert.Equal("CylA", service.GetInterlockHolder("G3"));

            // 同组 CylB 被互锁拒绝
            var cylB = new CylinderDefinition
            {
                Name = "CylB",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_B",
                InterlockGroup = "G3",
                PulseWidthMs = 10
            };
            var resB = await service.MoveToAsync(cylB, CylinderPosition.Work);
            Assert.False(resB.Success);
            Assert.Contains("互锁", resB.Message);

            // 人工强制释放：返回 true，持有者清空
            Assert.True(service.ForceReleaseInterlock("G3"));
            Assert.Null(service.GetInterlockHolder("G3"));

            // 重复释放返回 false；不存在组 / 空组名返回 false 与 null
            Assert.False(service.ForceReleaseInterlock("G3"));
            Assert.False(service.ForceReleaseInterlock("NoSuchGroup"));
            Assert.False(service.ForceReleaseInterlock(""));
            Assert.Null(service.GetInterlockHolder("NoSuchGroup"));
            Assert.Null(service.GetInterlockHolder(""));

            // 释放后同组气缸可重新占用
            var resB2 = await service.MoveToAsync(cylB, CylinderPosition.Work);
            Assert.True(resB2.Success);
            Assert.Equal("CylB", service.GetInterlockHolder("G3"));
        }

        #endregion

        #region 安全等级聚合

        [Fact]
        public void 安全等级聚合_全部正常判为Safe()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs());
            Assert.Equal(SafetyLevel.Safe, eval.Level);
            Assert.True(eval.IsSafe);
            Assert.False(eval.IsFaultEStop);
            Assert.False(string.IsNullOrWhiteSpace(eval.Reason));
        }

        [Fact]
        public void 安全等级聚合_软限位越界判为Warning()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { SoftLimitTriggered = true });
            Assert.Equal(SafetyLevel.Warning, eval.Level);
            Assert.Contains("软限位", eval.Reason);
        }

        [Fact]
        public void 安全等级聚合_气缸互锁占用判为Interlocked()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { CylinderInterlockEngaged = true });
            Assert.Equal(SafetyLevel.Interlocked, eval.Level);
            Assert.Contains("互锁", eval.Reason);
        }

        [Fact]
        public void 安全等级聚合_瞬态方向禁止判为Interlocked()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { MotionProhibited = true });
            Assert.Equal(SafetyLevel.Interlocked, eval.Level);
        }

        [Fact]
        public void 安全等级聚合_Error级报警判为Interlocked()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { MaxAlarmSeverity = AlarmSeverity.Error });
            Assert.Equal(SafetyLevel.Interlocked, eval.Level);
        }

        [Fact]
        public void 安全等级聚合_看门狗超时判为FaultEStop()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { WatchdogActive = false });
            Assert.Equal(SafetyLevel.FaultEStop, eval.Level);
            Assert.True(eval.IsFaultEStop);
            Assert.Contains("看门狗", eval.Reason);
        }

        [Fact]
        public void 安全等级聚合_硬限位判为FaultEStop()
        {
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs { HardLimitTriggered = true });
            Assert.Equal(SafetyLevel.FaultEStop, eval.Level);
        }

        [Fact]
        public void 安全等级聚合_Stop与EStop级报警判为FaultEStop()
        {
            Assert.Equal(SafetyLevel.FaultEStop,
                SafetyEvaluation.Evaluate(new SafetyInputs { MaxAlarmSeverity = AlarmSeverity.Stop }).Level);
            Assert.Equal(SafetyLevel.FaultEStop,
                SafetyEvaluation.Evaluate(new SafetyInputs { MaxAlarmSeverity = AlarmSeverity.EStop }).Level);
        }

        [Fact]
        public void 安全等级聚合_故障急停优先于互锁与警告()
        {
            // 同时存在互锁、软限位与看门狗超时时，必须取最严重等级 FaultEStop
            var eval = SafetyEvaluation.Evaluate(new SafetyInputs
            {
                WatchdogActive = false,
                CylinderInterlockEngaged = true,
                SoftLimitTriggered = true,
                MaxAlarmSeverity = AlarmSeverity.Error
            });
            Assert.Equal(SafetyLevel.FaultEStop, eval.Level);
        }

        [Fact]
        public void 安全等级聚合_基于AlarmCenter全局报警严重级别联动()
        {
            var center = new AlarmCenter(historyFilePath: _tempFile);

            // 无活动报警 → Safe
            Assert.Equal(SafetyLevel.Safe,
                SafetyEvaluation.Evaluate(BuildInputsFromAlarmCenter(center)).Level);

            // 软限位 Error 报警激活 → Interlocked
            center.Raise("SOFT_LIMIT_ERROR#0", "Axis:0, Pos:105.0 > Max:100.0");
            Assert.Equal(SafetyLevel.Interlocked,
                SafetyEvaluation.Evaluate(BuildInputsFromAlarmCenter(center)).Level);

            // 升级为硬限位 EStop 报警 → FaultEStop
            center.Register(new AlarmDefinition("HARD_LIMIT_ESTOP#0", "轴 0 硬限位", AlarmSeverity.EStop, LinkageMode.EStopAll));
            center.Raise("HARD_LIMIT_ESTOP#0", "Axis:0, Point:DI_LIMIT, Dir:Pos, Hard:True");
            Assert.Equal(SafetyLevel.FaultEStop,
                SafetyEvaluation.Evaluate(BuildInputsFromAlarmCenter(center)).Level);

            // 全部消除 → 回到 Safe
            center.Clear("HARD_LIMIT_ESTOP#0");
            center.Clear("SOFT_LIMIT_ERROR#0");
            Assert.Empty(center.ActiveAlarms);
            Assert.Equal(SafetyLevel.Safe,
                SafetyEvaluation.Evaluate(BuildInputsFromAlarmCenter(center)).Level);
        }

        private static SafetyInputs BuildInputsFromAlarmCenter(AlarmCenter center)
        {
            var active = center.ActiveAlarms;
            return new SafetyInputs
            {
                MaxAlarmSeverity = active.Count == 0 ? null : active.Max(a => a.Severity)
            };
        }

        #endregion

        #region 看门狗 Fail-Safe DO 复位

        [Fact]
        public void 看门狗超时_触发报警并FailSafe复位气动电磁阀DO()
        {
            var axisDef = new AxisDefinition { AxisId = 0 };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            // 刷新周期设大，避免 AxisManager 后台线程自动推送快照干扰看门狗测试
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            var io = new FakeIoControllerForAlarmTeach();
            io.DoMap["DO_SOL_A"] = true;  // 气动电磁阀 A：动作中
            io.DoMap["DO_SOL_B"] = true;  // 气动电磁阀 B：动作中

            using var monitor = new GlobalLimitMonitor(
                axisMgr,
                center,
                new[] { axisDef },
                ioController: io,
                intervalMs: 15,
                watchdogTimeout: TimeSpan.FromMilliseconds(80),
                failSafeDoResetList: new[] { "DO_SOL_A", "DO_SOL_B" });

            // 初始心跳：无报警、无 Fail-Safe
            monitor.OnSnapshotsUpdated(new List<AxisSnapshot>());
            Assert.Empty(center.ActiveAlarms);
            Assert.False(monitor.FailSafeTriggered);
            Assert.True(io.DoMap["DO_SOL_A"]);
            Assert.True(io.DoMap["DO_SOL_B"]);

            // 快照停止更新超过阈值 → 看门狗超时
            Thread.Sleep(200);

            // 1) WATCHDOG_TIMEOUT 报警：Error / StopAllAxes
            var alarm = center.ActiveAlarms.FirstOrDefault(a => a.Code == "WATCHDOG_TIMEOUT");
            Assert.NotNull(alarm);
            Assert.Equal(AlarmSeverity.Error, alarm.Severity);
            Assert.Equal(LinkageMode.StopAllAxes, alarm.LinkageMode);

            // 2) Fail-Safe IO 关断：气动电磁阀 DO 立即强制置 FALSE
            Assert.True(monitor.FailSafeTriggered);
            Assert.False(io.DoMap["DO_SOL_A"]);
            Assert.False(io.DoMap["DO_SOL_B"]);
            Assert.Contains("DO_SOL_A", monitor.FailSafeResetDoPoints);
            Assert.Contains("DO_SOL_B", monitor.FailSafeResetDoPoints);

            // 3) 通信恢复：报警自动消除，但 Fail-Safe 锁存保持（防止静默恢复）
            monitor.OnSnapshotsUpdated(new List<AxisSnapshot>());
            Assert.DoesNotContain(center.ActiveAlarms, a => a.Code == "WATCHDOG_TIMEOUT");
            Assert.True(monitor.FailSafeTriggered);
            Assert.False(io.DoMap["DO_SOL_A"]);

            // 4) 人工确认后诊断复位：仅清锁存，不会重新点亮 DO
            monitor.ResetFailSafeIoShutdown();
            Assert.False(monitor.FailSafeTriggered);
            Assert.Empty(monitor.FailSafeResetDoPoints);
            Assert.False(io.DoMap["DO_SOL_A"]);
            Assert.False(io.DoMap["DO_SOL_B"]);
        }

        [Fact]
        public void 看门狗超时_未配置FailSafe清单时不写任何DO()
        {
            var axisDef = new AxisDefinition { AxisId = 0 };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            var io = new FakeIoControllerForAlarmTeach();
            io.DoMap["DO_SOL_A"] = true;

            // 未提供 failSafeDoResetList
            using var monitor = new GlobalLimitMonitor(
                axisMgr,
                center,
                new[] { axisDef },
                ioController: io,
                intervalMs: 15,
                watchdogTimeout: TimeSpan.FromMilliseconds(80));

            monitor.OnSnapshotsUpdated(new List<AxisSnapshot>());
            Thread.Sleep(200);

            Assert.Contains(center.ActiveAlarms, a => a.Code == "WATCHDOG_TIMEOUT");
            Assert.True(monitor.FailSafeTriggered);
            Assert.Empty(monitor.FailSafeResetDoPoints);
            // 未配置清单 → 不触碰任何 DO
            Assert.True(io.DoMap["DO_SOL_A"]);
        }

        [Fact]
        public void 人工急停_显式触发FailSafe关断指定DO()
        {
            var axisDef = new AxisDefinition { AxisId = 0 };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(axisDef);
            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 10000);
            var center = new AlarmCenter(historyFilePath: _tempFile);

            var io = new FakeIoControllerForAlarmTeach();
            io.DoMap["DO_SOL_A"] = true;
            io.DoMap["DO_LAMP"] = true;

            using var monitor = new GlobalLimitMonitor(
                axisMgr,
                center,
                new[] { axisDef },
                ioController: io,
                intervalMs: 15,
                watchdogTimeout: TimeSpan.FromMilliseconds(80),
                failSafeDoResetList: new[] { "DO_SOL_A" });

            // 人工显式触发（如急停按钮）：仅关断清单内的 DO
            monitor.TriggerFailSafeIoShutdown("EmergencyStop");

            Assert.True(monitor.FailSafeTriggered);
            Assert.False(io.DoMap["DO_SOL_A"]);
            Assert.True(io.DoMap["DO_LAMP"]); // 清单外 DO 不受影响
            Assert.Single(monitor.FailSafeResetDoPoints);
        }

        #endregion
    }
}
