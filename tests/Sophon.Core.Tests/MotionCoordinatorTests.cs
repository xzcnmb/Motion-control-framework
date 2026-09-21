using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Coordinator;
using Xunit;

namespace Sophon.Core.Tests
{
    public class MotionCoordinatorTests
    {
        [Fact]
        public void 轴组注册与单轴抢占仲裁拦截()
        {
            var defX = new AxisDefinition { AxisId = 0, Name = "X轴" };
            var defY = new AxisDefinition { AxisId = 1, Name = "Y轴" };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(defX, defY);

            using var coordinator = new MotionCoordinator(fakeMotion);

            var group = new AxisGroupDefinition
            {
                GroupName = "GantryXY",
                AxisIds = new List<int> { 0, 1 },
                DefaultSpeed = 100.0
            };
            coordinator.RegisterGroup(group);

            Assert.Equal(AxisGroupState.GroupStandby, coordinator.GetGroupState("GantryXY"));

            // 静态仲裁：未运动时单轴允许访问
            Assert.True(coordinator.TryAcquireSingleAxisAccess(0, out _));
            Assert.True(coordinator.TryAcquireSingleAxisAccess(1, out _));
        }

        [Fact]
        public async Task 轴组协调运动_成功到位与状态恢复()
        {
            var defX = new AxisDefinition { AxisId = 0, Name = "X轴" };
            var defY = new AxisDefinition { AxisId = 1, Name = "Y轴" };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(defX, defY);

            using var coordinator = new MotionCoordinator(fakeMotion);
            var group = new AxisGroupDefinition
            {
                GroupName = "GantryXY",
                AxisIds = new List<int> { 0, 1 }
            };
            coordinator.RegisterGroup(group);

            var done = await coordinator.MoveLinearAsync("GantryXY", new double[] { 100.0, 200.0 }, timeoutMs: 1000);

            Assert.True(done.Success);
            Assert.Equal(CommandCompletionStatus.Done, done.Status);
            Assert.Equal(AxisGroupState.GroupStandby, coordinator.GetGroupState("GantryXY"));
            Assert.Equal(100.0, fakeMotion.GetPosition(0));
            Assert.Equal(200.0, fakeMotion.GetPosition(1));
        }

        [Fact]
        public void 轴组分层停止_状态迁移符合PLCopen标准()
        {
            var defX = new AxisDefinition { AxisId = 0 };
            var defY = new AxisDefinition { AxisId = 1 };
            var fakeMotion = new FakeMotionControllerForAlarmTeach(defX, defY);

            using var coordinator = new MotionCoordinator(fakeMotion);
            var group = new AxisGroupDefinition
            {
                GroupName = "GantryXY",
                AxisIds = new List<int> { 0, 1 }
            };
            coordinator.RegisterGroup(group);

            // 1. Level 1: Halt -> 回到 GroupStandby
            coordinator.GroupHalt("GantryXY");
            Assert.Equal(AxisGroupState.GroupStandby, coordinator.GetGroupState("GantryXY"));

            // 2. Level 2: Stop -> 进入 GroupStopping 锁定
            coordinator.GroupStop("GantryXY");
            Assert.Equal(AxisGroupState.GroupStopping, coordinator.GetGroupState("GantryXY"));

            // 3. Level 3: E-Stop -> 进入 GroupErrorStop 故障锁定
            coordinator.GroupEmergencyStop("GantryXY");
            Assert.Equal(AxisGroupState.GroupErrorStop, coordinator.GetGroupState("GantryXY"));

            // 4. MC_GroupReset -> 复位恢复 GroupStandby
            coordinator.GroupReset("GantryXY");
            Assert.Equal(AxisGroupState.GroupStandby, coordinator.GetGroupState("GantryXY"));
        }
    }
}
