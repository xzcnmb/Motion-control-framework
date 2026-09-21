namespace Sophon.Vision.Correction
{
    /// <summary>
    /// 视觉示教基准模型。
    /// 记录产品的标准物理基准点与视觉匹配得到的标准特征参数。
    /// </summary>
    public sealed class VisionTeachBase
    {
        /// <summary>
        /// 示教基准名称或型号标识。
        /// </summary>
        public string Name { get; set; } = "DefaultBase";

        /// <summary>
        /// 关联的相机标识。
        /// </summary>
        public string CameraId { get; set; } = string.Empty;

        /// <summary>
        /// 物理机械基准坐标 X (mm)。
        /// </summary>
        public double TeachWorldX { get; set; }

        /// <summary>
        /// 物理机械基准坐标 Y (mm)。
        /// </summary>
        public double TeachWorldY { get; set; }

        /// <summary>
        /// 物理机械基准角度 (deg)。
        /// </summary>
        public double TeachWorldAngleDeg { get; set; }

        /// <summary>
        /// 标准产品对应的模板/特征像素位置 X (px)。
        /// </summary>
        public double BasePixelX { get; set; }

        /// <summary>
        /// 标准产品对应的模板/特征像素位置 Y (px)。
        /// </summary>
        public double BasePixelY { get; set; }

        /// <summary>
        /// 标准特征像素角度 (deg)。
        /// </summary>
        public double BasePixelAngleDeg { get; set; }

        /// <summary>
        /// 构造函数。
        /// </summary>
        public VisionTeachBase()
        {
        }

        /// <summary>
        /// 便捷构造函数。
        /// </summary>
        public VisionTeachBase(
            string name,
            string cameraId,
            double teachWorldX,
            double teachWorldY,
            double teachWorldAngleDeg,
            double basePixelX,
            double basePixelY,
            double basePixelAngleDeg = 0.0)
        {
            Name = name;
            CameraId = cameraId;
            TeachWorldX = teachWorldX;
            TeachWorldY = teachWorldY;
            TeachWorldAngleDeg = teachWorldAngleDeg;
            BasePixelX = basePixelX;
            BasePixelY = basePixelY;
            BasePixelAngleDeg = basePixelAngleDeg;
        }
    }
}
