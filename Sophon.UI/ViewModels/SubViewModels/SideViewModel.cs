#nullable enable
using System;
using Common;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Application;
using Sophon.Core.Event;
using Sophon.Infrastructure;

namespace Sophon.UI.ViewModels
{
    public class SideViewModel : BindableBase
    {
        private readonly IRegionManager _regionManager;
        private readonly INavigationGuardService _guardService;
        private readonly IUserContext _userContext;
        private readonly IEventAggregator _eventAggregator;
        private readonly ILoggerFactory? _loggerFactory;

        public DelegateCommand<object> NavigateCommand { get; private set; }

        public string CurrentUserName => _userContext.IsLoggedIn ? _userContext.CurrentUser : "未登录";

        public string CurrentRoleBadge => _userContext.CurrentLevel switch
        {
            UserLevel.Admin => "系统管理员",
            UserLevel.Engineer => "工程师",
            UserLevel.Operator => "操作员",
            _ => "访客"
        };

        public string RoleBadgeColor => _userContext.CurrentLevel switch
        {
            UserLevel.Admin => "#722ED1",
            UserLevel.Engineer => "#1890FF",
            UserLevel.Operator => "#52C41A",
            _ => "#8C8C8C"
        };

        public SideViewModel(
            IRegionManager regionManager,
            INavigationGuardService guardService,
            IUserContext userContext,
            IEventAggregator eventAggregator)
        {
            _regionManager = regionManager;
            _guardService = guardService;
            _userContext = userContext;
            _eventAggregator = eventAggregator;
            _loggerFactory = TryResolve<ILoggerFactory>();

            NavigateCommand = new DelegateCommand<object>(ExecuteNavigate);

            _eventAggregator.GetEvent<UserChangeEvent>().Subscribe(OnUserChanged, ThreadOption.UIThread, true);
        }

        private void OnUserChanged(string _)
        {
            RaisePropertyChanged(nameof(CurrentUserName));
            RaisePropertyChanged(nameof(CurrentRoleBadge));
            RaisePropertyChanged(nameof(RoleBadgeColor));
            CheckAndEvictActiveView();
        }

        public void ExecuteNavigate(object param)
        {
            if (param is string viewName && !string.IsNullOrWhiteSpace(viewName))
            {
                // 工业级统一权限拦截：未授权禁止进入敏感硬件视图
                if (!_guardService.CanNavigate(viewName, out var requiredLevel, out var reason))
                {
                    Growl.Warning(reason);
                    return;
                }

                NavigateContent(viewName);
            }
        }

        public void ShowHome() => NavigateContent("HomeView");

        private void NavigateContent(string viewName, Action? onSuccess = null)
        {
            _regionManager.RequestNavigate("ContentRegion", viewName, result =>
            {
                if (result.Success)
                {
                    onSuccess?.Invoke();
                    return;
                }

                // 导航失败只弹 Message 会丢掉现场（AlarmRegisterView 定位问题时看不到 inner exception / stack）。
                // Growl 文案仍取最深 InnerException 的 Message，完整异常走日志，不静默、不吞。
                string logDetail = result.Exception?.ToString() ?? "导航结果未携带 Exception";
                string logMessage = $"导航到 {viewName} 失败：{logDetail}";
                _loggerFactory?.CreateLogger("SideViewModel").Error(logMessage);
                System.Diagnostics.Debug.WriteLine($"[SideViewModel] {logMessage}");

                Growl.Error($"无法打开页面 {viewName}：{DescribeNavigationError(result.Exception)}");
            });
        }

        /// <summary>
        /// 取异常链最深处的 Message 作为用户可见文案；无异常时保持原「未知原因」口径。
        /// </summary>
        private static string DescribeNavigationError(Exception? ex)
        {
            if (ex == null)
            {
                return "未知原因";
            }

            Exception deepest = ex;
            while (deepest.InnerException != null)
            {
                deepest = deepest.InnerException;
            }

            return deepest.Message;
        }

        private static T? TryResolve<T>() where T : class
        {
            try
            {
                return ContainerLocator.Container.Resolve<T>();
            }
            catch
            {
                return null;
            }
        }

        private void CheckAndEvictActiveView()
        {
            try
            {
                var region = _regionManager.Regions["ContentRegion"];
                var activeView = region.ActiveViews;
                foreach (var v in activeView)
                {
                    string viewName = v.GetType().Name;
                    if (!_guardService.CanNavigate(viewName, out _, out _))
                    {
                        _regionManager.RequestNavigate("ContentRegion", "HomeView");
                        Growl.Info("用户已注销，已自动退出受限工程页面返回系统主页。");
                        break;
                    }
                }
            }
            catch { }
        }
    }
}
