#nullable enable
using Prism.Ioc;
using Sophon.Application;
using Sophon.Application.Services.Alarm;
using Sophon.Application.Services.Teach;
using Sophon.Core.Alarm;
using Sophon.Core.Teach;
using Sophon.UI.ViewModels.Alarm;
using Sophon.UI.ViewModels.Teach;
using Sophon.UI.Views.Alarm;
using Sophon.UI.Views.Teach;

namespace Sophon.UI
{
    /// <summary>
    /// 报警中心、全局限位联动与示教模块的 DI 注册与页面导航扩展类。
    /// （由 Wave4 主控在 App.xaml.cs 的 RegisterTypes 中调用 containerRegistry.RegisterAlarmTeachUi()）。
    /// </summary>
    public static class AlarmTeachModuleRegister
    {
        public static void RegisterAlarmTeachUi(this IContainerRegistry containerRegistry)
        {
            // 1. 注册核心域单例（Core / Alarm & Teach）
            containerRegistry.RegisterSingleton<AlarmCenter>();
            containerRegistry.RegisterSingleton<IAlarmLinkageHandler, MotionAlarmLinkageHandler>();
            containerRegistry.RegisterSingleton<GlobalLimitMonitor>();
            containerRegistry.RegisterSingleton<TeachPointStore>();
            containerRegistry.RegisterSingleton<TeachService>();

            // 2. 注册应用层桥接服务（Application / Services）
            containerRegistry.RegisterSingleton<AlarmRepositoryBridge>();
            containerRegistry.RegisterSingleton<TeachAppService>();

            // 3. 注册 UI 视图与 ViewModel 导航
            containerRegistry.RegisterForNavigation<AlarmCenterView, AlarmCenterViewModel>("AlarmCenterView");
            containerRegistry.RegisterForNavigation<LimitMonitorView, LimitMonitorViewModel>("LimitMonitorView");
            containerRegistry.RegisterForNavigation<TeachView, TeachViewModel>("TeachView");
        }
    }
}
