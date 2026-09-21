using Sophon.Motion.Profiles;

namespace Sophon.Motion.Interpolation;

/// <summary>
/// 圆弧所在主平面。
/// </summary>
public enum ArcPlane
{
    /// <summary>XY 平面（Z 轴保持不变）</summary>
    XY,
    /// <summary>XZ 平面（Y 轴保持不变）</summary>
    XZ,
    /// <summary>YZ 平面（X 轴保持不变）</summary>
    YZ
}

/// <summary>
/// 平面圆弧插补器。
/// 支持 XY/XZ/YZ 平面、三点圆构造及圆心+半径构造。
/// 支持以弦高误差（Chord Error）评估步长，保证轮廓精度。
/// </summary>
public sealed class ArcInterpolator : IMotionPath
{
    private readonly ArcPlane _plane;
    private readonly double[] _center; // 3D: [x, y, z]
    private readonly double _radius;
    private readonly double _startAngle; // 极角 (rad)
    private readonly double _sweepAngle; // 扫掠角 (rad, 逆时针为正，顺时针为负)
    private readonly double _arcLength;
    private readonly TrapezoidalProfile _profile;
    private readonly double[] _startPoint;
    private readonly double[] _endPoint;

    public int AxisCount => 3;
    public double Duration => _profile.Duration;
    public double Radius => _radius;
    public double ArcLength => _arcLength;
    public ArcPlane Plane => _plane;
    public double[] Center => (double[])_center.Clone();
    public TrapezoidalProfile Profile => _profile;

    /// <summary>
    /// 圆心 + 半径 + 起始/终止角度构造平面圆弧。
    /// </summary>
    /// <param name="plane">主平面</param>
    /// <param name="center">圆心三维坐标 [x, y, z]</param>
    /// <param name="radius">圆弧半径</param>
    /// <param name="startAngle">起始角度 (rad)</param>
    /// <param name="sweepAngle">扫角 (rad，正为逆时针，负为顺时针)</param>
    /// <param name="vmax">最大速度</param>
    /// <param name="amax">最大加速度</param>
    /// <param name="decel">最大减速度</param>
    public ArcInterpolator(
        ArcPlane plane,
        double[] center,
        double radius,
        double startAngle,
        double sweepAngle,
        double vmax,
        double amax,
        double decel)
    {
        if (center.Length < 3) throw new ArgumentException("Center point must have at least 3 coordinates.");
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive.");

        _plane = plane;
        _center = new double[3] { center[0], center[1], center[2] };
        _radius = radius;
        _startAngle = startAngle;
        _sweepAngle = sweepAngle;
        _arcLength = Math.Abs(sweepAngle) * radius;

        _profile = new TrapezoidalProfile(0.0, 0.0, vmax, amax, decel, _arcLength);

        _startPoint = EvaluatePositionOnArc(0.0);
        _endPoint = EvaluatePositionOnArc(_arcLength);
    }

    /// <summary>
    /// 三点圆弧构造：起点 p1、圆弧上中间点 p2、终点 p3。
    /// 自动解算外接圆心、半径及主平面。
    /// </summary>
    public static ArcInterpolator FromThreePoints(
        double[] p1,
        double[] p2,
        double[] p3,
        double vmax,
        double amax,
        double decel,
        ArcPlane? forcedPlane = null)
    {
        if (p1.Length < 3 || p2.Length < 3 || p3.Length < 3)
            throw new ArgumentException("Points must have at least 3 coordinates.");

        // 判断平面：若未指定，看哪个坐标几乎不变
        ArcPlane plane;
        if (forcedPlane.HasValue)
        {
            plane = forcedPlane.Value;
        }
        else
        {
            double dz = Math.Abs(p1[2] - p2[2]) + Math.Abs(p2[2] - p3[2]);
            double dy = Math.Abs(p1[1] - p2[1]) + Math.Abs(p2[1] - p3[1]);
            double dx = Math.Abs(p1[0] - p2[0]) + Math.Abs(p2[0] - p3[0]);

            if (dz <= 1e-6) plane = ArcPlane.XY;
            else if (dy <= 1e-6) plane = ArcPlane.XZ;
            else if (dx <= 1e-6) plane = ArcPlane.YZ;
            else plane = ArcPlane.XY; // 默认 XY
        }

        // 提取 2D 平面坐标 (u, v)
        int idxU = plane switch { ArcPlane.XY => 0, ArcPlane.XZ => 0, ArcPlane.YZ => 1, _ => 0 };
        int idxV = plane switch { ArcPlane.XY => 1, ArcPlane.XZ => 2, ArcPlane.YZ => 2, _ => 1 };
        int idxConst = plane switch { ArcPlane.XY => 2, ArcPlane.XZ => 1, ArcPlane.YZ => 0, _ => 2 };

        double u1 = p1[idxU], v1 = p1[idxV];
        double u2 = p2[idxU], v2 = p2[idxV];
        double u3 = p3[idxU], v3 = p3[idxV];

        // 三角形三顶点求外接圆圆心
        // 2*(u2-u1)*Uc + 2*(v2-v1)*Vc = (u2^2+v2^2) - (u1^2+v1^2)
        // 2*(u3-u2)*Uc + 2*(v3-v2)*Vc = (u3^2+v3^2) - (u2^2+v2^2)
        double a1 = 2 * (u2 - u1);
        double b1 = 2 * (v2 - v1);
        double c1 = (u2 * u2 + v2 * v2) - (u1 * u1 + v1 * v1);

        double a2 = 2 * (u3 - u2);
        double b2 = 2 * (v3 - v2);
        double c2 = (u3 * u3 + v3 * v3) - (u2 * u2 + v2 * v2);

        double det = a1 * b2 - a2 * b1;
        if (Math.Abs(det) < 1e-12)
        {
            throw new InvalidOperationException("The three points are collinear; cannot construct an arc.");
        }

        double uc = (c1 * b2 - c2 * b1) / det;
        double vc = (a1 * c2 - a2 * c1) / det;

        double radius = Math.Sqrt((u1 - uc) * (u1 - uc) + (v1 - vc) * (v1 - vc));

        double[] center = new double[3];
        center[idxU] = uc;
        center[idxV] = vc;
        center[idxConst] = (p1[idxConst] + p2[idxConst] + p3[idxConst]) / 3.0;

        double theta1 = Math.Atan2(v1 - vc, u1 - uc);
        double theta2 = Math.Atan2(v2 - vc, u2 - uc);
        double theta3 = Math.Atan2(v3 - vc, u3 - uc);

        // 确定顺逆时针扫角
        // 设经过 p1 -> p2 -> p3
        double dTheta12 = NormalizeAngle(theta2 - theta1);
        double dTheta23 = NormalizeAngle(theta3 - theta2);

        // 如果从 1 到 2 逆时针，1 到 3 的方向也必须与 2 一致
        double sweep;
        if (dTheta12 > 0)
        {
            // 逆时针
            double total = NormalizeAngle(theta3 - theta1);
            sweep = total > 0 ? total : total + 2 * Math.PI;
        }
        else
        {
            // 顺时针
            double total = NormalizeAngle(theta3 - theta1);
            sweep = total < 0 ? total : total - 2 * Math.PI;
        }

        return new ArcInterpolator(plane, center, radius, theta1, sweep, vmax, amax, decel);
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= 2 * Math.PI;
        while (angle <= -Math.PI) angle += 2 * Math.PI;
        return angle;
    }

