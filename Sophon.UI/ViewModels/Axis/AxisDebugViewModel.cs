#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.UI.ViewModels.Axis
{
    /// <summary>
    /// 轴调试页面 ViewModel
    /// </summary>
    public class AxisDebugViewModel : BindableBase, IDisposable, INavigationAware
    {
        private readonly AxisManager _axisManager;
        private readonly Dictionary<int, AxisCardViewModel> _cardMap = new();
        private bool _isDisposed;

        public ObservableCollection<AxisCardViewModel> Axes { get; } = new();

        public DelegateCommand EnableAllCommand { get; }
        public DelegateCommand DisableAllCommand { get; }
        public DelegateCommand HomeAllCommand { get; }
        public DelegateCommand StopAllCommand { get; }
        public DelegateCommand AbortAllCommand { get; }

        public AxisDebugViewModel(AxisManager axisManager)
        {
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));

            EnableAllCommand = new DelegateCommand(() => _axisManager.EnableAll());
            DisableAllCommand = new DelegateCommand(() => _axisManager.DisableAll());
            HomeAllCommand = new DelegateCommand(() => _axisManager.HomeAll());
            StopAllCommand = new DelegateCommand(() => _axisManager.StopAll());
            AbortAllCommand = new DelegateCommand(() => _axisManager.AbortAll());

            InitializeAxes();
            SubscribeEvents();
        }

        private bool _isSubscribed;

        private void InitializeAxes()
        {
            Axes.Clear();
            _cardMap.Clear();

            var definitions = _axisManager.Controller?.Axes;
            if (definitions != null)
            {
                foreach (var def in definitions)
                {
                    var card = new AxisCardViewModel(_axisManager, def);
                    Axes.Add(card);
                    _cardMap[def.AxisId] = card;
                }
            }

            // 加载初始快照
            var snapshots = _axisManager.GetSnapshots();
            foreach (var snap in snapshots)
            {
                if (_cardMap.TryGetValue(snap.AxisId, out var card))
                {
                    card.UpdateSnapshot(snap);
                }
            }
        }

        private void SubscribeEvents()
        {
            if (_isSubscribed) return;
            _isSubscribed = true;
            _axisManager.SnapshotsUpdated += OnSnapshotsUpdated;
            _axisManager.GlobalLimitAlarm += OnGlobalLimitAlarm;
            _axisManager.AxisFault += OnAxisFault;
        }

        private void UnsubscribeEvents()
        {
            if (!_isSubscribed) return;
            _isSubscribed = false;
            _axisManager.SnapshotsUpdated -= OnSnapshotsUpdated;
            _axisManager.GlobalLimitAlarm -= OnGlobalLimitAlarm;
            _axisManager.AxisFault -= OnAxisFault;
        }

        private void OnSnapshotsUpdated(IReadOnlyList<AxisSnapshot> snapshots)
        {
            if (_isDisposed) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed) return;
                foreach (var snap in snapshots)
                {
                    if (_cardMap.TryGetValue(snap.AxisId, out var card))
                    {
                        card.UpdateSnapshot(snap);
                    }
                }
            }), DispatcherPriority.Render);
        }

        private void OnGlobalLimitAlarm(GlobalLimitAlarmArgs args)
        {
            if (_isDisposed) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed) return;
                if (_cardMap.TryGetValue(args.AxisId, out var card))
                {
                    card.HandleLimitAlarm(args);
                }
            }));
        }

        private void OnAxisFault(AxisFaultArgs args)
        {
            if (_isDisposed) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed) return;
                if (_cardMap.TryGetValue(args.AxisId, out var card))
                {
                    card.HandleFault(args);
                }
            }));
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            SubscribeEvents();
            // 进入页面时如需重新同步状态
            var snapshots = _axisManager.GetSnapshots();
            foreach (var snap in snapshots)
            {
                if (_cardMap.TryGetValue(snap.AxisId, out var card))
                {
                    card.UpdateSnapshot(snap);
                }
            }
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            UnsubscribeEvents();
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                UnsubscribeEvents();
            }
        }
    }
}
