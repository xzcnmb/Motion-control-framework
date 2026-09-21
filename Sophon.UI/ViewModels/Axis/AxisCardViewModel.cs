#nullable enable
using System;
using System.Windows.Media;
using Prism.Commands;
using Prism.Mvvm;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.UI.ViewModels.Axis
{
    /// <summary>
    /// 单轴调试卡片 ViewModel
    /// </summary>
    public class AxisCardViewModel : BindableBase
    {
        private readonly AxisManager _axisManager;
        private readonly AxisDefinition _definition;

        public int AxisId => _definition.AxisId;
        public string Name => string.IsNullOrWhiteSpace(_definition.Name) ? $"轴 {AxisId}" : _definition.Name;
        public string Unit => string.IsNullOrWhiteSpace(_definition.Unit) ? "mm" : _definition.Unit;
        public double MaxSpeed => _definition.MaxSpeed;
        public double SoftLimitMin => _definition.SoftLimitMin;
        public double SoftLimitMax => _definition.SoftLimitMax;

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

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                {
                    RaisePropertyChanged(nameof(EnableButtonText));
                }
            }
        }

        public string EnableButtonText => IsEnabled ? "已使能" : "未使能";

        private bool _isHomed;
        public bool IsHomed
        {
            get => _isHomed;
            set => SetProperty(ref _isHomed, value);
        }

        private AxisState _state = AxisState.Disabled;
        public AxisState State
        {
            get => _state;
            set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(StateText));
                    RaisePropertyChanged(nameof(StateBrush));
                }
            }
        }

        public string StateText => State switch
        {
            AxisState.Disabled => "未使能",
            AxisState.Idle => "空闲就绪",
            AxisState.Homing => "回零中",
            AxisState.Jogging => "点动运行",
            AxisState.Moving => "定位运行",
            AxisState.Error => "故障报警",
            _ => "未知状态"
        };

        public Brush StateBrush => State switch
        {
            AxisState.Disabled => new SolidColorBrush(Color.FromRgb(140, 140, 140)),
            AxisState.Idle => new SolidColorBrush(Color.FromRgb(82, 196, 26)),
            AxisState.Homing => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
            AxisState.Jogging => new SolidColorBrush(Color.FromRgb(250, 173, 20)),
            AxisState.Moving => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
            AxisState.Error => new SolidColorBrush(Color.FromRgb(245, 34, 45)),
            _ => Brushes.Gray
        };

        // 限位状态
        private bool _isNegativeHardLimit;
        public bool IsNegativeHardLimit
        {
            get => _isNegativeHardLimit;
            set => SetProperty(ref _isNegativeHardLimit, value);
        }

        private bool _isPositiveHardLimit;
        public bool IsPositiveHardLimit
        {
            get => _isPositiveHardLimit;
            set => SetProperty(ref _isPositiveHardLimit, value);
        }

        private bool _isNegativeSoftLimit;
        public bool IsNegativeSoftLimit
        {
            get => _isNegativeSoftLimit;
            set => SetProperty(ref _isNegativeSoftLimit, value);
        }

        private bool _isPositiveSoftLimit;
        public bool IsPositiveSoftLimit
        {
            get => _isPositiveSoftLimit;
            set => SetProperty(ref _isPositiveSoftLimit, value);
        }

        // 点动设置
        private bool _isFastJogMode = true;
        public bool IsFastJogMode
        {
            get => _isFastJogMode;
            set => SetProperty(ref _isFastJogMode, value);
        }

        private double _fastJogSpeed = 20.0;
        public double FastJogSpeed
        {
            get => _fastJogSpeed;
            set => SetProperty(ref _fastJogSpeed, value);
        }

        private double _fineJogSpeed = 1.0;
        public double FineJogSpeed
        {
            get => _fineJogSpeed;
            set => SetProperty(ref _fineJogSpeed, value);
        }

        public double CurrentJogSpeed => IsFastJogMode ? FastJogSpeed : FineJogSpeed;

        // 定位参数
        private double _targetPosition;
        public double TargetPosition
        {
            get => _targetPosition;
            set => SetProperty(ref _targetPosition, value);
        }

        private double _relativeDistance = 10.0;
        public double RelativeDistance
        {
            get => _relativeDistance;
            set => SetProperty(ref _relativeDistance, value);
        }

        private double _moveSpeed = 50.0;
        public double MoveSpeed
        {
            get => _moveSpeed;
            set => SetProperty(ref _moveSpeed, value);
        }

        private double _moveAccel = 200.0;
        public double MoveAccel
        {
            get => _moveAccel;
            set => SetProperty(ref _moveAccel, value);
        }

        private double _moveDecel = 200.0;
        public double MoveDecel
        {
            get => _moveDecel;
            set => SetProperty(ref _moveDecel, value);
        }

        // 命令
        public DelegateCommand ToggleEnableCommand { get; }
        public DelegateCommand HomeCommand { get; }
        public DelegateCommand StopCommand { get; }
        public DelegateCommand AbortCommand { get; }
        public DelegateCommand JogPositiveStartCommand { get; }
        public DelegateCommand JogPositiveStopCommand { get; }
        public DelegateCommand JogNegativeStartCommand { get; }
        public DelegateCommand JogNegativeStopCommand { get; }
        public DelegateCommand MoveAbsCommand { get; }
        public DelegateCommand MoveRelCommand { get; }
        public DelegateCommand ClearAlarmsCommand { get; }

        public AxisCardViewModel(AxisManager axisManager, AxisDefinition definition)
        {
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));

            // 初始化默认速度
            if (_definition.MaxSpeed > 0)
            {
                FastJogSpeed = Math.Round(_definition.MaxSpeed * 0.25, 2);
                FineJogSpeed = Math.Max(0.1, Math.Round(_definition.MaxSpeed * 0.02, 2));
                MoveSpeed = Math.Round(_definition.MaxSpeed * 0.5, 2);
            }
            if (_definition.MaxAccel > 0) MoveAccel = _definition.MaxAccel;
            if (_definition.MaxDecel > 0) MoveDecel = _definition.MaxDecel;

            ToggleEnableCommand = new DelegateCommand(ExecuteToggleEnable);
            HomeCommand = new DelegateCommand(ExecuteHome);
            StopCommand = new DelegateCommand(ExecuteStop);
            AbortCommand = new DelegateCommand(ExecuteAbort);

            JogPositiveStartCommand = new DelegateCommand(() => _axisManager.Jog(AxisId, 1, CurrentJogSpeed));
            JogPositiveStopCommand = new DelegateCommand(() => _axisManager.StopMotion(AxisId));
            JogNegativeStartCommand = new DelegateCommand(() => _axisManager.Jog(AxisId, -1, CurrentJogSpeed));
            JogNegativeStopCommand = new DelegateCommand(() => _axisManager.StopMotion(AxisId));

            MoveAbsCommand = new DelegateCommand(ExecuteMoveAbs);
            MoveRelCommand = new DelegateCommand(ExecuteMoveRel);
            ClearAlarmsCommand = new DelegateCommand(ExecuteClearAlarms);
        }

        public void UpdateSnapshot(AxisSnapshot snapshot)
        {
            Position = Math.Round(snapshot.Position, 3);
            Velocity = Math.Round(snapshot.Velocity, 3);
            IsEnabled = snapshot.Enabled;
            IsHomed = snapshot.Homed;
            State = snapshot.State;

            // 软限位判断
            if (_definition.SoftLimitEnabled)
            {
                IsNegativeSoftLimit = Position <= _definition.SoftLimitMin;
                IsPositiveSoftLimit = Position >= _definition.SoftLimitMax;
            }
        }

        public void HandleLimitAlarm(GlobalLimitAlarmArgs args)
        {
            if (args.IsHardLimit)
            {
                if (args.IsPositiveDirection) IsPositiveHardLimit = true;
                else IsNegativeHardLimit = true;
            }
            else
            {
                if (args.IsPositiveDirection) IsPositiveSoftLimit = true;
                else IsNegativeSoftLimit = true;
            }
        }

        public void HandleFault(AxisFaultArgs args)
        {
            State = AxisState.Error;
        }

        private void ExecuteToggleEnable()
        {
            if (IsEnabled)
            {
                _axisManager.DisableAxis(AxisId);
            }
            else
            {
                _axisManager.EnableAxis(AxisId);
            }
        }

        private void ExecuteHome()
        {
            _axisManager.Home(AxisId, _definition.HomeMode, _definition.HomeDir, _definition.HomeSpeed);
        }

        private void ExecuteStop()
        {
            _axisManager.StopMotion(AxisId);
        }

        private void ExecuteAbort()
        {
            _axisManager.Abort(AxisId);
        }

        private void ExecuteMoveAbs()
        {
            _axisManager.MoveAbs(AxisId, TargetPosition, MoveSpeed, MoveAccel, MoveDecel);
        }

        private void ExecuteMoveRel()
        {
            _axisManager.Controller.MoveRel(AxisId, RelativeDistance, MoveSpeed, MoveAccel, MoveDecel);
        }

        private void ExecuteClearAlarms()
        {
            IsNegativeHardLimit = false;
            IsPositiveHardLimit = false;
            if (State == AxisState.Error)
            {
                State = IsEnabled ? AxisState.Idle : AxisState.Disabled;
            }
        }
    }
}
