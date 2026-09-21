using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Teach;
using Sophon.Infrastructure.Motion.Axis;
using Xunit;

namespace Sophon.Core.Tests
{
    public class Teach_ServiceTests : IDisposable
    {
        private readonly string _tempFile;

        public Teach_ServiceTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"teach_test_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile)) File.Delete(_tempFile);
        }

        [Fact]
        public void 捕获示教点_在软限位内成功保存_超限时拒绝()
        {
            var axis0 = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                SoftLimitEnabled = true,
                SoftLimitMin = 0.0,
                SoftLimitMax = 100.0
            };
            var axis1 = new AxisDefinition
            {
                AxisId = 1,
                Name = "Y轴",
                SoftLimitEnabled = true,
                SoftLimitMin = -50.0,
                SoftLimitMax = 50.0
            };

            var fakeMotion = new FakeMotionControllerForAlarmTeach(axis0, axis1);
            fakeMotion.Positions[0] = 50.0;
            fakeMotion.Positions[1] = 20.0;

            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 20);
            var store = new TeachPointStore(_tempFile);
            var teachService = new TeachService(axisMgr, store, new[] { axis0, axis1 });

            // 1. 正常捕获
            var p1 = teachService.CaptureCurrent("取料点", "WorkGroup", speed: 25.0, description: "第一个工位");
            Assert.NotNull(p1);
            Assert.Equal("取料点", p1.Name);
            Assert.Equal(50.0, p1.AxisPositions[0]);
            Assert.Equal(20.0, p1.AxisPositions[1]);

            // 2. 超限捕获：设置 X 轴位置为 105.0（超限）
            fakeMotion.Positions[0] = 105.0;
            // 稍等以让 AxisManager 刷新或直接调用捕获
            // 注意 AxisManager 内部快照由后台线程刷新，等待 40ms
            System.Threading.Thread.Sleep(40);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                teachService.CaptureCurrent("超限点");
            });
            Assert.Contains("超出软限位范围", ex.Message);
        }

        [Fact]
        public async Task 运行到点_等待AxisDone完成_禁止轮询位置()
        {
            var axis0 = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                SoftLimitEnabled = true,
                SoftLimitMin = 0.0,
                SoftLimitMax = 200.0
            };

            var fakeMotion = new FakeMotionControllerForAlarmTeach(axis0)
            {
                AutoFireAxisDoneOnMoveAbs = true // 自动回复 AxisDone
            };

            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 20);
            var store = new TeachPointStore(_tempFile);
            var teachService = new TeachService(axisMgr, store, new[] { axis0 });

            var point = new TeachPoint
            {
                Name = "测试点",
                AxisPositions = new Dictionary<int, double> { { 0, 80.0 } },
                Speed = 30.0
            };
            store.SaveOrUpdatePoint(point);

            // 执行运行到点
            await teachService.RunToPointAsync(point);

            // 验证下发了 MoveAbs
            Assert.Single(fakeMotion.MoveAbsCalls);
            Assert.Equal(0, fakeMotion.MoveAbsCalls[0].axisId);
            Assert.Equal(80.0, fakeMotion.MoveAbsCalls[0].target);
            Assert.Equal(30.0, fakeMotion.MoveAbsCalls[0].speed);
        }

        [Fact]
        public async Task 运行点位组_按顺序依次执行()
        {
            var axis0 = new AxisDefinition
            {
                AxisId = 0,
                Name = "X轴",
                SoftLimitEnabled = true,
                SoftLimitMin = 0.0,
                SoftLimitMax = 200.0
            };

            var fakeMotion = new FakeMotionControllerForAlarmTeach(axis0)
            {
                AutoFireAxisDoneOnMoveAbs = true
            };

            using var axisMgr = new AxisManager(fakeMotion, refreshIntervalMs: 20);
            var store = new TeachPointStore(_tempFile);
            var teachService = new TeachService(axisMgr, store, new[] { axis0 });

            var p1 = new TeachPoint { Id = "P1", Name = "P1", AxisPositions = new Dictionary<int, double> { { 0, 10.0 } } };
            var p2 = new TeachPoint { Id = "P2", Name = "P2", AxisPositions = new Dictionary<int, double> { { 0, 20.0 } } };
            var p3 = new TeachPoint { Id = "P3", Name = "P3", AxisPositions = new Dictionary<int, double> { { 0, 30.0 } } };

            store.SaveOrUpdatePoint(p1);
            store.SaveOrUpdatePoint(p2);
            store.SaveOrUpdatePoint(p3);

            var group = new TeachPointGroup
            {
                Name = "轨迹组",
                PointIds = new List<string> { "P1", "P2", "P3" }
            };
            store.SaveOrUpdateGroup(group);

            await teachService.RunGroupAsync(group);

            Assert.Equal(3, fakeMotion.MoveAbsCalls.Count);
            Assert.Equal(10.0, fakeMotion.MoveAbsCalls[0].target);
            Assert.Equal(20.0, fakeMotion.MoveAbsCalls[1].target);
            Assert.Equal(30.0, fakeMotion.MoveAbsCalls[2].target);
        }
    }
}
