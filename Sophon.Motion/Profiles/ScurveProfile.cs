namespace Sophon.Motion.Profiles;

/// <summary>
/// 7段式 S 型速度规划（S-Curve / Jerk-limited Profile）。
/// 包含加加速、匀加速、减加速、匀速、加减速、匀减速、减减速 7 个阶段。
/// 支持 5 段（无匀加速/匀减速段）和 6 段/无匀速段的退化情况判定。
/// 严格解析积分，加加速度恒定，杜绝数值积分。
/// </summary>
public sealed class ScurveProfile
{
    private readonly ProfileSegment[] _segments;
    private readonly double _direction;
    private readonly double _totalDistance;

    /// <summary>总持续时长 (s)</summary>
    public double Duration { get; }

    /// <summary>位移 (物理单位，带正负号)</summary>
    public double Distance { get; }

    /// <summary>初速度 (物理单位/s)</summary>
    public double InitialVelocity { get; }

    /// <summary>末速度 (物理单位/s)</summary>
    public double TargetVelocity { get; }

    /// <summary>最大速度 (正数)</summary>
    public double MaxVelocity { get; }

    /// <summary>最大加速度 (正数)</summary>
    public double MaxAcceleration { get; }

    /// <summary>最大加加速度 (Jerk，正数)</summary>
    public double MaxJerk { get; }

    /// <summary>各分段明细</summary>
    public IReadOnlyList<ProfileSegment> Segments => _segments;

    /// <summary>
    /// 构造 7 段式 S 曲线规划器。
    /// </summary>
    /// <param name="v0">初速度</param>
    /// <param name="vt">末速度</param>
    /// <param name="vmax">最大速度约束</param>
    /// <param name="amax">最大加速度约束</param>
    /// <param name="jmax">最大加加速度约束 (Jerk)</param>
    /// <param name="s">总位移</param>
    public ScurveProfile(double v0, double vt, double vmax, double amax, double jmax, double s)
    {
        if (vmax <= 0) throw new ArgumentOutOfRangeException(nameof(vmax), "Max velocity must be positive.");
        if (amax <= 0) throw new ArgumentOutOfRangeException(nameof(amax), "Max acceleration must be positive.");
        if (jmax <= 0) throw new ArgumentOutOfRangeException(nameof(jmax), "Max jerk must be positive.");

        Distance = s;
        InitialVelocity = v0;
        TargetVelocity = vt;
        MaxVelocity = vmax;
        MaxAcceleration = amax;
        MaxJerk = jmax;

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

        double v0Proj = Math.Max(0.0, v0 * _direction);
        double vtProj = Math.Max(0.0, vt * _direction);

        v0Proj = Math.Min(v0Proj, vmax);
        vtProj = Math.Min(vtProj, vmax);

        // 尝试按 vmax 进行完整规划
        var (t1, t2, t3, t4, t5, t6, t7, vPeakActual) = PlanTimes(sAbs, v0Proj, vtProj, vmax, amax, jmax);

        Duration = t1 + t2 + t3 + t4 + t5 + t6 + t7;

        // 生成各段的解析分段记录
        double[] dt = { t1, t2, t3, t4, t5, t6, t7 };
        double[] jList = { jmax, 0.0, -jmax, 0.0, -jmax, 0.0, jmax };
        ProfileSegmentType[] types = {
            ProfileSegmentType.ConstantJerkPositive,
            ProfileSegmentType.ConstantAcceleration,
            ProfileSegmentType.ConstantJerkNegative,
            ProfileSegmentType.ConstantVelocity,
            ProfileSegmentType.ConstantJerkNegative,
            ProfileSegmentType.ConstantDeceleration,
            ProfileSegmentType.ConstantJerkPositive
        };

        var list = new List<ProfileSegment>();
        double curTime = 0.0;
        double curPos = 0.0;
        double curVel = v0Proj;
        double curAcc = 0.0;

        for (int i = 0; i < 7; i++)
        {
            double duration = dt[i];
            if (duration <= 1e-12)
            {
                continue;
            }

            double jerk = jList[i];
            double nextTime = curTime + duration;
            double nextAcc = curAcc + jerk * duration;
            double nextVel = curVel + curAcc * duration + 0.5 * jerk * duration * duration;
            double nextPos = curPos + curVel * duration + 0.5 * curAcc * duration * duration + (1.0 / 6.0) * jerk * duration * duration * duration;

            list.Add(new ProfileSegment(
                curTime, nextTime, duration,
                curPos * _direction, nextPos * _direction,
                curVel * _direction, nextVel * _direction,
                curAcc * _direction, nextAcc * _direction,
                jerk * _direction,
                types[i]
            ));

            curTime = nextTime;
            curPos = nextPos;
            curVel = nextVel;
            curAcc = nextAcc;
        }

        _segments = list.ToArray();
    }

