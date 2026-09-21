using System;
using System.Collections.Generic;
using Sophon.Vision.Calibration;
using Sophon.Vision.Coordinates;
using Sophon.Vision.Correction;
using Xunit;

namespace Sophon.Vision.Tests
{
    /// <summary>
    /// 坐标系框架（CoordinateFrame / Transform2D / CoordinateFrameTree）及视觉对位集成测试。
    /// </summary>
    public class CoordinateFrameTreeTests
    {
        /// <summary>
        /// 构造无噪声九点标定：已知真实仿射矩阵
        /// Xw = 0.05 * Px - 0.01 * Py + 10.0
        /// Yw = 0.01 * Px + 0.05 * Py + 20.0
        /// </summary>
        private static NinePointCalibration CreateKnownCalibration()
        {
            double a11 = 0.05, a12 = -0.01, dx = 10.0;
            double a21 = 0.01, a22 = 0.05, dy = 20.0;

            var points = new List<CalibrationPoint>();
            double[] pxArr = { 100, 300, 500 };
            double[] pyArr = { 100, 250, 400 };

            foreach (var py in pyArr)
            {
                foreach (var px in pxArr)
                {
                    points.Add(new CalibrationPoint(px, py, a11 * px + a12 * py + dx, a21 * px + a22 * py + dy));
                }
            }

            return NinePointCalibration.Calibrate(points);
        }

        // ------------------------------------------------------------------
        // Transform2D：组合、求逆、点/矢量变换、手性
        // ------------------------------------------------------------------

        [Fact]
        public void Transform2D_组合变换_等价于依次进行单步变换()
        {
            // T_{B←A}：平移 (10, 20)
            var tBA = Transform2D.FromTranslation(10, 20);
            // T_{C←B}：绕原点旋转 90°
            var tCB = Transform2D.FromRotationDeg(90);

            // T_{C←A} = T_{C←B} * T_{B←A}（先应用 T_{B←A}，再应用 T_{C←B}）
            var tCA = tCB * tBA;

            var composed = tCA.Transform(1, 2);
            var sequential = tCB.Transform(tBA.Transform(1, 2));

            // 手算：p_A=(1,2) -> p_B=(11,22) -> R90*(11,22) = (-22, 11)
            Assert.Equal(-22.0, composed.X, 9);
            Assert.Equal(11.0, composed.Y, 9);
            Assert.Equal(composed.X, sequential.X, 12);
            Assert.Equal(composed.Y, sequential.Y, 12);
        }

        [Fact]
        public void Transform2D_求逆_往返还原且乘积为单位变换()
        {
            var t = new Transform2D(0.5, -1.5, 30.0, 2.5, 0.5, -40.0);

            var (fx, fy) = t.Transform(7.3, -11.8);
            var (bx, by) = t.Inverse().Transform(fx, fy);
            Assert.Equal(7.3, bx, 9);
            Assert.Equal(-11.8, by, 9);

            var product = t * t.Inverse();
            Assert.Equal(1.0, product.R11, 9);
            Assert.Equal(0.0, product.R12, 9);
            Assert.Equal(0.0, product.Tx, 9);
            Assert.Equal(0.0, product.R21, 9);
            Assert.Equal(1.0, product.R22, 9);
            Assert.Equal(0.0, product.Ty, 9);
        }

        [Fact]
        public void Transform2D_矢量变换_忽略平移分量()
        {
            var t = new Transform2D(2, 0, 100.0, 0, 3, 200.0);

            var (dx, dy) = t.TransformVector(5, 7);
            Assert.Equal(10.0, dx, 12);   // 2*5，不含平移
            Assert.Equal(21.0, dy, 12);   // 3*7，不含平移

            // 点变换则包含平移
            var (px, py) = t.Transform(0, 0);
            Assert.Equal(100.0, px, 12);
            Assert.Equal(200.0, py, 12);
        }

