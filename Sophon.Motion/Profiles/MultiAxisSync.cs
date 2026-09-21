namespace Sophon.Motion.Profiles;

/// <summary>
/// 多轴同步辅助工具。
/// 在多轴联动时，按用时最长（瓶颈）轴的时间归一化各轴速度、加速度与 Jerk 参数，
/// 使得所有轴同时起跑、同时到位。
/// </summary>
public static class MultiAxisSync
{
    /// <summary>
    /// 对多轴梯形速度规划进行时间同步。
    /// 给定各轴的位移与约束（初末速度假定为0），找到耗时最长轴的 Duration，
    /// 并调整各轴的等效最大速度与加速度，输出同步后的各轴梯形 Profile。
    /// </summary>
    /// <param name="distances">各轴位移</param>
    /// <param name="maxVelocities">各轴最大速度约束</param>
    /// <param name="accelerations">各轴最大加速度约束</param>
    /// <param name="decelerations">各轴最大减速度约束</param>
    /// <returns>同步后的各轴梯形速度规划实例</returns>
    public static TrapezoidalProfile[] SyncTrapezoidal(
        double[] distances,
        double[] maxVelocities,
        double[] accelerations,
        double[] decelerations)
    {
        int n = distances.Length;
        if (n == 0) return Array.Empty<TrapezoidalProfile>();

        // 1. 先计算各轴在自身极限约束下的最短时间
        var baseProfiles = new TrapezoidalProfile[n];
        double maxDuration = 0.0;

        for (int i = 0; i < n; i++)
        {
            baseProfiles[i] = new TrapezoidalProfile(0, 0, maxVelocities[i], accelerations[i], decelerations[i], distances[i]);
            if (baseProfiles[i].Duration > maxDuration)
            {
                maxDuration = baseProfiles[i].Duration;
            }
        }

        if (maxDuration <= 1e-9)
        {
            return baseProfiles;
        }

        // 2. 针对非瓶颈轴，将其运动时间拉长至 maxDuration
        // 令时间缩放因子 lambda_i = Duration_i / maxDuration (<= 1)
        // 速度缩放为 lambda_i * vmax, 加速度缩放为 lambda_i^2 * amax
        // 这样形状保持相似，时间按比例扩大至 maxDuration
        var syncedProfiles = new TrapezoidalProfile[n];
        for (int i = 0; i < n; i++)
        {
            double dist = Math.Abs(distances[i]);
            if (dist < 1e-12)
            {
                syncedProfiles[i] = new TrapezoidalProfile(0, 0, maxVelocities[i], accelerations[i], decelerations[i], distances[i]);
                continue;
            }

            double curDur = baseProfiles[i].Duration;
            if (Math.Abs(curDur - maxDuration) < 1e-6)
            {
                syncedProfiles[i] = baseProfiles[i];
            }
            else
            {
                double scale = curDur / maxDuration;
                double scaledV = Math.Max(1e-6, maxVelocities[i] * scale);
                double scaledA = Math.Max(1e-6, accelerations[i] * scale * scale);
                double scaledD = Math.Max(1e-6, decelerations[i] * scale * scale);
                syncedProfiles[i] = new TrapezoidalProfile(0, 0, scaledV, scaledA, scaledD, distances[i]);
            }
        }

        return syncedProfiles;
    }

    /// <summary>
    /// 对多轴 S 曲线速度规划进行时间同步。
    /// </summary>
    public static ScurveProfile[] SyncScurve(
        double[] distances,
        double[] maxVelocities,
        double[] accelerations,
        double[] jerks)
    {
        int n = distances.Length;
        if (n == 0) return Array.Empty<ScurveProfile>();

        var baseProfiles = new ScurveProfile[n];
        double maxDuration = 0.0;

        for (int i = 0; i < n; i++)
        {
            baseProfiles[i] = new ScurveProfile(0, 0, maxVelocities[i], accelerations[i], jerks[i], distances[i]);
            if (baseProfiles[i].Duration > maxDuration)
            {
                maxDuration = baseProfiles[i].Duration;
            }
        }

        if (maxDuration <= 1e-9)
        {
            return baseProfiles;
        }

        var syncedProfiles = new ScurveProfile[n];
        for (int i = 0; i < n; i++)
        {
            double dist = Math.Abs(distances[i]);
            if (dist < 1e-12)
            {
                syncedProfiles[i] = new ScurveProfile(0, 0, maxVelocities[i], accelerations[i], jerks[i], distances[i]);
                continue;
            }

            double curDur = baseProfiles[i].Duration;
            if (Math.Abs(curDur - maxDuration) < 1e-6)
            {
                syncedProfiles[i] = baseProfiles[i];
            }
            else
            {
                double scale = curDur / maxDuration;
                double scaledV = Math.Max(1e-6, maxVelocities[i] * scale);
                double scaledA = Math.Max(1e-6, accelerations[i] * scale * scale);
                double scaledJ = Math.Max(1e-6, jerks[i] * scale * scale * scale);
                syncedProfiles[i] = new ScurveProfile(0, 0, scaledV, scaledA, scaledJ, distances[i]);
            }
        }

        return syncedProfiles;
    }
}
