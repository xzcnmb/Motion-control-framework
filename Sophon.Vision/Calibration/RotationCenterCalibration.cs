using System;
using System.Collections.Generic;

namespace Sophon.Vision.Calibration
{
    /// <summary>
    /// 旋转采样点（旋转机构角度及对应的特征点物理坐标）。
    /// </summary>
    /// <param name="AngleDeg">旋转轴绝对角度或相对旋转角度 (deg)</param>
    /// <param name="WorldX">旋转后特征点对应的物理 X 坐标 (mm)</param>
    /// <param name="WorldY">旋转后特征点对应的物理 Y 坐标 (mm)</param>
    public record RotationSamplePoint(double AngleDeg, double WorldX, double WorldY);

    /// <summary>
    /// 旋转中心标定结果。
    /// </summary>
    /// <param name="CenterX">旋转中心物理 X 坐标 (mm)</param>
    /// <param name="CenterY">旋转中心物理 Y 坐标 (mm)</param>
    /// <param name="RadiusMm">特征点到旋转中心的旋转半径 (mm)</param>
    /// <param name="RmsResidualMm">多点拟合残差 RMS (mm)，2点时残差为0</param>
    public record RotationCenterResult(double CenterX, double CenterY, double RadiusMm, double RmsResidualMm = 0.0);

    /// <summary>
    /// 旋转中心标定（两圆法 / 任意多角度最小二乘圆心标定）。
    /// 
    /// 数学推导说明（两圆法）：
    /// 设机构物理旋转中心为 C(cx, cy)。特征点在角度 theta1 下的物理坐标为 P1(x1, y1)，
    /// 在角度 theta2 下旋转到 P2(x2, y2)。相对旋转角 deltaTheta = theta2 - theta1。
    /// 则 P2 是 P1 绕中心 C 旋转 deltaTheta 得到：
    /// [ x2 - cx ]   [ cos(Δθ)  -sin(Δθ) ] [ x1 - cx ]
    /// [ y2 - cy ] = [ sin(Δθ)   cos(Δθ) ] [ y1 - cy ]
    /// 
    /// 展开为线性方程组形式：
    /// (1 - cos(Δθ)) * cx + sin(Δθ) * cy = x1 - x2*cos(Δθ) + y2*sin(Δθ) ... 经化简：
    /// 令 R(Δθ) 为旋转矩阵，则 (I - R) * C = P1 - R*P1... 即：
    /// P2 - C = R(Δθ) * (P1 - C)  =>  (R - I) * C = R * P1 - P2
    /// 当 Δθ 不是 360° 的整数倍（如 Δθ 在 10°~180° 之间）时，(R - I) 是满秩且非奇异的：
    /// det(R - I) = (cos(Δθ) - 1)^2 + sin(Δθ)^2 = 2 * (1 - cos(Δθ)) > 0。
    /// 可以精确解析出唯一旋转中心坐标 C(cx, cy)。
    /// 
    /// 对于 3 个及以上采样角度，系统转化为超定圆拟合问题：
    /// (x_i - cx)^2 + (y_i - cy)^2 = r^2，展开后利用最小二乘法求解最佳圆心 (cx, cy) 与半径 r。
    /// </summary>
    public static class RotationCenterCalibration
    {
        /// <summary>
        /// 基于两点（两圆法 / 旋转角法）计算旋转中心。
        /// </summary>
        /// <param name="p1">位置 1 对应的角度和物理坐标</param>
        /// <param name="p2">位置 2 对应的角度和物理坐标</param>
        public static RotationCenterResult CalibrateTwoPoints(RotationSamplePoint p1, RotationSamplePoint p2)
        {
            if (p1 == null || p2 == null)
            {
                throw new ArgumentNullException("采样点不能为空");
            }

            double deltaThetaDeg = p2.AngleDeg - p1.AngleDeg;
            double rad = deltaThetaDeg * Math.PI / 180.0;

            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            // 方程组形式：
            // (cos - 1) * cx - sin * cy = cos * x1 - sin * y1 - x2
            // sin * cx + (cos - 1) * cy = sin * x1 + cos * y1 - y2
            double m00 = cos - 1.0;
            double m01 = -sin;
            double m10 = sin;
            double m11 = cos - 1.0;

            double b0 = cos * p1.WorldX - sin * p1.WorldY - p2.WorldX;
            double b1 = sin * p1.WorldX + cos * p1.WorldY - p2.WorldY;

            double det = m00 * m11 - m01 * m10; // 2 * (1 - cos)
            if (Math.Abs(det) < 1e-9)
            {
                throw new InvalidOperationException("两点旋转角度差过小或接近360度整倍数，无法唯一确定旋转中心");
            }

            double cx = (b0 * m11 - b1 * m01) / det;
            double cy = (m00 * b1 - m10 * b0) / det;

            double r1 = Math.Sqrt(Math.Pow(p1.WorldX - cx, 2) + Math.Pow(p1.WorldY - cy, 2));
            double r2 = Math.Sqrt(Math.Pow(p2.WorldX - cx, 2) + Math.Pow(p2.WorldY - cy, 2));
            double avgR = (r1 + r2) / 2.0;

            return new RotationCenterResult(cx, cy, avgR, Math.Abs(r1 - r2));
        }

