using System;

namespace Sophon.Vision.Coordinates
{
    /// <summary>
    /// 2D 齐次变换（3x3 矩阵；SE(2) 的仿射扩展，允许旋转、缩放、剪切与镜像）：
    /// <code>
    /// [ x' ]   [ r11  r12  tx ] [ x ]
    /// [ y' ] = [ r21  r22  ty ] [ y ]
    /// [ 1  ]   [  0    0    1 ] [ 1 ]
    /// </code>
    ///
    /// 语义：把「某点在 from 坐标系下的坐标 (x, y)」映射为「同一点在 to 坐标系下的坐标 (x', y')」，
    /// 记作 T_{to←from}，即 p_to = T * p_from。
    /// 线性部分 (r11..r22) 承载旋转/缩放/剪切/镜像，平移部分 (tx, ty) 承载原点偏移。
    ///
    /// 与 <see cref="Calibration.NinePointCalibration"/> 的 2x3 仿射矩阵逐元素对应：
    /// [ [a11, a12, dx], [a21, a22, dy] ] == (r11, r12, tx, r21, r22, ty)。
    /// </summary>
    public readonly record struct Transform2D
    {
        /// <summary>
        /// 用 6 个自由度构造齐次变换（第三行恒为 [0, 0, 1]）。
        /// </summary>
        public Transform2D(double r11, double r12, double tx, double r21, double r22, double ty)
        {
            R11 = r11;
            R12 = r12;
            Tx = tx;
            R21 = r21;
            R22 = r22;
            Ty = ty;
        }

        /// <summary>线性部分第 1 行第 1 列。</summary>
        public double R11 { get; }

        /// <summary>线性部分第 1 行第 2 列。</summary>
        public double R12 { get; }

        /// <summary>平移部分 X 分量。</summary>
        public double Tx { get; }

        /// <summary>线性部分第 2 行第 1 列。</summary>
        public double R21 { get; }

        /// <summary>线性部分第 2 行第 2 列。</summary>
        public double R22 { get; }

        /// <summary>平移部分 Y 分量。</summary>
        public double Ty { get; }

        /// <summary>单位变换（恒等映射）。</summary>
        public static Transform2D Identity { get; } = new Transform2D(1, 0, 0, 0, 1, 0);

        /// <summary>
        /// 构造纯平移变换。
        /// </summary>
        public static Transform2D FromTranslation(double tx, double ty) =>
            new Transform2D(1, 0, tx, 0, 1, ty);

        /// <summary>
        /// 构造绕原点的旋转变换（角度制，逆时针为正）。
        /// </summary>
        public static Transform2D FromRotationDeg(double degrees) =>
            FromRotationRad(degrees * Math.PI / 180.0);

        /// <summary>
        /// 构造绕原点的旋转变换（弧度制，逆时针为正）。
        /// </summary>
        public static Transform2D FromRotationRad(double radians)
        {
            double c = Math.Cos(radians);
            double s = Math.Sin(radians);
            return new Transform2D(c, -s, 0, s, c, 0);
        }

        /// <summary>
        /// 构造各轴缩放变换（sx 或 sy 为负时即形成镜像，手性反转）。
        /// </summary>
        public static Transform2D FromScale(double sx, double sy) =>
            new Transform2D(sx, 0, 0, 0, sy, 0);

        /// <summary>
        /// 从 2x3 仿射矩阵（[ [a11, a12, dx], [a21, a22, dy] ]，如九点标定结果）构造变换。
        /// </summary>
        public static Transform2D FromAffine2x3(double[][] matrix)
        {
            if (matrix == null || matrix.Length != 2 ||
                matrix[0] == null || matrix[1] == null ||
                matrix[0].Length != 3 || matrix[1].Length != 3)
            {
                throw new ArgumentException("仿射矩阵必须为 2x3 大小", nameof(matrix));
            }

            return new Transform2D(
                matrix[0][0], matrix[0][1], matrix[0][2],
                matrix[1][0], matrix[1][1], matrix[1][2]);
        }

        /// <summary>
        /// 变换组合：T_A_C = T_A_B * T_B_C。
        /// 即先应用 right（C -&gt; B），再应用 left（B -&gt; A）。
        /// </summary>
        public static Transform2D operator *(Transform2D left, Transform2D right)
        {
            // 3x3 齐次矩阵乘法（right 的第三列即其平移，third row 恒为 [0,0,1]）：
            // (L*R)[0][0] = l.r11*r.r11 + l.r12*r.r21
            // (L*R)[0][1] = l.r11*r.r12 + l.r12*r.r22
            // (L*R)[0][2] = l.r11*r.tx  + l.r12*r.ty + l.tx
            return new Transform2D(
                left.R11 * right.R11 + left.R12 * right.R21,
                left.R11 * right.R12 + left.R12 * right.R22,
                left.R11 * right.Tx + left.R12 * right.Ty + left.Tx,
                left.R21 * right.R11 + left.R22 * right.R21,
                left.R21 * right.R12 + left.R22 * right.R22,
                left.R21 * right.Tx + left.R22 * right.Ty + left.Ty);
        }

        /// <summary>
        /// 线性部分行列式。det = r11*r22 - r12*r21。
        /// </summary>
        public double Determinant => R11 * R22 - R12 * R21;

        /// <summary>
        /// 线性部分是否可逆（行列式绝对值大于数值容限）。
        /// </summary>
        public bool IsInvertible => Math.Abs(Determinant) > 1e-12;

        /// <summary>
        /// 手性（镜像）检测：det &lt; 0 表示坐标系发生了镜像翻转
        /// （例如图像 Y 轴向下、物理 Y 轴向上时常见的朝向反转）。
        /// </summary>
        public bool IsMirrored => Determinant < 0.0;

        /// <summary>
        /// 线性部分等效旋转角（度，逆时针为正，由 atan2(r21, r11) 提取）。
        /// </summary>
        public double RotationDeg => Math.Atan2(R21, R11) * 180.0 / Math.PI;

        /// <summary>
        /// 求逆变换：T_B_A = T_A_B.Inverse()。
        /// 若 y = A*x + b，则 x = A^-1 * (y - b)。
        /// </summary>
        public Transform2D Inverse()
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-12)
            {
                throw new InvalidOperationException("变换线性部分行列式接近0，无法求逆");
            }

