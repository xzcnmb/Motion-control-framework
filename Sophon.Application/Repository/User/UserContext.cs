using Prism.Events;
using Prism.Mvvm;
using Sophon.Common;
using Sophon.Core.Event;
using Sophon.Infrastructure;

namespace Sophon.Application
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class UserContext : BindableBase, IUserContext
    {
        private string _currentUser = "未登录";
        public string CurrentUser
        {
            get => _currentUser;
            set => SetProperty(ref _currentUser, value);
        }

        private UserLevel _currentLevel = UserLevel.None;
        public UserLevel CurrentLevel
        {
            get => _currentLevel;
            set => SetProperty(ref _currentLevel, value);
        }

        private bool _isLoggedIn = false;
        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            set => SetProperty(ref _isLoggedIn, value);
        }

        private readonly IEventAggregator _eventAggregator;
        private readonly IUserRepository _userRepository;

        public UserContext(IEventAggregator eventAggregator, IUserRepository userRepository)
        {
            _eventAggregator = eventAggregator;
            _userRepository = userRepository;
            _eventAggregator.GetEvent<UserChangeEvent>().Subscribe(OnUserChanged, ThreadOption.PublisherThread, true);
        }

        public void ApplyLogin(string userName)
        {
            OnUserChanged(userName);
        }

        private void OnUserChanged(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName) || userName == "未登录")
            {
                CurrentUser = "未登录";
                CurrentLevel = UserLevel.None;
                IsLoggedIn = false;
                return;
            }

            CurrentUser = userName;
            IsLoggedIn = true;
            try
            {
                CurrentLevel = _userRepository.GetLevelByUserName(userName);
            }
            catch
            {
                CurrentLevel = UserLevel.Operator;
            }
        }
    }
}