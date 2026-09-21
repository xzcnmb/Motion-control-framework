using Sophon.Contracts;
using Sophon.Motion.Constraints;
using Sophon.Motion.Interpolation;
using Xunit;

namespace Sophon.Motion.Tests;

public class MotionLimitsAndSafetyTests
{
    [Fact]
    public void Test_ValidateTarget_RejectsOutOfBounds()
    {
        var limits = new MotionLimits
        {
            SoftLimitMin = -50.0,
            SoftLimitMax = 200.0,
            SoftLimitEnabled = true
        };

        // 正常范围
        Assert.True(limits.ValidateTarget(0.0, 100.0, out var err1));
        Assert.Null(err1);

        // 超过正向软限位
        Assert.False(limits.ValidateTarget(100.0, 250.0, out var err2));
        Assert.NotNull(err2);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.ValidateTarget(100.0, 250.0));

        // 超过负向软限位
        Assert.False(limits.ValidateTarget(0.0, -80.0, out var err3));
        Assert.NotNull(err3);
        Assert.Throws<ArgumentOutOfRangeException>(() => limits.ValidateTarget(0.0, -80.0));
    }

    [Fact]
    public void Test_ClampProfile_StopsAtLimit_NeverOverruns()
    {
        var limits = new MotionLimits
        {
            SoftLimitMin = 0.0,
            SoftLimitMax = 100.0,
            SoftLimitEnabled = true,
            MaxSpeed = 50.0,
            MaxAccel = 100.0,
            MaxDecel = 100.0
        };

        // 当前位置 80，当前速度 20，期望奔向 150（已严重越界）
        var (profile, safeTarget) = limits.ClampProfile(80.0, 20.0, 150.0);

        // 1. 终点严格被裁剪在软限位 100
        Assert.Equal(100.0, safeTarget, 6);

        // 2. 运动规划到达安全终点时，速度为 0
        Assert.Equal(20.0, profile.Distance, 6); // 100 - 80 = 20
        Assert.Equal(20.0, profile.PositionAt(profile.Duration), 6);
        Assert.Equal(0.0, profile.VelocityAt(profile.Duration), 6);

        // 3. 全程任何时刻位置不会超过限位 100
        int steps = 100;
        double dt = profile.Duration / steps;
        for (int i = 0; i <= steps; i++)
        {
            double pos = 80.0 + profile.PositionAt(i * dt);
            Assert.True(pos <= 100.000001, $"Position {pos} exceeded limit 100 at step {i}");
        }
    }

    [Fact]
    public void Test_ManualTimeSource_DeterministicSampling()
    {
        // 验证时间确定性：使用 ManualTimeSource 进行虚拟时钟推进采样
        var timeSource = new ManualTimeSource();

        double[] start = { 0.0, 0.0 };
        double[] end = { 100.0, 200.0 };
        var line = new LineInterpolator(start, end, 50.0, 100.0, 100.0);

        timeSource.Set(TimeSpan.Zero);
        var pt0 = line.SampleAt(timeSource.Elapsed.TotalSeconds);
        Assert.Equal(0.0, pt0.Positions[0], 6);
        Assert.Equal(0.0, pt0.Positions[1], 6);

        // 步进 100ms
        timeSource.Advance(TimeSpan.FromMilliseconds(100));
        var pt1 = line.SampleAt(timeSource.Elapsed.TotalSeconds);
        Assert.True(pt1.Positions[0] > 0);
        Assert.True(pt1.Positions[1] > 0);

        // 步进到 Duration
        timeSource.Set(TimeSpan.FromSeconds(line.Duration));
        var ptEnd = line.SampleAt(timeSource.Elapsed.TotalSeconds);
        Assert.Equal(100.0, ptEnd.Positions[0], 6);
        Assert.Equal(200.0, ptEnd.Positions[1], 6);
    }
}
