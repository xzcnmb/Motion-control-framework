#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Vision.Calibration;
using Sophon.Vision.Correction;

namespace Sophon.UI.ViewModels.Vision
{
    /// <summary>
    /// 标定数据点绑定的展示模型。
    /// </summary>
    public class CalibrationPointModel : BindableBase
    {
        /// <summary>标定序号（1~9）。</summary>
        public int Index { get; set; }

        private double _pixelX;
        /// <summary>图像像素 X 坐标 (px)。</summary>
        public double PixelX
        {
            get => _pixelX;
            set => SetProperty(ref _pixelX, value);
        }

        private double _pixelY;
        /// <summary>图像像素 Y 坐标 (px)。</summary>
        public double PixelY
        {
            get => _pixelY;
            set => SetProperty(ref _pixelY, value);
        }

        private double _worldX;
        /// <summary>物理机械世界 X 坐标 (mm)。</summary>
        public double WorldX
        {
            get => _worldX;
            set => SetProperty(ref _worldX, value);
        }

        private double _worldY;
        /// <summary>物理机械世界 Y 坐标 (mm)。</summary>
        public double WorldY
        {
            get => _worldY;
            set => SetProperty(ref _worldY, value);
        }

        private bool _isSampled;
        /// <summary>是否已完成该点采集。</summary>
        public bool IsSampled
        {
            get => _isSampled;
            set
            {
                if (SetProperty(ref _isSampled, value))
                {
                    RaisePropertyChanged(nameof(StatusText));
                    RaisePropertyChanged(nameof(StatusColor));
                }
            }
        }

        public string StatusText => IsSampled ? "已采集" : "待采集";
        public string StatusColor => IsSampled ? "#52C41A" : "#8C8C8C";
    }

    /// <summary>
    /// 视觉标定向导视图模型。
    /// 涵盖九点仿射标定（向导采集、正反向残差评估、合格判定、JSON持久化）、
    /// 旋转中心标定（两圆法/角度法计算与存储）及示教基准与 XYR 纠偏演示。
    /// </summary>
    public class VisionCalibrationViewModel : BindableBase, INavigationAware, IDisposable
    {
        private readonly IVisionProvider _visionProvider;
        private readonly Dispatcher _dispatcher;

        private NinePointCalibration? _currentCalibration;
        private VisionResult? _lastTriggerResult;
        private bool _isSubscribed;
        private bool _disposed;

        #region Common Properties

        private ObservableCollection<string> _cameraList = new();
        /// <summary>可用相机列表。</summary>
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

        private string _calibrationStatus = "请先在「相机配置」中保存并应用相机，再触发采集";
        /// <summary>向导操作状态描述。</summary>
        public string CalibrationStatus
        {
            get => _calibrationStatus;
            set => SetProperty(ref _calibrationStatus, value);
        }

        #endregion

        #region Nine-Point Calibration Properties

        private ObservableCollection<CalibrationPointModel> _points = new();
        /// <summary>九点标定点集合（1~9）。</summary>
        public ObservableCollection<CalibrationPointModel> Points
        {
            get => _points;
            set => SetProperty(ref _points, value);
        }

        private int _currentPointIndex = 1;
        /// <summary>当前待录入/采集的点序号 (1~9)。</summary>
        public int CurrentPointIndex
        {
            get => _currentPointIndex;
            set => SetProperty(ref _currentPointIndex, Math.Clamp(value, 1, 9));
        }

        private double _inputPixelX;
        /// <summary>当前录入点的像素 X (px)。</summary>
        public double InputPixelX
        {
            get => _inputPixelX;
            set => SetProperty(ref _inputPixelX, value);
        }

        private double _inputPixelY;
        /// <summary>当前录入点的像素 Y (px)。</summary>
        public double InputPixelY
        {
            get => _inputPixelY;
            set => SetProperty(ref _inputPixelY, value);
        }

        private double _inputWorldX;
        /// <summary>当前录入点的物理世界 X (mm)。</summary>
        public double InputWorldX
        {
            get => _inputWorldX;
            set => SetProperty(ref _inputWorldX, value);
        }

