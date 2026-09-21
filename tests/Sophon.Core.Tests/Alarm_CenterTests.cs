using System;
using System.Collections.Generic;
using System.IO;
using Sophon.Core.Alarm;
using Xunit;

namespace Sophon.Core.Tests
{
    public class Alarm_CenterTests
    {
        private class TestLinkageHandler : IAlarmLinkageHandler
        {
            public int HandledCount { get; private set; }
            public AlarmDefinition? LastDef { get; private set; }
            public string? LastDetail { get; private set; }

            public void Handle(AlarmDefinition def, string detail)
            {
                HandledCount++;
                LastDef = def;
                LastDetail = detail;
            }
        }

        [Fact]
        public void 报警注册_触发与消除_状态流转与事件正常()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"alarm_test_{Guid.NewGuid():N}.json");
            try
            {
                var linkage = new TestLinkageHandler();
                var center = new AlarmCenter(linkage, tempFile);

                var def = new AlarmDefinition("ERR_001", "电机过载", AlarmSeverity.Error, LinkageMode.StopAxis);
                center.Register(def);

                ActiveAlarm? raised = null;
                string? clearedCode = null;
                center.AlarmRaised += a => raised = a;
                center.AlarmCleared += c => clearedCode = c;

                // 触发报警
                var active = center.Raise("ERR_001", "Axis: 0, Current=12.5A", source: "Driver");

                Assert.NotNull(active);
                Assert.Equal("ERR_001", active.Code);
                Assert.Equal("电机过载", active.Message);
                Assert.Equal(AlarmSeverity.Error, active.Severity);
                Assert.Equal(LinkageMode.StopAxis, active.LinkageMode);
                Assert.Single(center.ActiveAlarms);
                Assert.NotNull(raised);
                Assert.Equal(1, linkage.HandledCount);
                Assert.Equal("Axis: 0, Current=12.5A", linkage.LastDetail);

                // 消除报警
                bool cleared = center.Clear("ERR_001");
                Assert.True(cleared);
                Assert.Empty(center.ActiveAlarms);
                Assert.Equal("ERR_001", clearedCode);

                // 历史记录验证
                var history = center.GetHistory();
                Assert.Single(history);
                Assert.NotNull(history[0].ClearTime);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void 自动复位报警_触发后立即自清()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"alarm_test_{Guid.NewGuid():N}.json");
            try
            {
                var center = new AlarmCenter(historyFilePath: tempFile);
                var def = new AlarmDefinition("INFO_PULSE", "微量脉冲提醒", AlarmSeverity.Info, LinkageMode.None, AutoReset: true);
                center.Register(def);

                center.Raise("INFO_PULSE", "Done");

                // AutoReset 为 true 时，ActiveAlarms 会被自动清理
                Assert.Empty(center.ActiveAlarms);

                // 历史依然保留
                var history = center.GetHistory();
                Assert.Single(history);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void 联动模式_EStopAll与StopAllAxes分级()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"alarm_test_{Guid.NewGuid():N}.json");
            try
            {
                var linkage = new TestLinkageHandler();
                var center = new AlarmCenter(linkage, tempFile);

                var estopDef = new AlarmDefinition("ESTOP_01", "急停按扭触发", AlarmSeverity.EStop, LinkageMode.EStopAll);
                center.Register(estopDef);

                center.Raise("ESTOP_01", "Panel Button");

                Assert.Equal(1, linkage.HandledCount);
                Assert.Equal(AlarmSeverity.EStop, linkage.LastDef?.Severity);
                Assert.Equal(LinkageMode.EStopAll, linkage.LastDef?.LinkageMode);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
