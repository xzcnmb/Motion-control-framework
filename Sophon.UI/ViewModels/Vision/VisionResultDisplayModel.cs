#nullable enable
using System;
using Sophon.Contracts;

namespace Sophon.UI.ViewModels.Vision
{
    /// <summary>
    /// 视觉测量结果的界面展示模型（纯数据转换，供视图绑定与独立单元测试）。
    /// </summary>
    public sealed class VisionResultDisplayModel
    {
        /// <summary>请求唯一标识。</summary>
        public Guid RequestId { get; init; }

        /// <summary>简短请求标识（前8位）。</summary>
        public string RequestIdShort => RequestId.ToString("N").Length >= 8
            ? RequestId.ToString("N")[..8]
            : RequestId.ToString("N");

        /// <summary>触发相机编号。</summary>
        public string CameraId { get; init; } = string.Empty;

        /// <summary>测量判定结果。</summary>
        public bool Ok { get; init; }

        /// <summary>状态文本（OK/NG）。</summary>
        public string StatusText => Ok ? "OK" : "NG";

        /// <summary>状态提示颜色 Hex 字符串（绿色 #52C41A / 红色 #F5222D）。</summary>
        public string StatusColor => Ok ? "#52C41A" : "#F5222D";

        /// <summary>像素 X 坐标 (px)。</summary>
        public double X { get; init; }

        /// <summary>像素 Y 坐标 (px)。</summary>
        public double Y { get; init; }

        /// <summary>像素旋转角度 (deg)。</summary>
        public double AngleDeg { get; init; }

        /// <summary>匹配置信度打分 (0.0~1.0)。</summary>
        public double Score { get; init; }

        /// <summary>打分百分比格式化。</summary>
        public string ScorePercentText => $"{Score * 100.0:F1}%";

        /// <summary>物理世界 X 坐标 (mm)。</summary>
        public double WorldX { get; init; }

        /// <summary>物理世界 Y 坐标 (mm)。</summary>
        public double WorldY { get; init; }

        /// <summary>物理世界旋转角度 (deg)。</summary>
        public double WorldAngleDeg { get; init; }

        /// <summary>记录生成时间戳。</summary>
        public DateTime Timestamp { get; init; } = DateTime.Now;

        /// <summary>时间戳显示文本。</summary>
        public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");

        /// <summary>原始视觉结果。</summary>
        public VisionResult? RawResult { get; init; }

        /// <summary>
        /// 从后端契约 VisionResult 纯函数转换为展示模型。
        /// </summary>
        /// <param name="result">后端视觉测量结果</param>
        /// <param name="timestamp">指定时间戳，为空时取当前本地时间</param>
        /// <returns>界面展示模型</returns>
        public static VisionResultDisplayModel From(VisionResult result, DateTime? timestamp = null)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return new VisionResultDisplayModel
            {
                RequestId = result.RequestId,
                CameraId = result.CameraId ?? string.Empty,
                Ok = result.Ok,
                X = result.X,
                Y = result.Y,
                AngleDeg = result.AngleDeg,
                Score = result.Score,
                WorldX = result.WorldX,
                WorldY = result.WorldY,
                WorldAngleDeg = result.WorldAngleDeg,
                Timestamp = timestamp ?? DateTime.Now,
                RawResult = result
            };
        }
    }
}
