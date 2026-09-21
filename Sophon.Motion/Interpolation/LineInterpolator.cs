using Sophon.Motion.Profiles;

namespace Sophon.Motion.Interpolation;

/// <summary>
/// N 轴直线插补器。
/// 基于时间参数化标量位移 s(t)，各轴严格按单位方向向量线性映射。
/// </summary>
public sealed class LineInterpolator : IMotionPath
{
    private readonly double[] _start;
    private readonly double[] _end;
    private readonly double[] _directionVector;
    private readonly double _totalLength;
    private readonly TrapezoidalProfile _profile;

    public int AxisCount => _start.Length;
    public double Duration => _profile.Duration;
    public double TotalLength => _totalLength;
    public TrapezoidalProfile Profile => _profile;

    /// <summary>
    /// 构造 N 轴直线插补器。
    /// </summary>
    /// <param name="start">起点各轴坐标</param>
    /// <param name="end">终点各轴坐标</param>
    /// <param name="vmax">最大路径速度</param>
    /// <param name="amax">最大加速度</param>
    /// <param name="decel">最大减速度</param>
    public LineInterpolator(double[] start, double[] end, double vmax, double amax, double decel)
        : this(start, end, 0.0, 0.0, vmax, amax, decel)
    {
    }

    /// <summary>
    /// 构造 N 轴直线插补器（带初末速度）。
    /// </summary>
    public LineInterpolator(double[] start, double[] end, double v0, double vt, double vmax, double amax, double decel)
    {
        if (start == null || end == null) throw new ArgumentNullException(start == null ? nameof(start) : nameof(end));
        if (start.Length != end.Length) throw new ArgumentException("Start and End axis count mismatch.");
        if (start.Length == 0) throw new ArgumentException("Axis count must be greater than 0.");

        _start = (double[])start.Clone();
        _end = (double[])end.Clone();

        double sumSq = 0.0;
        for (int i = 0; i < start.Length; i++)
        {
            double diff = _end[i] - _start[i];
            sumSq += diff * diff;
        }

        _totalLength = Math.Sqrt(sumSq);
        _directionVector = new double[start.Length];

        if (_totalLength > 1e-12)
        {
            for (int i = 0; i < start.Length; i++)
            {
                _directionVector[i] = (_end[i] - _start[i]) / _totalLength;
            }
        }

        _profile = new TrapezoidalProfile(v0, vt, vmax, amax, decel, _totalLength);
    }

    /// <summary>
    /// 采样时刻 t 处的位置与速度。
    /// </summary>
    public PathPoint SampleAt(double t)
    {
        double s = _profile.PositionAt(t);
        double v = _profile.VelocityAt(t);

        var pos = new double[AxisCount];
        var vel = new double[AxisCount];

        for (int i = 0; i < AxisCount; i++)
        {
            pos[i] = _start[i] + _directionVector[i] * s;
            vel[i] = _directionVector[i] * v;
        }

        return new PathPoint(t, pos, vel, v);
    }
}
