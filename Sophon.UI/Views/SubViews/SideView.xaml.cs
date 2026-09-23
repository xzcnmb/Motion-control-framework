using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HandyControl.Controls;
using Prism.Ioc;
using Prism.Navigation.Regions;

namespace Sophon.UI.Views
{
    /// <summary>
    /// SideView.xaml 的交互逻辑
    /// </summary>
    public partial class SideView : UserControl
    {
        private const string ContentRegionName = "ContentRegion";

        private IRegionManager _regionManager;
        private IRegion _contentRegion;

        private bool _regionsCollectionWired;
        private bool _syncQueued;
        private bool _syncing;

        public SideView()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            // HandyControl 在 OnMouseLeftButtonUp 里先置 IsSelected=true 才执行 Command，
            // 所以这里必须用「事件冒泡 + Dispatcher 延后」的方式校正，不能在事件里同步改。
            // handledEventsToo：即使前序处理器标记为已处理也照样校正。
            NavMenu.AddHandler(SideMenuItem.SelectedEvent, new RoutedEventHandler(OnSideMenuItemSelected), true);

            // 鼠标点击 / 键盘操作都可能让 HandyControl 抢先选中，统一再排一次校正。
            NavMenu.MouseLeftButtonUp += OnMenuPointerOrKeyInput;
            NavMenu.PreviewKeyDown += OnMenuPointerOrKeyInput;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            WireRegionSync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnwireRegionSync();
        }

        /// <summary>
        /// 挂上 ContentRegion 的三类导航来源：Region 集合（延迟注册）、ActiveViews（各来源导航的最终结果）、
        /// NavigationService（含导航失败）。三者都只是「排一次校正」，真正读的是 ActiveViews 首项。
        /// </summary>
        private void WireRegionSync()
        {
            if (_regionManager == null)
            {
                _regionManager = ResolveRegionManager();
            }

            if (_regionManager == null) return;

            if (!_regionsCollectionWired)
            {
                _regionManager.Regions.CollectionChanged += OnRegionsCollectionChanged;
                _regionsCollectionWired = true;
            }

            AttachContentRegion();

            // 初始化同步：应用启动时 ContentRegion 可能已经是 HomeView。
            QueueSync();
        }

        private void UnwireRegionSync()
        {
            DetachContentRegion();

            if (_regionsCollectionWired && _regionManager != null)
            {
                _regionManager.Regions.CollectionChanged -= OnRegionsCollectionChanged;
                _regionsCollectionWired = false;
            }
        }

        private static IRegionManager ResolveRegionManager()
        {
            try
            {
                return ContainerLocator.Container.Resolve<IRegionManager>();
            }
            catch
            {
                // 解析失败时（例如设计期/容器未初始化）不高亮也不抛，留空由后续 Loaded 重试。
                return null;
            }
        }

        private void AttachContentRegion()
        {
            if (_contentRegion != null) return;
            if (_regionManager == null) return;
            if (!_regionManager.Regions.ContainsRegionWithName(ContentRegionName)) return;

            _contentRegion = _regionManager.Regions[ContentRegionName];
            _contentRegion.ActiveViews.CollectionChanged += OnActiveViewsChanged;

            IRegionNavigationService navigationService = _contentRegion.NavigationService;
            if (navigationService != null)
            {
                navigationService.Navigated += OnContentNavigated;
                navigationService.NavigationFailed += OnContentNavigationFailed;
            }
        }

        private void DetachContentRegion()
        {
            if (_contentRegion == null) return;

            _contentRegion.ActiveViews.CollectionChanged -= OnActiveViewsChanged;

            IRegionNavigationService navigationService = _contentRegion.NavigationService;
            if (navigationService != null)
            {
                navigationService.Navigated -= OnContentNavigated;
                navigationService.NavigationFailed -= OnContentNavigationFailed;
            }

            _contentRegion = null;
        }

        private void OnRegionsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // ContentRegion 可能晚于侧边栏注册（延迟 region 创建），补挂后按当前状态校正一次。
            AttachContentRegion();
            QueueSync();
        }

        private void OnActiveViewsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            QueueSync();
        }

        private void OnContentNavigated(object sender, RegionNavigationEventArgs e)
        {
            QueueSync();
        }

        private void OnContentNavigationFailed(object sender, RegionNavigationFailedEventArgs e)
        {
            // 导航失败：页面没变，ActiveViews 仍是旧页，这里只是把被点击项抢先置上的高亮纠正回真实当前页。
            QueueSync();
        }

        private void OnSideMenuItemSelected(object sender, RoutedEventArgs e)
        {
            QueueSync();
        }

        private void OnMenuPointerOrKeyInput(object sender, RoutedEventArgs e)
        {
            QueueSync();
        }

        private void QueueSync()
        {
            if (_syncQueued) return;
            _syncQueued = true;

            Dispatcher.BeginInvoke(new Action(SyncMenuSelectionWithActiveView), DispatcherPriority.Normal);
        }

        /// <summary>
        /// 唯一的事实来源是 ContentRegion.ActiveViews 首项的视图类型名。
        /// 只改 IsSelected（高亮由模板 Trigger 驱动），绝不在这里重新导航，也不读 VM 的点击状态。
        /// 没有 CommandParameter 的分组父项永远匹配不上，因此不会冒充当前页。
        /// </summary>
        private void SyncMenuSelectionWithActiveView()
        {
            _syncQueued = false;

            // 卸载后回调 / 重入双保险：不碰已销毁的视觉树，也不打断正在进行的同步。
            if (_syncing) return;
            if (!IsLoaded) return;

            string activeViewName = GetActiveViewName();

            _syncing = true;
            try
            {
                foreach (SideMenuItem item in EnumerateMenuItems(NavMenu))
                {
                    bool isCurrentPage =
                        !string.IsNullOrEmpty(activeViewName) &&
                        string.Equals(item.CommandParameter as string, activeViewName, StringComparison.Ordinal);

                    item.IsSelected = isCurrentPage;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>
        /// ActiveViews 首项即单活区域（ContentControl → SingleActiveRegion）的当前页，取它的类型名。
        /// 取不到（region 未挂上、还没有任何活动视图）时返回 null，表示「无当前页」。
        /// </summary>
        private string GetActiveViewName()
        {
            IRegion region = _contentRegion;
            if (region == null) return null;

            foreach (object view in region.ActiveViews)
            {
                return view?.GetType().Name;
            }

            return null;
        }

        /// <summary>
        /// 沿 Items 递归枚举全部 SideMenuItem（含折叠分组下的二级子项）。
        /// 不用 VisualTreeHelper：HandyControl 的 SimpleItemsControl 用 internal 的 ItemsHost/PART_Panel，
        /// 且子项不一定已生成容器；Items 本身是逻辑结构，未展开的项也一定在里面。
        /// </summary>
        private static IEnumerable<SideMenuItem> EnumerateMenuItems(SimpleItemsControl parent)
        {
            if (parent == null) yield break;

            foreach (object child in parent.Items)
            {
                if (child is not SideMenuItem menuItem) continue;

                yield return menuItem;

                foreach (SideMenuItem nested in EnumerateMenuItems(menuItem))
                {
                    yield return nested;
                }
            }
        }
    }
}
