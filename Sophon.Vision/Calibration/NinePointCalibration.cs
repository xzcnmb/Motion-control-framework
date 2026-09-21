using System;
using System.Collections.Generic;
using System.Linq;
using Sophon.Vision.Coordinates;

namespace Sophon.Vision.Calibration
{
    /// <summary>
    /// 标定点对（像素坐标点与对应物理世界坐标点）。
    /// </summary>
    /// <param name="PixelX">图像像素 X 坐标 (px)</param>
    /// <param name="PixelY">图像像素 Y 坐标 (px)</param>
    /// <param name="WorldX">物理世界 X 坐标 (mm)</param>
    /// <param name="WorldY">物理世界 Y 坐标 (mm)</param>
    public record CalibrationPoint(double PixelX, double PixelY, double WorldX, double WorldY);

    /// <summary>
    /// 标定残差指标。
    /// </summary>
    /// <param name="RmsErrorMm">物理坐标均方根残差 (RMS, mm)</param>
    /// <param name="MaxErrorMm">物理坐标最大单点残差 (mm)</param>
    /// <param name="RmsErrorPx">像素坐标反投影均方根残差 (RMS, px)</param>
    /// <param name="MaxErrorPx">像素坐标反投影最大单点残差 (px)</param>
    public record CalibrationResidual(
        double RmsErrorMm,
        double MaxErrorMm,
        double RmsErrorPx,
        double MaxErrorPx);

    /// <summary>
    /// 九点仿射标定（2D Affine Transformation：像素坐标 -> 物理世界坐标）。
    /// 
    /// 数学原理说明：
    /// 仿射变换模型包含平移、旋转、尺度缩放及剪切，方程为：
    /// [ X_w ]   [ a11  a12 ] [ X_p ]   [ dx ]
    /// [ Y_w ] = [ a21  a22 ] [ Y_p ] + [ dy ]
    /// 
    /// 对于 N (>=3，通常为 9) 个标定点，分别对 X 和 Y 物理坐标建立线性超定方程组：
    /// A * [a11, a12, dx]^T = X_w
    /// A * [a21, a22, dy]^T = Y_w
    /// 其中设计矩阵 A 的每一行为 [ X_p_i, Y_p_i, 1 ]。
    /// 采用正规方程最小二乘法 (A^T * A) * p = A^T * Y 求解参数，
    /// 具有极佳的抗高斯噪声与数值鲁棒性。
    /// </summary>
    public sealed class NinePointCalibration
    {
        /// <summary>
        /// 2x3 仿射变换矩阵（像素 -> 物理）。
        /// 格式：[ [a11, a12, dx], [a21, a22, dy] ]
        /// </summary>
        public double[][] Matrix { get; }

        /// <summary>
        /// 逆矩阵（物理 -> 像素）。
        /// </summary>
        public double[][] InverseMatrix { get; }

        /// <summary>
        /// 标定计算得到的残差统计指标。
        /// </summary>
        public CalibrationResidual Residual { get; }

        /// <summary>
        /// 允许的最大物理残差阈值 (mm)，用于合格判定。
        /// </summary>
        public double MaxResidualMmThreshold { get; }

        /// <summary>
        /// 允许的最大像素反投影残差阈值 (px)，用于合格判定。
        /// </summary>
        public double MaxResidualPxThreshold { get; }

        /// <summary>
        /// 标定精度是否在允许容差范围内。
        /// </summary>
        public bool IsAcceptable =>
            Residual.MaxErrorMm <= MaxResidualMmThreshold &&
            Residual.MaxErrorPx <= MaxResidualPxThreshold;

        /// <summary>
        /// 私有构造，由静态 Calibrate 方法构造。
        /// </summary>
        public NinePointCalibration(
            double[][] matrix,
            CalibrationResidual residual,
            double maxResidualMmThreshold = 0.05,
            double maxResidualPxThreshold = 1.0)
        {
            if (matrix == null || matrix.Length != 2 || matrix[0].Length != 3 || matrix[1].Length != 3)
            {
                throw new ArgumentException("仿射矩阵必须为 2x3 大小", nameof(matrix));
            }

            Matrix = matrix;
            Residual = residual;
            MaxResidualMmThreshold = maxResidualMmThreshold;
            MaxResidualPxThreshold = maxResidualPxThreshold;
            InverseMatrix = InvertAffine(matrix);
        }

