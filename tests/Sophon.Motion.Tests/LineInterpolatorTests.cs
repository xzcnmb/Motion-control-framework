using Sophon.Motion.Interpolation;
using Xunit;

namespace Sophon.Motion.Tests;

public class LineInterpolatorTests
{
    [Fact]
    public void Test_CollinearityResidual_And_EndPointError()
    {
        double[] start = { 10.0, 20.0, 30.0 };
        double[] end = { 110.0, 170.0, 230.0 };

        var interpolator = new LineInterpolator(start, end, 100.0, 200.0, 200.0);

        Assert.Equal(3, interpolator.AxisCount);
        Assert.True(interpolator.Duration > 0);

        // 终点误差 <= 1e-9
        var endSample = interpolator.SampleAt(interpolator.Duration);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(end[i], endSample.Positions[i], 9);
        }

        // 起点误差 <= 1e-9
        var startSample = interpolator.SampleAt(0);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(start[i], startSample.Positions[i], 9);
        }

        // 任意采样时刻各轴位置落在直线上（共线性残差 <= 1e-9）
        // 向量 (P(t) - start) 必须与 (end - start) 严格平行
        double[] dir = { end[0] - start[0], end[1] - start[1], end[2] - start[2] };
        double totalLen = interpolator.TotalLength;

        int steps = 100;
        double dt = interpolator.Duration / steps;

        for (int step = 0; step <= steps; step++)
        {
            double t = step * dt;
            var sample = interpolator.SampleAt(t);

            // 检查与起点的差向量
            double ratio = sample.PathVelocity >= 0 ? interpolator.Profile.PositionAt(t) / totalLen : 0;
            for (int i = 0; i < 3; i++)
            {
                double expectedCoord = start[i] + dir[i] * ratio;
                Assert.Equal(expectedCoord, sample.Positions[i], 9);
            }
        }
    }
}
