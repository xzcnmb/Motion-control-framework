#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Vision.Providers;

namespace Sophon.UI.Vision
{
    /// <summary>
    /// 可切换的视觉提供者包装。
    /// 默认持有仿真提供者；调用 <see cref="Apply"/> 后切换到 <see cref="VisionProviderFactory.CreateProvider(CameraConfig)"/>
    /// 构建的真实/仿真后端。所有成员均转发给当前内部提供者。
    /// </summary>
    public sealed class SwitchableVisionProvider : IVisionProvider
    {
        private readonly object _gate = new();
        private IVisionProvider _inner;
        private bool _disposed;

        public SwitchableVisionProvider()
        {
            _inner = VisionProviderFactory.CreateSimProvider();
            HookEvents(_inner);
        }

        /// <summary>
        /// 内部当前生效的提供者（仅诊断用）。
        /// </summary>
        public IVisionProvider Inner
        {
            get
            {
                lock (_gate)
                {
                    return _inner;
                }
            }
        }

        public IReadOnlyList<string> Cameras
        {
            get
            {
                lock (_gate)
                {
                    return _inner.Cameras;
                }
            }
        }

        public event Action<VisionResult>? ResultReady;

        public event Action<VisionFrame>? FrameReady;

        /// <summary>
        /// 根据相机配置切换底层提供者：先停止取景、断开并释放旧提供者，
        /// 再以配置创建新提供者，并重新转发事件。
        /// </summary>
        public void Apply(CameraConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(SwitchableVisionProvider));
                }

                var old = _inner;

                // 先摘除事件转发，避免切换过程中旧提供者事件外泄
                UnhookEvents(old);

                try
                {
                    old.StopLive();
                }
                catch
                {
                    // 切换时忽略停止取景异常
                }

                try
                {
                    old.DisconnectAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // 切换时忽略断开异常
                }

                try
                {
                    old.Dispose();
                }
                catch
                {
                    // 切换时忽略释放异常
                }

                var next = VisionProviderFactory.CreateProvider(config);
                HookEvents(next);
                _inner = next;
            }
        }

        public Task ConnectAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return _inner.ConnectAsync(ct);
            }
        }

        public Task DisconnectAsync()
        {
            lock (_gate)
            {
                return _inner.DisconnectAsync();
            }
        }

        public Guid Trigger(string cameraId)
        {
            lock (_gate)
            {
                return _inner.Trigger(cameraId);
            }
        }

        public void StartLive(string cameraId)
        {
            lock (_gate)
            {
                _inner.StartLive(cameraId);
            }
        }

        public void StopLive()
        {
            lock (_gate)
            {
                _inner.StopLive();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    _inner.StopLive();
                }
                catch
                {
                }

                try
                {
                    _inner.DisconnectAsync().GetAwaiter().GetResult();
                }
                catch
                {
                }

                UnhookEvents(_inner);
                _inner.Dispose();
            }
        }

        private void HookEvents(IVisionProvider provider)
        {
            provider.ResultReady += OnInnerResultReady;
            provider.FrameReady += OnInnerFrameReady;
        }

        private void UnhookEvents(IVisionProvider provider)
        {
            provider.ResultReady -= OnInnerResultReady;
            provider.FrameReady -= OnInnerFrameReady;
        }

        private void OnInnerResultReady(VisionResult result)
        {
            ResultReady?.Invoke(result);
        }

        private void OnInnerFrameReady(VisionFrame frame)
        {
            FrameReady?.Invoke(frame);
        }

        private sealed class UnconfiguredVisionProvider : IVisionProvider
        {
            public IReadOnlyList<string> Cameras => Array.Empty<string>();
            public event Action<VisionResult>? ResultReady;
            public event Action<VisionFrame>? FrameReady;
            public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task DisconnectAsync() => Task.CompletedTask;
            public Guid Trigger(string cameraId) =>
                throw new InvalidOperationException("请先在「相机配置」中填写设备标识并应用到运行时。");
            public void StartLive(string cameraId) =>
                throw new InvalidOperationException("请先应用相机配置后再取景。");
            public void StopLive() { }
            public void Dispose() { }
        }
    }
}