        private double _inputWorldY;
        /// <summary>当前录入点的物理世界 Y (mm)。</summary>
        public double InputWorldY
        {
            get => _inputWorldY;
            set => SetProperty(ref _inputWorldY, value);
        }

        private double _maxResidualMmThreshold = 0.05;
        /// <summary>允许的最大物理残差阈值 (mm)。</summary>
        public double MaxResidualMmThreshold
        {
            get => _maxResidualMmThreshold;
            set => SetProperty(ref _maxResidualMmThreshold, value);
        }

        private double _maxResidualPxThreshold = 1.0;
        /// <summary>允许的最大像素反投影残差阈值 (px)。</summary>
        public double MaxResidualPxThreshold
        {
            get => _maxResidualPxThreshold;
            set => SetProperty(ref _maxResidualPxThreshold, value);
        }

        private bool _hasCalibrated;
        /// <summary>是否已完成九点标定计算。</summary>
        public bool HasCalibrated
        {
            get => _hasCalibrated;
            set => SetProperty(ref _hasCalibrated, value);
        }

        private bool _isAcceptable;
        /// <summary>标定精度是否合格。</summary>
        public bool IsAcceptable
        {
            get => _isAcceptable;
            set => SetProperty(ref _isAcceptable, value);
        }

        private double _rmsResidualMm;
        /// <summary>物理均方根残差 RMS (mm)。</summary>
        public double RmsResidualMm
        {
            get => _rmsResidualMm;
            set => SetProperty(ref _rmsResidualMm, value);
        }

        private double _maxResidualMm;
        /// <summary>物理最大残差 (mm)。</summary>
        public double MaxResidualMm
        {
            get => _maxResidualMm;
            set => SetProperty(ref _maxResidualMm, value);
        }

        private double _rmsResidualPx;
        /// <summary>像素反投影 RMS 残差 (px)。</summary>
        public double RmsResidualPx
        {
            get => _rmsResidualPx;
            set => SetProperty(ref _rmsResidualPx, value);
        }

        private double _maxResidualPx;
        /// <summary>像素反投影最大残差 (px)。</summary>
        public double MaxResidualPx
        {
            get => _maxResidualPx;
            set => SetProperty(ref _maxResidualPx, value);
        }

        private string _matrixDescription = "--";
        /// <summary>仿射矩阵展示文本。</summary>
        public string MatrixDescription
        {
            get => _matrixDescription;
            set => SetProperty(ref _matrixDescription, value);
        }

        #endregion

        #region Rotation Center Properties

        private double _rotAngle1 = 0.0;
        /// <summary>旋转采样点1角度 (deg)。</summary>
        public double RotAngle1
        {
            get => _rotAngle1;
            set => SetProperty(ref _rotAngle1, value);
        }

        private double _rotWorldX1 = 100.0;
        /// <summary>旋转采样点1物理 X (mm)。</summary>
        public double RotWorldX1
        {
            get => _rotWorldX1;
            set => SetProperty(ref _rotWorldX1, value);
        }

        private double _rotWorldY1 = 100.0;
        /// <summary>旋转采样点1物理 Y (mm)。</summary>
        public double RotWorldY1
        {
            get => _rotWorldY1;
            set => SetProperty(ref _rotWorldY1, value);
        }

        private double _rotAngle2 = 45.0;
        /// <summary>旋转采样点2角度 (deg)。</summary>
        public double RotAngle2
        {
            get => _rotAngle2;
            set => SetProperty(ref _rotAngle2, value);
        }

        private double _rotWorldX2 = 114.142;
        /// <summary>旋转采样点2物理 X (mm)。</summary>
        public double RotWorldX2
        {
            get => _rotWorldX2;
            set => SetProperty(ref _rotWorldX2, value);
        }

        private double _rotWorldY2 = 85.858;
        /// <summary>旋转采样点2物理 Y (mm)。</summary>
        public double RotWorldY2
        {
            get => _rotWorldY2;
            set => SetProperty(ref _rotWorldY2, value);
        }

