#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using Sophon.Contracts;
using Sophon.Core.Alarm;
using Sophon.Infrastructure.Motion.Axis;
using WpfApplication = System.Windows.Application;

namespace Sophon.UI.ViewModels.Alarm
{
    public class AxisLimitStateVm : BindableBase
    {
        public int AxisId { get; set; }
        public string AxisName { get; set; } = string.Empty;

        private double _currentPosition;
        public double CurrentPosition
        {
            get => _currentPosition;
            set => SetProperty(ref _currentPosition, value);
        }

        public double SoftLimitMin { get; set; }
        public double SoftLimitMax { get; set; }
        public bool SoftLimitEnabled { get; set; }

        private bool _softLimitTriggered;
        public bool SoftLimitTriggered
        {
            get => _softLimitTriggered;
            set
            {
                if (SetProperty(ref _softLimitTriggered, value))
                {
                    RaisePropertyChanged(nameof(StatusColor));
                }
            }
        }

        private bool _hardLimitTriggered;
        public bool HardLimitTriggered
        {
            get => _hardLimitTriggered;
            set
            {
                if (SetProperty(ref _hardLimitTriggered, value))
                {
                    RaisePropertyChanged(nameof(StatusColor));
                }
            }
        }

        private string? _activeLimitPoint;
        public string? ActiveLimitPoint
        {
            get => _activeLimitPoint;
            set => SetProperty(ref _activeLimitPoint, value);
        }

        private bool _motionInForbiddenDirectionProhibited;
        public bool MotionInForbiddenDirectionProhibited
        {
            get => _motionInForbiddenDirectionProhibited;
            set => SetProperty(ref _motionInForbiddenDirectionProhibited, value);
        }

        public string StatusColor
        {
            get
            {
                if (HardLimitTriggered) return "#780650"; // 硬限位
                if (SoftLimitTriggered) return "#FF4D4F"; // 软限位
                return "#52C41A"; // 正常绿色
            }
        }
    }

    public class LimitMonitorViewModel : BindableBase, Prism.Navigation.Regions.INavigationAware
    {
        private readonly GlobalLimitMonitor _limitMonitor;
        private readonly AxisManager _axisManager;
        private bool _isSubscribed;

        public ObservableCollection<AxisLimitStateVm> AxisLimitStates { get; } = new();

        private AxisLimitStateVm? _selectedAxis;
        public AxisLimitStateVm? SelectedAxis
        {
            get => _selectedAxis;
            set => SetProperty(ref _selectedAxis, value);
        }

        public DelegateCommand ResetLimitCommand { get; }

        public LimitMonitorViewModel(GlobalLimitMonitor limitMonitor, AxisManager axisManager)
        {
            _limitMonitor = limitMonitor ?? throw new ArgumentNullException(nameof(limitMonitor));
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));

            ResetLimitCommand = new DelegateCommand(OnResetLimit, () => SelectedAxis != null)
                .ObservesProperty(() => SelectedAxis);

            InitAxes();
            Subscribe();
        }

        private void Subscribe()
        {
            if (_isSubscribed) return;
            _isSubscribed = true;
            _axisManager.SnapshotsUpdated += OnSnapshotsUpdated;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            _isSubscribed = false;
            _axisManager.SnapshotsUpdated -= OnSnapshotsUpdated;
        }

        public void OnNavigatedTo(Prism.Navigation.Regions.NavigationContext navigationContext)
        {
            Subscribe();
            InitAxes();
        }

        public bool IsNavigationTarget(Prism.Navigation.Regions.NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(Prism.Navigation.Regions.NavigationContext navigationContext)
        {
            Unsubscribe();
        }

        private void InitAxes()
        {
            AxisLimitStates.Clear();
            var axes = _axisManager.Controller?.Axes ?? new System.Collections.Generic.List<AxisDefinition>();
            foreach (var ax in axes)
            {
                AxisLimitStates.Add(new AxisLimitStateVm
                {
                    AxisId = ax.AxisId,
                    AxisName = string.IsNullOrEmpty(ax.Name) ? $"轴 {ax.AxisId}" : ax.Name,
                    SoftLimitMin = ax.SoftLimitMin,
                    SoftLimitMax = ax.SoftLimitMax,
                    SoftLimitEnabled = ax.SoftLimitEnabled
                });
            }
        }

        private void OnSnapshotsUpdated(System.Collections.Generic.IReadOnlyList<AxisSnapshot> snapshots)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                foreach (var s in snapshots)
                {
                    var vm = AxisLimitStates.FirstOrDefault(a => a.AxisId == s.AxisId);
                    if (vm != null)
                    {
                        vm.CurrentPosition = s.Position;
                        if (_limitMonitor.LimitStates.TryGetValue(s.AxisId, out var state))
                        {
                            vm.HardLimitTriggered = state.HardLimitTriggered;
                            vm.SoftLimitTriggered = state.SoftLimitTriggered;
                            vm.ActiveLimitPoint = state.ActiveLimitPoint;
                            vm.MotionInForbiddenDirectionProhibited = state.MotionInForbiddenDirectionProhibited;
                        }
                    }
                }
            });
        }

        private void OnResetLimit()
        {
            if (SelectedAxis != null)
            {
                bool success = _limitMonitor.TryResetLimit(SelectedAxis.AxisId);
                if (success)
                {
                    SelectedAxis.HardLimitTriggered = false;
                    SelectedAxis.SoftLimitTriggered = false;
                    SelectedAxis.MotionInForbiddenDirectionProhibited = false;
                    SelectedAxis.ActiveLimitPoint = null;
                }
            }
        }
    }
}
