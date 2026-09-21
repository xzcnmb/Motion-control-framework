using System.IO;
using OpenCvSharp;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 目录轮询图像源。
    /// 工业现场相机抓图落盘或测试图片轮询的通用联调实现。
    /// </summary>
    public sealed class DirectoryFrameSource : IFrameSource
    {
        private readonly string _directoryPath;
        private readonly string[] _searchPatterns;
        private string? _lastReadFilePath;
        private bool _disposed;

        /// <summary>
        /// 构造函数。
        /// </summary>
        /// <param name="directoryPath">监控图像文件的目录</param>
        /// <param name="searchPatterns">支持的图像扩展名模式，如 *.bmp, *.png, *.jpg</param>
        public DirectoryFrameSource(string directoryPath, string[]? searchPatterns = null)
        {
            _directoryPath = directoryPath;
            _searchPatterns = searchPatterns ?? new[] { "*.bmp", "*.png", "*.jpg", "*.jpeg", "*.tif" };
        }

        /// <inheritdoc />
        public bool TryGrab(out Mat? frame)
        {
            frame = null;
            if (_disposed || !Directory.Exists(_directoryPath))
            {
                return false;
            }

            try
            {
                FileInfo? latestFile = null;
                foreach (var pattern in _searchPatterns)
                {
                    var dirInfo = new DirectoryInfo(_directoryPath);
                    foreach (var file in dirInfo.GetFiles(pattern))
                    {
                        if (latestFile == null || file.LastWriteTimeUtc > latestFile.LastWriteTimeUtc)
                        {
                            latestFile = file;
                        }
                    }
                }

                if (latestFile == null)
                {
                    return false;
                }

                _lastReadFilePath = latestFile.FullName;
                frame = Cv2.ImRead(latestFile.FullName, ImreadModes.Grayscale);
                return frame != null && !frame.Empty();
            }
            catch
            {
                frame?.Dispose();
                frame = null;
                return false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
