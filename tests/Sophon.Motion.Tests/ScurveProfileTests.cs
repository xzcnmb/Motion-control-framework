using Sophon.Motion.Profiles;
using Xunit;

namespace Sophon.Motion.Tests;

public class ScurveProfileTests
{
    [Fact]
    public void Test_FullSevenSegments_PositionAtDuration()
    {
        // 较长位移，足以达到最大加速度与最大速度
        double s = 200.0;
        double vmax = 50.0;
        double amax = 100.0;
        double jmax = 500.0;

        var profile = new ScurveProfile(0, 0, vmax, amax, jmax, s);

        Assert.True(profile.Duration > 0);
        Assert.Equal(s, profile.PositionAt(profile.Duration), 5);
        Assert.Equal(0.0, profile.PositionAt(0), 6);
        Assert.Equal(0.0, profile.VelocityAt(profile.Duration), 5);

        // 验证 7 段结构存在
        Assert.True(profile.Segments.Count >= 5);
    }

    [Fact]
    public void Test_JerkBounded()
    {
        // 加加速度受限测试：验证任意时刻 |j| <= jmax
        double s = 100.0;
        double vmax = 60.0;
        double amax = 120.0;
        double jmax = 600.0;

        var profile = new ScurveProfile(0, 0, vmax, amax, jmax, s);

        int steps = 1000;
        double dt = profile.Duration / steps;
        for (int i = 0; i <= steps; i++)
        {
            double t = i * dt;
            double j = profile.JerkAt(t);
            Assert.True(Math.Abs(j) <= jmax + 1e-6, $"Jerk {j} exceeded jmax {jmax} at t={t}");
        }
    }

    [Fact]
    public void Test_Degeneration_NoConstantVelocity()
    {
        // 无匀速段退化（位移较小，二分求解峰值速度）
        double s = 5.0;
        double vmax = 100.0;
        double amax = 50.0;
        double jmax = 200.0;

        var profile = new ScurveProfile(0, 0, vmax, amax, jmax, s);

        Assert.True(profile.Duration > 0);
        Assert.Equal(s, profile.PositionAt(profile.Duration), 4);
        Assert.True(profile.VelocityAt(profile.Duration * 0.5) < vmax);
    }

    [Fact]
    public void Test_MultiAxisSync()
    {
        // 多轴同步测试
        double[] dists = { 100.0, 50.0, 20.0 };
        double[] vmax = { 50.0, 50.0, 50.0 };
        double[] amax = { 100.0, 100.0, 100.0 };
        double[] jmax = { 500.0, 500.0, 500.0 };

        var profiles = MultiAxisSync.SyncScurve(dists, vmax, amax, jmax);

        Assert.Equal(3, profiles.Length);
        double targetDuration = profiles[0].Duration;

        // 各轴的 Duration 应该一致（误差在容差范围内）
        for (int i = 1; i < profiles.Length; i++)
        {
            Assert.True(Math.Abs(profiles[i].Duration - targetDuration) < 1e-4,
                $"Axis {i} duration {profiles[i].Duration} mismatch with axis 0 duration {targetDuration}");
            Assert.Equal(dists[i], profiles[i].PositionAt(profiles[i].Duration), 4);
        }
    }
}
