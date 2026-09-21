using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Sophon.Contracts;
using Sophon.Vision.Utilities;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 仿真视觉提供者。
    /// 合成灰度图（白底黑圆/十字/矩形图案），位置与角度由内部真值与注入噪声决定。
    /// 支持全链路离线测量、连续取景测试与故障注入。
    /// </summary>
    public sealed class SimVisionProvider : IVisionProvider
    {
        private readonly List<string> _cameras;
        private readonly object _stateLock = new();
        private readonly Random _random = new();

        private double _groundTruthX = 320.0;
        private double _groundTruthY = 240.0;
        private double _groundTruthAngleDeg = 0.0;

        private double _noiseSigmaPx = 0.0;
        private bool _injectFailure = false;

        private int _frameWidth = 640;
        private int _frameHeight = 480;
        private int _liveFps = 20;

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
        /// <param name="cameras">相机列表，缺省为 ["SimCam1", "SimCam2"]</param>
        public SimVisionProvider(IEnumerable<string>? cameras = null)
        {
            _cameras = cameras != null ? new List<string>(cameras) : new List<string> { "SimCam1", "SimCam2" };
        }

        /// <summary>
        /// 设置当前仿真特征的真值坐标与角度。
        /// </summary>
        public void SetGroundTruth(double x, double y, double angleDeg)
        {
            lock (_stateLock)
            {
                _groundTruthX = x;
                _groundTruthY = y;
                _groundTruthAngleDeg = angleDeg;
            }
        }

        /// <summary>
        /// 设置高斯白噪声标准差（像素单位）。
        /// </summary>
        public void SetNoiseLevel(double sigmaPx)
        {
            lock (_stateLock)
            {
                _noiseSigmaPx = Math.Max(0.0, sigmaPx);
            }
        }

        /// <summary>
        /// 注入硬故障（使后续 Trigger 结果 Ok=false）。
        /// </summary>
        public void InjectFailure(bool fail)
        {
            lock (_stateLock)
            {
                _injectFailure = fail;
            }
        }

        /// <summary>
        /// 配置仿真帧尺寸与实时预览帧率。
        /// </summary>
        public void ConfigureImage(int width, int height, int fps = 20)
        {
            lock (_stateLock)
            {
                _frameWidth = Math.Max(64, width);
                _frameHeight = Math.Max(48, height);
                _liveFps = Math.Clamp(fps, 1, 60);
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
        public async Task DisconnectAsync()
        {
            StopLive();
            lock (_stateLock)
            {
                _isConnected = false;
            }
            await Task.CompletedTask;
        }

        /// <inheritdoc />
        public Guid Trigger(string cameraId)
        {
            var requestId = Guid.NewGuid();

            double gtX, gtY, gtAngle, noise;
            bool fail;
            lock (_stateLock)
            {
                gtX = _groundTruthX;
                gtY = _groundTruthY;
                gtAngle = _groundTruthAngleDeg;
                noise = _noiseSigmaPx;
                fail = _injectFailure;
            }

            // 模拟工业视觉处理耗时 20~80ms
            int delayMs;
            lock (_random)
            {
                delayMs = _random.Next(20, 81);
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(delayMs).ConfigureAwait(false);

                double nX = 0.0, nY = 0.0, nA = 0.0;
                if (noise > 1e-6)
                {
                    lock (_random)
                    {
                        nX = NextGaussian(_random, 0, noise);
                        nY = NextGaussian(_random, 0, noise);
                        nA = NextGaussian(_random, 0, noise * 0.1);
                    }
                }

                double measuredX = gtX + nX;
                double measuredY = gtY + nY;
                double measuredAngle = gtAngle + nA;

                // 若噪声偏差过大或显式注入故障，则判定为失败
                bool ok = !fail && (Math.Abs(nX) < 5.0 * Math.Max(noise, 1.0));
                double score = ok ? Math.Clamp(0.99 - (Math.Sqrt(nX * nX + nY * nY) * 0.02), 0.5, 1.0) : 0.2;

                // 未结合外部标定前，World 坐标与像素坐标默认等同
                var result = new VisionResult(
                    RequestId: requestId,
                    CameraId: cameraId,
                    Ok: ok,
                    X: measuredX,
                    Y: measuredY,
                    AngleDeg: measuredAngle,
                    Score: score,
                    WorldX: measuredX,
                    WorldY: measuredY,
                    WorldAngleDeg: measuredAngle
                );

                ResultReady?.Invoke(result);
            });

            return requestId;
        }

        /// <inheritdoc />
        public void StartLive(string cameraId)
        {
            lock (_stateLock)
            {
                if (_liveCts != null)
                {
                    return; // 已经在运行
                }

                _liveCts = new CancellationTokenSource();
                var token = _liveCts.Token;
                int intervalMs = 1000 / _liveFps;

                _liveTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            var frame = GenerateCurrentFrame();
                            FrameReady?.Invoke(frame);
                            await Task.Delay(intervalMs, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch
                        {
                            // 避免未处理异常导致 Task 崩溃
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

        /// <summary>
        /// 合成一帧包含图案（白底、黑圆、十字、黑矩形）的灰度图像帧。
        /// </summary>
        public VisionFrame GenerateCurrentFrame()
        {
            int w, h;
            double cx, cy, angle;
            lock (_stateLock)
            {
                w = _frameWidth;
                h = _frameHeight;
                cx = _groundTruthX;
                cy = _groundTruthY;
                angle = _groundTruthAngleDeg;
            }

            using var mat = new Mat(h, w, MatType.CV_8UC1, new Scalar(255)); // 白底

            var center = new Point((int)Math.Round(cx), (int)Math.Round(cy));

            // 绘制特征图形：外矩形与同心黑圆与十字
            Cv2.Circle(mat, center, 20, new Scalar(0), -1); // 黑圆
            Cv2.Circle(mat, center, 12, new Scalar(255), -1); // 白心
            Cv2.Line(mat, new Point(center.X - 30, center.Y), new Point(center.X + 30, center.Y), new Scalar(0), 2); // 十字水平
            Cv2.Line(mat, new Point(center.X, center.Y - 30), new Point(center.X, center.Y + 30), new Scalar(0), 2); // 十字垂直

            // 绘制一个带角度的定位小框
            var rotRect = new RotatedRect(new Point2f((float)cx, (float)cy), new Size2f(60, 40), (float)angle);
            Point2f[] vertices = rotRect.Points();
            for (int i = 0; i < 4; i++)
            {
                Cv2.Line(mat, (Point)vertices[i], (Point)vertices[(i + 1) % 4], new Scalar(80), 2);
            }

            return ImageBufferUtils.ToVisionFrame(mat);
        }

        private static double NextGaussian(Random rand, double mean, double stdDev)
        {
            // Box-Muller 变换
            double u1 = 1.0 - rand.NextDouble();
            double u2 = 1.0 - rand.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                StopLive();
            }
        }
    }
}