        private double _rotationCenterX = 100.0;
        /// <summary>标定出的旋转中心物理 X (mm)。</summary>
        public double RotationCenterX
        {
            get => _rotationCenterX;
            set => SetProperty(ref _rotationCenterX, value);
        }

        private double _rotationCenterY = 80.0;
        /// <summary>标定出的旋转中心物理 Y (mm)。</summary>
        public double RotationCenterY
        {
            get => _rotationCenterY;
            set => SetProperty(ref _rotationCenterY, value);
        }

        private double _rotationRadius;
        /// <summary>旋转半径 (mm)。</summary>
        public double RotationRadius
        {
            get => _rotationRadius;
            set => SetProperty(ref _rotationRadius, value);
        }

        private double _rotationRms;
        /// <summary>拟合残差 (mm)。</summary>
        public double RotationRms
        {
            get => _rotationRms;
            set => SetProperty(ref _rotationRms, value);
        }

        private bool _hasRotationCenter;
        /// <summary>是否已计算出旋转中心。</summary>
        public bool HasRotationCenter
        {
            get => _hasRotationCenter;
            set => SetProperty(ref _hasRotationCenter, value);
        }

        #endregion

        #region Teach Base & Offset Correction Properties

        private string _teachBaseName = "标准产品基准_A";
        /// <summary>示教基准模板名称。</summary>
        public string TeachBaseName
        {
            get => _teachBaseName;
            set => SetProperty(ref _teachBaseName, value);
        }

        private double _basePixelX = 320.0;
        /// <summary>示教基准像素 X (px)。</summary>
        public double BasePixelX
        {
            get => _basePixelX;
            set => SetProperty(ref _basePixelX, value);
        }

        private double _basePixelY = 240.0;
        /// <summary>示教基准像素 Y (px)。</summary>
        public double BasePixelY
        {
            get => _basePixelY;
            set => SetProperty(ref _basePixelY, value);
        }

        private double _basePixelAngleDeg = 0.0;
        /// <summary>示教基准像素角度 (deg)。</summary>
        public double BasePixelAngleDeg
        {
            get => _basePixelAngleDeg;
            set => SetProperty(ref _basePixelAngleDeg, value);
        }

        private double _teachWorldX = 100.0;
        /// <summary>示教基准物理世界 X (mm)。</summary>
        public double TeachWorldX
        {
            get => _teachWorldX;
            set => SetProperty(ref _teachWorldX, value);
        }

        private double _teachWorldY = 100.0;
        /// <summary>示教基准物理世界 Y (mm)。</summary>
        public double TeachWorldY
        {
            get => _teachWorldY;
            set => SetProperty(ref _teachWorldY, value);
        }

        private double _teachWorldAngleDeg = 0.0;
        /// <summary>示教基准物理世界角度 (deg)。</summary>
        public double TeachWorldAngleDeg
        {
            get => _teachWorldAngleDeg;
            set => SetProperty(ref _teachWorldAngleDeg, value);
        }

        // 偏差计算结果
        private double _offsetDeltaWorldX;
        public double OffsetDeltaWorldX { get => _offsetDeltaWorldX; set => SetProperty(ref _offsetDeltaWorldX, value); }

        private double _offsetDeltaWorldY;
        public double OffsetDeltaWorldY { get => _offsetDeltaWorldY; set => SetProperty(ref _offsetDeltaWorldY, value); }

        private double _offsetDeltaAngleDeg;
        public double OffsetDeltaAngleDeg { get => _offsetDeltaAngleDeg; set => SetProperty(ref _offsetDeltaAngleDeg, value); }

        private double _offsetDeltaPixelX;
        public double OffsetDeltaPixelX { get => _offsetDeltaPixelX; set => SetProperty(ref _offsetDeltaPixelX, value); }

        private double _offsetDeltaPixelY;
        public double OffsetDeltaPixelY { get => _offsetDeltaPixelY; set => SetProperty(ref _offsetDeltaPixelY, value); }

        // 对位纠偏目标结果
        private double _compDeltaX;
        public double CompDeltaX { get => _compDeltaX; set => SetProperty(ref _compDeltaX, value); }