        /// <summary>
        /// 使用 9 组（或至少 3 组）点对执行仿射标定计算。
        /// </summary>
        /// <param name="points">标定点集合（建议 9 点井字分布）</param>
        /// <param name="maxResidualMmThreshold">最大允许物理残差阈值（mm，默认 0.05）</param>
        /// <param name="maxResidualPxThreshold">最大允许像素残差阈值（px，默认 1.0）</param>
        public static NinePointCalibration Calibrate(
            IReadOnlyList<CalibrationPoint> points,
            double maxResidualMmThreshold = 0.05,
            double maxResidualPxThreshold = 1.0)
        {
            if (points == null || points.Count < 3)
            {
                throw new ArgumentException("标定点至少需要 3 个非共线点，通常采用 9 点", nameof(points));
            }

            int n = points.Count;

            // 求解最小二乘：
            // A * [a11, a12, dx]^T = [xw1, xw2, ... xwN]^T
            // A * [a21, a22, dy]^T = [yw1, yw2, ... ywN]^T
            // 设计矩阵 A 每行 = [px, py, 1]
            // A^T * A 为 3x3 矩阵：
            // [ sum(px^2)     sum(px*py)   sum(px) ]
            // [ sum(px*py)    sum(py^2)    sum(py) ]
            // [ sum(px)       sum(py)      n       ]

            double s_xx = 0, s_xy = 0, s_x = 0;
            double s_yy = 0, s_y = 0;
            double s_xw_x = 0, s_xw_y = 0, s_xw = 0;
            double s_yw_x = 0, s_yw_y = 0, s_yw = 0;

            for (int i = 0; i < n; i++)
            {
                double px = points[i].PixelX;
                double py = points[i].PixelY;
                double xw = points[i].WorldX;
                double yw = points[i].WorldY;

                s_xx += px * px;
                s_xy += px * py;
                s_x  += px;
                s_yy += py * py;
                s_y  += py;

                s_xw_x += xw * px;
                s_xw_y += xw * py;
                s_xw   += xw;

                s_yw_x += yw * px;
                s_yw_y += yw * py;
                s_yw   += yw;
            }

            double[,] ata = new double[3, 3]
            {
                { s_xx, s_xy, s_x },
                { s_xy, s_yy, s_y },
                { s_x,  s_y,  n   }
            };

            double[] bX = new[] { s_xw_x, s_xw_y, s_xw };
            double[] bY = new[] { s_yw_x, s_yw_y, s_yw };

            double[] row1 = SolveLinear3x3(ata, bX);
            double[] row2 = SolveLinear3x3(ata, bY);

            double[][] matrix = new double[][]
            {
                new double[] { row1[0], row1[1], row1[2] },
                new double[] { row2[0], row2[1], row2[2] }
            };

            // 计算正向与反向残差
            double[][] invMat = InvertAffine(matrix);

            double sumSqMm = 0;
            double maxErrMm = 0;
            double sumSqPx = 0;
            double maxErrPx = 0;

            for (int i = 0; i < n; i++)
            {
                var pt = points[i];

                // 正向变换预测
                var (estXw, estYw) = TransformInternal(matrix, pt.PixelX, pt.PixelY);
                double errMm = Math.Sqrt(Math.Pow(estXw - pt.WorldX, 2) + Math.Pow(estYw - pt.WorldY, 2));
                sumSqMm += errMm * errMm;
                if (errMm > maxErrMm) maxErrMm = errMm;

                // 反向投影预测
                var (estPx, estPy) = TransformInternal(invMat, pt.WorldX, pt.WorldY);
                double errPx = Math.Sqrt(Math.Pow(estPx - pt.PixelX, 2) + Math.Pow(estPy - pt.PixelY, 2));
                sumSqPx += errPx * errPx;
                if (errPx > maxErrPx) maxErrPx = errPx;
            }

            double rmsMm = Math.Sqrt(sumSqMm / n);
            double rmsPx = Math.Sqrt(sumSqPx / n);

            var residual = new CalibrationResidual(rmsMm, maxErrMm, rmsPx, maxErrPx);

            return new NinePointCalibration(matrix, residual, maxResidualMmThreshold, maxResidualPxThreshold);
        }

