using System;
using Sophon.Contracts;
using Sophon.Vision.Calibration;

namespace Sophon.Vision.Correction
{
    /// <summary>
    /// 视觉偏差量。
    /// </summary>
    /// <param name="DeltaWorldX">物理世界 X 方向偏差 (mm，即 当前 - 基准)</param>
    /// <param name="DeltaWorldY">物理世界 Y 方向偏差 (mm，即 当前 - 基准)</param>
    /// <param name="DeltaAngleDeg">物理世界角度偏差 (deg，即 当前 - 基准)</param>
    /// <param name="DeltaPixelX">像素 X 偏差 (px)</param>
    /// <param name="DeltaPixelY">像素 Y 偏差 (px)</param>
    public record VisionOffset(
        double DeltaWorldX,
        double DeltaWorldY,
        double DeltaAngleDeg,
        double DeltaPixelX,
        double DeltaPixelY);

    /// <summary>
    /// 偏差计算器。
    /// 计算当前实时视觉测量结果 VisionResult 与示教基准 VisionTeachBase 之间的偏差，
    /// 并通过九点标定矩阵精确转换到物理坐标系。
    /// </summary>
    public static class OffsetCalculator
    {
        /// <summary>
        /// 计算当前测量与示教基准的偏差。
        /// </summary>
        /// <param name="current">当前视觉测量结果</param>
        /// <param name="teachBase">示教基准点</param>
        /// <param name="calibration">相机的九点仿射标定（若为 null，则假定像素尺度即物理尺度）</param>
        /// <returns>计算得到的物理与像素偏差量</returns>
        public static VisionOffset Calculate(
            VisionResult current,
            VisionTeachBase teachBase,
            NinePointCalibration? calibration = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (teachBase == null) throw new ArgumentNullException(nameof(teachBase));

            double dPx = current.X - teachBase.BasePixelX;
            double dPy = current.Y - teachBase.BasePixelY;
            double dAngle = current.AngleDeg - teachBase.BasePixelAngleDeg;

            double deltaWorldX;
            double deltaWorldY;
            double deltaWorldAngle;

            if (calibration != null)
            {
                // 注意：仿射变换中纯增量向量变换不包含平移项 dx, dy
                // [ dX_w ]   [ a11  a12 ] [ dPx ]
                // [ dY_w ] = [ a21  a22 ] [ dPy ]
                var m = calibration.Matrix;
                deltaWorldX = m[0][0] * dPx + m[0][1] * dPy;
                deltaWorldY = m[1][0] * dPx + m[1][1] * dPy;

                // 若仿射变换中存在坐标轴镜像翻转（手性反转，det < 0，如常见图像 Y 轴向下而物理坐标系 Y 轴向上），
                // 角度增量符号必须对应取反，否则旋转纠偏方向将完全相反。
                double det = m[0][0] * m[1][1] - m[0][1] * m[1][0];
                deltaWorldAngle = det < 0 ? -dAngle : dAngle;
            }
            else
            {
                // 无标定时回退为 World 坐标直接相减
                deltaWorldX = current.WorldX - teachBase.TeachWorldX;
                deltaWorldY = current.WorldY - teachBase.TeachWorldY;
                deltaWorldAngle = Math.Abs(current.WorldAngleDeg - teachBase.TeachWorldAngleDeg) > 1e-6
                    ? (current.WorldAngleDeg - teachBase.TeachWorldAngleDeg)
                    : dAngle;
            }

            return new VisionOffset(
                DeltaWorldX: deltaWorldX,
                DeltaWorldY: deltaWorldY,
                DeltaAngleDeg: deltaWorldAngle,
                DeltaPixelX: dPx,
                DeltaPixelY: dPy
            );
        }
    }
}
