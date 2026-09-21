namespace Sophon.Motion.Trajectory;

/// <summary>
/// 前瞻规划约束参数。
/// </summary>
/// <param name="MaxSpeed">全局最大线速度 (物理单位/s)</param>
/// <param name="MaxAccel">全局最大切向加速度 (物理单位/s²)</param>
/// <param name="MaxDecel">全局最大切向减速度 (物理单位/s²)</param>
/// <param name="MaxCentripetalAccel">拐角允许的最大向心/法向加速度 (物理单位/s²)</param>
public record LookAheadConstraints(
    double MaxSpeed,
    double MaxAccel,
    double MaxDecel,
    double MaxCentripetalAccel
);

/// <summary>
/// 连续小线段/混合轨迹的前瞻速度规划器 (Look-Ahead Planner)。
/// 结合相邻段夹角、向心加速度限制计算拐角限速，
/// 通过反向扫描（逆推减速能力）与正向扫描（顺推加速能力）双向收敛，
/// 确保末段安全停止到 0，且任意相邻过渡处均不发生速度突变与超限。
/// </summary>
public static class LookAheadPlanner
{
    /// <summary>
    /// 对给定轨迹队列执行前瞻速度规划，计算并更新每个段的 EntrySpeed 与 ExitSpeed。
    /// </summary>
    /// <param name="queue">运动段队列</param>
    /// <param name="constraints">动力学与拐角约束</param>
    public static void Plan(SegmentQueue queue, LookAheadConstraints constraints)
    {
        if (queue == null) throw new ArgumentNullException(nameof(queue));
        if (constraints == null) throw new ArgumentNullException(nameof(constraints));
        var segments = queue.Segments;
        int n = segments.Count;
        if (n == 0) return;

        // 1. 计算每个连接节点（拐角）的几何允许最大过渡速度
        // junctionVelocities[i] 表示第 i 段与第 i-1 段之间节点的允许最大速度
        // junctionVelocities[0] 为起点，junctionVelocities[n] 为终点
        double[] junctionLimits = new double[n + 1];

        // 默认起止点静止停靠
        junctionLimits[0] = 0.0;
        junctionLimits[n] = 0.0;

        for (int i = 1; i < n; i++)
        {
            var segPrev = segments[i - 1];
            var segNext = segments[i];

            double cornerLimit = CalculateCornerSpeedLimit(segPrev, segNext, constraints);
            double allowedSpeed = Math.Min(segPrev.TargetSpeed, segNext.TargetSpeed);
            allowedSpeed = Math.Min(allowedSpeed, constraints.MaxSpeed);
            allowedSpeed = Math.Min(allowedSpeed, cornerLimit);

            junctionLimits[i] = allowedSpeed;
        }

        // 初始化每段的初末速度为上限
        for (int i = 0; i < n; i++)
        {
            segments[i].EntrySpeed = Math.Min(segments[i].TargetSpeed, junctionLimits[i]);
            segments[i].ExitSpeed = Math.Min(segments[i].TargetSpeed, junctionLimits[i + 1]);
        }

        // 2. 反向扫描（Backward Pass）：由终点向起点反推
        // 从后往前：为了保证能在第 i 段末尾减速到 ExitSpeed，入口速度 EntrySpeed 满足：
        // EntrySpeed^2 <= ExitSpeed^2 + 2 * decel * length
        for (int i = n - 1; i >= 0; i--)
        {
            var seg = segments[i];
            double maxEntryAllowed = Math.Sqrt(seg.ExitSpeed * seg.ExitSpeed + 2.0 * constraints.MaxDecel * Math.Max(0.0, seg.Length));
            if (seg.EntrySpeed > maxEntryAllowed)
            {
                seg.EntrySpeed = maxEntryAllowed;
            }

            // 前一段的 ExitSpeed 不能高于当前段的 EntrySpeed
            if (i > 0)
            {
                if (segments[i - 1].ExitSpeed > seg.EntrySpeed)
                {
                    segments[i - 1].ExitSpeed = seg.EntrySpeed;
                }
            }
        }

        // 3. 正向扫描（Forward Pass）：由起点向终点顺推
        // 从前往后：由于加速能力限制，ExitSpeed 满足：
        // ExitSpeed^2 <= EntrySpeed^2 + 2 * accel * length
        for (int i = 0; i < n; i++)
        {
            var seg = segments[i];
            double maxExitAllowed = Math.Sqrt(seg.EntrySpeed * seg.EntrySpeed + 2.0 * constraints.MaxAccel * Math.Max(0.0, seg.Length));
            if (seg.ExitSpeed > maxExitAllowed)
            {
                seg.ExitSpeed = maxExitAllowed;
            }

            // 下一段的 EntrySpeed 也不能高于当前段的 ExitSpeed
            if (i < n - 1)
            {
                if (segments[i + 1].EntrySpeed > seg.ExitSpeed)
                {
                    segments[i + 1].EntrySpeed = seg.ExitSpeed;
                }
            }
        }
    }

    /// <summary>
    /// 根据相邻段的单位切线方向夹角与向心加速度计算拐角速度上限。
    /// 当夹角为 0（共线直行）时，无拐角限制；
    /// 当夹角越大（折返/锐角），通过速度越低；180 度反向时速度降至 0。
    /// 公式基于向心加速度与等效拐角半径：v ≤ sqrt(a_c * R_equiv)。
    /// </summary>
    public static double CalculateCornerSpeedLimit(MotionSegment prev, MotionSegment next, LookAheadConstraints constraints)
    {
        double[] t1 = prev.GetEndTangent();
        double[] t2 = next.GetStartTangent();

        double dot = 0.0;
        int dim = Math.Min(t1.Length, t2.Length);
        for (int i = 0; i < dim; i++)
        {
            dot += t1[i] * t2[i];
        }

        // 限制在 [-1, 1]
        dot = Math.Clamp(dot, -1.0, 1.0);

        // 如果夹角几乎为 0 (方向一致)
        if (dot >= 0.999999)
        {
            return constraints.MaxSpeed;
        }

        // 拐角转向角 theta = acos(dot) in (0, pi]
        double theta = Math.Acos(dot);

        // 向心加速度模型：拐角等效过渡距离取微小段长度（例如两段最短长度的一半）或根据角度衰减
        // 常用工业近似算法：v_corner = sqrt( a_c * L_char * (1 + cos(theta)) / (2 * sin(theta)) )
        // 或更稳健的形式：v_corner = sqrt( a_c * r_eff )，
        // 其中 r_eff = (L_min / 2) * tan((pi - theta) / 2)
        double lEff = Math.Min(prev.Length, next.Length) * 0.5;
        if (lEff <= 1e-6) return 0.0;

        double halfAngle = (Math.PI - theta) * 0.5; // (pi - theta) / 2
        double rEff = lEff * Math.Tan(Math.Max(1e-4, halfAngle));

        double vLimit = Math.Sqrt(constraints.MaxCentripetalAccel * Math.Max(1e-6, rEff));

        // 角度极小折角时进行安全截断
        if (dot < -0.999)
        {
            return 0.0; // 几乎180度反折，必须减速至0停下
        }

        return Math.Min(vLimit, constraints.MaxSpeed);
    }
}
