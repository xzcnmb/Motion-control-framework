using Sophon.Application;
using Sophon.Infrastructure;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    public class NavigationGuardServiceTests
    {
        private class FakeUserContext : IUserContext
        {
            public string CurrentUser { get; set; } = "未登录";
            public UserLevel CurrentLevel { get; set; } = UserLevel.None;
            public bool IsLoggedIn { get; set; } = false;
            public void ApplyLogin(string userName)
            {
                CurrentUser = userName;
                IsLoggedIn = !string.IsNullOrWhiteSpace(userName) && userName != "未登录";
            }
        }

        [Fact]
        public void 未登录访客_只允许访问公开视图_受限视图被拦截()
        {
            var userCtx = new FakeUserContext
            {
                CurrentUser = "未登录",
                CurrentLevel = UserLevel.None,
                IsLoggedIn = false
            };
            var guard = new NavigationGuardService(userCtx);

            // 公开页面允许
            Assert.True(guard.CanNavigate("HomeView", out _, out _));
            Assert.True(guard.CanNavigate("UserView", out _, out _));
            Assert.True(guard.CanNavigate("AlarmCenterView", out _, out _));
            Assert.True(guard.CanNavigate("AlarmHistoryView", out _, out _));

            // 操作员、工程师、管理员页面严格拦截
            Assert.False(guard.CanNavigate("LimitMonitorView", out var reqLvl1, out var r1));
            Assert.Equal(UserLevel.Operator, reqLvl1);
            Assert.Contains("权限拦截", r1);

            Assert.False(guard.CanNavigate("AxisDebugView", out var reqLvl2, out var r2));
            Assert.Equal(UserLevel.Engineer, reqLvl2);
            Assert.Contains("工程师", r2);

            Assert.False(guard.CanNavigate("MotionCardConfigView", out var reqLvl3, out var r3));
            Assert.Equal(UserLevel.Admin, reqLvl3);
            Assert.Contains("管理员", r3);
        }

        [Fact]
        public void 操作员登录_可查看监控页面_但无法进入轴调试与参数配置()
        {
            var userCtx = new FakeUserContext
            {
                CurrentUser = "操作员",
                CurrentLevel = UserLevel.Operator,
                IsLoggedIn = true
            };
            var guard = new NavigationGuardService(userCtx);

            // 操作员权限：可看监控和视觉取景
            Assert.True(guard.CanNavigate("LimitMonitorView", out _, out _));
            Assert.True(guard.CanNavigate("VisionMonitorView", out _, out _));

            // 工程师权限视图被拦截
            Assert.False(guard.CanNavigate("AxisDebugView", out _, out _));
            Assert.False(guard.CanNavigate("TeachView", out _, out _));
            Assert.False(guard.CanNavigate("DeviceControlView", out _, out _));

            // 管理员配置视图被拦截
            Assert.False(guard.CanNavigate("MotionCardConfigView", out _, out _));
            Assert.False(guard.CanNavigate("ParamView", out _, out _));
        }

        [Fact]
        public void 工程师登录_可操作硬件调试示教_但不可更改底层控制卡档案()
        {
            var userCtx = new FakeUserContext
            {
                CurrentUser = "工程师",
                CurrentLevel = UserLevel.Engineer,
                IsLoggedIn = true
            };
            var guard = new NavigationGuardService(userCtx);

            // 工程师权限：所有调试、示教、流程与机构控制全放行
            Assert.True(guard.CanNavigate("AxisDebugView", out _, out _));
            Assert.True(guard.CanNavigate("TeachView", out _, out _));
            Assert.True(guard.CanNavigate("FlowEditorView", out _, out _));
            Assert.True(guard.CanNavigate("DeviceControlView", out _, out _));
            Assert.True(guard.CanNavigate("IOInfrastructureView", out _, out _));
            Assert.True(guard.CanNavigate("VisionCalibrationView", out _, out _));

            // 底层控制卡与核心参数仍需管理员权限
            Assert.False(guard.CanNavigate("MotionCardConfigView", out _, out _));
            Assert.False(guard.CanNavigate("ParamView", out _, out _));
        }

        [Fact]
        public void 管理员登录_全功能全视图无阻碍放行()
        {
            var userCtx = new FakeUserContext
            {
                CurrentUser = "管理员",
                CurrentLevel = UserLevel.Admin,
                IsLoggedIn = true
            };
            var guard = new NavigationGuardService(userCtx);

            Assert.True(guard.CanNavigate("HomeView", out _, out _));
            Assert.True(guard.CanNavigate("AxisDebugView", out _, out _));
            Assert.True(guard.CanNavigate("MotionCardConfigView", out _, out _));
            Assert.True(guard.CanNavigate("ParamView", out _, out _));
            Assert.True(guard.CanNavigate("IOInfrastructureView", out _, out _));
        }
    }
}
