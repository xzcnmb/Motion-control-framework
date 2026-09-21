using System;
using System.Collections.Generic;
using Sophon.Vision.Calibration;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class RotationCenterCalibrationTests
    {
        [Fact]
        public void CalibrateTwoPoints_KnownCenter_ShouldRecoverCenterAccurately()
        {
            // 已知旋转中心物理坐标 C(150.0, 100.0)，半径 R = 50.0 mm
            double trueCx = 150.0;
            double trueCy = 100.0;
            double r = 50.0;

            // 采样点 1：角度 10 度
            double deg1 = 10.0;
            double rad1 = deg1 * Math.PI / 180.0;
            double x1 = trueCx + r * Math.Cos(rad1);
            double y1 = trueCy + r * Math.Sin(rad1);
            var p1 = new RotationSamplePoint(deg1, x1, y1);

            // 采样点 2：角度 70 度 (Δθ = 60度)
            double deg2 = 70.0;
            double rad2 = deg2 * Math.PI / 180.0;
            double x2 = trueCx + r * Math.Cos(rad2);
            double y2 = trueCy + r * Math.Sin(rad2);
            var p2 = new RotationSamplePoint(deg2, x2, y2);

            var result = RotationCenterCalibration.CalibrateTwoPoints(p1, p2);

            Assert.True(Math.Abs(result.CenterX - trueCx) < 1e-6);
            Assert.True(Math.Abs(result.CenterY - trueCy) < 1e-6);
            Assert.True(Math.Abs(result.RadiusMm - r) < 1e-6);
        }

        [Fact]
        public void Calibrate_MultiplePoints_LeastSquares_ShouldRecoverCenter()
        {
            double trueCx = 220.0;
            double trueCy = 180.0;
            double r = 65.0;

            var samples = new List<RotationSamplePoint>();
            double[] angles = { 0.0, 30.0, 60.0, 90.0, 135.0, 180.0 };

            foreach (var a in angles)
            {
                double rad = a * Math.PI / 180.0;
                double x = trueCx + r * Math.Cos(rad);
                double y = trueCy + r * Math.Sin(rad);
                samples.Add(new RotationSamplePoint(a, x, y));
            }

            var result = RotationCenterCalibration.Calibrate(samples);

            Assert.True(Math.Abs(result.CenterX - trueCx) < 1e-6);
            Assert.True(Math.Abs(result.CenterY - trueCy) < 1e-6);
            Assert.True(Math.Abs(result.RadiusMm - r) < 1e-6);
            Assert.True(result.RmsResidualMm < 1e-6);
        }

        [Fact]
        public void Calibrate_ThreePoints_KnownSmallAngles_ShouldRecoverCenterWithZeroResidual()
        {
            // 真实旋转中心 (100.0, 50.0)，旋转半径 R = 40.0 mm
            // 工业小角度旋转场景：0° -> 10° -> 20°
            double trueCx = 100.0;
            double trueCy = 50.0;
            double r = 40.0;

            var samples = new List<RotationSamplePoint>
            {
                new(0.0, trueCx + r * Math.Cos(0.0), trueCy + r * Math.Sin(0.0)),
                new(10.0, trueCx + r * Math.Cos(10.0 * Math.PI / 180.0), trueCy + r * Math.Sin(10.0 * Math.PI / 180.0)),
                new(20.0, trueCx + r * Math.Cos(20.0 * Math.PI / 180.0), trueCy + r * Math.Sin(20.0 * Math.PI / 180.0))
            };

            var result = RotationCenterCalibration.Calibrate(samples);

            Assert.True(Math.Abs(result.CenterX - trueCx) < 1e-6, $"CenterX 误差: {result.CenterX - trueCx}");
            Assert.True(Math.Abs(result.CenterY - trueCy) < 1e-6, $"CenterY 误差: {result.CenterY - trueCy}");
            Assert.True(Math.Abs(result.RadiusMm - r) < 1e-6, $"Radius 误差: {result.RadiusMm - r}");
            Assert.True(result.RmsResidualMm < 1e-6);
        }

        [Fact]
        public void Calibrate_FallbackToCircleFitting_WhenAnglesAreZeroOrIdentical()
        {
            // 当所有采样点角度未提供（全为 0）时，自动回退到代数圆拟合
            double trueCx = 80.0;
            double trueCy = 90.0;
            double r = 35.0;

            var samples = new List<RotationSamplePoint>();
            double[] angles = { 0.0, 45.0, 90.0, 180.0 };

            foreach (var a in angles)
            {
                double rad = a * Math.PI / 180.0;
                double x = trueCx + r * Math.Cos(rad);
                double y = trueCy + r * Math.Sin(rad);
                // 强制角度全设为 0，模拟无角度测量的纯点集圆拟合
                samples.Add(new RotationSamplePoint(0.0, x, y));
            }

            var result = RotationCenterCalibration.Calibrate(samples);

            Assert.True(Math.Abs(result.CenterX - trueCx) < 1e-6);
            Assert.True(Math.Abs(result.CenterY - trueCy) < 1e-6);
            Assert.True(Math.Abs(result.RadiusMm - r) < 1e-6);
        }

        [Fact]
        public void CalibrateTwoPoints_HandComputable_90DegRotation()
        {
            // 手算特例：旋转中心 C(0, 0)，半径 10
            // P1(10, 0) 角度 0°，P2(0, 10) 角度 90°
            var p1 = new RotationSamplePoint(0.0, 10.0, 0.0);
            var p2 = new RotationSamplePoint(90.0, 0.0, 10.0);

            var res = RotationCenterCalibration.CalibrateTwoPoints(p1, p2);

            Assert.Equal(0.0, res.CenterX, 6);
            Assert.Equal(0.0, res.CenterY, 6);
            Assert.Equal(10.0, res.RadiusMm, 6);
            Assert.Equal(0.0, res.RmsResidualMm, 6);
        }
    }
}
