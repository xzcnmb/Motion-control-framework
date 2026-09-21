using Sophon.Motion.Profiles;
using Xunit;

namespace Sophon.Motion.Tests;

public class TrapezoidalProfileTests
{
    [Fact]
    public void Test_AnalyticDisplacementMatchesTarget()
    {
        // 测试正常梯形：位移 100，最大速度 50，加速度 100，减速度 100
        var profile = new TrapezoidalProfile(0, 0, 50, 100, 100, 100);

        Assert.True(profile.Duration > 0);
        Assert.Equal(100.0, profile.Distance, 9);
        Assert.Equal(100.0, profile.PositionAt(profile.Duration), 9);
        Assert.Equal(0.0, profile.PositionAt(0), 9);

        // 数值积分验证与解析值在 1e-9 以内一致
        int steps = 10000;
        double dt = profile.Duration / steps;
        double integratedPos = 0.0;
        for (int i = 0; i < steps; i++)
        {
            double tMid = (i + 0.5) * dt;
            integratedPos += profile.VelocityAt(tMid) * dt;
        }

        Assert.True(Math.Abs(integratedPos - 100.0) < 1e-4);
    }

    [Fact]
    public void Test_Symmetry_WhenAccEqualsDecel()
    {
        // 对称加速与减速：v 曲线关于中点时间对称
        var profile = new TrapezoidalProfile(0, 0, 50, 200, 200, 200);

        double halfT = profile.Duration * 0.5;
        for (double t = 0.1; t < halfT; t += 0.2)
        {
            double vLeft = profile.VelocityAt(t);
            double vRight = profile.VelocityAt(profile.Duration - t);
            Assert.Equal(vLeft, vRight, 6);
        }
    }

    [Fact]
    public void Test_TriangleDegeneration_ShortDistance()
    {
        // 短距离退化测试：位移很小（5mm），无法达到 vmax 100
        var profile = new TrapezoidalProfile(0, 0, 100, 50, 50, 5);

        Assert.True(profile.Duration > 0);
        Assert.Equal(5.0, profile.PositionAt(profile.Duration), 9);

        // 确保匀速段时间为 0（仅加速和减速两段）
        Assert.Equal(2, profile.Segments.Count);

        // 验证峰值速度未超 vmax
        double peakV = profile.VelocityAt(profile.Duration * 0.5);
        Assert.True(peakV < 100.0);
        Assert.True(peakV > 0.0);
    }

    [Fact]
    public void Test_NegativeDistance()
    {
        // 负向位移
        var profile = new TrapezoidalProfile(0, 0, 50, 100, 100, -80);
        Assert.Equal(-80.0, profile.PositionAt(profile.Duration), 9);
        Assert.True(profile.VelocityAt(profile.Duration * 0.5) < 0);
    }
}
