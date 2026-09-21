using System.Collections.Generic;
using Sophon.Vision.Calibration;
using Sophon.Vision.Correction;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class VisualGuidanceAlignerTests
    {
        private static NinePointCalibration CreateOrthogonalCalibration(double mmPerPixelX = 0.01, double mmPerPixelY = 0.01)
        {
            // 构造一个简单的正交标定矩阵：
            // Xw = 0.01 * Px + 100.0
            // Yw = 0.01 * Py + 200.0
            var points = new List<CalibrationPoint>
            {
                new(100, 100, 100 * mmPerPixelX + 100, 100 * mmPerPixelY + 200),
                new(200, 100, 200 * mmPerPixelX + 100, 100 * mmPerPixelY + 200),
                new(300, 100, 300 * mmPerPixelX + 100, 100 * mmPerPixelY + 200),
                new(100, 200, 100 * mmPerPixelX + 100, 200 * mmPerPixelY + 200),
                new(200, 200, 200 * mmPerPixelX + 100, 200 * mmPerPixelY + 200),
                new(300, 200, 300 * mmPerPixelX + 100, 200 * mmPerPixelY + 200),
                new(100, 300, 100 * mmPerPixelX + 100, 300 * mmPerPixelY + 200),
                new(200, 300, 200 * mmPerPixelX + 100, 300 * mmPerPixelY + 200),
                new(300, 300, 300 * mmPerPixelX + 100, 300 * mmPerPixelY + 200),
            };

            return NinePointCalibration.Calibrate(points);
        }

        [Fact]
        public void 点击对位_移动相机模式_计算物理位移正确()
        {
            var calib = CreateOrthogonalCalibration(0.01, 0.01);

            // 准星在中心 (500, 500)，鼠标点击 (600, 700)
            // dpx = 100, dpy = 200
            // 物理移动应为: dX = 100 * 0.01 = 1.0mm, dY = 200 * 0.01 = 2.0mm
            double curX = 50.0;
            double curY = 60.0;

            var result = VisualGuidanceAligner.CalculateClickToAlign(
                calib,
                clickedPixelX: 600,
                clickedPixelY: 700,
                reticlePixelX: 500,
                reticlePixelY: 500,
                currentWorldX: curX,
                currentWorldY: curY,
                mountMode: CameraMountMode.EyeInHand);

            Assert.Equal(100.0, result.PixelDeltaX, 3);
            Assert.Equal(200.0, result.PixelDeltaY, 3);
            Assert.Equal(1.0, result.DeltaWorldX, 3);
            Assert.Equal(2.0, result.DeltaWorldY, 3);
            Assert.Equal(51.0, result.TargetWorldX, 3);
            Assert.Equal(62.0, result.TargetWorldY, 3);
        }

        [Fact]
        public void 点击对位_固定相机模式_反向移动工件()
        {
            var calib = CreateOrthogonalCalibration(0.01, 0.01);

            // 固定相机看工作台：工件需反向移动使目标对准准星
            var result = VisualGuidanceAligner.CalculateClickToAlign(
                calib,
                clickedPixelX: 600,
                clickedPixelY: 700,
                reticlePixelX: 500,
                reticlePixelY: 500,
                currentWorldX: 50.0,
                currentWorldY: 60.0,
                mountMode: CameraMountMode.EyeToHand);

            Assert.Equal(-1.0, result.DeltaWorldX, 3);
            Assert.Equal(-2.0, result.DeltaWorldY, 3);
            Assert.Equal(49.0, result.TargetWorldX, 3);
            Assert.Equal(58.0, result.TargetWorldY, 3);
        }

        [Fact]
        public void 鼠标拖拽微调_旋转坐标系下物理位移正确映射()
        {
            // 构造 90 度旋转标定矩阵：
            // Xw = -0.02 * Py
            // Yw =  0.02 * Px
            var points = new List<CalibrationPoint>
            {
                new(100, 100, -2.0, 2.0),
                new(200, 100, -2.0, 4.0),
                new(300, 100, -2.0, 6.0),
                new(100, 200, -4.0, 2.0),
                new(200, 200, -4.0, 4.0),
                new(300, 200, -4.0, 6.0),
                new(100, 300, -6.0, 2.0),
                new(200, 300, -6.0, 4.0),
                new(300, 300, -6.0, 6.0),
            };

            var calib = NinePointCalibration.Calibrate(points);

            // 操作员在图像视口中水平拖动 50 像素 (dpx = 50, dpy = 0)
            // 经 90 度旋转后：
            // DeltaWorldX = m[0,0]*50 + m[0,1]*0 = 0
            // DeltaWorldY = m[1,0]*50 + m[1,1]*0 = 0.02 * 50 = 1.0mm
            var result = VisualGuidanceAligner.CalculateDragMove(
                calib,
                dragDeltaPixelX: 50,
                dragDeltaPixelY: 0,
                currentWorldX: 0,
                currentWorldY: 0,
                mountMode: CameraMountMode.EyeInHand);

            Assert.InRange(result.DeltaWorldX, -0.01, 0.01);
            Assert.InRange(result.DeltaWorldY, 0.99, 1.01);
        }
    }
}
