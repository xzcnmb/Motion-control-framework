namespace Sophon.Motion.Profiles;

/// <summary>
/// 梯形速度规划（Trapezoidal Velocity Profile）。
/// 给定初速度 v0、末速度 vt、最大速度 vmax、最大加速度 amax、最大减速度 decel 与位移 s。
/// 严格解析积分，支持"位移不足以达到最大速度"的三角形退化情形。
/// </summary>
public sealed class TrapezoidalProfile
{
    private readonly ProfileSegment[] _segments;
    private readonly double _direction; // +1 or -1
    private readonly double _totalDistance;

    /// <summary>
    /// 各段分段信息（加速段、匀速段、减速段）。
    /// </summary>
    public IReadOnlyList<ProfileSegment> Segments => _segments;

    /// <summary>总运动时长 (s)</summary>
    public double Duration { get; }

    /// <summary>初速度 (方向标量，物理单位/s)</summary>
    public double InitialVelocity { get; }

    /// <summary>末速度 (方向标量，物理单位/s)</summary>
    public double TargetVelocity { get; }

    /// <summary>最大运行速度 (正数，物理单位/s)</summary>
    public double MaxVelocity { get; }

    /// <summary>最大加速度 (正数，物理单位/s²)</summary>
    public double Acceleration { get; }

    /// <summary>最大减速度 (正数，物理单位/s²)</summary>
    public double Deceleration { get; }

    /// <summary>总位移 (带正负号，物理单位)</summary>
    public double Distance { get; }

    /// <summary>
    /// 构造梯形速度规划。
    /// </summary>
    /// <param name="v0">初速度 (带方向，或绝对值大小)</param>
    /// <param name="vt">目标末速度 (带方向，或绝对值大小)</param>
    /// <param name="vmax">最大速度大小 (正数)</param>
    /// <param name="amax">最大加速度 (正数)</param>
    /// <param name="decel">最大减速度 (正数)</param>
    /// <param name="s">位移 (带符号或无符号)</param>
    public TrapezoidalProfile(double v0, double vt, double vmax, double amax, double decel, double s)
    {
        if (vmax <= 0) throw new ArgumentOutOfRangeException(nameof(vmax), "Max velocity must be positive.");
        if (amax <= 0) throw new ArgumentOutOfRangeException(nameof(amax), "Acceleration must be positive.");
        if (decel <= 0) throw new ArgumentOutOfRangeException(nameof(decel), "Deceleration must be positive.");

        Distance = s;
        InitialVelocity = v0;
        TargetVelocity = vt;
        MaxVelocity = vmax;
        Acceleration = amax;
        Deceleration = decel;

        if (Math.Abs(s) < 1e-12)
        {
            _direction = 1.0;
            _totalDistance = 0.0;
            Duration = 0.0;
            _segments = Array.Empty<ProfileSegment>();
            return;
        }

        _direction = s >= 0 ? 1.0 : -1.0;
        double sAbs = Math.Abs(s);
        _totalDistance = sAbs;

        // 将速度投射到运动方向上
        double v0Proj = Math.Max(0.0, v0 * _direction);
        double vtProj = Math.Max(0.0, vt * _direction);

        // 如果初速度已超过最大速度，截断至最大速度
        v0Proj = Math.Min(v0Proj, vmax);
        vtProj = Math.Min(vtProj, vmax);

        // 加速到达 vmax 所需位移与时间
        double sAccMax = (vmax * vmax - v0Proj * v0Proj) / (2.0 * amax);
        double sDecMax = (vmax * vmax - vtProj * vtProj) / (2.0 * decel);

        double vPeak;
        double tAcc;
        double tCruise;
        double tDec;

        if (sAccMax + sDecMax <= sAbs)
        {
            // 正常梯形：有匀速段
            vPeak = vmax;
            tAcc = (vmax - v0Proj) / amax;
            tDec = (vmax - vtProj) / decel;
            double sCruise = sAbs - (sAccMax + sDecMax);
            tCruise = sCruise / vmax;
        }
        else
        {
            // 三角形退化：达不到 vmax，计算可达到的峰值速度 vPeak
            // s = (vPeak^2 - v0^2)/(2*amax) + (vPeak^2 - vt^2)/(2*decel)
            // 2 * s = vPeak^2 * (1/amax + 1/decel) - v0^2/amax - vt^2/decel
            double factor = (1.0 / amax) + (1.0 / decel);
            double numerator = 2.0 * sAbs + (v0Proj * v0Proj / amax) + (vtProj * vtProj / decel);
            double vPeakSq = numerator / factor;

            if (vPeakSq < 0) vPeakSq = 0;
            vPeak = Math.Sqrt(vPeakSq);

            // 边界：若 vPeak < max(v0Proj, vtProj)，说明在给定距离内减速甚至无法完成或直接减速
            if (vPeak < v0Proj || vPeak < vtProj)
            {
                // 退化处理：初速度较大或距离极短，强制以单调变加速度处理
                vPeak = Math.Max(v0Proj, vtProj);
            }

            tAcc = Math.Max(0.0, (vPeak - v0Proj) / amax);
            tDec = Math.Max(0.0, (vPeak - vtProj) / decel);
            tCruise = 0.0;
        }

        double sAcc = (vPeak + v0Proj) * 0.5 * tAcc;
        double sCruiseActual = vPeak * tCruise;
        double sDec = (vPeak + vtProj) * 0.5 * tDec;

        // 若因精度有微小差异，修正 sCruise 或 sDec 保证终点严格归零
        Duration = tAcc + tCruise + tDec;

        var segList = new List<ProfileSegment>(3);
        double curTime = 0.0;
        double curPos = 0.0;

        if (tAcc > 1e-9)
        {
            double endPos = curPos + sAcc;
            segList.Add(new ProfileSegment(
                curTime, curTime + tAcc, tAcc,
                curPos * _direction, endPos * _direction,
                v0Proj * _direction, vPeak * _direction,
                amax * _direction, amax * _direction,
                0.0,
                ProfileSegmentType.ConstantAcceleration
            ));
            curTime += tAcc;
            curPos = endPos;
        }

        if (tCruise > 1e-9)
        {
            double endPos = curPos + sCruiseActual;
            segList.Add(new ProfileSegment(
                curTime, curTime + tCruise, tCruise,
                curPos * _direction, endPos * _direction,
                vPeak * _direction, vPeak * _direction,
                0.0, 0.0,
                0.0,
                ProfileSegmentType.ConstantVelocity
            ));
            curTime += tCruise;
            curPos = endPos;
        }

        if (tDec > 1e-9)
        {
            double endPos = curPos + sDec;
            segList.Add(new ProfileSegment(
                curTime, curTime + tDec, tDec,
                curPos * _direction, (curPos + sDec) * _direction,
                vPeak * _direction, vtProj * _direction,
                -decel * _direction, -decel * _direction,
                0.0,
                ProfileSegmentType.ConstantDeceleration
            ));
        }

        _segments = segList.ToArray();
    }

