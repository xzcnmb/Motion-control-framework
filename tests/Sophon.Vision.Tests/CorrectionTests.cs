using System;
using System.Collections.Generic;
using Sophon.Contracts;
using Sophon.Vision.Calibration;
using Sophon.Vision.Correction;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class CorrectionTests
    {
        [Fact]
        public void OffsetCalculator_WithCalibration_ShouldConvertPixelDeltaToWorldDelta()
        {
            // 假设 1 px = 0.05 mm，无旋转，平移原点偏移 (10, 20)
            var points = new List<CalibrationPoint>
            {
                new(0, 0, 10, 20),
                new(100, 0, 15, 20),
                new(0, 100, 10, 25),
                new(100, 100, 15, 25)
            };
            var calib = NinePointCalibration.Calibrate(points);

            var teachBase = new VisionTeachBase(
                name: "ProductA",
                cameraId: "Cam1",
                teachWorldX: 100.0,
                teachWorldY: 50.0,
                teachWorldAngleDeg: 0.0,
                basePixelX: 300.0,
                basePixelY: 200.0,
                basePixelAngleDeg: 0.0
            );

            // 当前产品偏离基准：X 偏了 +20 px，Y 偏了 -10 px，角度偏了 +2.5 度
            var currentResult = new VisionResult(
                RequestId: Guid.NewGuid(),
                CameraId: "Cam1",
                Ok: true,
                X: 320.0,
                Y: 190.0,
                AngleDeg: 2.5,
                Score: 0.95,
                WorldX: 0,
                WorldY: 0,
                WorldAngleDeg: 0
            );

            var offset = OffsetCalculator.Calculate(currentResult, teachBase, calib);

            Assert.Equal(20.0, offset.DeltaPixelX, 4);
            Assert.Equal(-10.0, offset.DeltaPixelY, 4);
            Assert.Equal(2.5, offset.DeltaAngleDeg, 4);

            // 物理增量：20 px * 0.05 = 1.0 mm，-10 px * 0.05 = -0.5 mm
            Assert.Equal(1.0, offset.DeltaWorldX, 4);
            Assert.Equal(-0.5, offset.DeltaWorldY, 4);
        }

        [Fact]
        public void XyrCorrection_PureTranslation_ShouldCompensateLinearly()
        {
            // 纯平移测试：当前 (102, 53, 0)，目标 (100, 50, 0)，无角度偏
            var target = XyrCorrection.Calculate(
                currentWorldX: 102.0,
                currentWorldY: 53.0,
                currentAngleDeg: 0.0,
                targetWorldX: 100.0,
                targetWorldY: 50.0,
                targetAngleDeg: 0.0,
                rotationCenterX: 0.0,
                rotationCenterY: 0.0
            );

            Assert.Equal(-2.0, target.DeltaX, 6);
            Assert.Equal(-3.0, target.DeltaY, 6);
            Assert.Equal(0.0, target.DeltaAngle, 6);
            Assert.Equal(100.0, target.CorrectedX, 6);
            Assert.Equal(50.0, target.CorrectedY, 6);
        }

        [Fact]
        public void XyrCorrection_WithRotation_AroundNonOriginCenter_ShouldAlignPerfect()
        {
            // 机构旋转中心在 C(50.0, 50.0)
            double rcX = 50.0, rcY = 50.0;

            // 基准目标特征点在 P0(80.0, 50.0)，基准角度 0 度
            double targetX = 80.0, targetY = 50.0, targetAngle = 0.0;

            // 工件以 C(50, 50) 为中心旋转了 +30 度，特征点随之转到 P(x, y)
            double rotRad = 30.0 * Math.PI / 180.0;
            double currentX = rcX + (targetX - rcX) * Math.Cos(rotRad) - (targetY - rcY) * Math.Sin(rotRad);
            double currentY = rcY + (targetX - rcX) * Math.Sin(rotRad) + (targetY - rcY) * Math.Cos(rotRad);
            double currentAngle = 30.0;

            var target = XyrCorrection.Calculate(
                currentWorldX: currentX,
                currentWorldY: currentY,
                currentAngleDeg: currentAngle,
                targetWorldX: targetX,
                targetWorldY: targetY,
                targetAngleDeg: targetAngle,
                rotationCenterX: rcX,
                rotationCenterY: rcY
            );

            // 角度需要反向旋转 -30 度
            Assert.Equal(-30.0, target.DeltaAngle, 6);

            // 因为当前点纯粹由绕 (50, 50) 旋转 30 度生成，因此一旦机构反转 -30 度，
            // 该特征点就会自然回到 target (80.0, 50.0)，平移补偿量 DeltaX/DeltaY 应为 0！
            Assert.True(Math.Abs(target.DeltaX) < 1e-6);
            Assert.True(Math.Abs(target.DeltaY) < 1e-6);
            Assert.Equal(targetX, target.CorrectedX, 5);
            Assert.Equal(targetY, target.CorrectedY, 5);
        }

        [Fact]
        public void XyrCorrection_CombinedTranslationAndRotation_NumericalVerification()
        {
            double rcX = 100.0, rcY = 100.0;
            double targetX = 150.0, targetY = 120.0, targetAngle = 10.0;

            // 设当前物料不仅角度偏了 15 度（当前 25 度），平移还额外偏了 (+5.0, -3.0)
            double deltaAngleDeg = 15.0;
            double rad = deltaAngleDeg * Math.PI / 180.0;

            // 绕 (100, 100) 旋转后的坐标再加上平移偏差
            double rx = rcX + (targetX - rcX) * Math.Cos(rad) - (targetY - rcY) * Math.Sin(rad);
            double ry = rcY + (targetX - rcX) * Math.Sin(rad) + (targetY - rcY) * Math.Cos(rad);

            double currentX = rx + 5.0;
            double currentY = ry - 3.0;
            double currentAngle = targetAngle + deltaAngleDeg;

            var target = XyrCorrection.Calculate(
                currentWorldX: currentX,
                currentWorldY: currentY,
                currentAngleDeg: currentAngle,
                targetWorldX: targetX,
                targetWorldY: targetY,
                targetAngleDeg: targetAngle,
                rotationCenterX: rcX,
                rotationCenterY: rcY
            );

            Assert.Equal(-15.0, target.DeltaAngle, 6);

            // 验证纠偏执行后：将 (currentX, currentY) 先平移 (DeltaX, DeltaY)，并绕旋转中心旋转 DeltaAngle
            // 其最终位置是否严格吻合 targetX, targetY
            // 先旋转纠正：绕 (rcX, rcY) 旋转 -15 度
            double unrotRad = -15.0 * Math.PI / 180.0;
            double unrotX = rcX + (currentX - rcX) * Math.Cos(unrotRad) - (currentY - rcY) * Math.Sin(unrotRad);
            double unrotY = rcY + (currentX - rcX) * Math.Sin(unrotRad) + (currentY - rcY) * Math.Cos(unrotRad);

            // 再平移 DeltaX, DeltaY
            double finalX = unrotX + target.DeltaX;
            double finalY = unrotY + target.DeltaY;

            Assert.Equal(targetX, finalX, 5);
            Assert.Equal(targetY, finalY, 5);
        }

        [Fact]
        public void OffsetCalculator_FlippedYChirality_ShouldInvertAngleSign()
        {
            // 工业典型场景：图像 Y 轴向下，机器人/物理坐标系 Y 轴向上 (手性反转 det < 0)
            // 标定矩阵：1px = 0.05mm，但 Y 轴反向 (a22 = -0.05)
            var points = new List<CalibrationPoint>
            {
                new(0, 0, 10, 20),
                new(100, 0, 15, 20),
                new(0, 100, 10, 15),     // Py 增加，Yw 减少
                new(100, 100, 15, 15)
            };
            var calib = NinePointCalibration.Calibrate(points);
            Assert.True(calib.Matrix[0][0] * calib.Matrix[1][1] - calib.Matrix[0][1] * calib.Matrix[1][0] < 0);

            var teachBase = new VisionTeachBase(
                name: "ProductChirality",
                cameraId: "Cam1",
                teachWorldX: 100.0,
                teachWorldY: 50.0,
                teachWorldAngleDeg: 0.0,
                basePixelX: 200.0,
                basePixelY: 200.0,
                basePixelAngleDeg: 0.0
            );

            // 图像中特征顺时针旋转 +5.0 度 (AngleDeg = 5.0)
            var currentResult = new VisionResult(
                RequestId: Guid.NewGuid(),
                CameraId: "Cam1",
                Ok: true,
                X: 200.0,
                Y: 200.0,
                AngleDeg: 5.0,
                Score: 0.99,
                WorldX: 0,
                WorldY: 0,
                WorldAngleDeg: 0
            );

            var offset = OffsetCalculator.Calculate(currentResult, teachBase, calib);

            // 像素角度偏差为 +5.0，但在 Y 轴翻转的世界坐标系下，物理角度偏差必须反转为 -5.0 度！
            Assert.Equal(5.0, offset.DeltaPixelX == 0 ? 5.0 : 0.0);
            Assert.Equal(5.0, currentResult.AngleDeg - teachBase.BasePixelAngleDeg, 4);
            Assert.Equal(-5.0, offset.DeltaAngleDeg, 4);
        }

        [Fact]
        public void OffsetCalculator_WithoutCalibration_ShouldUseWorldCoordinatesDirectly()
        {
            var teachBase = new VisionTeachBase(
                name: "ProductNoCalib",
                cameraId: "CamSim",
                teachWorldX: 100.0,
                teachWorldY: 50.0,
                teachWorldAngleDeg: 10.0,
                basePixelX: 0.0,
                basePixelY: 0.0,
                basePixelAngleDeg: 0.0
            );

            var currentResult = new VisionResult(
                RequestId: Guid.NewGuid(),
                CameraId: "CamSim",
                Ok: true,
                X: 0,
                Y: 0,
                AngleDeg: 0,
                Score: 1.0,
                WorldX: 105.0,
                WorldY: 48.0,
                WorldAngleDeg: 13.5
            );

            var offset = OffsetCalculator.Calculate(currentResult, teachBase, calibration: null);

            Assert.Equal(5.0, offset.DeltaWorldX, 4);
            Assert.Equal(-2.0, offset.DeltaWorldY, 4);
            Assert.Equal(3.5, offset.DeltaAngleDeg, 4);
        }

        [Fact]
        public void XyrCorrection_HandComputable_KnownRotationAboutCenter_RoundTrips()
        {
            // 手算基准：平台旋转中心 C(100.0, 100.0)
            // 示教基准目标点 (150.0, 100.0)，基准角度 0.0°
            double rcX = 100.0, rcY = 100.0;
            double targetX = 150.0, targetY = 100.0, targetAngle = 0.0;

            // 工件特征绕 C(100, 100) 旋转了 +30°
            // 50 * cos(30°) = 43.301270, 50 * sin(30°) = 25.0
            double currentX = 143.301270;
            double currentY = 125.0;
            double currentAngle = 30.0;

            var target = XyrCorrection.Calculate(
                currentWorldX: currentX,
                currentWorldY: currentY,
                currentAngleDeg: currentAngle,
                targetWorldX: targetX,
                targetWorldY: targetY,
                targetAngleDeg: targetAngle,
                rotationCenterX: rcX,
                rotationCenterY: rcY
            );

            // 机构旋转角度必须为 -30°，且无需额外平移
            Assert.Equal(-30.0, target.DeltaAngle, 5);
            Assert.Equal(0.0, target.DeltaX, 4);
            Assert.Equal(0.0, target.DeltaY, 4);
            Assert.Equal(targetX, target.CorrectedX, 4);
            Assert.Equal(targetY, target.CorrectedY, 4);
        }

        [Fact]
        public void XyrCorrection_WorkpieceRotatedAboutFeature_RoundTrips()
        {
            // 上料偏斜场景：工件在特征点 P0(150.0, 100.0) 自身原地偏转了 +30 度
            // 特征点物理位置仍在 (150.0, 100.0)，但角度为 30.0°
            double rcX = 100.0, rcY = 100.0;
            double targetX = 150.0, targetY = 100.0, targetAngle = 0.0;
            double currentX = 150.0, currentY = 100.0, currentAngle = 30.0;

            var target = XyrCorrection.Calculate(
                currentWorldX: currentX,
                currentWorldY: currentY,
                currentAngleDeg: currentAngle,
                targetWorldX: targetX,
                targetWorldY: targetY,
                targetAngleDeg: targetAngle,
                rotationCenterX: rcX,
                rotationCenterY: rcY
            );

            // 机构只能绕物理中心 (100, 100) 旋转 -30 度：
            // 旋转后特征点到达 (100 + 50*cos(-30°), 100 + 50*sin(-30°)) = (143.30127, 75.0)
            // 随后平移补偿：DeltaX = 150 - 143.30127 = 6.69873 mm, DeltaY = 100 - 75 = 25.0 mm
            Assert.Equal(-30.0, target.DeltaAngle, 5);
            Assert.Equal(6.69873, target.DeltaX, 4);
            Assert.Equal(25.0, target.DeltaY, 4);

            // 机构执行验证：
            // 1. 绕 (rcX, rcY) 旋转 DeltaAngle
            double rad = target.DeltaAngle * Math.PI / 180.0;
            double rx = rcX + (currentX - rcX) * Math.Cos(rad) - (currentY - rcY) * Math.Sin(rad);
            double ry = rcY + (currentX - rcX) * Math.Sin(rad) + (currentY - rcY) * Math.Cos(rad);

            // 2. 叠加坡内平移 DeltaX, DeltaY
            double finalX = rx + target.DeltaX;
            double finalY = ry + target.DeltaY;

            Assert.Equal(targetX, finalX, 4);
            Assert.Equal(targetY, finalY, 4);
        }
    }
}
