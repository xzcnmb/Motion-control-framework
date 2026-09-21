using Prism.Events;
using Prism.Mvvm;
using Sophon.Application;
using Sophon.Core.Event;

namespace Sophon.UI.ViewModels
{
    public class MainWindowViewModel : BindableBase
    {
        private readonly IUserContext _userContext;
        private bool _showLoginOverlay = true;

        public bool ShowLoginOverlay
        {
            get => _showLoginOverlay;
            set => SetProperty(ref _showLoginOverlay, value);
        }

        public MainWindowViewModel(IUserContext userContext, IEventAggregator eventAggregator)
        {
            _userContext = userContext;
            ShowLoginOverlay = !_userContext.IsLoggedIn;
            eventAggregator.GetEvent<UserChangeEvent>().Subscribe(
                _ => ShowLoginOverlay = !_userContext.IsLoggedIn,
                ThreadOption.UIThread,
                true);
        }
    }
}