        private double _compDeltaY;
        public double CompDeltaY { get => _compDeltaY; set => SetProperty(ref _compDeltaY, value); }

        private double _compDeltaAngle;
        public double CompDeltaAngle { get => _compDeltaAngle; set => SetProperty(ref _compDeltaAngle, value); }

        private double _correctedTargetX;
        public double CorrectedTargetX { get => _correctedTargetX; set => SetProperty(ref _correctedTargetX, value); }

        private double _correctedTargetY;
        public double CorrectedTargetY { get => _correctedTargetY; set => SetProperty(ref _correctedTargetY, value); }

        #endregion

        #region Commands

        // 九点标定相关指令
        public DelegateCommand TriggerCurrentPointCommand { get; }
        public DelegateCommand RecordCurrentPointCommand { get; }
        public DelegateCommand ResetPointsCommand { get; }
        public DelegateCommand GenerateDemoGridCommand { get; }
        public DelegateCommand CalibrateNinePointCommand { get; }
        public DelegateCommand SaveCalibrationCommand { get; }
        public DelegateCommand LoadCalibrationCommand { get; }

        // 旋转中心标定指令
        public DelegateCommand CalibrateRotationCenterCommand { get; }
        public DelegateCommand SaveRotationCenterCommand { get; }
        public DelegateCommand SampleRotPoint1Command { get; }
        public DelegateCommand SampleRotPoint2Command { get; }

        // 示教与纠偏测试指令
        public DelegateCommand TeachCurrentAsBaseCommand { get; }
        public DelegateCommand TriggerAndDemoCorrectionCommand { get; }

        #endregion

        public VisionCalibrationViewModel(IVisionProvider visionProvider)
        {
            _visionProvider = visionProvider ?? throw new ArgumentNullException(nameof(visionProvider));
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            // 构造指令
            TriggerCurrentPointCommand = new DelegateCommand(ExecuteTriggerCurrentPoint);
            RecordCurrentPointCommand = new DelegateCommand(ExecuteRecordCurrentPoint);
            ResetPointsCommand = new DelegateCommand(ExecuteResetPoints);
            GenerateDemoGridCommand = new DelegateCommand(ExecuteGenerateDemoGrid);
            CalibrateNinePointCommand = new DelegateCommand(ExecuteCalibrateNinePoint);
            SaveCalibrationCommand = new DelegateCommand(ExecuteSaveCalibration);
            LoadCalibrationCommand = new DelegateCommand(ExecuteLoadCalibration);

            CalibrateRotationCenterCommand = new DelegateCommand(ExecuteCalibrateRotationCenter);
            SaveRotationCenterCommand = new DelegateCommand(ExecuteSaveRotationCenter);
            SampleRotPoint1Command = new DelegateCommand(ExecuteSampleRotPoint1);
            SampleRotPoint2Command = new DelegateCommand(ExecuteSampleRotPoint2);

            TeachCurrentAsBaseCommand = new DelegateCommand(ExecuteTeachCurrentAsBase);
            TriggerAndDemoCorrectionCommand = new DelegateCommand(ExecuteTriggerAndDemoCorrection);

            InitializeDefaultPoints();
            RefreshCameraList();
            SubscribeEvents();
        }

        private void InitializeDefaultPoints()
        {
            Points.Clear();
            for (int i = 1; i <= 9; i++)
            {
                Points.Add(new CalibrationPointModel
                {
                    Index = i,
                    PixelX = 0,
                    PixelY = 0,
                    WorldX = 0,
                    WorldY = 0,
                    IsSampled = false
                });
            }
            CurrentPointIndex = 1;
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
            if (string.IsNullOrEmpty(SelectedCamera)) return;

            // 尝试自动加载已有标定配置
            ExecuteLoadCalibration();
        }

        #region Nine-Point Calibration Actions

