using System.Windows;
using System.Windows.Controls;
using Sophon.UI.ViewModels;

namespace Sophon.UI.Views
{
    public partial class UserView : UserControl
    {
        public UserView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await RefreshUserListAsync();
        }

        private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                await RefreshUserListAsync();
            }
        }

        private async System.Threading.Tasks.Task RefreshUserListAsync()
        {
            if (DataContext is UserViewModel vm)
            {
                await vm.LoadUserListAsync();
            }
        }
    }
}