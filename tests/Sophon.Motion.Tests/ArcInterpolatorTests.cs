using Sophon.Motion.Interpolation;
using Xunit;

namespace Sophon.Motion.Tests;

public class ArcInterpolatorTests
{
    [Fact]
    public void Test_ThreePointArc_And_CenterVerification()
    {
        // 构造 XY 平面圆弧，已知圆心 (50, 50, 0)，半径 50
        // 起点 (100, 50, 0) => theta=0
        // 中间点 (50, 100, 0) => theta=pi/2
        // 终点 (0, 50, 0) => theta=pi
        double[] p1 = { 100.0, 50.0, 0.0 };
        double[] p2 = { 50.0, 100.0, 0.0 };
        double[] p3 = { 0.0, 50.0, 0.0 };

        var arc = ArcInterpolator.FromThreePoints(p1, p2, p3, 50.0, 100.0, 100.0);

        // 验证圆心解算正确
        Assert.Equal(50.0, arc.Center[0], 6);
        Assert.Equal(50.0, arc.Center[1], 6);
        Assert.Equal(0.0, arc.Center[2], 6);
        Assert.Equal(50.0, arc.Radius, 6);

        // 验证终点与起点
        var startSample = arc.SampleAt(0);
        Assert.Equal(p1[0], startSample.Positions[0], 5);
        Assert.Equal(p1[1], startSample.Positions[1], 5);

        var endSample = arc.SampleAt(arc.Duration);
        Assert.Equal(p3[0], endSample.Positions[0], 5);
        Assert.Equal(p3[1], endSample.Positions[1], 5);
    }

    [Fact]
    public void Test_RadiusResidual_And_ChordErrorBound()
    {
        // 采样点半径误差与弦高误差界
        double[] center = { 0.0, 0.0, 10.0 };
        double radius = 100.0;
        double startAngle = 0.0;
        double sweepAngle = Math.PI; // 半圆

        var arc = new ArcInterpolator(ArcPlane.XY, center, radius, startAngle, sweepAngle, 80.0, 200.0, 200.0);

        // 弦高误差上限设定为 0.01 mm
        double maxChordError = 0.01;
        double recommendedDt = arc.CalculateMaxStepTime(maxChordError, 80.0);
        Assert.True(recommendedDt > 0);

        // 密集采样验证每个采样点在圆弧上的几何半径误差 <= 1e-9
        int sampleCount = 500;
        double dt = arc.Duration / sampleCount;

        for (int i = 0; i <= sampleCount; i++)
        {
            double t = i * dt;
            var pt = arc.SampleAt(t);

            double dx = pt.Positions[0] - center[0];
            double dy = pt.Positions[1] - center[1];
            double dist = Math.Sqrt(dx * dx + dy * dy);

            Assert.Equal(radius, dist, 8);
            Assert.Equal(10.0, pt.Positions[2], 8);
        }
    }
}
