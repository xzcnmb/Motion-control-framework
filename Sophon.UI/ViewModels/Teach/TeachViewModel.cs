#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Sophon.Application.Services.Teach;
using Sophon.Contracts;
using Sophon.Core.Teach;
using Sophon.Infrastructure.Motion.Axis;
using WpfApplication = System.Windows.Application;

namespace Sophon.UI.ViewModels.Teach
{
    public class AxisCoordinateVm : BindableBase
    {
        public int AxisId { get; set; }
        public string AxisName { get; set; } = string.Empty;

        private double _position;
        public double Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private double _velocity;
        public double Velocity
        {
            get => _velocity;
            set => SetProperty(ref _velocity, value);
        }

        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set => SetProperty(ref _enabled, value);
        }

        private bool _homed;
        public bool Homed
        {
            get => _homed;
            set => SetProperty(ref _homed, value);
        }
    }

    public class TeachViewModel : BindableBase, Prism.Navigation.Regions.INavigationAware
    {
        private readonly TeachAppService _teachAppService;
        private readonly AxisManager _axisManager;
        private bool _isSubscribed;

        public ObservableCollection<AxisCoordinateVm> AxisList { get; } = new();
        public ObservableCollection<TeachPoint> PointsList { get; } = new();
        public ObservableCollection<TeachPointGroup> GroupsList { get; } = new();

        private AxisCoordinateVm? _selectedAxis;
        public AxisCoordinateVm? SelectedAxis
        {
            get => _selectedAxis;
            set => SetProperty(ref _selectedAxis, value);
        }

        private TeachPoint? _selectedPoint;
        public TeachPoint? SelectedPoint
        {
            get => _selectedPoint;
            set => SetProperty(ref _selectedPoint, value);
        }

        private TeachPointGroup? _selectedGroup;
        public TeachPointGroup? SelectedGroup
        {
            get => _selectedGroup;
            set => SetProperty(ref _selectedGroup, value);
        }

        // 点动设置
        private bool _isHighSpeed;
        public bool IsHighSpeed
        {
            get => _isHighSpeed;
            set
            {
                if (SetProperty(ref _isHighSpeed, value))
                {
                    RaisePropertyChanged(nameof(IsLowSpeed));
                }
            }
        }

        public bool IsLowSpeed
        {
            get => !IsHighSpeed;
            set
            {
                if (value) IsHighSpeed = false;
            }
        }

        private double _stepDistance = 1.0;
        public double StepDistance
        {
            get => _stepDistance;
            set
            {
                if (SetProperty(ref _stepDistance, value))
                {
                    RaisePropertyChanged(nameof(Step001));
                    RaisePropertyChanged(nameof(Step01));
                    RaisePropertyChanged(nameof(Step1));
                    RaisePropertyChanged(nameof(Step10));
                }
            }
        }

        public bool Step001
        {
            get => _stepDistance == 0.01;
            set { if (value) StepDistance = 0.01; }
        }

        public bool Step01
        {
            get => _stepDistance == 0.1;
            set { if (value) StepDistance = 0.1; }
        }

        public bool Step1
        {
            get => _stepDistance == 1.0;
            set { if (value) StepDistance = 1.0; }
        }

        public bool Step10
        {
            get => _stepDistance == 10.0;
            set { if (value) StepDistance = 10.0; }
        }

        // 新建示教点输入
        private string _newPointName = "P1";
        public string NewPointName
        {
            get => _newPointName;
            set => SetProperty(ref _newPointName, value);
        }

        private string _newPointGroup = "Default";
        public string NewPointGroup
        {
            get => _newPointGroup;
            set => SetProperty(ref _newPointGroup, value);
        }

        private double _newPointSpeed = 20.0;
        public double NewPointSpeed
        {
            get => _newPointSpeed;
            set => SetProperty(ref _newPointSpeed, value);
        }

        private string _newPointDescription = "";
        public string NewPointDescription
        {
            get => _newPointDescription;
            set => SetProperty(ref _newPointDescription, value);
        }

        // 命令
        public DelegateCommand EnableAxisCommand { get; }
        public DelegateCommand DisableAxisCommand { get; }
        public DelegateCommand JogPositiveCommand { get; }
        public DelegateCommand JogNegativeCommand { get; }
        public DelegateCommand StepPositiveCommand { get; }
        public DelegateCommand StepNegativeCommand { get; }
        public DelegateCommand StopAxisCommand { get; }

        public DelegateCommand CapturePointCommand { get; }
        public DelegateCommand DeletePointCommand { get; }
        public DelegateCommand RunToPointCommand { get; }
        public DelegateCommand RunGroupCommand { get; }
        public DelegateCommand RefreshDataCommand { get; }

        public TeachViewModel(TeachAppService teachAppService, AxisManager axisManager)
        {
            _teachAppService = teachAppService ?? throw new ArgumentNullException(nameof(teachAppService));
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));

            EnableAxisCommand = new DelegateCommand(OnEnableAxis, () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            DisableAxisCommand = new DelegateCommand(OnDisableAxis, () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            JogPositiveCommand = new DelegateCommand(() => OnJog(1), () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            JogNegativeCommand = new DelegateCommand(() => OnJog(-1), () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            StepPositiveCommand = new DelegateCommand(() => OnStep(1), () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            StepNegativeCommand = new DelegateCommand(() => OnStep(-1), () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);
            StopAxisCommand = new DelegateCommand(OnStopAxis, () => SelectedAxis != null).ObservesProperty(() => SelectedAxis);

            CapturePointCommand = new DelegateCommand(OnCapturePoint);
            DeletePointCommand = new DelegateCommand(OnDeletePoint, () => SelectedPoint != null).ObservesProperty(() => SelectedPoint);
            RunToPointCommand = new DelegateCommand(async () => await OnRunToPoint(), () => SelectedPoint != null).ObservesProperty(() => SelectedPoint);
            RunGroupCommand = new DelegateCommand(async () => await OnRunGroup(), () => SelectedGroup != null).ObservesProperty(() => SelectedGroup);
            RefreshDataCommand = new DelegateCommand(RefreshData);

            InitAxes();
            RefreshData();
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
            RefreshData();
        }

        public bool IsNavigationTarget(Prism.Navigation.Regions.NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(Prism.Navigation.Regions.NavigationContext navigationContext)
        {
            Unsubscribe();
        }

        private void InitAxes()
        {
            AxisList.Clear();
            var axes = _axisManager.Controller?.Axes ?? new System.Collections.Generic.List<AxisDefinition>();
            foreach (var ax in axes)
            {
                AxisList.Add(new AxisCoordinateVm
                {
                    AxisId = ax.AxisId,
                    AxisName = string.IsNullOrEmpty(ax.Name) ? $"轴 {ax.AxisId}" : ax.Name
                });
            }
            if (AxisList.Count > 0)
            {
                SelectedAxis = AxisList[0];
            }
        }

        private void OnSnapshotsUpdated(System.Collections.Generic.IReadOnlyList<AxisSnapshot> snapshots)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                foreach (var s in snapshots)
                {
                    var ax = AxisList.FirstOrDefault(a => a.AxisId == s.AxisId);
                    if (ax != null)
                    {
                        ax.Position = s.Position;
                        ax.Velocity = s.Velocity;
                        ax.Enabled = s.Enabled;
                        ax.Homed = s.Homed;
                    }
                }
            });
        }

        private void OnEnableAxis()
        {
            if (SelectedAxis == null) return;
            try
            {
                _axisManager.EnableAxis(SelectedAxis.AxisId);
                Growl.Success($"轴 {SelectedAxis.AxisName} 已使能");
            }
            catch (Exception ex)
            {
                Growl.Error($"使能轴 {SelectedAxis.AxisName} 失败: {ex.Message}");
            }
        }

        private void OnDisableAxis()
        {
            if (SelectedAxis == null) return;
            try
            {
                _axisManager.DisableAxis(SelectedAxis.AxisId);
                Growl.Success($"轴 {SelectedAxis.AxisName} 已下使能");
            }
            catch (Exception ex)
            {
                Growl.Error($"下使能轴 {SelectedAxis.AxisName} 失败: {ex.Message}");
            }
        }

        private void OnJog(int dir)
        {
            if (SelectedAxis == null) return;
            if (!SelectedAxis.Enabled)
            {
                Growl.Warning("请先使能当前轴");
                return;
            }
            try
            {
                double speed = IsHighSpeed ? 50.0 : 5.0; // 高速50，微调5
                Guid requestId = _axisManager.Jog(SelectedAxis.AxisId, dir, speed);
                SubscribeAxisDone(requestId, $"轴 {SelectedAxis.AxisName} 点动失败");
            }
            catch (Exception ex)
            {
                Growl.Error($"点动失败: {ex.Message}");
            }
        }

        private void OnStep(int dir)
        {
            if (SelectedAxis == null) return;
            if (!SelectedAxis.Enabled)
            {
                Growl.Warning("请先使能当前轴");
                return;
            }
            try
            {
                double speed = IsHighSpeed ? 30.0 : 5.0;
                double target = SelectedAxis.Position + dir * StepDistance;
                Guid requestId = _axisManager.MoveAbs(SelectedAxis.AxisId, target, speed, 200, 200);
                SubscribeAxisDone(requestId, $"轴 {SelectedAxis.AxisName} 步进失败");
            }
            catch (Exception ex)
            {
                Growl.Error($"步进失败: {ex.Message}");
            }
        }

        private void OnStopAxis()
        {
            if (SelectedAxis == null) return;
            try
            {
                _axisManager.StopMotion(SelectedAxis.AxisId);
            }
            catch (Exception ex)
            {
                Growl.Error($"停止轴 {SelectedAxis.AxisName} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 订阅一次 AxisDone 完成回报：按 requestId 匹配，成功后即解除订阅，不泄漏。
        /// </summary>
        private void SubscribeAxisDone(Guid requestId, string failMessage)
        {
            Action<AxisDoneArgs>? handler = null;
            handler = args =>
            {
                if (args.RequestId != requestId) return;
                _axisManager.Controller.AxisDone -= handler;

                // 完成回报可能来自后台线程，切回 UI 线程显示
                WpfApplication.Current?.Dispatcher?.Invoke(() =>
                {
                    if (!args.Success)
                    {
                        Growl.Error(string.IsNullOrEmpty(args.Reason) ? failMessage : $"{failMessage}: {args.Reason}");
                    }
                });
            };
            _axisManager.Controller.AxisDone += handler;
        }

        private void OnCapturePoint()
        {
            try
            {
                var pt = _teachAppService.CaptureCurrent(NewPointName, NewPointGroup, NewPointSpeed, NewPointDescription);
                Growl.Success($"示教点 '{pt.Name}' 捕获并保存成功！");
                RefreshData();
            }
            catch (Exception ex)
            {
                Growl.Error($"捕获示教点失败: {ex.Message}");
            }
        }

        private void OnDeletePoint()
        {
            if (SelectedPoint != null)
            {
                _teachAppService.DeletePoint(SelectedPoint.Id);
                Growl.Success($"示教点 '{SelectedPoint.Name}' 已删除");
                RefreshData();
            }
        }

        private async Task OnRunToPoint()
        {
            if (SelectedPoint == null) return;
            try
            {
                Growl.Info($"开始运行到示教点 '{SelectedPoint.Name}'...");
                await _teachAppService.RunToPointAsync(SelectedPoint);
                Growl.Success($"已到达示教点 '{SelectedPoint.Name}'！");
            }
            catch (Exception ex)
            {
                Growl.Error($"运行到点失败: {ex.Message}");
            }
        }

        private async Task OnRunGroup()
        {
            if (SelectedGroup == null) return;
            try
            {
                Growl.Info($"开始运行点位组 '{SelectedGroup.Name}'...");
                await _teachAppService.RunGroupAsync(SelectedGroup);
                Growl.Success($"点位组 '{SelectedGroup.Name}' 运行完成！");
            }
            catch (Exception ex)
            {
                Growl.Error($"点位组运行失败: {ex.Message}");
            }
        }

        private void RefreshData()
        {
            PointsList.Clear();
            foreach (var p in _teachAppService.GetAllPoints())
            {
                PointsList.Add(p);
            }

            GroupsList.Clear();
            foreach (var g in _teachAppService.GetAllGroups())
            {
                GroupsList.Add(g);
            }
        }
    }
}
