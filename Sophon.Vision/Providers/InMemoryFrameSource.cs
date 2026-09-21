using OpenCvSharp;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 内存图像源（主要用于测试及单元仿真）。
    /// </summary>
    public sealed class InMemoryFrameSource : IFrameSource
    {
        private readonly object _lock = new();
        private Mat? _currentFrame;
        private bool _disposed;

        /// <summary>
        /// 设置当前内存帧（内部会 Clone 一份）。
        /// </summary>
        public void SetFrame(Mat frame)
        {
            lock (_lock)
            {
                _currentFrame?.Dispose();
                _currentFrame = frame.Clone();
            }
        }

        /// <inheritdoc />
        public bool TryGrab(out Mat? frame)
        {
            lock (_lock)
            {
                if (_disposed || _currentFrame == null || _currentFrame.IsDisposed)
                {
                    frame = null;
                    return false;
                }

                frame = _currentFrame.Clone();
                return true;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _currentFrame?.Dispose();
                    _currentFrame = null;
                }
            }
        }
    }
}