        [Fact]
        public void Transform2D_手性检测_镜像矩阵判定为反转()
        {
            Assert.False(Transform2D.Identity.IsMirrored);
            Assert.Equal(1.0, Transform2D.Identity.Determinant, 12);

            // 任意旋转保持手性（det = 1）
            Assert.False(Transform2D.FromRotationDeg(90).IsMirrored);
            Assert.False(Transform2D.FromRotationDeg(-137.5).IsMirrored);

            // X 轴翻转：det < 0，手性反转
            var mirrored = new Transform2D(-1, 0, 0, 0, 1, 0);
            Assert.True(mirrored.IsMirrored);
            Assert.True(mirrored.Determinant < 0);
            Assert.Equal(-1.0, mirrored.Determinant, 12);

            // 图像 Y 向下、物理 Y 向上的典型标定矩阵（Y 缩放为负）
            var yFlipped = new Transform2D(0.02, 0, 10.0, 0, -0.02, 200.0);
            Assert.True(yFlipped.IsMirrored);
        }

        [Fact]
        public void Transform2D_旋转角提取_与构造角一致()
        {
            Assert.Equal(37.5, Transform2D.FromRotationDeg(37.5).RotationDeg, 9);
            Assert.Equal(-90.0, Transform2D.FromRotationDeg(-90).RotationDeg, 9);
        }

        [Fact]
        public void Transform2D_仿射矩阵互操作_与九点标定逐元素对应()
        {
            var calib = CreateKnownCalibration();
            var t = calib.ToTransform2D();

            var m = calib.Matrix;
            Assert.Equal(m[0][0], t.R11, 12);
            Assert.Equal(m[0][1], t.R12, 12);
            Assert.Equal(m[0][2], t.Tx, 12);
            Assert.Equal(m[1][0], t.R21, 12);
            Assert.Equal(m[1][1], t.R22, 12);
            Assert.Equal(m[1][2], t.Ty, 12);

            var (tx, ty) = t.Transform(123.4, 567.8);
            var (cx, cy) = calib.Transform(123.4, 567.8);
            Assert.Equal(cx, tx, 9);
            Assert.Equal(cy, ty, 9);

            // 往返回 2x3 接口
            var back = t.ToAffine2x3();
            Assert.Equal(0.05, back[0][0], 12);
            Assert.Equal(-0.01, back[0][1], 12);
            Assert.Equal(10.0, back[0][2], 12);
            Assert.Equal(0.01, back[1][0], 12);
            Assert.Equal(0.05, back[1][1], 12);
            Assert.Equal(20.0, back[1][2], 12);
        }

        // ------------------------------------------------------------------
        // CoordinateFrameTree：多跳查找、反向查找、连通性
        // ------------------------------------------------------------------

        [Fact]
        public void 坐标树_多跳变换_像素经相机工具到世界()
        {
            var tree = new CoordinateFrameTree();

            // T_{Camera←Pixel}：0.02 mm/px 缩放 + 平移 (5, 7)
            var tCamPixel = new Transform2D(0.02, 0, 5.0, 0, 0.02, 7.0);
            // T_{Tool←Camera}：绕原点旋转 90° 后平移 (100, 50)
            var tToolCam = Transform2D.FromTranslation(100, 50) * Transform2D.FromRotationDeg(90);
            // T_{World←Tool}：绕原点旋转 -90° 后平移 (200, 300)
            var tWorldTool = Transform2D.FromTranslation(200, 300) * Transform2D.FromRotationDeg(-90);

            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, tCamPixel);
            tree.SetTransform(CoordinateFrame.CameraFrame, CoordinateFrame.ToolFrame, tToolCam);
            tree.SetTransform(CoordinateFrame.ToolFrame, CoordinateFrame.WorldFrame, tWorldTool);

            Assert.Equal(3, tree.EdgeCount);
            Assert.Equal(4, tree.RegisteredFrames.Count);

            // 复合变换应等于三段链式乘积
            var expected = tWorldTool * tToolCam * tCamPixel;
            var actual = tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame);
            Assert.Equal(expected, actual);

