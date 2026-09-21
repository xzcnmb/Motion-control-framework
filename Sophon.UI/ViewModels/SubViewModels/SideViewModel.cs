#nullable enable
using HandyControl.Controls;
using Prism.Commands;
using Prism.Events;
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

                if (string.Equals(viewName, "HomeView", System.StringComparison.OrdinalIgnoreCase))
                {
                    ShowHome();
                    return;
                }

                _regionManager.RequestNavigate("ContentRegion", viewName);
            }
        }

        public void ShowHome()
        {
            try
            {
                if (!_regionManager.Regions.ContainsRegionWithName("ContentRegion"))
                {
                    return;
                }

                var region = _regionManager.Regions["ContentRegion"];
                var views = new System.Collections.Generic.List<object>();
                foreach (var view in region.Views)
                {
                    views.Add(view);
                }
                foreach (var view in views)
                {
                    region.Remove(view);
                }
            }
            catch
            {
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