        private void ExecuteTriggerCurrentPoint()
        {
            try
            {
                if (_visionProvider?.Cameras == null || _visionProvider.Cameras.Count == 0)
                {
                    Growl.Warning("请先在相机配置中添加并应用相机");
                    CalibrationStatus = "请先在「相机配置」中保存并应用相机，再触发采集";
                    return;
                }

                if (string.IsNullOrEmpty(SelectedCamera))
                {
                    Growl.Warning("请先选择标定目标相机");
                    return;
                }

                CalibrationStatus = $"正在触发相机 {SelectedCamera} 测量像素特征...";
                _visionProvider.Trigger(SelectedCamera);
            }
            catch (Exception ex)
            {
                Growl.Error($"触发测量失败: {ex.Message}");
                CalibrationStatus = $"触发异常: {ex.Message}";
            }
        }

        private void ExecuteRecordCurrentPoint()
        {
            if (CurrentPointIndex < 1 || CurrentPointIndex > Points.Count)
            {
                Growl.Warning("当前点序号超出范围");
                return;
            }

            var pt = Points[CurrentPointIndex - 1];
            pt.PixelX = InputPixelX;
            pt.PixelY = InputPixelY;
            pt.WorldX = InputWorldX;
            pt.WorldY = InputWorldY;
            pt.IsSampled = true;

            CalibrationStatus = $"第 {CurrentPointIndex} 点已录入: Px=({pt.PixelX:F1},{pt.PixelY:F1}), World=({pt.WorldX:F2},{pt.WorldY:F2})";
            Growl.Success($"已录入第 {CurrentPointIndex} 标定点");

            // 自动推荐步进到下一个点
            if (CurrentPointIndex < 9)
            {
                CurrentPointIndex++;
            }
        }

        private void ExecuteResetPoints()
        {
            InitializeDefaultPoints();
            HasCalibrated = false;
            MatrixDescription = "--";
            CalibrationStatus = "已重置所有标定点";
            Growl.Info("标定点列表已重置");
        }

        private void ExecuteGenerateDemoGrid()
        {
            // 生成标准 3x3 示例网格点（物理中心 100, 100，间距 10mm；像素中心 320, 240，比例约 0.05mm/px）
            double originWx = 90.0, originWy = 90.0, stepW = 10.0;
            double originPx = 120.0, originPy = 40.0, stepP = 200.0;

            int idx = 0;
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    var pt = Points[idx];
                    pt.PixelX = originPx + c * stepP;
                    pt.PixelY = originPy + r * stepP;
                    pt.WorldX = originWx + c * stepW;
                    pt.WorldY = originWy + r * stepW;
                    pt.IsSampled = true;
                    idx++;
                }
            }

            InputPixelX = Points[0].PixelX;
            InputPixelY = Points[0].PixelY;
            InputWorldX = Points[0].WorldX;
            InputWorldY = Points[0].WorldY;
            CurrentPointIndex = 1;