        /// <summary>
        /// 任意数量（>=2）采样点的旋转中心标定。
        /// 2点走两圆法精确解；>=3点若包含已知旋转角度则采用多点已知角旋转几何最小二乘求解，
        /// 若无角度信息（角度恒定）则回退至几何圆心拟合。
        /// </summary>
        public static RotationCenterResult Calibrate(IReadOnlyList<RotationSamplePoint> samples)
        {
            if (samples == null || samples.Count < 2)
            {
                throw new ArgumentException("至少需要2个旋转采样点", nameof(samples));
            }

            if (samples.Count == 2)
            {
                return CalibrateTwoPoints(samples[0], samples[1]);
            }

            // 检查是否有已知旋转角度信息（任意两点角度差超过阈值）
            bool hasDistinctAngles = false;
            for (int i = 1; i < samples.Count; i++)
            {
                if (Math.Abs(samples[i].AngleDeg - samples[0].AngleDeg) > 1e-4)
                {
                    hasDistinctAngles = true;
                    break;
                }
            }

            if (hasDistinctAngles)
            {
                // 基于已知旋转角度的最小二乘旋转中心求解 (rotate feature by known angles, solve fixed center)
                // 对任意点对 (i, j)，P_j - C = R(Δθ) * (P_i - C) => (R(Δθ) - I) * C = R(Δθ) * P_i - P_j
                // 构建全点对超定正规方程，利用对角满秩结构直接精确求解
                double rhsX = 0.0;
                double rhsY = 0.0;
                double weightSum = 0.0;

                int count = samples.Count;
                for (int i = 0; i < count; i++)
                {
                    for (int j = i + 1; j < count; j++)
                    {
                        double deltaThetaDeg = samples[j].AngleDeg - samples[i].AngleDeg;
                        double rad = deltaThetaDeg * Math.PI / 180.0;
                        double cos = Math.Cos(rad);
                        double sin = Math.Sin(rad);

                        double weight = 2.0 * (1.0 - cos);
                        if (weight < 1e-9) continue; // 角度差接近 0 或 360 度整倍数跳过

                        double b0 = cos * samples[i].WorldX - sin * samples[i].WorldY - samples[j].WorldX;
                        double b1 = sin * samples[i].WorldX + cos * samples[i].WorldY - samples[j].WorldY;

                        // (R - I)^T * b:
                        // Row 0: (cos - 1) * b0 + sin * b1
                        // Row 1: -sin * b0 + (cos - 1) * b1
                        rhsX += (cos - 1.0) * b0 + sin * b1;
                        rhsY += -sin * b0 + (cos - 1.0) * b1;
                        weightSum += weight;
                    }
                }

                if (weightSum > 1e-9)
                {
                    double cxKnown = rhsX / weightSum;
                    double cyKnown = rhsY / weightSum;

                    double sumR = 0.0;
                    for (int i = 0; i < count; i++)
                    {
                        sumR += Math.Sqrt(Math.Pow(samples[i].WorldX - cxKnown, 2) + Math.Pow(samples[i].WorldY - cyKnown, 2));
                    }
                    double radiusKnown = sumR / count;

                    double sumResidSqKnown = 0.0;
                    for (int i = 0; i < count; i++)
                    {
                        double dist = Math.Sqrt(Math.Pow(samples[i].WorldX - cxKnown, 2) + Math.Pow(samples[i].WorldY - cyKnown, 2));
                        sumResidSqKnown += Math.Pow(dist - radiusKnown, 2);
                    }
                    double rmsKnown = Math.Sqrt(sumResidSqKnown / count);

                    return new RotationCenterResult(cxKnown, cyKnown, radiusKnown, rmsKnown);
                }
            }

            // 无已知角度信息时回退至最小二乘拟合圆：
            // (x - cx)^2 + (y - cy)^2 = r^2
            // x^2 - 2*x*cx + cx^2 + y^2 - 2*y*cy + cy^2 = r^2
            // 2*x*cx + 2*y*cy + (r^2 - cx^2 - cy^2) = x^2 + y^2
            // 令 A = 2*cx, B = 2*cy, C = r^2 - cx^2 - cy^2
            // 线性方程：A * x_i + B * y_i + C = x_i^2 + y_i^2
            int n = samples.Count;
            double s_x = 0, s_y = 0, s_xx = 0, s_yy = 0, s_xy = 0;
            double s_xxx_xyy = 0, s_xxy_yyy = 0;

            for (int i = 0; i < n; i++)
            {
                double x = samples[i].WorldX;
                double y = samples[i].WorldY;
                double sq = x * x + y * y;

                s_x += x;
                s_y += y;
                s_xx += x * x;
                s_yy += y * y;
                s_xy += x * y;

                s_xxx_xyy += x * sq;
                s_xxy_yyy += y * sq;
            }

            // 正规方程组：
            // [ s_xx  s_xy  s_x ] [ A ]   [ s_xxx_xyy ]
            // [ s_xy  s_yy  s_y ] [ B ] = [ s_xxy_yyy ]
            // [ s_x   s_y   n   ] [ C ]   [ s_xx + s_yy ]
            double[,] mat = new double[3, 3]
            {
                { s_xx, s_xy, s_x },
                { s_xy, s_yy, s_y },
                { s_x,  s_y,  n   }
            };
            double[] rhs = new double[] { s_xxx_xyy, s_xxy_yyy, s_xx + s_yy };

            double det = Det3x3(mat);
            if (Math.Abs(det) < 1e-9)
            {
                throw new InvalidOperationException("采样点共线或退化，无法拟合旋转中心圆");
            }

            double aVal = Det3x3(new double[,] { { rhs[0], mat[0, 1], mat[0, 2] }, { rhs[1], mat[1, 1], mat[1, 2] }, { rhs[2], mat[2, 1], mat[2, 2] } }) / det;
            double bVal = Det3x3(new double[,] { { mat[0, 0], rhs[0], mat[0, 2] }, { mat[1, 0], rhs[1], mat[1, 2] }, { mat[2, 0], rhs[2], mat[2, 2] } }) / det;
            double cVal = Det3x3(new double[,] { { mat[0, 0], mat[0, 1], rhs[0] }, { mat[1, 0], mat[1, 1], rhs[1] }, { mat[2, 0], mat[2, 1], rhs[2] } }) / det;

            double cx = aVal / 2.0;
            double cy = bVal / 2.0;
            double rSq = cVal + cx * cx + cy * cy;
            double radius = rSq > 0 ? Math.Sqrt(rSq) : 0.0;

            // 残差 RMS
            double sumResidSq = 0;
            for (int i = 0; i < n; i++)
            {
                double dist = Math.Sqrt(Math.Pow(samples[i].WorldX - cx, 2) + Math.Pow(samples[i].WorldY - cy, 2));
                sumResidSq += Math.Pow(dist - radius, 2);
            }
            double rms = Math.Sqrt(sumResidSq / n);

            return new RotationCenterResult(cx, cy, radius, rms);
        }

        private static double Det3x3(double[,] m)
        {
            return m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                   m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                   m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        }
    }
}
