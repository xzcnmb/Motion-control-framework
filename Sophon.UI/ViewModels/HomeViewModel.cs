#nullable enable
using System;
using Common;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Application;
using Sophon.Contracts;
using Sophon.Core.Alarm;
using Sophon.Core.Event;
using Sophon.Infrastructure;

namespace Sophon.UI.ViewModels
{
    public class HomeViewModel : BindableBase, INavigationAware
    {
        private readonly IMotionController? _motionController;
        private readonly IRegionManager? _regionManager;
        private readonly AlarmCenter? _alarmCenter;
        private readonly INavigationGuardService? _guardService;
        private readonly IUserContext? _userContext;
        private readonly ILoggerFactory? _loggerFactory;
        private bool _isSubscribed;

        public DelegateCommand<string> NavigateCommand { get; }

        public string WelcomeTitle => _userContext?.IsLoggedIn == true
            ? $"欢迎回来，{_userContext.CurrentUser}"
            : "欢迎使用运动控制上位机";

        public string WelcomeSubtitle => _userContext?.IsLoggedIn == true
            ? $"{CurrentRoleText} 已登录。请从下方入口或左侧菜单进入现场操作。"
            : "请先登录后再进入轴调试、示教与流程。";

        public string CurrentOperatorText => _userContext?.IsLoggedIn == true
            ? $"{_userContext.CurrentUser} · {CurrentRoleText}"
            : "未登录";

        private string CurrentRoleText => _userContext?.CurrentLevel switch
        {
            UserLevel.Admin => "系统管理员",
            UserLevel.Engineer => "工程师",
            UserLevel.Operator => "操作员",
            _ => "访客"
        };

        public string DriverKindName
        {
            get
            {
                if (_motionController == null)
                {
                    return "未连接控制卡";
                }

                string kindText = MotionCardCatalog.Display(_motionController.Kind);
                return MotionCardCatalog.IsImplemented(_motionController.Kind)
                    ? kindText
                    : kindText + "（未接入）";
            }
        }

        public string ConnectionStatusText => _motionController?.State switch
        {
            ConnectionState.Ready => "控制卡已连接",
            ConnectionState.Connecting => "正在连接控制卡",
            ConnectionState.Fault => "控制卡故障",
            ConnectionState.Reconnecting => "控制卡重连中",
            _ => "控制卡未连接"
        };

        public string AlarmSummaryText
        {
            get
            {
                int n = _alarmCenter?.ActiveAlarms?.Count ?? 0;
                return n == 0 ? "当前无活跃报警" : $"当前存在 {n} 项未消除报警";
            }
        }

        public HomeViewModel(
            IMotionController? motionController = null,
            IRegionManager? regionManager = null,
            AlarmCenter? alarmCenter = null,
            INavigationGuardService? guardService = null,
            IUserContext? userContext = null,
            ILoggerFactory? loggerFactory = null,
            IEventAggregator? eventAggregator = null)
        {
            _motionController = motionController;
            _regionManager = regionManager;
            _alarmCenter = alarmCenter;
            _guardService = guardService;
            _userContext = userContext;
            _loggerFactory = loggerFactory;
            NavigateCommand = new DelegateCommand<string>(ExecuteNavigate);
            eventAggregator?.GetEvent<UserChangeEvent>().Subscribe(_ => RefreshWelcome(), ThreadOption.UIThread, true);
            Subscribe();
        }

        private void OnMotionStateChanged(ConnectionState _)
        {
            RaisePropertyChanged(nameof(ConnectionStatusText));
        }

        private void OnAlarmRaised(ActiveAlarm _) => RaisePropertyChanged(nameof(AlarmSummaryText));
        private void OnAlarmCleared(string _) => RaisePropertyChanged(nameof(AlarmSummaryText));

        private void Subscribe()
        {
            if (_isSubscribed) return;
            _isSubscribed = true;
            if (_motionController != null) _motionController.StateChanged += OnMotionStateChanged;
            if (_alarmCenter != null)
            {
                _alarmCenter.AlarmRaised += OnAlarmRaised;
                _alarmCenter.AlarmCleared += OnAlarmCleared;
            }
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            Subscribe();
            RefreshWelcome();
            RaisePropertyChanged(nameof(DriverKindName));
            RaisePropertyChanged(nameof(ConnectionStatusText));
            RaisePropertyChanged(nameof(AlarmSummaryText));
        }

        private void RefreshWelcome()
        {
            RaisePropertyChanged(nameof(WelcomeTitle));
            RaisePropertyChanged(nameof(WelcomeSubtitle));
            RaisePropertyChanged(nameof(CurrentOperatorText));
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;
        public void OnNavigatedFrom(NavigationContext navigationContext) { }

        private void ExecuteNavigate(string viewName)
        {
            if (string.IsNullOrWhiteSpace(viewName)) return;
            if (_guardService != null && !_guardService.CanNavigate(viewName, out _, out var reason))
            {
                Growl.Warning(reason);
                return;
            }
            _regionManager?.RequestNavigate("ContentRegion", viewName, result =>
            {
                if (!result.Success)
                {
                    // 只弹 Message 会丢掉 inner exception / stack；Growl 文案取最深 InnerException 的 Message，
                    // 完整异常写日志（_loggerFactory 缺失时退回 Debug 输出），不静默、不吞。
                    string logDetail = result.Exception?.ToString() ?? "导航结果未携带 Exception";
                    string logMessage = $"导航到 {viewName} 失败：{logDetail}";
                    _loggerFactory?.CreateLogger("HomeViewModel").Error(logMessage);
                    System.Diagnostics.Debug.WriteLine($"[HomeViewModel] {logMessage}");

                    Growl.Error($"无法打开页面 {viewName}：{DescribeNavigationError(result.Exception)}");
                }
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
    }
}
