using System;
using System.Collections.Generic;
using Sophon.Vision.Calibration;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class NinePointCalibrationTests
    {
        [Fact]
        public void Calibrate_WithoutNoise_ShouldRecoverAffineMatrixAccurately()
        {
            // 已知真实仿射矩阵（平移 + 旋转 + 缩放）
            // WorldX = 0.05 * Px - 0.01 * Py + 10.0
            // WorldY = 0.01 * Px + 0.05 * Py + 20.0
            double trueA11 = 0.05, trueA12 = -0.01, trueDx = 10.0;
            double trueA21 = 0.01, trueA22 = 0.05,  trueDy = 20.0;

            var points = new List<CalibrationPoint>();

            // 构造 3x3 标定阵列（9点）
            double[] pxArr = { 100, 300, 500 };
            double[] pyArr = { 100, 250, 400 };

            foreach (var py in pyArr)
            {
                foreach (var px in pxArr)
                {
                    double xw = trueA11 * px + trueA12 * py + trueDx;
                    double yw = trueA21 * px + trueA22 * py + trueDy;
                    points.Add(new CalibrationPoint(px, py, xw, yw));
                }
            }

            var calib = NinePointCalibration.Calibrate(points);

            // 矩阵参数恢复精度 (1e-9)
            Assert.True(Math.Abs(calib.Matrix[0][0] - trueA11) < 1e-9);
            Assert.True(Math.Abs(calib.Matrix[0][1] - trueA12) < 1e-9);
            Assert.True(Math.Abs(calib.Matrix[0][2] - trueDx) < 1e-9);

            Assert.True(Math.Abs(calib.Matrix[1][0] - trueA21) < 1e-9);
            Assert.True(Math.Abs(calib.Matrix[1][1] - trueA22) < 1e-9);
            Assert.True(Math.Abs(calib.Matrix[1][2] - trueDy) < 1e-9);

            // 残差接近 0
            Assert.True(calib.Residual.RmsErrorMm < 1e-9);
            Assert.True(calib.Residual.MaxErrorMm < 1e-9);
            Assert.True(calib.IsAcceptable);

            // 验证单点与逆变换
            var (testWkX, testWkY) = calib.Transform(250, 180);
            double expectedX = trueA11 * 250 + trueA12 * 180 + trueDx;
            double expectedY = trueA21 * 250 + trueA22 * 180 + trueDy;
            Assert.Equal(expectedX, testWkX, 7);
            Assert.Equal(expectedY, testWkY, 7);

            var (invPx, invPy) = calib.InvertTransform(testWkX, testWkY);
            Assert.Equal(250.0, invPx, 7);
            Assert.Equal(180.0, invPy, 7);
        }

        [Fact]
        public void Calibrate_WithNoise_ShouldRespectAcceptanceThreshold()
        {
            double trueA11 = 0.02, trueA12 = 0.0, trueDx = 5.0;
            double trueA21 = 0.0,  trueA22 = 0.02, trueDy = 5.0;

            var points = new List<CalibrationPoint>();
            double[] pxArr = { 100, 300, 500 };
            double[] pyArr = { 100, 250, 400 };

            int idx = 0;
            // 构造稍微偏离的物理噪声点
            double[] noiseMm = { 0.01, -0.01, 0.02, -0.02, 0.005, -0.01, 0.015, -0.005, 0.008 };

            foreach (var py in pyArr)
            {
                foreach (var px in pxArr)
                {
                    double xw = trueA11 * px + trueA12 * py + trueDx + noiseMm[idx % noiseMm.Length];
                    double yw = trueA21 * px + trueA22 * py + trueDy - noiseMm[idx % noiseMm.Length];
                    points.Add(new CalibrationPoint(px, py, xw, yw));
                    idx++;
                }
            }

            // 设置允许阈值 MaxResidualMm = 0.03 mm
            var calib = NinePointCalibration.Calibrate(points, maxResidualMmThreshold: 0.03, maxResidualPxThreshold: 2.0);

            Assert.True(calib.Residual.MaxErrorMm > 0.0);
            Assert.True(calib.Residual.MaxErrorMm < 0.03);
            Assert.True(calib.IsAcceptable);

            // 当收紧阈值到 0.005 mm 时，判定应为不合格 (IsAcceptable == false)
            var strictCalib = NinePointCalibration.Calibrate(points, maxResidualMmThreshold: 0.005, maxResidualPxThreshold: 2.0);
            Assert.False(strictCalib.IsAcceptable);
        }

        [Fact]
        public void Calibrate_HandComputable_KnownGrid_AffineRoundTrips()
        {
            // 手算基准九点网格 (3x3)：
            // 像素尺度: Px in {0, 50, 100}, Py in {0, 50, 100}
            // 真实物理变换（尺度 0.1、剪切/旋转分量 -0.05、平移 50, 80）：
            // Xw = 0.1 * Px - 0.05 * Py + 50.0
            // Yw = 0.05 * Px + 0.1 * Py + 80.0
            double a11 = 0.1, a12 = -0.05, dx = 50.0;
            double a21 = 0.05, a22 = 0.1, dy = 80.0;

            var points = new List<CalibrationPoint>();
            double[] pxArr = { 0, 50, 100 };
            double[] pyArr = { 0, 50, 100 };

            foreach (var py in pyArr)
            {
                foreach (var px in pxArr)
                {
                    double xw = a11 * px + a12 * py + dx;
                    double yw = a21 * px + a22 * py + dy;
                    points.Add(new CalibrationPoint(px, py, xw, yw));
                }
            }

            var calib = NinePointCalibration.Calibrate(points);

            // 矩阵参数恢复与理论参数残差 < 1e-12
            Assert.True(Math.Abs(calib.Matrix[0][0] - a11) < 1e-12);
            Assert.True(Math.Abs(calib.Matrix[0][1] - a12) < 1e-12);
            Assert.True(Math.Abs(calib.Matrix[0][2] - dx) < 1e-12);

            Assert.True(Math.Abs(calib.Matrix[1][0] - a21) < 1e-12);
            Assert.True(Math.Abs(calib.Matrix[1][1] - a22) < 1e-12);
            Assert.True(Math.Abs(calib.Matrix[1][2] - dy) < 1e-12);

            // 残差接近 0
            Assert.True(calib.Residual.RmsErrorMm < 1e-12);
            Assert.True(calib.Residual.MaxErrorMm < 1e-12);
            Assert.True(calib.Residual.RmsErrorPx < 1e-12);
            Assert.True(calib.Residual.MaxErrorPx < 1e-12);
            Assert.True(calib.IsAcceptable);

            // 往返验证：对任意测试点 (73.5, 42.8) 执行正逆变换
            double testPx = 73.5, testPy = 42.8;
            var (xwEst, ywEst) = calib.Transform(testPx, testPy);
            var (pxBack, pyBack) = calib.InvertTransform(xwEst, ywEst);

            Assert.Equal(testPx, pxBack, 8);
            Assert.Equal(testPy, pyBack, 8);
        }
    }
}