    /// <summary>
    /// 根据最大允许弦高误差 (Chord Error) 计算推荐的最大时间步长 (dt)。
    /// 弦高公式：h = R * (1 - cos(delta_theta / 2)) ≈ R * (delta_theta)^2 / 8
    /// delta_theta = (v * dt) / R
    /// => h ≈ (v * dt)^2 / (8 * R)  => dt ≤ sqrt(8 * R * h_max) / v
    /// </summary>
    /// <param name="maxChordError">最大允许弦高误差 (物理单位)</param>
    /// <param name="feedrate">运行线速度</param>
    /// <returns>推荐最大步长时间 dt (s)</returns>
    public double CalculateMaxStepTime(double maxChordError, double feedrate)
    {
        if (maxChordError <= 0) throw new ArgumentOutOfRangeException(nameof(maxChordError));
        if (feedrate <= 0) feedrate = _profile.MaxVelocity;

        double dt = Math.Sqrt(8.0 * _radius * maxChordError) / feedrate;
        return dt;
    }

    private double[] EvaluatePositionOnArc(double s)
    {
        double ratio = _arcLength > 1e-12 ? (s / _arcLength) : 0.0;
        double currentAngle = _startAngle + _sweepAngle * ratio;

        int idxU = _plane switch { ArcPlane.XY => 0, ArcPlane.XZ => 0, ArcPlane.YZ => 1, _ => 0 };
        int idxV = _plane switch { ArcPlane.XY => 1, ArcPlane.XZ => 2, ArcPlane.YZ => 2, _ => 1 };
        int idxConst = _plane switch { ArcPlane.XY => 2, ArcPlane.XZ => 1, ArcPlane.YZ => 0, _ => 2 };

        var pos = new double[3];
        pos[idxU] = _center[idxU] + _radius * Math.Cos(currentAngle);
        pos[idxV] = _center[idxV] + _radius * Math.Sin(currentAngle);
        pos[idxConst] = _center[idxConst];
        return pos;
    }

    /// <summary>
    /// 在时刻 t 处采样圆弧路径点。
    /// </summary>
    public PathPoint SampleAt(double t)
    {
        double s = _profile.PositionAt(t);
        double v = _profile.VelocityAt(t);

        double ratio = _arcLength > 1e-12 ? (s / _arcLength) : 0.0;
        double currentAngle = _startAngle + _sweepAngle * ratio;

        int idxU = _plane switch { ArcPlane.XY => 0, ArcPlane.XZ => 0, ArcPlane.YZ => 1, _ => 0 };
        int idxV = _plane switch { ArcPlane.XY => 1, ArcPlane.XZ => 2, ArcPlane.YZ => 2, _ => 1 };
        int idxConst = _plane switch { ArcPlane.XY => 2, ArcPlane.XZ => 1, ArcPlane.YZ => 0, _ => 2 };

        var pos = new double[3];
        pos[idxU] = _center[idxU] + _radius * Math.Cos(currentAngle);
        pos[idxV] = _center[idxV] + _radius * Math.Sin(currentAngle);
        pos[idxConst] = _center[idxConst];

        // 速度切向向量：[-sin(theta), cos(theta)] * sign(sweepAngle)
        double dir = _sweepAngle >= 0 ? 1.0 : -1.0;
        var vel = new double[3];
        vel[idxU] = -Math.Sin(currentAngle) * v * dir;
        vel[idxV] = Math.Cos(currentAngle) * v * dir;
        vel[idxConst] = 0.0;

        return new PathPoint(t, pos, vel, v);
    }
}
