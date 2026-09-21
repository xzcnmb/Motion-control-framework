using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Sophon.Contracts;
using Sophon.Vision.Utilities;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 基于 OpenCvSharp4 的通用模板匹配视觉提供者。
    /// 支持从 IFrameSource 实时拉取图像帧进行模板匹配与亚像素峰值细分定位。
    /// </summary>
    public sealed class OpenCvVisionProvider : IVisionProvider
    {
        private readonly List<string> _cameras;
        private readonly IFrameSource _frameSource;
        private readonly OpenCvVisionConfig _config;
        private readonly object _stateLock = new();

        private Mat? _templateMat;
        private CancellationTokenSource? _liveCts;
        private Task? _liveTask;
        private bool _isConnected;
        private bool _disposed;

        /// <summary>
        /// 是否处于已连接状态。
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_stateLock)
                {
                    return _isConnected;
                }
            }
        }

        /// <inheritdoc />
        public IReadOnlyList<string> Cameras => _cameras;

        /// <inheritdoc />
        public event Action<VisionResult>? ResultReady;

        /// <inheritdoc />
        public event Action<VisionFrame>? FrameReady;

        /// <summary>
        /// 构造函数。
        /// </summary>
        /// <param name="frameSource">图像源（内存或文件夹轮询）</param>
        /// <param name="config">匹配参数配置</param>
        /// <param name="cameras">相机列表</param>
        public OpenCvVisionProvider(
            IFrameSource frameSource,
            OpenCvVisionConfig? config = null,
            IEnumerable<string>? cameras = null)
        {
            _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
            _config = config ?? new OpenCvVisionConfig();
            _cameras = cameras != null ? new List<string>(cameras) : new List<string> { "Camera1" };

            // 若配置了模板文件路径且存在，加载模板
            if (!string.IsNullOrEmpty(_config.TemplateImagePath) && File.Exists(_config.TemplateImagePath))
            {
                SetTemplate(Cv2.ImRead(_config.TemplateImagePath, ImreadModes.Grayscale));
            }
        }

        /// <summary>
        /// 设置/更新匹配模板图像。
        /// </summary>
        /// <param name="template">模板图像</param>
        public void SetTemplate(Mat template)
        {
            lock (_stateLock)
            {
                _templateMat?.Dispose();
                if (template.Channels() > 1)
                {
                    _templateMat = new Mat();
                    Cv2.CvtColor(template, _templateMat, ColorConversionCodes.BGR2GRAY);
                }
                else
                {
                    _templateMat = template.Clone();
                }
            }
        }

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                _isConnected = true;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            StopLive();
            lock (_stateLock)
            {
                _isConnected = false;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Guid Trigger(string cameraId)
        {
            var requestId = Guid.NewGuid();

            _ = Task.Run(() =>
            {
                VisionResult result;
                try
                {
                    result = ExecuteMatch(requestId, cameraId);
                }
                catch (Exception)
                {
                    result = new VisionResult(
                        RequestId: requestId,
                        CameraId: cameraId,
                        Ok: false,
                        X: 0,
                        Y: 0,
                        AngleDeg: 0,
                        Score: 0,
                        WorldX: 0,
                        WorldY: 0,
                        WorldAngleDeg: 0);
                }

                ResultReady?.Invoke(result);
            });

            return requestId;
        }

        /// <summary>
        /// 同步执行一次匹配计算。
        /// </summary>
        public VisionResult ExecuteMatch(Guid requestId, string cameraId)
        {
            Mat? frame = null;
            Mat? searchMat = null;
            try
            {
                if (!_frameSource.TryGrab(out frame) || frame == null || frame.Empty())
                {
                    return new VisionResult(requestId, cameraId, false, 0, 0, 0, 0, 0, 0, 0);
                }

                // 统一为单通道灰度
                if (frame.Channels() > 1)
                {
                    searchMat = new Mat();
                    Cv2.CvtColor(frame, searchMat, ColorConversionCodes.BGR2GRAY);
                }
                else
                {
                    searchMat = frame;
                }

                Mat? template;
                lock (_stateLock)
                {
                    template = _templateMat != null && !_templateMat.IsDisposed ? _templateMat.Clone() : null;
                }

                if (template == null || template.Empty())
                {
                    return new VisionResult(requestId, cameraId, false, 0, 0, 0, 0, 0, 0, 0);
                }

                using (template)
                {
                    // 若有 ROI，裁剪区域
                    Mat roiMat = searchMat;
                    int roiOffsetX = 0;
                    int roiOffsetY = 0;
                    bool needDisposeRoi = false;

                    if (_config.SearchRegion.HasValue)
                    {
                        var roi = _config.SearchRegion.Value;
                        // 边界保护
                        int x = Math.Max(0, roi.X);
                        int y = Math.Max(0, roi.Y);
                        int w = Math.Min(searchMat.Width - x, roi.Width);
                        int h = Math.Min(searchMat.Height - y, roi.Height);

                        if (w > template.Width && h > template.Height)
                        {
                            roiMat = new Mat(searchMat, new Rect(x, y, w, h));
                            roiOffsetX = x;
                            roiOffsetY = y;
                            needDisposeRoi = true;
                        }
                    }

                    try
                    {
                        if (roiMat.Width < template.Width || roiMat.Height < template.Height)
                        {
                            return new VisionResult(requestId, cameraId, false, 0, 0, 0, 0, 0, 0, 0);
                        }

                        // 执行模板匹配
                        var (matchX, matchY, angle, score) = PerformMatchWithSubpixel(roiMat, template, _config);

                        double finalX = matchX + roiOffsetX;
                        double finalY = matchY + roiOffsetY;
                        bool ok = score >= _config.MinScore;

                        return new VisionResult(
                            RequestId: requestId,
                            CameraId: cameraId,
                            Ok: ok,
                            X: finalX,
                            Y: finalY,
                            AngleDeg: angle,
                            Score: score,
                            WorldX: finalX,
                            WorldY: finalY,
                            WorldAngleDeg: angle);
                    }
                    finally
                    {
                        if (needDisposeRoi)
                        {
                            roiMat.Dispose();
                        }
                    }
                }
            }
            finally
            {
                if (searchMat != null && !ReferenceEquals(searchMat, frame))
                {
                    searchMat.Dispose();
                }
                frame?.Dispose();
            }
        }

        /// <summary>
        /// 执行带亚像素峰值插值与可选旋转角搜索的模板匹配。
        /// </summary>
        private static (double X, double Y, double Angle, double Score) PerformMatchWithSubpixel(
            Mat src,
            Mat tpl,
            OpenCvVisionConfig cfg)
        {
            if (!cfg.EnableAngleEstimation)
            {
                return MatchSingleAngleWithSubpixel(src, tpl, cfg.MatchMethod);
            }

            // 多角度金字塔/旋转搜索
            double bestScore = -1.0;
            double bestX = 0;
            double bestY = 0;
            double bestAngle = 0;

            double halfRange = Math.Abs(cfg.AngleSearchRangeDeg);
            double step = Math.Max(0.1, cfg.AngleSearchStepDeg);

            for (double angle = -halfRange; angle <= halfRange; angle += step)
            {
                using var rotatedTpl = RotateImage(tpl, angle);
                if (src.Width < rotatedTpl.Width || src.Height < rotatedTpl.Height)
                {
                    continue;
                }

                var (curX, curY, _, curScore) = MatchSingleAngleWithSubpixel(src, rotatedTpl, cfg.MatchMethod);
                if (curScore > bestScore)
                {
                    bestScore = curScore;
                    bestX = curX;
                    bestY = curY;
                    bestAngle = angle;
                }
            }

            return (bestX, bestY, bestAngle, bestScore);
        }

        /// <summary>
        /// 单一角度下的模板匹配与二次曲面（Parabola / Taylor）亚像素极值拟合。
        /// </summary>
        private static (double X, double Y, double Angle, double Score) MatchSingleAngleWithSubpixel(
            Mat src,
            Mat tpl,
            TemplateMatchModes method)
        {
            using var resultMat = new Mat();
            Cv2.MatchTemplate(src, tpl, resultMat, method);

            Cv2.MinMaxLoc(resultMat, out double minVal, out double maxVal, out Point minLoc, out Point maxLoc);

            bool isSqDiff = method == TemplateMatchModes.SqDiff || method == TemplateMatchModes.SqDiffNormed;
            Point peakLoc = isSqDiff ? minLoc : maxLoc;
            double peakVal = isSqDiff ? (1.0 - minVal) : maxVal;

            double subX = peakLoc.X;
            double subY = peakLoc.Y;

            // 亚像素 3x3 邻域二次多项式拟合
            // 确保不越界
            if (peakLoc.X > 0 && peakLoc.X < resultMat.Width - 1 &&
                peakLoc.Y > 0 && peakLoc.Y < resultMat.Height - 1)
            {
                // 取 X 方向三点 f(x-1), f(x), f(x+1)
                float fXm1 = resultMat.At<float>(peakLoc.Y, peakLoc.X - 1);
                float fX0  = resultMat.At<float>(peakLoc.Y, peakLoc.X);
                float fXp1 = resultMat.At<float>(peakLoc.Y, peakLoc.X + 1);

                double denomX = 2.0 * (fXm1 - 2.0 * fX0 + fXp1);
                if (Math.Abs(denomX) > 1e-7)
                {
                    double deltaX = (fXm1 - fXp1) / denomX;
                    if (Math.Abs(deltaX) <= 1.0)
                    {
                        subX += deltaX;
                    }
                }

                // 取 Y 方向三点 f(y-1), f(y), f(y+1)
                float fYm1 = resultMat.At<float>(peakLoc.Y - 1, peakLoc.X);
                float fY0  = resultMat.At<float>(peakLoc.Y, peakLoc.X);
                float fYp1 = resultMat.At<float>(peakLoc.Y + 1, peakLoc.X);

                double denomY = 2.0 * (fYm1 - 2.0 * fY0 + fYp1);
                if (Math.Abs(denomY) > 1e-7)
                {
                    double deltaY = (fYm1 - fYp1) / denomY;
                    if (Math.Abs(deltaY) <= 1.0)
                    {
                        subY += deltaY;
                    }
                }
            }

            // 匹配输出的目标中心点 = 左上角 + 模板中心半宽半高
            double centerX = subX + tpl.Width / 2.0;
            double centerY = subY + tpl.Height / 2.0;

            return (centerX, centerY, 0.0, peakVal);
        }

        private static Mat RotateImage(Mat src, double angleDeg)
        {
            var center = new Point2f(src.Width / 2.0f, src.Height / 2.0f);
            using var rotMat = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
            var dst = new Mat();
            Cv2.WarpAffine(src, dst, rotMat, src.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            return dst;
        }

        /// <inheritdoc />
        public void StartLive(string cameraId)
        {
            lock (_stateLock)
            {
                if (_liveCts != null) return;
                _liveCts = new CancellationTokenSource();
                var token = _liveCts.Token;
                int interval = 1000 / Math.Clamp(_config.LiveFps, 1, 60);

                _liveTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (_frameSource.TryGrab(out var frame) && frame != null && !frame.Empty())
                            {
                                using (frame)
                                {
                                    var vf = ImageBufferUtils.ToVisionFrame(frame);
                                    FrameReady?.Invoke(vf);
                                }
                            }
                            await Task.Delay(interval, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                        }
                    }
                }, token);
            }
        }

        /// <inheritdoc />
        public void StopLive()
        {
            CancellationTokenSource? cts;
            lock (_stateLock)
            {
                cts = _liveCts;
                _liveCts = null;
                _liveTask = null;
            }

            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopLive();
                _templateMat?.Dispose();
                _templateMat = null;
                _frameSource.Dispose();
            }
        }
    }
}