            // 手算逐步验证：p_px=(100,200)
            // p_cam = (0.02*100+5, 0.02*200+7) = (7, 11)
            // p_tool = R90*(7,11) + (100,50) = (-11+100, 7+50) = (89, 57)
            // p_world = R(-90)*(89,57) + (200,300) = (57, -89) + (200,300) = (257, 211)
            var (wx, wy) = actual.Transform(100, 200);
            Assert.Equal(257.0, wx, 9);
            Assert.Equal(211.0, wy, 9);
        }

        [Fact]
        public void 坐标树_反向查找_自动沿逆变换复合()
        {
            var tree = new CoordinateFrameTree();
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame,
                new Transform2D(0.02, 0, 5.0, 0, 0.02, 7.0));
            tree.SetTransform(CoordinateFrame.CameraFrame, CoordinateFrame.ToolFrame,
                Transform2D.FromTranslation(100, 50) * Transform2D.FromRotationDeg(90));
            tree.SetTransform(CoordinateFrame.ToolFrame, CoordinateFrame.WorldFrame,
                Transform2D.FromTranslation(200, 300) * Transform2D.FromRotationDeg(-90));

            var forward = tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame);
            var backward = tree.GetTransform(CoordinateFrame.WorldFrame, CoordinateFrame.PixelFrame);

            // World -> Pixel 应精确抵消 Pixel -> World
            var (wx, wy) = forward.Transform(100, 200);
            var (px, py) = backward.Transform(wx, wy);
            Assert.Equal(100.0, px, 6);
            Assert.Equal(200.0, py, 6);

            // 同一坐标系到自身恒为单位变换
            Assert.Equal(Transform2D.Identity, tree.GetTransform(CoordinateFrame.WorldFrame, CoordinateFrame.WorldFrame));
        }

        [Fact]
        public void 坐标树_未连通坐标系_查找失败并抛出异常()
        {
            var tree = new CoordinateFrameTree();
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame,
                Transform2D.FromTranslation(1, 2));

            Assert.True(tree.TryGetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, out _));
            Assert.False(tree.TryGetTransform(CoordinateFrame.ToolFrame, CoordinateFrame.WorldFrame, out _));
            Assert.False(tree.TryGetTransform(CoordinateFrame.CameraFrame, CoordinateFrame.WorldFrame, out _));

            Assert.Throws<InvalidOperationException>(
                () => tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame));
        }

        [Fact]
        public void 坐标树_注册九点标定_像素世界双向一致()
        {
            var calib = CreateKnownCalibration();

            var tree = new CoordinateFrameTree();
            tree.SetCalibration(calib);

            Assert.True(tree.HasEdge(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame));
            Assert.Equal(1, tree.EdgeCount);

            // 正向：与九点标定逐点一致
            var tPixelToWorld = tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame);
            var (tx, ty) = tPixelToWorld.Transform(250, 180);
            var (cx, cy) = calib.Transform(250, 180);
            Assert.Equal(cx, tx, 9);
            Assert.Equal(cy, ty, 9);

            // 反向：与 InvertTransform 一致
            var tWorldToPixel = tree.GetTransform(CoordinateFrame.WorldFrame, CoordinateFrame.PixelFrame);
            var (bx, by) = tWorldToPixel.Transform(cx, cy);
            var (ix, iy) = calib.InvertTransform(cx, cy);
            Assert.Equal(ix, bx, 9);
            Assert.Equal(iy, by, 9);
        }

        [Fact]
        public void 坐标树_覆盖与移除变换边()
        {
            var tree = new CoordinateFrameTree();
            var t1 = Transform2D.FromTranslation(1, 2);
            var t2 = Transform2D.FromTranslation(3, 4);

            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, t1);
            Assert.Equal(t1, tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame));

            // 覆盖同一条边
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, t2);
            Assert.Equal(t2, tree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame));
            Assert.Equal(1, tree.EdgeCount);

            // 移除后不再连通
            Assert.True(tree.RemoveTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame));
            Assert.False(tree.HasEdge(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame));
            Assert.False(tree.TryGetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, out _));
            Assert.Equal(0, tree.EdgeCount);

            // 移除不存在的边返回 false
            Assert.False(tree.RemoveTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame));

            // 清空
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame, t1);
            tree.Clear();
            Assert.Equal(0, tree.EdgeCount);
            Assert.Empty(tree.RegisteredFrames);
        }

        [Fact]
        public void 坐标树_拒绝奇异变换边()
        {
            var tree = new CoordinateFrameTree();

            // det = 1*4 - 2*2 = 0，线性部分奇异
            var singular = new Transform2D(1, 2, 0, 2, 4, 0);
            Assert.True(singular.Determinant == 0.0);
            Assert.False(singular.IsInvertible);

            Assert.Throws<ArgumentException>(
                () => tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, singular));
        }

        // ------------------------------------------------------------------
        // VisualGuidanceAligner 集成：Transform2D / 坐标树 与 九点标定 等价
        // ------------------------------------------------------------------

        [Fact]
        public void 点击对位_Transform2D重载_与九点标定结果一致()
        {
            var calib = CreateKnownCalibration();
            var t = calib.ToTransform2D();

            foreach (var mode in new[] { CameraMountMode.EyeInHand, CameraMountMode.EyeToHand })
            {
                var legacy = VisualGuidanceAligner.CalculateClickToAlign(
                    calib, 600, 700, 500, 500, 50, 60, mode);
                var viaTransform = VisualGuidanceAligner.CalculateClickToAlign(
                    t, 600, 700, 500, 500, 50, 60, mode);

                Assert.Equal(legacy.DeltaWorldX, viaTransform.DeltaWorldX, 12);
                Assert.Equal(legacy.DeltaWorldY, viaTransform.DeltaWorldY, 12);
                Assert.Equal(legacy.TargetWorldX, viaTransform.TargetWorldX, 12);
                Assert.Equal(legacy.TargetWorldY, viaTransform.TargetWorldY, 12);
                Assert.Equal(legacy.PixelDeltaX, viaTransform.PixelDeltaX, 12);
                Assert.Equal(legacy.PixelDeltaY, viaTransform.PixelDeltaY, 12);
            }

            // 手算校核（EyeInHand）：dpx=100, dpy=200
            // ΔX = 0.05*100 + (-0.01)*200 = 3.0 ；ΔY = 0.01*100 + 0.05*200 = 11.0
            var r = VisualGuidanceAligner.CalculateClickToAlign(t, 600, 700, 500, 500, 50, 60);
            Assert.Equal(3.0, r.DeltaWorldX, 9);
            Assert.Equal(11.0, r.DeltaWorldY, 9);
            Assert.Equal(53.0, r.TargetWorldX, 9);
            Assert.Equal(71.0, r.TargetWorldY, 9);
        }

        [Fact]
        public void 拖拽微调_Transform2D重载_与九点标定结果一致()
        {
            var calib = CreateKnownCalibration();
            var t = calib.ToTransform2D();

            foreach (var mode in new[] { CameraMountMode.EyeInHand, CameraMountMode.EyeToHand })
            {
                var legacy = VisualGuidanceAligner.CalculateDragMove(calib, 50, -30, 10, 20, mode);
                var viaTransform = VisualGuidanceAligner.CalculateDragMove(t, 50, -30, 10, 20, mode);

                Assert.Equal(legacy.DeltaWorldX, viaTransform.DeltaWorldX, 12);
                Assert.Equal(legacy.DeltaWorldY, viaTransform.DeltaWorldY, 12);
                Assert.Equal(legacy.TargetWorldX, viaTransform.TargetWorldX, 12);
                Assert.Equal(legacy.TargetWorldY, viaTransform.TargetWorldY, 12);
            }

            // 手算校核：ΔX = 0.05*50 + (-0.01)*(-30) = 2.8 ；ΔY = 0.01*50 + 0.05*(-30) = -1.0
            var r = VisualGuidanceAligner.CalculateDragMove(t, 50, -30, 10, 20);
            Assert.Equal(2.8, r.DeltaWorldX, 9);
            Assert.Equal(-1.0, r.DeltaWorldY, 9);
            Assert.Equal(12.8, r.TargetWorldX, 9);
            Assert.Equal(19.0, r.TargetWorldY, 9);
        }

        [Fact]
        public void 点击对位_坐标树多跳链路_与单跳标定结果一致()
        {
            var calib = CreateKnownCalibration();
            var tWorldPixel = calib.ToTransform2D();

            // 构造 Pixel -> Camera -> Tool -> World 三段链，其复合恰等于 T_{World←Pixel}
            var tCamPixel = new Transform2D(0.5, 0.1, 3.0, -0.2, 0.5, 4.0);
            var tToolCam = Transform2D.FromRotationDeg(90);
            var tWorldTool = tWorldPixel * (tToolCam * tCamPixel).Inverse();

            var tree = new CoordinateFrameTree();
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame, tCamPixel);
            tree.SetTransform(CoordinateFrame.CameraFrame, CoordinateFrame.ToolFrame, tToolCam);
            tree.SetTransform(CoordinateFrame.ToolFrame, CoordinateFrame.WorldFrame, tWorldTool);

            // 点击对位：多跳链路 vs 九点标定
            var legacy = VisualGuidanceAligner.CalculateClickToAlign(
                calib, 640, 480, 500, 500, 12.5, -7.25);
            var viaTree = VisualGuidanceAligner.CalculateClickToAlign(
                tree, 640, 480, 500, 500, 12.5, -7.25);

            Assert.Equal(legacy.DeltaWorldX, viaTree.DeltaWorldX, 9);
            Assert.Equal(legacy.DeltaWorldY, viaTree.DeltaWorldY, 9);
            Assert.Equal(legacy.TargetWorldX, viaTree.TargetWorldX, 9);
            Assert.Equal(legacy.TargetWorldY, viaTree.TargetWorldY, 9);

            // 拖拽微调
            var legacyDrag = VisualGuidanceAligner.CalculateDragMove(calib, 50, -30, 10, 20);
            var viaTreeDrag = VisualGuidanceAligner.CalculateDragMove(tree, 50, -30, 10, 20);
            Assert.Equal(legacyDrag.DeltaWorldX, viaTreeDrag.DeltaWorldX, 9);
            Assert.Equal(legacyDrag.DeltaWorldY, viaTreeDrag.DeltaWorldY, 9);

            // 绝对定位
            var (ax, ay) = VisualGuidanceAligner.CalculateAbsolutePosition(tree, 250, 180);
            var (lx, ly) = calib.Transform(250, 180);
            Assert.Equal(lx, ax, 9);
            Assert.Equal(ly, ay, 9);
        }

        [Fact]
        public void 坐标树对位_像素世界未连通时抛出异常()
        {
            var tree = new CoordinateFrameTree();
            tree.SetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.CameraFrame,
                Transform2D.FromTranslation(1, 2));

            Assert.Throws<InvalidOperationException>(
                () => VisualGuidanceAligner.CalculateClickToAlign(tree, 600, 700, 500, 500, 0, 0));
            Assert.Throws<InvalidOperationException>(
                () => VisualGuidanceAligner.CalculateDragMove(tree, 10, 10, 0, 0));
            Assert.Throws<InvalidOperationException>(
                () => VisualGuidanceAligner.CalculateAbsolutePosition(tree, 100, 100));
            Assert.Throws<ArgumentNullException>(
                () => VisualGuidanceAligner.CalculateClickToAlign((CoordinateFrameTree)null, 600, 700, 500, 500, 0, 0));
        }
    }
}
