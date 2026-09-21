using Sophon.Motion.Trajectory;
using Xunit;

namespace Sophon.Motion.Tests;

public class TrajectoryAndLookAheadTests
{
    [Fact]
    public void Test_LookAhead_CornerSpeedAndZeroFinalSpeed()
    {
        var queue = new SegmentQueue();
        // 构造三段轨迹：折线 (0,0) -> (100,0) -> (100,100) -> (200,100)
        // 中间有两个 90 度直角转弯
        queue.EnqueueLine(new double[] { 0, 0 }, new double[] { 100, 0 }, 100.0);
        queue.EnqueueLine(new double[] { 100, 0 }, new double[] { 100, 100 }, 100.0);
        queue.EnqueueLine(new double[] { 100, 100 }, new double[] { 200, 100 }, 100.0);

        var constraints = new LookAheadConstraints(
            MaxSpeed: 100.0,
            MaxAccel: 500.0,
            MaxDecel: 500.0,
            MaxCentripetalAccel: 200.0
        );

        LookAheadPlanner.Plan(queue, constraints);

        var segs = queue.Segments;
        Assert.Equal(3, segs.Count);

        // 1. 首段入口速度 = 0 (静止起步)
        Assert.Equal(0.0, segs[0].EntrySpeed, 6);

        // 2. 末段出口速度 = 0 (末段必须减速到停止)
        Assert.Equal(0.0, segs[2].ExitSpeed, 6);

        // 3. 拐角处的速度受向心加速度限制，必须显著小于直线最高速度 100
        Assert.True(segs[0].ExitSpeed < 100.0);
        Assert.True(segs[1].EntrySpeed < 100.0);
        Assert.Equal(segs[0].ExitSpeed, segs[1].EntrySpeed, 6); // 相邻段过渡速度相等

        Assert.True(segs[1].ExitSpeed < 100.0);
        Assert.True(segs[2].EntrySpeed < 100.0);
        Assert.Equal(segs[1].ExitSpeed, segs[2].EntrySpeed, 6);
    }

    [Fact]
    public void Test_BlendCorner_DeviationWithinTolerance()
    {
        // 拐角点 (100, 0)，前段来自 (0, 0)，后段去向 (100, 100)（90度转角）
        double[] pPrev = { 0.0, 0.0 };
        double[] corner = { 100.0, 0.0 };
        double[] pNext = { 100.0, 100.0 };

        double tolerance = 1.0; // 允许最大 1.0 mm 几何偏差

        var blend = BlendCorner.ComputeBlend(pPrev, corner, pNext, tolerance);

        Assert.NotNull(blend);
        // 过渡点距原拐角偏差 <= ε
        Assert.True(blend!.MaxDeviation <= tolerance + 1e-9);
        Assert.True(blend.MaxDeviation > 0);

        // 验证圆心到拐角点的距离与半径之差等于实际偏差
        double dcx = blend.Center[0] - corner[0];
        double dcy = blend.Center[1] - corner[1];
        double distCornerToCenter = Math.Sqrt(dcx * dcx + dcy * dcy);
        double expectedDev = distCornerToCenter - blend.Radius;
        Assert.Equal(expectedDev, blend.MaxDeviation, 6);
    }
}
