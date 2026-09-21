using System;
using System.Runtime.InteropServices;
using OpenCvSharp;
using Sophon.Contracts;

namespace Sophon.Vision.Utilities
{
    /// <summary>
    /// 图像缓冲与格式转换工具类。
    /// 提供高性能的帧复用与灰度数据提取，为 WPF/UI 渲染及下层数据转换提供支持。
    /// </summary>
    public static class ImageBufferUtils
    {
        /// <summary>
        /// 从 OpenCvSharp.Mat 中提取行优先的灰度字节数组。
        /// 若 Mat 不是 8 位单通道，会自动转换为单通道灰度格式。
        /// </summary>
        public static byte[] ToGrayscaleBytes(Mat mat)
        {
            if (mat == null || mat.Empty())
            {
                return Array.Empty<byte>();
            }

            Mat grayMat = mat;
            bool needDispose = false;

            if (mat.Channels() > 1)
            {
                grayMat = new Mat();
                Cv2.CvtColor(mat, grayMat, ColorConversionCodes.BGR2GRAY);
                needDispose = true;
            }
            else if (mat.Type() != MatType.CV_8UC1)
            {
                grayMat = new Mat();
                mat.ConvertTo(grayMat, MatType.CV_8UC1);
                needDispose = true;
            }

            try
            {
                int width = grayMat.Width;
                int height = grayMat.Height;
                byte[] bytes = new byte[width * height];

                if (grayMat.IsContinuous())
                {
                    Marshal.Copy(grayMat.Data, bytes, 0, bytes.Length);
                }
                else
                {
                    // 非连续内存，逐行复制
                    long step = grayMat.Step();
                    IntPtr ptr = grayMat.Data;
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(ptr, (int)(y * step)), bytes, y * width, width);
                    }
                }

                return bytes;
            }
            finally
            {
                if (needDispose)
                {
                    grayMat.Dispose();
                }
            }
        }

        /// <summary>
        /// 将 Mat 封装为契约中的 VisionFrame 对象。
        /// </summary>
        public static VisionFrame ToVisionFrame(Mat mat, long? timeStampMs = null)
        {
            byte[] data = ToGrayscaleBytes(mat);
            long ts = timeStampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return new VisionFrame(data, mat.Width, mat.Height, ts);
        }
    }
}