    /// <summary>
    /// 计算 7 段各段时间。
    /// 当 s 足够大时，达到 vmax（含匀速段 t4 > 0）。
    /// 当 s 较小时，进入退化判定：首先无匀速段 (t4 = 0)，二分或解析搜索可达到的最大峰值速度 vPeak。
    /// 在加速/减速内部，如果加速到 vPeak 无法达到 amax，则 t2=0（变为 5 段退化）。
    /// </summary>
    private static (double t1, double t2, double t3, double t4, double t5, double t6, double t7, double vPeak)
        PlanTimes(double s, double v0, double vt, double vmax, double amax, double jmax)
    {
        // 判定达到给定峰值速度 v 时，加速段需要的时间与位移
        (double t1, double t2, double t3, double sa) CalcAcc(double vStart, double vEnd)
        {
            double dv = vEnd - vStart;
            if (dv <= 1e-12) return (0.0, 0.0, 0.0, 0.0);

            // 能否达到 amax？达到 amax 需要 delta_v >= amax^2 / jmax
            double dvThresh = (amax * amax) / jmax;
            if (dv >= dvThresh)
            {
                // 能够达到 amax
                double tJ = amax / jmax;
                double tConst = (dv - dvThresh) / amax;
                // 位移解析：
                // 段1: j, t in [0, tJ] => v0*tJ + 1/6 * j * tJ^3
                // 段2: amax, t in [0, tConst] => (v0 + 1/2*j*tJ^2)*tConst + 1/2*amax*tConst^2
                // 段3: -j, t in [0, tJ]
                // 实际上加减速是对称的，平均速度 = (vStart + vEnd) / 2
                double totalT = 2.0 * tJ + tConst;
                double dist = 0.5 * (vStart + vEnd) * totalT;
                return (tJ, tConst, tJ, dist);
            }
            else
            {
                // 无法达到 amax (5段退化，无恒加速段 t2 = 0)
                double tJ = Math.Sqrt(dv / jmax);
                double totalT = 2.0 * tJ;
                double dist = 0.5 * (vStart + vEnd) * totalT;
                return (tJ, 0.0, tJ, dist);
            }
        }

        // 减速段计算
        (double t5, double t6, double t7, double sd) CalcDec(double vStart, double vEnd)
        {
            var (t1, t2, t3, sa) = CalcAcc(vEnd, vStart);
            return (t1, t2, t3, sa);
        }

        // 检查达到 vmax 时的距离
        var (t1Max, t2Max, t3Max, saMax) = CalcAcc(v0, vmax);
        var (t5Max, t6Max, t7Max, sdMax) = CalcDec(vmax, vt);

        if (saMax + sdMax <= s)
        {
            // 完整 7 段（或含匀速段的退化形式）
            double t4 = (s - (saMax + sdMax)) / vmax;
            return (t1Max, t2Max, t3Max, t4, t5Max, t6Max, t7Max, vmax);
        }

        // 无法达到 vmax，匀速段 t4 = 0。二分求解 vPeak in [max(v0, vt), vmax]
        double low = Math.Max(v0, vt);
        double high = vmax;
        double bestVPeak = low;

        for (int iter = 0; iter < 60; iter++)
        {
            double mid = 0.5 * (low + high);
            var (_, _, _, saMid) = CalcAcc(v0, mid);
            var (_, _, _, sdMid) = CalcDec(mid, vt);
            double totalDist = saMid + sdMid;

            if (totalDist <= s)
            {
                bestVPeak = mid;
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var (t1, t2, t3, _) = CalcAcc(v0, bestVPeak);
        var (t5, t6, t7, _) = CalcDec(bestVPeak, vt);

        // 如果仍有极微小的余量 s - (sa + sd)，微调匀速段
        var (finalT1, finalT2, finalT3, finalSa) = CalcAcc(v0, bestVPeak);
        var (finalT5, finalT6, finalT7, finalSd) = CalcDec(bestVPeak, vt);
        double rem = s - (finalSa + finalSd);
        double t4Adj = (bestVPeak > 1e-6 && rem > 0) ? rem / bestVPeak : 0.0;

        return (finalT1, finalT2, finalT3, t4Adj, finalT5, finalT6, finalT7, bestVPeak);
    }

    /// <summary>
    /// 任意时刻 t 的位置 x(t)（严格解析积分）。
    /// </summary>
    public double PositionAt(double t)
    {
        if (Duration <= 1e-12 || _totalDistance <= 1e-12) return 0.0;
        if (t <= 0) return 0.0;
        if (t >= Duration) return Distance;

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                double dt = t - seg.StartTime;
                double j = seg.Jerk;
                double a0 = seg.StartAcceleration;
                double v0 = seg.StartVelocity;
                double x0 = seg.StartPosition;
                return x0 + v0 * dt + 0.5 * a0 * dt * dt + (1.0 / 6.0) * j * dt * dt * dt;
            }
        }

        return Distance;
    }

    /// <summary>
    /// 任意时刻 t 的速度 v(t)（严格解析计算）。
    /// </summary>
    public double VelocityAt(double t)
    {
        if (Duration <= 1e-12) return TargetVelocity;
        if (t <= 0) return InitialVelocity;
        if (t >= Duration) return TargetVelocity;

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                double dt = t - seg.StartTime;
                double j = seg.Jerk;
                double a0 = seg.StartAcceleration;
                double v0 = seg.StartVelocity;
                return v0 + a0 * dt + 0.5 * j * dt * dt;
            }
        }

        return TargetVelocity;
    }

    /// <summary>
    /// 任意时刻 t 的加速度 a(t)（严格解析计算）。
    /// </summary>
    public double AccelAt(double t)
    {
        if (Duration <= 1e-12 || t <= 0 || t >= Duration) return 0.0;

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                double dt = t - seg.StartTime;
                return seg.StartAcceleration + seg.Jerk * dt;
            }
        }

        return 0.0;
    }

    /// <summary>
    /// 任意时刻 t 的加加速度 j(t)。
    /// </summary>
    public double JerkAt(double t)
    {
        if (Duration <= 1e-12 || t <= 0 || t >= Duration) return 0.0;

        foreach (var seg in _segments)
        {
            if (t >= seg.StartTime && t <= seg.EndTime)
            {
                return seg.Jerk;
            }
        }

        return 0.0;
    }
}