            CalibrationStatus = "已生成 3×3 标准仿真网格标定点对";
            Growl.Success("已快速填充 9 组示例标定点对");
        }

        private void ExecuteCalibrateNinePoint()
        {
            try
            {
                var sampled = Points.Where(p => p.IsSampled).ToList();
                if (sampled.Count < 3)
                {
                    Growl.Warning("有效标定点至少需要 3 点，建议完成全部 9 点采集");
                    return;
                }

                var calibPoints = sampled.Select(p => new CalibrationPoint(p.PixelX, p.PixelY, p.WorldX, p.WorldY)).ToList();

                var calib = NinePointCalibration.Calibrate(
                    calibPoints,
                    maxResidualMmThreshold: MaxResidualMmThreshold,
                    maxResidualPxThreshold: MaxResidualPxThreshold);

                _currentCalibration = calib;
                HasCalibrated = true;
                IsAcceptable = calib.IsAcceptable;

                RmsResidualMm = calib.Residual.RmsErrorMm;
                MaxResidualMm = calib.Residual.MaxErrorMm;
                RmsResidualPx = calib.Residual.RmsErrorPx;
                MaxResidualPx = calib.Residual.MaxErrorPx;

                var m = calib.Matrix;
                MatrixDescription = $"[ [{m[0][0]:F6}, {m[0][1]:F6}, {m[0][2]:F3}],\n  [{m[1][0]:F6}, {m[1][1]:F6}, {m[1][2]:F3}] ]";

                if (IsAcceptable)
                {
                    CalibrationStatus = $"标定成功 (合格)：物理最大残差 {MaxResidualMm:F4} mm <= {MaxResidualMmThreshold} mm";
                    Growl.Success("九点标定成功且精度合格！");
                }
                else
                {
                    CalibrationStatus = $"标定完成 (超差)：物理最大残差 {MaxResidualMm:F4} mm 超过设定阈值 {MaxResidualMmThreshold} mm";
                    Growl.Warning("标定残差超差，请检查点位采集质量或重采异常点");
                }
            }
            catch (Exception ex)
            {
                Growl.Error($"标定计算失败: {ex.Message}");
                CalibrationStatus = $"标定计算异常: {ex.Message}";
            }
        }

        private void ExecuteSaveCalibration()
        {
            try
            {
                if (_currentCalibration == null || string.IsNullOrEmpty(SelectedCamera))
                {
                    Growl.Warning("暂无有效的标定计算结果可保存");
                    return;
                }

                // 读取原有数据以保留可能已存在的旋转中心或备注
                var existing = CalibrationStore.Load(SelectedCamera) ?? new CalibrationData();

                existing.CameraId = SelectedCamera;
                existing.AffineMatrix = _currentCalibration.Matrix;
                existing.MaxResidualMm = _currentCalibration.Residual.MaxErrorMm;
                existing.RmsResidualMm = _currentCalibration.Residual.RmsErrorMm;
                existing.MaxResidualPx = _currentCalibration.Residual.MaxErrorPx;
                existing.RmsResidualPx = _currentCalibration.Residual.RmsErrorPx;
                existing.IsAcceptable = _currentCalibration.IsAcceptable;
                existing.CalibratedTimeUtc = DateTime.UtcNow;
                existing.Operator = "Operator";
                existing.Remarks = $"九点标定保存，采样点数: {Points.Count(p => p.IsSampled)}";

                if (HasRotationCenter)
                {
                    existing.RotationCenterX = RotationCenterX;
                    existing.RotationCenterY = RotationCenterY;
                    existing.RotationRadiusMm = RotationRadius;
                }

                CalibrationStore.Save(existing);
                CalibrationStatus = $"标定参数已成功保存至磁盘 (相机: {SelectedCamera})";
                Growl.Success($"标定文件已保存: {SelectedCamera}.json");
            }
            catch (Exception ex)
            {
                Growl.Error($"保存标定失败: {ex.Message}");
                CalibrationStatus = $"保存失败: {ex.Message}";
            }
        }

        private void ExecuteLoadCalibration()
        {
            try
            {
                if (string.IsNullOrEmpty(SelectedCamera)) return;

                var data = CalibrationStore.Load(SelectedCamera);
                if (data == null || data.AffineMatrix == null)
                {
                    CalibrationStatus = $"相机 {SelectedCamera} 尚未创建标定文件";
                    return;
                }

                var res = new CalibrationResidual(data.RmsResidualMm, data.MaxResidualMm, data.RmsResidualPx, data.MaxResidualPx);
                _currentCalibration = new NinePointCalibration(data.AffineMatrix, res, MaxResidualMmThreshold, MaxResidualPxThreshold);

                HasCalibrated = true;
                IsAcceptable = data.IsAcceptable;
                RmsResidualMm = data.RmsResidualMm;
                MaxResidualMm = data.MaxResidualMm;
                RmsResidualPx = data.RmsResidualPx;
                MaxResidualPx = data.MaxResidualPx;

                var m = data.AffineMatrix;
                MatrixDescription = $"[ [{m[0][0]:F6}, {m[0][1]:F6}, {m[0][2]:F3}],\n  [{m[1][0]:F6}, {m[1][1]:F6}, {m[1][2]:F3}] ]";

                if (data.RotationCenterX.HasValue && data.RotationCenterY.HasValue)
                {
                    RotationCenterX = data.RotationCenterX.Value;
                    RotationCenterY = data.RotationCenterY.Value;
                    RotationRadius = data.RotationRadiusMm ?? 0.0;
                    HasRotationCenter = true;
                }

                CalibrationStatus = $"已加载相机 {SelectedCamera} 标定参数 (时间: {data.CalibratedTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm})";
                Growl.Info($"已载入 {SelectedCamera} 历史标定数据");
            }
            catch (Exception ex)
            {
                CalibrationStatus = $"加载标定异常: {ex.Message}";
            }
        }

        #endregion

        #region Rotation Center Actions

        private void ExecuteSampleRotPoint1()
        {
            if (_lastTriggerResult != null)
            {
                RotWorldX1 = _lastTriggerResult.WorldX;
                RotWorldY1 = _lastTriggerResult.WorldY;
                Growl.Success($"已将最新测量坐标填入采样点1: ({RotWorldX1:F3}, {RotWorldY1:F3})");
            }
            else
            {
                Growl.Warning("尚未获取到最新测量结果，请先执行触发测量");
            }
        }

        private void ExecuteSampleRotPoint2()
        {
            if (_lastTriggerResult != null)
            {
                RotWorldX2 = _lastTriggerResult.WorldX;
                RotWorldY2 = _lastTriggerResult.WorldY;
                Growl.Success($"已将最新测量坐标填入采样点2: ({RotWorldX2:F3}, {RotWorldY2:F3})");
            }
            else
            {
                Growl.Warning("尚未获取到最新测量结果，请先执行触发测量");
            }
        }

        private void ExecuteCalibrateRotationCenter()
        {
            try
            {
                var p1 = new RotationSamplePoint(RotAngle1, RotWorldX1, RotWorldY1);
                var p2 = new RotationSamplePoint(RotAngle2, RotWorldX2, RotWorldY2);

                var res = RotationCenterCalibration.CalibrateTwoPoints(p1, p2);

                RotationCenterX = res.CenterX;
                RotationCenterY = res.CenterY;
                RotationRadius = res.RadiusMm;
                RotationRms = res.RmsResidualMm;
                HasRotationCenter = true;

                CalibrationStatus = $"旋转中心标定完成: Center=({RotationCenterX:F3}, {RotationCenterY:F3}) mm, 半径={RotationRadius:F3} mm";
                Growl.Success("两点法旋转中心标定成功！");
            }
            catch (Exception ex)
            {
                Growl.Error($"旋转中心计算失败: {ex.Message}");
                CalibrationStatus = $"旋转中心计算异常: {ex.Message}";
            }
        }

        private void ExecuteSaveRotationCenter()
        {
            try
            {
                if (!HasRotationCenter || string.IsNullOrEmpty(SelectedCamera))
                {
                    Growl.Warning("尚未计算出有效的旋转中心");
                    return;
                }

                var existing = CalibrationStore.Load(SelectedCamera) ?? new CalibrationData();
                existing.CameraId = SelectedCamera;
                existing.RotationCenterX = RotationCenterX;
                existing.RotationCenterY = RotationCenterY;
                existing.RotationRadiusMm = RotationRadius;

                CalibrationStore.Save(existing);
                CalibrationStatus = $"旋转中心已保存至相机 {SelectedCamera} 标定文件";
                Growl.Success("旋转中心坐标已保存");
            }
            catch (Exception ex)
            {
                Growl.Error($"保存旋转中心失败: {ex.Message}");
            }
        }

        #endregion

        #region Teach Base & Offset Correction Actions

        private void ExecuteTeachCurrentAsBase()
        {
            if (_lastTriggerResult != null)
            {
                BasePixelX = _lastTriggerResult.X;
                BasePixelY = _lastTriggerResult.Y;
                BasePixelAngleDeg = _lastTriggerResult.AngleDeg;
                TeachWorldX = _lastTriggerResult.WorldX;
                TeachWorldY = _lastTriggerResult.WorldY;
                TeachWorldAngleDeg = _lastTriggerResult.WorldAngleDeg;

                CalibrationStatus = $"已将当前测量结果示教为标准基准 [{TeachBaseName}]";
                Growl.Success($"示教基准更新: 像素({BasePixelX:F1},{BasePixelY:F1}), 物理({TeachWorldX:F2},{TeachWorldY:F2})");
            }
            else
            {
                Growl.Warning("无有效测量结果，请先点击单次触发");
            }
        }

        private void ExecuteTriggerAndDemoCorrection()
        {
            try
            {
                if (_visionProvider?.Cameras == null || _visionProvider.Cameras.Count == 0)
                {
                    Growl.Warning("请先在相机配置中添加并应用相机");
                    CalibrationStatus = "请先在「相机配置」中保存并应用相机，再触发采集";
                    return;
                }

                if (string.IsNullOrEmpty(SelectedCamera))
                {
                    Growl.Warning("请先选择相机");
                    return;
                }

                CalibrationStatus = "触发单次测量并演示对位纠偏计算...";
                _visionProvider.Trigger(SelectedCamera);
            }
            catch (Exception ex)
            {
                Growl.Error($"触发测量失败: {ex.Message}");
            }
        }

        private void PerformCorrectionCalculation(VisionResult currentResult)
        {
            try
            {
                var teachBase = new VisionTeachBase(
                    name: TeachBaseName,
                    cameraId: SelectedCamera,
                    teachWorldX: TeachWorldX,
                    teachWorldY: TeachWorldY,
                    teachWorldAngleDeg: TeachWorldAngleDeg,
                    basePixelX: BasePixelX,
                    basePixelY: BasePixelY,
                    basePixelAngleDeg: BasePixelAngleDeg);

                // 计算视觉偏差
                var offset = OffsetCalculator.Calculate(currentResult, teachBase, _currentCalibration);
                OffsetDeltaWorldX = offset.DeltaWorldX;
                OffsetDeltaWorldY = offset.DeltaWorldY;
                OffsetDeltaAngleDeg = offset.DeltaAngleDeg;
                OffsetDeltaPixelX = offset.DeltaPixelX;
                OffsetDeltaPixelY = offset.DeltaPixelY;

                // 计算 XYR 平台纠偏量与修正目标
                var target = XyrCorrection.CalculateFromOffset(
                    teachBase,
                    offset,
                    rotationCenterX: RotationCenterX,
                    rotationCenterY: RotationCenterY);

                CompDeltaX = target.DeltaX;
                CompDeltaY = target.DeltaY;
                CompDeltaAngle = target.DeltaAngle;
                CorrectedTargetX = target.CorrectedX;
                CorrectedTargetY = target.CorrectedY;

                CalibrationStatus = $"纠偏计算完成: ΔX={CompDeltaX:F3} mm, ΔY={CompDeltaY:F3} mm, Δθ={CompDeltaAngle:F3}°";
            }
            catch (Exception ex)
            {
                CalibrationStatus = $"纠偏计算异常: {ex.Message}";
            }
        }

        #endregion

        #region Event Handlers

        private void SubscribeEvents()
        {
            if (_isSubscribed || _visionProvider == null) return;
            _visionProvider.ResultReady += OnResultReady;
            _isSubscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_isSubscribed || _visionProvider == null) return;
            _visionProvider.ResultReady -= OnResultReady;
            _isSubscribed = false;
        }

        private void OnResultReady(VisionResult result)
        {
            if (result == null) return;

            _dispatcher.InvokeAsync(() =>
            {
                _lastTriggerResult = result;

                // 更新当前录入框的像素坐标缓存
                InputPixelX = result.X;
                InputPixelY = result.Y;

                // 若同时处在纠偏演示阶段，自动联动更新偏差与对位目标
                PerformCorrectionCalculation(result);

                CalibrationStatus = $"已获取测量值: 像素=({result.X:F1}, {result.Y:F1}), 物理=({result.WorldX:F2}, {result.WorldY:F2}), Ok={result.Ok}";
            });
        }

        #endregion

        #region INavigationAware

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            RefreshCameraList();
            SubscribeEvents();

            // 未应用相机时提示先到「相机配置」页保存并应用
            if (_visionProvider?.Cameras == null || _visionProvider.Cameras.Count == 0)
            {
                CalibrationStatus = "请先在「相机配置」中保存并应用相机，再触发采集";
            }
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            UnsubscribeEvents();
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                UnsubscribeEvents();
            }
        }
    }
}
