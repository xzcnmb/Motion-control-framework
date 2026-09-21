#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;
using Sophon.Vision.Calibration;
using Sophon.Vision.Correction;

namespace Sophon.UI.ViewModels.Vision
{
    /// <summary>
    /// 视觉监视页视图模型。
    /// 负责相机连接控制、连续实时取景渲染（WriteableBitmap固定复用）、
    /// 单次触发测量、最新与历史测量结果展示，以及视觉引导点击/拖拽对位控制。
    /// </summary>
    public class VisionMonitorViewModel : BindableBase, INavigationAware, IDisposable
    {
        private readonly IVisionProvider _visionProvider;
        private readonly AxisManager? _axisManager;
        private readonly Dispatcher _dispatcher;

        private WriteableBitmap? _writeableBitmap;
        private int _renderingFrameFlag;
        private long _lastFpsTimestamp;
        private int _fpsCounter;
        private Guid _pendingRequestId = Guid.Empty;
        private bool _isSubscribed;
        private bool _disposed;

        #region Bindable Properties

        private ObservableCollection<string> _cameraList = new();
        /// <summary>可用相机 ID 清单。</summary>
        public ObservableCollection<string> CameraList
        {
            get => _cameraList;
            set => SetProperty(ref _cameraList, value);
        }

        private string _selectedCamera = string.Empty;
        /// <summary>当前选中的相机 ID。</summary>
        public string SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                if (SetProperty(ref _selectedCamera, value))
                {
                    OnSelectedCameraChanged();
                }
            }
        }

        private bool _isConnected;
        /// <summary>相机通讯连接状态。</summary>
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (SetProperty(ref _isConnected, value))
                {
                    RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isLive;
        /// <summary>是否处于连续取景状态。</summary>
        public bool IsLive
        {
            get => _isLive;
            set
            {
                if (SetProperty(ref _isLive, value))
                {
                    RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isWaitingResult;
        /// <summary>是否正在等待触发测量结果回报。</summary>
        public bool IsWaitingResult
        {
            get => _isWaitingResult;
            set
            {
                if (SetProperty(ref _isWaitingResult, value))
                {
                    RaiseCanExecuteChanged();
                }
            }
        }

        private string _statusMessage = "就绪";
        /// <summary>底部操作状态消息。</summary>
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private ImageSource? _videoSource;
        /// <summary>实时视频画面图像源（复用 WriteableBitmap）。</summary>
        public ImageSource? VideoSource
        {
            get => _videoSource;
            private set => SetProperty(ref _videoSource, value);
        }

        private string _frameInfo = "无视频信号";
        /// <summary>视频帧信息（尺寸、帧率、时间戳）。</summary>
        public string FrameInfo
        {
            get => _frameInfo;
            set => SetProperty(ref _frameInfo, value);
        }

        private double _currentFps;
        /// <summary>当前实时取景帧率。</summary>
        public double CurrentFps
        {
            get => _currentFps;
            set => SetProperty(ref _currentFps, value);
        }

        private VisionResultDisplayModel? _latestResult;
        /// <summary>最新一次测量结果展示模型。</summary>
        public VisionResultDisplayModel? LatestResult
        {
            get => _latestResult;
            set => SetProperty(ref _latestResult, value);
        }

        private ObservableCollection<VisionResultDisplayModel> _resultHistory = new();
        /// <summary>测量结果历史记录（最近 20 条）。</summary>
        public ObservableCollection<VisionResultDisplayModel> ResultHistory
        {
            get => _resultHistory;
            set => SetProperty(ref _resultHistory, value);
        }

        #region Visual Guidance & Drag Control Properties

        private bool _isVisualGuidanceEnabled;
        /// <summary>是否开启视口鼠标点击/拖动对位引导控制。</summary>
        public bool IsVisualGuidanceEnabled
        {
            get => _isVisualGuidanceEnabled;
            set => SetProperty(ref _isVisualGuidanceEnabled, value);
        }

        private CameraMountMode _mountMode = CameraMountMode.EyeInHand;
        /// <summary>相机手眼安装模式（移动相机 EyeInHand / 固定相机 EyeToHand）。</summary>
        public CameraMountMode MountMode
        {
            get => _mountMode;
            set => SetProperty(ref _mountMode, value);
        }

        public Array MountModes => Enum.GetValues(typeof(CameraMountMode));

        private double _guidanceSpeed = 20.0;
        /// <summary>引导定位运行速度 (mm/s)。</summary>
        public double GuidanceSpeed
        {
            get => _guidanceSpeed;
            set => SetProperty(ref _guidanceSpeed, value);
        }

        private string _lastAlignmentInfo = "暂无对位操作";
        /// <summary>最近一次视觉对位计算或运动信息。</summary>
        public string LastAlignmentInfo
        {
            get => _lastAlignmentInfo;
            set => SetProperty(ref _lastAlignmentInfo, value);
        }

        private bool _isAligning;
        /// <summary>是否正在执行对位运动。</summary>
        public bool IsAligning
        {
            get => _isAligning;
            set => SetProperty(ref _isAligning, value);
        }

        #endregion

        #endregion

        #region Commands

        public DelegateCommand ToggleConnectCommand { get; }
        public DelegateCommand ToggleLiveCommand { get; }
        public DelegateCommand TriggerCommand { get; }
        public DelegateCommand ClearHistoryCommand { get; }

        #endregion

        public VisionMonitorViewModel(IVisionProvider visionProvider, AxisManager? axisManager = null)
        {
            _visionProvider = visionProvider ?? throw new ArgumentNullException(nameof(visionProvider));
            _axisManager = axisManager;
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            ToggleConnectCommand = new DelegateCommand(async () => await ExecuteToggleConnectAsync(), CanExecuteToggleConnect);
            ToggleLiveCommand = new DelegateCommand(ExecuteToggleLive, CanExecuteToggleLive);
            TriggerCommand = new DelegateCommand(ExecuteTrigger, CanExecuteTrigger);
            ClearHistoryCommand = new DelegateCommand(ExecuteClearHistory);

            RefreshCameraList();
            SubscribeEvents();
        }

        private void RefreshCameraList()
        {
            CameraList.Clear();
            if (_visionProvider?.Cameras != null)
            {
                foreach (var cam in _visionProvider.Cameras)
                {
                    CameraList.Add(cam);
                }
            }

            if (CameraList.Count > 0 && string.IsNullOrEmpty(SelectedCamera))
            {
                SelectedCamera = CameraList[0];
            }
        }

        private void OnSelectedCameraChanged()
        {
            if (IsLive)
            {
                _visionProvider.StopLive();
                IsLive = false;
                StatusMessage = $"已停止前一相机取景，切换至 {SelectedCamera}";
            }
            RaiseCanExecuteChanged();
        }

        private void RaiseCanExecuteChanged()
        {
            ToggleConnectCommand.RaiseCanExecuteChanged();
            ToggleLiveCommand.RaiseCanExecuteChanged();
            TriggerCommand.RaiseCanExecuteChanged();
        }

        private bool CanExecuteToggleConnect() => !string.IsNullOrEmpty(SelectedCamera);

        private async Task ExecuteToggleConnectAsync()
        {
            try
            {
                if (!IsConnected)
                {
                    StatusMessage = $"正在连接相机 {SelectedCamera}...";
                    await _visionProvider.ConnectAsync().ConfigureAwait(false);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        IsConnected = true;
                        StatusMessage = $"相机 {SelectedCamera} 连接成功";
                        Growl.Success($"相机 {SelectedCamera} 连接成功");
                    });
                }
                else
                {
                    if (IsLive)
                    {
                        ExecuteToggleLive();
                    }

                    StatusMessage = $"正在断开相机 {SelectedCamera}...";
                    await _visionProvider.DisconnectAsync().ConfigureAwait(false);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        IsConnected = false;
                        StatusMessage = $"相机 {SelectedCamera} 已断开连接";
                        Growl.Info($"相机 {SelectedCamera} 已断开连接");
                    });
                }
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"连接操作失败: {ex.Message}";
                    Growl.Error($"连接操作异常: {ex.Message}");
                });
            }
        }

        private bool CanExecuteToggleLive() => IsConnected && !string.IsNullOrEmpty(SelectedCamera);

        private void ExecuteToggleLive()
        {
            try
            {
                if (!IsLive)
                {
                    _fpsCounter = 0;
                    _lastFpsTimestamp = Stopwatch.GetTimestamp();
                    _visionProvider.StartLive(SelectedCamera);
                    IsLive = true;
                    StatusMessage = $"已开启连续取景 [{SelectedCamera}]";
                }
                else
                {
                    _visionProvider.StopLive();
                    IsLive = false;
                    StatusMessage = "已停止取景";
                    FrameInfo = "取景已暂停";
                }
            }
            catch (Exception ex)
            {
                Growl.Error($"取景操作异常: {ex.Message}");
                StatusMessage = $"取景异常: {ex.Message}";
            }
        }

        private bool CanExecuteTrigger() => IsConnected && !string.IsNullOrEmpty(SelectedCamera) && !IsWaitingResult;

        private void ExecuteTrigger()
        {
            try
            {
                IsWaitingResult = true;
                StatusMessage = $"已下发触发测量请求 [{SelectedCamera}]，等待结果...";
                _pendingRequestId = _visionProvider.Trigger(SelectedCamera);
            }
            catch (Exception ex)
            {
                IsWaitingResult = false;
                Growl.Error($"触发测量失败: {ex.Message}");
                StatusMessage = $"触发异常: {ex.Message}";
            }
        }

        private void ExecuteClearHistory()
        {
            ResultHistory.Clear();
            Growl.Success("已清空历史测量记录");
        }

        #region Event Handlers

        private void SubscribeEvents()
        {
            if (_isSubscribed || _visionProvider == null) return;
            _visionProvider.FrameReady += OnFrameReady;
            _visionProvider.ResultReady += OnResultReady;
            _isSubscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_isSubscribed || _visionProvider == null) return;
            _visionProvider.FrameReady -= OnFrameReady;
            _visionProvider.ResultReady -= OnResultReady;
            _isSubscribed = false;
        }

        private void OnFrameReady(VisionFrame frame)
        {
            if (frame?.GrayscaleData == null || frame.Width <= 0 || frame.Height <= 0) return;

            // 统计 FPS
            _fpsCounter++;
            long now = Stopwatch.GetTimestamp();
            double elapsedSec = (now - _lastFpsTimestamp) / (double)Stopwatch.Frequency;
            if (elapsedSec >= 0.5)
            {
                double fps = _fpsCounter / elapsedSec;
                _fpsCounter = 0;
                _lastFpsTimestamp = now;

                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    CurrentFps = fps;
                    FrameInfo = $"分辨率: {frame.Width} × {frame.Height} | 帧率: {CurrentFps:F1} FPS | 时间戳: {frame.TimeStampMs} ms";
                }));
            }

            // 防高频堆积：若上一帧渲染尚在 Dispatcher 队列中，跳过当前帧以保实时性
            if (Interlocked.CompareExchange(ref _renderingFrameFlag, 1, 0) != 0)
            {
                return;
            }

            _dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    // 固定复用 WriteableBitmap，禁止每帧 new BitmapSource
                    if (_writeableBitmap == null ||
                        _writeableBitmap.PixelWidth != frame.Width ||
                        _writeableBitmap.PixelHeight != frame.Height)
                    {
                        _writeableBitmap = new WriteableBitmap(
                            frame.Width,
                            frame.Height,
                            96,
                            96,
                            PixelFormats.Gray8,
                            null);
                        VideoSource = _writeableBitmap;
                    }

                    int stride = frame.Width;
                    var rect = new Int32Rect(0, 0, frame.Width, frame.Height);

                    _writeableBitmap.WritePixels(
                        rect,
                        frame.GrayscaleData,
                        stride,
                        0);
                }
                catch
                {
                    // 忽略偶尔由于窗口关闭/卸载导致的渲染冲突
                }
                finally
                {
                    Interlocked.Exchange(ref _renderingFrameFlag, 0);
                }
            }));
        }

        private void OnResultReady(VisionResult result)
        {
            if (result == null) return;

            _dispatcher.InvokeAsync(() =>
            {
                IsWaitingResult = false;
                var display = VisionResultDisplayModel.From(result);
                LatestResult = display;

                ResultHistory.Insert(0, display);
                while (ResultHistory.Count > 20)
                {
                    ResultHistory.RemoveAt(ResultHistory.Count - 1);
                }

                StatusMessage = result.Ok
                    ? $"[OK] 测量成功: X={result.X:F2}, Y={result.Y:F2}, θ={result.AngleDeg:F2}° (Score={result.Score:F2})"
                    : $"[NG] 测量失败/未匹配: (Score={result.Score:F2})";
            });
        }

        #endregion

        #region Visual Guidance Actions

        /// <summary>
        /// 点击图像对位（使点击点移至视口中心/十字准星）。
        /// </summary>
        public async Task ExecuteClickToAlignAsync(double clickedPx, double clickedPy, double imageWidth, double imageHeight)
        {
            if (!IsVisualGuidanceEnabled) return;
            if (_axisManager == null)
            {
                Growl.Warning("未注入 AxisManager 运动管理服务，无法执行硬件对位移动！");
                return;
            }
            if (IsAligning) return;

            IsAligning = true;
            try
            {
                // 获取相机对应的仿射标定矩阵，若未标定则基于默认 0.01mm/px 正交阵
                var calib = GetEffectiveCalibration();

                double reticleX = imageWidth / 2.0;
                double reticleY = imageHeight / 2.0;

                // 读取当前 X(轴0) 与 Y(轴1) 的物理坐标快照
                var snapshots = _axisManager.GetSnapshots();
                double curX = snapshots.FirstOrDefault(s => s.AxisId == 0)?.Position ?? 0.0;
                double curY = snapshots.FirstOrDefault(s => s.AxisId == 1)?.Position ?? 0.0;

                var alignResult = VisualGuidanceAligner.CalculateClickToAlign(
                    calib,
                    clickedPx,
                    clickedPy,
                    reticleX,
                    reticleY,
                    curX,
                    curY,
                    MountMode);

                LastAlignmentInfo = $"点击对位: ΔX={alignResult.DeltaWorldX:F3}mm, ΔY={alignResult.DeltaWorldY:F3}mm, 目标=({alignResult.TargetWorldX:F3}, {alignResult.TargetWorldY:F3})";
                StatusMessage = LastAlignmentInfo;

                double accel = 200.0;
                double decel = 200.0;

                // 并发驱动两轴移动到位
                var taskX = _axisManager.MoveAbsAsync(0, alignResult.TargetWorldX, GuidanceSpeed, accel, decel);
                var taskY = _axisManager.MoveAbsAsync(1, alignResult.TargetWorldY, GuidanceSpeed, accel, decel);
                var results = await Task.WhenAll(taskX, taskY);

                if (results.All(r => r.Success))
                {
                    Growl.Success($"视觉对位到位成功！目标: ({alignResult.TargetWorldX:F3}, {alignResult.TargetWorldY:F3})");
                }
                else
                {
                    var fail = results.FirstOrDefault(r => !r.Success);
                    Growl.Error($"视觉对位失败: {fail?.Reason}");
                }
            }
            catch (Exception ex)
            {
                Growl.Error($"视觉对位异常: {ex.Message}");
            }
            finally
            {
                IsAligning = false;
            }
        }

        /// <summary>
        /// 拖动矢量微调对位。
        /// </summary>
        public async Task ExecuteDragAlignAsync(double dragPixelDeltaX, double dragPixelDeltaY)
        {
            if (!IsVisualGuidanceEnabled) return;
            if (_axisManager == null) return;
            if (IsAligning) return;

            IsAligning = true;
            try
            {
                var calib = GetEffectiveCalibration();

                var snapshots = _axisManager.GetSnapshots();
                double curX = snapshots.FirstOrDefault(s => s.AxisId == 0)?.Position ?? 0.0;
                double curY = snapshots.FirstOrDefault(s => s.AxisId == 1)?.Position ?? 0.0;

                var alignResult = VisualGuidanceAligner.CalculateDragMove(
                    calib,
                    dragPixelDeltaX,
                    dragPixelDeltaY,
                    curX,
                    curY,
                    MountMode);

                LastAlignmentInfo = $"拖拽微调: ΔX={alignResult.DeltaWorldX:F3}mm, ΔY={alignResult.DeltaWorldY:F3}mm";
                StatusMessage = LastAlignmentInfo;

                double accel = 200.0;
                double decel = 200.0;

                var taskX = _axisManager.MoveAbsAsync(0, alignResult.TargetWorldX, GuidanceSpeed, accel, decel);
                var taskY = _axisManager.MoveAbsAsync(1, alignResult.TargetWorldY, GuidanceSpeed, accel, decel);
                await Task.WhenAll(taskX, taskY);
            }
            catch (Exception ex)
            {
                Growl.Error($"拖拽微调异常: {ex.Message}");
            }
            finally
            {
                IsAligning = false;
            }
        }

        private NinePointCalibration GetEffectiveCalibration()
        {
            if (!string.IsNullOrWhiteSpace(SelectedCamera))
            {
                var data = CalibrationStore.Load(SelectedCamera);
                if (data?.AffineMatrix != null && data.AffineMatrix.Length == 2)
                {
                    return new NinePointCalibration(data.AffineMatrix, new CalibrationResidual(0, 0, 0, 0));
                }
            }

            // 默认正交 0.01 mm/px (100 px = 1 mm)
            double[][] defaultMatrix = new double[][]
            {
                new double[] { 0.01, 0, 0 },
                new double[] { 0, 0.01, 0 }
            };
            return new NinePointCalibration(defaultMatrix, new CalibrationResidual(0, 0, 0, 0));
        }

        #endregion

        #region INavigationAware

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            RefreshCameraList();
            SubscribeEvents();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            if (IsLive)
            {
                try
                {
                    _visionProvider.StopLive();
                    IsLive = false;
                }
                catch
                {
                    // 导航退出时忽略停止取景异常
                }
            }
            UnsubscribeEvents();
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                UnsubscribeEvents();
                if (IsLive)
                {
                    try { _visionProvider?.StopLive(); } catch { }
                }
            }
        }
    }
}
