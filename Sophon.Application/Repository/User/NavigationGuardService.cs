#nullable enable
using System;
using System.Collections.Generic;
using Sophon.Common;
using Sophon.Infrastructure;

namespace Sophon.Application
{
    /// <summary>
    /// 工业上位机导航权限守卫接口。
    /// 负责拦截无权限的页面导航请求，防止未登录/低权限操作员误入硬件调试、示教与参数配置界面。
    /// </summary>
    public interface INavigationGuardService
    {
        /// <summary>
        /// 检查当前上下文中的操作员是否允许访问目标视图。
        /// </summary>
        /// <param name="viewName">目标视图名称（如 AxisDebugView, ParamView）</param>
        /// <param name="requiredLevel">输出所需的最低权限等级</param>
        /// <param name="rejectionReason">输出拒绝原因（若不允许）</param>
        /// <returns>是否允许导航进入</returns>
        bool CanNavigate(string viewName, out UserLevel requiredLevel, out string rejectionReason);

        /// <summary>
        /// 获取某视图所要求的最低权限等级。
        /// </summary>
        UserLevel GetRequiredLevel(string viewName);
    }

    [Injectable(DependencyLifetime.Singleton)]
    public class NavigationGuardService : INavigationGuardService
    {
        private readonly IUserContext _userContext;

        // 工业三级权限视图矩阵：
        // None / Public: 任何人可访问（看板、登录、报警概览）
        // Operator: 操作员级别（生产查看、历史追溯、相机取景监视）
        // Engineer: 工程师级别（轴调试、点位示教、流程画布、标定向导、气动外设控制、IO映射）
        // Admin: 管理员级别（控制卡底层档案、系统核心工艺参数）
        private static readonly Dictionary<string, UserLevel> ViewPermissions = new(StringComparer.OrdinalIgnoreCase)
        {
            // 公开视图
            ["HomeView"] = UserLevel.None,
            ["UserView"] = UserLevel.None,
            ["AlarmCenterView"] = UserLevel.None,
            ["AlarmHistoryView"] = UserLevel.None,
            ["StationView"] = UserLevel.None,

            // 操作员及以上视图
            ["LimitMonitorView"] = UserLevel.Operator,
            ["VisionMonitorView"] = UserLevel.Operator,

            // 工程师及以上视图（涉及物理硬件点动与参数更改）
            ["AxisDebugView"] = UserLevel.Engineer,
            ["TeachView"] = UserLevel.Engineer,
            ["FlowEditorView"] = UserLevel.Engineer,
            ["VisionCalibrationView"] = UserLevel.Engineer,
            ["CameraConfigView"] = UserLevel.Engineer,
            ["DeviceControlView"] = UserLevel.Engineer,
            ["IOInfrastructureView"] = UserLevel.Engineer,
            ["AlarmRegisterView"] = UserLevel.Engineer,
            ["ProtocolInfrastructureView"] = UserLevel.Engineer,

            // 仅管理员视图
            ["MotionCardConfigView"] = UserLevel.Admin,
            ["ParamView"] = UserLevel.Admin,
        };

        public NavigationGuardService(IUserContext userContext)
        {
            _userContext = userContext ?? throw new ArgumentNullException(nameof(userContext));
        }

        public UserLevel GetRequiredLevel(string viewName)
        {
            if (string.IsNullOrWhiteSpace(viewName)) return UserLevel.None;
            return ViewPermissions.TryGetValue(viewName, out var lvl) ? lvl : UserLevel.Engineer;
        }

        public bool CanNavigate(string viewName, out UserLevel requiredLevel, out string rejectionReason)
        {
            requiredLevel = GetRequiredLevel(viewName);
            rejectionReason = string.Empty;

            if (requiredLevel == UserLevel.None)
            {
                return true;
            }

            var current = _userContext.CurrentLevel;

            // 权限层级判定：None(0) < Operator(1) < Engineer(2) < Admin(3)
            if ((int)current >= (int)requiredLevel)
            {
                return true;
            }

            string requiredName = GetLevelName(requiredLevel);
            string currentName = _userContext.IsLoggedIn ? GetLevelName(current) : "未登录 (访客)";

            rejectionReason = $"权限拦截：访问【{GetViewFriendlyName(viewName)}】需要【{requiredName}】及以上权限！当前身份: {currentName}。请先登录授权账号。";
            return false;
        }

        private static string GetLevelName(UserLevel level) => level switch
        {
            UserLevel.Admin => "系统管理员",
            UserLevel.Engineer => "工艺工程师",
            UserLevel.Operator => "产线操作员",
            _ => "未登录/访客"
        };

        private static string GetViewFriendlyName(string viewName) => viewName switch
        {
            "AxisDebugView" => "单轴/多轴硬件调试",
            "TeachView" => "点位示教与轨迹",
            "FlowEditorView" => "可视化流程编辑",
            "MotionCardConfigView" => "控制卡与轴参数配置",
            "DeviceControlView" => "气动与485外设总控",
            "ProtocolInfrastructureView" => "外设与气缸档案配置",
            "IOInfrastructureView" => "IO点位映射与强制调试",
            "VisionCalibrationView" => "机器视觉标定向导",
            "CameraConfigView" => "相机连接与采集配置",
            "ParamView" => "核心参数设置",
            "AlarmRegisterView" => "报警定义配置",
            _ => viewName
        };
    }
}
