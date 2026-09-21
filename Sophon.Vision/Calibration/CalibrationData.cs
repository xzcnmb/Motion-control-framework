using System;

namespace Sophon.Vision.Calibration
{
    /// <summary>
    /// 可持久化与 JSON 序列化的视觉标定数据实体。
    /// 包含相机标识、九点仿射矩阵、旋转中心、残差评估、标定时间及操作员。
    /// </summary>
    public sealed class CalibrationData
    {
        /// <summary>
        /// 相机唯一标识。
        /// </summary>
        public string CameraId { get; set; } = string.Empty;

        /// <summary>
        /// 2x3 仿射标定矩阵 [ [a11, a12, dx], [a21, a22, dy] ]。
        /// </summary>
        public double[][]? AffineMatrix { get; set; }

        /// <summary>
        /// 旋转中心物理坐标 (mm)。
        /// </summary>
        public double? RotationCenterX { get; set; }

        /// <summary>
        /// 旋转中心物理坐标 (mm)。
        /// </summary>
        public double? RotationCenterY { get; set; }

        /// <summary>
        /// 旋转特征半径 (mm)。
        /// </summary>
        public double? RotationRadiusMm { get; set; }

        /// <summary>
        /// 仿射变换最大残差 (mm)。
        /// </summary>
        public double MaxResidualMm { get; set; }

        /// <summary>
        /// 仿射变换 RMS 残差 (mm)。
        /// </summary>
        public double RmsResidualMm { get; set; }

        /// <summary>
        /// 仿射变换最大像素反投影残差 (px)。
        /// </summary>
        public double MaxResidualPx { get; set; }

        /// <summary>
        /// 仿射变换 RMS 像素反投影残差 (px)。
        /// </summary>
        public double RmsResidualPx { get; set; }

        /// <summary>
        /// 标定是否合格。
        /// </summary>
        public bool IsAcceptable { get; set; }

        /// <summary>
        /// 标定完成时间 (UTC)。
        /// </summary>
        public DateTime CalibratedTimeUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 标定操作员或自动化任务名称。
        /// </summary>
        public string Operator { get; set; } = "System";

        /// <summary>
        /// 备注信息。
        /// </summary>
        public string? Remarks { get; set; }
    }
}
