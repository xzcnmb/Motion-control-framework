using Prism;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Sophon.UI.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            StateChanged += MainWindow_StateChanged;
            Loaded += (_, __) => HookContentHost();
        }

        private void HookContentHost()
        {
            if (ContentHost == null) return;
            UpdateContentHostHitTest();
            DependencyPropertyDescriptor
                .FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
                .AddValueChanged(ContentHost, (_, __) => UpdateContentHostHitTest());
        }

        private void UpdateContentHostHitTest()
        {
            ContentHost.IsHitTestVisible = ContentHost.Content != null;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                RootBorder.Margin = new Thickness(7);
                BtnMaximize.ToolTip = "向下还原";
                MaximizePath.Data = Geometry.Parse("M2,0 L10,0 L10,8 L2,8 Z M0,2 L8,2 L8,10 L0,10 Z");
            }
            else
            {
                RootBorder.Margin = new Thickness(0);
                BtnMaximize.ToolTip = "最大化";
                MaximizePath.Data = Geometry.Parse("M0,0 L10,0 L10,10 L0,10 Z");
            }
        }

        private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            Close();
            PrismApplicationBase.Current?.Shutdown();
        }
    }
}