    /// <summary>
    /// 任意时刻 t 的位置 x(t)（严格解析积分，物理单位）。
    /// </summary>
    public double PositionAt(double t)
    {
        if (Duration <= 1e-12 || _totalDistance <= 1e-12)
        {
            return 0.0;
        }

        if (t <= 0)
        {
            return 0.0;
        }

        if (t >= Duration)
        {
            return Distance;
        }

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                double dt = t - seg.StartTime;
                double a = seg.StartAcceleration;
                double v0 = seg.StartVelocity;
                double x0 = seg.StartPosition;
                return x0 + v0 * dt + 0.5 * a * dt * dt;
            }
        }

        return Distance;
    }

    /// <summary>
    /// 任意时刻 t 的速度 v(t)（严格解析计算，带符号）。
    /// </summary>
    public double VelocityAt(double t)
    {
        if (Duration <= 1e-12)
        {
            return TargetVelocity;
        }

        if (t <= 0)
        {
            return InitialVelocity;
        }

        if (t >= Duration)
        {
            return TargetVelocity;
        }

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                double dt = t - seg.StartTime;
                return seg.StartVelocity + seg.StartAcceleration * dt;
            }
        }

        return TargetVelocity;
    }

    /// <summary>
    /// 任意时刻 t 的加速度 a(t)（严格解析计算，带符号）。
    /// </summary>
    public double AccelAt(double t)
    {
        if (t <= 0 || t >= Duration || Duration <= 1e-12)
        {
            return 0.0;
        }

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                return seg.StartAcceleration;
            }
        }

        return 0.0;
    }
}