        /// <summary>
        /// 将单个像素坐标转换为物理世界坐标。
        /// </summary>
        public (double WorldX, double WorldY) Transform(double pixelX, double pixelY)
        {
            return TransformInternal(Matrix, pixelX, pixelY);
        }

        /// <summary>
        /// 批量将像素坐标点转换为物理世界坐标。
        /// </summary>
        public IReadOnlyList<(double WorldX, double WorldY)> TransformBatch(IEnumerable<(double X, double Y)> pixelPoints)
        {
            return pixelPoints.Select(p => Transform(p.X, p.Y)).ToList();
        }

        /// <summary>
        /// 逆变换：将物理世界坐标反投影回像素坐标。
        /// </summary>
        public (double PixelX, double PixelY) InvertTransform(double worldX, double worldY)
        {
            return TransformInternal(InverseMatrix, worldX, worldY);
        }

        /// <summary>
        /// 将标定结果转换为坐标系树使用的 <see cref="Transform2D"/>（T_{World←Pixel}：像素坐标 -> 物理世界坐标）。
        /// 便于把九点标定接入 <see cref="CoordinateFrameTree"/> 做统一的坐标系管理。
        /// </summary>
        public Transform2D ToTransform2D()
        {
            return Transform2D.FromAffine2x3(Matrix);
        }

        private static (double X, double Y) TransformInternal(double[][] m, double inX, double inY)
        {
            double outX = m[0][0] * inX + m[0][1] * inY + m[0][2];
            double outY = m[1][0] * inX + m[1][1] * inY + m[1][2];
            return (outX, outY);
        }

        /// <summary>
        /// 计算 2x3 仿射变换的逆矩阵。
        /// 若原变换为 y = A*x + b，则逆变换为 x = A^-1 * (y - b) = A^-1 * y - A^-1 * b。
        /// </summary>
        private static double[][] InvertAffine(double[][] m)
        {
            double a11 = m[0][0], a12 = m[0][1], b1 = m[0][2];
            double a21 = m[1][0], a22 = m[1][1], b2 = m[1][2];

            double det = a11 * a22 - a12 * a21;
            if (Math.Abs(det) < 1e-12)
            {
                throw exciting("仿射矩阵行列式接近0，无法求逆", nameof(m));
            }

            double invDet = 1.0 / det;
            double invA11 =  a22 * invDet;
            double invA12 = -a12 * invDet;
            double invA21 = -a21 * invDet;
            double invA22 =  a11 * invDet;

            double invB1 = -(invA11 * b1 + invA12 * b2);
            double invB2 = -(invA21 * b1 + invA22 * b2);

            return new double[][]
            {
                new double[] { invA11, invA12, invB1 },
                new double[] { invA21, invA22, invB2 }
            };

            static Exception exciting(string msg, string p) => new InvalidOperationException(msg);
        }

        /// <summary>
        /// 解 3x3 线性方程组 (Cramer 法则)。
        /// </summary>
        private static double[] SolveLinear3x3(double[,] a, double[] b)
        {
            double detA = Det3x3(a[0,0], a[0,1], a[0,2],
                                 a[1,0], a[1,1], a[1,2],
                                 a[2,0], a[2,1], a[2,2]);

            if (Math.Abs(detA) < 1e-12)
            {
                throw new InvalidOperationException("标定点退化或共线，无法计算仿射矩阵");
            }

            double detX = Det3x3(b[0],   a[0,1], a[0,2],
                                 b[1],   a[1,1], a[1,2],
                                 b[2],   a[2,1], a[2,2]);

            double detY = Det3x3(a[0,0], b[0],   a[0,2],
                                 a[1,0], b[1],   a[1,2],
                                 a[2,0], b[2],   a[2,2]);

            double detZ = Det3x3(a[0,0], a[0,1], b[0],
                                 a[1,0], a[1,1], b[1],
                                 a[2,0], a[2,1], b[2]);

            return new[] { detX / detA, detY / detA, detZ / detA };
        }

        private static double Det3x3(
            double m00, double m01, double m02,
            double m10, double m11, double m12,
            double m20, double m21, double m22)
        {
            return m00 * (m11 * m22 - m12 * m21) -
                   m01 * (m10 * m22 - m12 * m20) +
                   m02 * (m10 * m21 - m11 * m20);
        }
    }
}
