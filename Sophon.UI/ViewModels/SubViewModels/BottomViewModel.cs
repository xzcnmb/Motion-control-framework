using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using Sophon.Contracts;
using Sophon.Core.Event;
using Sophon.Infrastructure;
using System;
using System.Windows.Threading;

namespace Sophon.UI.ViewModels
{
    public class BottomViewModel : BindableBase
    {
        private string _currentUser;
        private string _currentTime;
        private bool _isNotLogin;
        private string _hardwareStatus = "控制卡未连接";
        private string _hardwareBadgeBackground = "#FFF7E6";
        private string _hardwareBadgeBorder = "#FFD591";
        private string _hardwareBadgeDot = "#FA8C16";
        private bool _hardwareHooked;
        private readonly IEventAggregator _eventAggregator;
        private readonly IUserRepository _userRepository;

        public string CurrentUser
        {
            get { return _currentUser; }
            set { SetProperty(ref _currentUser, value); }
        }

        public string CurrentTime
        {
            get { return _currentTime; }
            set { SetProperty(ref _currentTime, value); }
        }

        public bool IsNotLogin
        {
            get { return _isNotLogin; }
            set { SetProperty(ref _isNotLogin, value); }
        }

        public string HardwareStatus
        {
            get { return _hardwareStatus; }
            set { SetProperty(ref _hardwareStatus, value); }
        }

        public string HardwareBadgeBackground
        {
            get { return _hardwareBadgeBackground; }
            set { SetProperty(ref _hardwareBadgeBackground, value); }
        }

        public string HardwareBadgeBorder
        {
            get { return _hardwareBadgeBorder; }
            set { SetProperty(ref _hardwareBadgeBorder, value); }
        }

        public string HardwareBadgeDot
        {
            get { return _hardwareBadgeDot; }
            set { SetProperty(ref _hardwareBadgeDot, value); }
        }

        public BottomViewModel(IEventAggregator eventAggregator, IUserRepository userRepository)
        {
            _eventAggregator = eventAggregator;
            _userRepository = userRepository;
            CurrentUser = "未登录";
            IsNotLogin = true;

            _eventAggregator.GetEvent<UserChangeEvent>().Subscribe(OnUserChanged, ThreadOption.UIThread, true);
            StartTimer();
            UpdateHardwareStatus();
        }

        private void UpdateHardwareStatus()
        {
            try
            {
                var motion = ContainerLocator.Container.Resolve<IMotionController>();
                ApplyHardwareStatus(motion.Kind, motion.State);

                if (!_hardwareHooked)
                {
                    _hardwareHooked = true;
                    motion.StateChanged += _ => UpdateHardwareStatus();
                }
            }
            catch
            {
                ApplyHardwareStatus(null, ConnectionState.Disconnected);
            }
        }

        private static string ControlCardStatusFallback(DriverKind? kind) => kind.HasValue
            ? MotionCardCatalog.Display(kind.Value) + "（未接入）已连接"
            : "控制卡已连接";

        private void ApplyHardwareStatus(DriverKind? kind, ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Ready:
                    HardwareStatus = kind switch
                    {
                        DriverKind.GoogolGts => "固高 GTS 脉冲卡已连接",
                        DriverKind.LeadShineDmc => "雷赛 DMC 脉冲卡已连接",
                        DriverKind.Simulated => "仿真控制器已就绪",
                        _ => ControlCardStatusFallback(kind)
                    };
                    HardwareBadgeBackground = "#F6FFED";
                    HardwareBadgeBorder = "#B7EB8F";
                    HardwareBadgeDot = "#389E0D";
                    break;
                case ConnectionState.Connecting:
                    HardwareStatus = "正在连接控制卡";
                    HardwareBadgeBackground = "#E6F7FF";
                    HardwareBadgeBorder = "#91D5FF";
                    HardwareBadgeDot = "#1890FF";
                    break;
                case ConnectionState.Reconnecting:
                    HardwareStatus = "控制卡重连中";
                    HardwareBadgeBackground = "#E6F7FF";
                    HardwareBadgeBorder = "#91D5FF";
                    HardwareBadgeDot = "#1890FF";
                    break;
                case ConnectionState.Fault:
                    HardwareStatus = "控制卡故障";
                    HardwareBadgeBackground = "#FFF1F0";
                    HardwareBadgeBorder = "#FFA39E";
                    HardwareBadgeDot = "#CF1322";
                    break;
                default:
                    HardwareStatus = "控制卡未连接";
                    HardwareBadgeBackground = "#FFF7E6";
                    HardwareBadgeBorder = "#FFD591";
                    HardwareBadgeDot = "#FA8C16";
                    break;
            }
        }

        private async void OnUserChanged(string userName)
        {
            CurrentUser = string.IsNullOrWhiteSpace(userName) ? "未登录" : userName;
            if (CurrentUser == "未登录")
            {
                IsNotLogin = true;
                return;
            }

            try
            {
                User user = await _userRepository.GetUserByName(CurrentUser);
                IsNotLogin = user == null;
            }
            catch
            {
                IsNotLogin = false;
            }
        }

        private void StartTimer()
        {
            DispatcherTimer timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += (s, e) => UpdateClock();
            timer.Start();
        }

        private void UpdateClock()
        {
            CurrentTime = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
        }
    }
}