            double invDet = 1.0 / det;
            double invR11 = R22 * invDet;
            double invR12 = -R12 * invDet;
            double invR21 = -R21 * invDet;
            double invR22 = R11 * invDet;

            double invTx = -(invR11 * Tx + invR12 * Ty);
            double invTy = -(invR21 * Tx + invR22 * Ty);

            return new Transform2D(invR11, invR12, invTx, invR21, invR22, invTy);
        }

        /// <summary>
        /// 点变换（含平移）：(x', y') = T * (x, y, 1)。
        /// </summary>
        public (double X, double Y) Transform(double x, double y)
        {
            return (R11 * x + R12 * y + Tx,
                    R21 * x + R22 * y + Ty);
        }

        /// <summary>
        /// 点变换（含平移），元组重载。
        /// </summary>
        public (double X, double Y) Transform((double X, double Y) point) =>
            Transform(point.X, point.Y);

        /// <summary>
        /// 矢量变换（忽略平移，仅线性部分）：(dx', dy') = A * (dx, dy)。
        /// 用于纯位移/增量（如像素偏差 -&gt; 物理轴偏移）的换算。
        /// </summary>
        public (double Dx, double Dy) TransformVector(double dx, double dy)
        {
            return (R11 * dx + R12 * dy,
                    R21 * dx + R22 * dy);
        }

        /// <summary>
        /// 导出为 2x3 仿射矩阵（[ [a11, a12, dx], [a21, a22, dy] ]），
        /// 用于与 <see cref="Calibration.NinePointCalibration"/> 等既有 2x3 接口互操作。
        /// </summary>
        public double[][] ToAffine2x3()
        {
            return new[]
            {
                new[] { R11, R12, Tx },
                new[] { R21, R22, Ty }
            };
        }

        /// <summary>
        /// 解构为 6 个矩阵元素 (r11, r12, tx, r21, r22, ty)。
        /// </summary>
        public void Deconstruct(out double r11, out double r12, out double tx,
                                out double r21, out double r22, out double ty)
        {
            r11 = R11;
            r12 = R12;
            tx = Tx;
            r21 = R21;
            r22 = R22;
            ty = Ty;
        }
    }
}
