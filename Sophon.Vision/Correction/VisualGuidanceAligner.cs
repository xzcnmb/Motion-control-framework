using System;
using Sophon.Vision.Calibration;
using Sophon.Vision.Coordinates;

namespace Sophon.Vision.Correction
{
    /// <summary>
    /// 相机安装与对位模式。
    /// </summary>
    public enum CameraMountMode
    {
        /// <summary>
        /// 移动相机（Eye-in-Hand / 随动相机）：相机安装在运动轴（如 Z 轴）末端随轴移动。
        /// 若目标点在准星右侧 (px > cx)，轴需向右正向移动以使准星对齐目标。
        /// </summary>
        EyeInHand,

        /// <summary>
        /// 固定相机（Eye-to-Hand / 静止下视）：相机固定在机架上方，运动轴驱动工件移动。
        /// 若目标点在准星右侧 (px > cx)，工件需向左负向移动以使目标对准准星。
        /// </summary>
        EyeToHand
    }

    /// <summary>
    /// 视觉引导对位计算结果。
    /// </summary>
    /// <param name="TargetWorldX">计算得出的目标物理 X 坐标 (mm)</param>
    /// <param name="TargetWorldY">计算得出的目标物理 Y 坐标 (mm)</param>
    /// <param name="DeltaWorldX">需移动的物理 X 偏移量 (mm)</param>
    /// <param name="DeltaWorldY">需移动的物理 Y 偏移量 (mm)</param>
    /// <param name="PixelDeltaX">图像像素 X 偏移 (px)</param>
    /// <param name="PixelDeltaY">图像像素 Y 偏移 (px)</param>
    public record VisualAlignmentResult(
        double TargetWorldX,
        double TargetWorldY,
        double DeltaWorldX,
        double DeltaWorldY,
        double PixelDeltaX,
        double PixelDeltaY);

    /// <summary>
    /// 视觉引导与拖动对位计算器（Visual Guidance &amp; Drag Aligner）。
    /// 工业点胶、贴片、晶圆对准、AOI 及标定示教常用核心算法：
    /// 1. 点击对准（Click-to-Center / Click-to-Align）：操作员在图像视口中点击感兴趣特征点，
    ///    系统将该特征点相对相机光学中心（或示教十字准星）的像素偏差，通过像素-&gt;世界变换的线性部分
    ///    （<see cref="Transform2D.TransformVector"/>）转换为物理轴偏移量 (ΔX, ΔY)，
    ///    驱动平台快速把目标移入准星中心。
    /// 2. 拖拽微调（Drag-to-Move）：操作员按住鼠标在图像上拖动画出微调矢量，
    ///    系统将拖拽像素位移等比映射为物理轴位移，实现所见即所得的直观微调。
    /// 3. 像素转绝对世界坐标（Pixel-to-World Move）：将视口任意像素直接反算为绝对物理位置。
    ///
    /// 坐标系来源支持三个层次（结果完全一致，按需选用）：
    ///   1) <see cref="NinePointCalibration"/>：直接给出 T_{World←Pixel}（向后兼容的经典入口）；
    ///   2) <see cref="Transform2D"/>：任一显式构造的像素-&gt;世界齐次变换；
    ///   3) <see cref="CoordinateFrameTree"/>：由坐标系树查找 PixelFrame -&gt; WorldFrame 的复合变换，
    ///      支持 Pixel -> Camera -> Tool -> World 多跳链路（如手眼标定 + 工具标定分别注册的场景）。
    /// </summary>
    public static class VisualGuidanceAligner
    {
        /// <summary>
        /// 计算点击对齐位移（使图像上的点击点对齐到相机视口十字准星中心）。
        /// </summary>
        /// <param name="calibration">九点仿射标定矩阵</param>
        /// <param name="clickedPixelX">鼠标点击的像素 X 坐标 (px)</param>
        /// <param name="clickedPixelY">鼠标点击的像素 Y 坐标 (px)</param>
        /// <param name="reticlePixelX">十字准星目标像素 X 坐标（通常为图像中心 Width/2）</param>
        /// <param name="reticlePixelY">十字准星目标像素 Y 坐标（通常为图像中心 Height/2）</param>
        /// <param name="currentWorldX">当前平台物理 X 坐标 (mm)</param>
        /// <param name="currentWorldY">当前平台物理 Y 坐标 (mm)</param>
        /// <param name="mountMode">相机安装模式（Eye-in-Hand 随动相机 / Eye-to-Hand 固定相机）</param>
        public static VisualAlignmentResult CalculateClickToAlign(
            NinePointCalibration calibration,
            double clickedPixelX,
            double clickedPixelY,
            double reticlePixelX,
            double reticlePixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            return CalculateClickToAlign(
                RequirePixelToWorld(calibration),
                clickedPixelX, clickedPixelY,
                reticlePixelX, reticlePixelY,
                currentWorldX, currentWorldY,
                mountMode);
        }

        /// <summary>
        /// 计算点击对齐位移（显式传入像素-&gt;世界齐次变换 T_{World←Pixel}）。
        /// </summary>
        public static VisualAlignmentResult CalculateClickToAlign(
            Transform2D pixelToWorld,
            double clickedPixelX,
            double clickedPixelY,
            double reticlePixelX,
            double reticlePixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            RequireInvertible(pixelToWorld, nameof(pixelToWorld));

            // 计算像素偏差矢量（从十字准星指向目标点击点）
            double dpx = clickedPixelX - reticlePixelX;
            double dpy = clickedPixelY - reticlePixelY;

            // 根据手眼相机安装模式确定移动方向
            dpx = ApplyMountModeSign(dpx, mountMode);
            dpy = ApplyMountModeSign(dpy, mountMode);

            // 像素 -> 世界变换的线性部分（忽略平移常数项），将像素位移转换为物理位移：
            // [ ΔX ]   [ r11  r12 ] [ dpx ]
            // [ ΔY ] = [ r21  r22 ] [ dpy ]
            var (deltaWorldX, deltaWorldY) = pixelToWorld.TransformVector(dpx, dpy);

            double targetWorldX = currentWorldX + deltaWorldX;
            double targetWorldY = currentWorldY + deltaWorldY;

            return new VisualAlignmentResult(
                TargetWorldX: targetWorldX,
                TargetWorldY: targetWorldY,
                DeltaWorldX: deltaWorldX,
                DeltaWorldY: deltaWorldY,
                PixelDeltaX: dpx,
                PixelDeltaY: dpy);
        }

        /// <summary>
        /// 计算点击对齐位移（由坐标系树查找 PixelFrame -&gt; WorldFrame 的复合变换）。
        /// 适用于坐标系树中注册了 Pixel -> Camera -> Tool -> World 多跳链路的场景。
        /// </summary>
        /// <exception cref="InvalidOperationException">坐标系树中 PixelFrame 与 WorldFrame 不连通。</exception>
        public static VisualAlignmentResult CalculateClickToAlign(
            CoordinateFrameTree frameTree,
            double clickedPixelX,
            double clickedPixelY,
            double reticlePixelX,
            double reticlePixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            if (frameTree == null) throw new ArgumentNullException(nameof(frameTree));

            return CalculateClickToAlign(
                frameTree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame),
                clickedPixelX, clickedPixelY,
                reticlePixelX, reticlePixelY,
                currentWorldX, currentWorldY,
                mountMode);
        }

        /// <summary>
        /// 计算鼠标拖拽微调的物理位移量。
        /// 操作员在视口中从 (startPx, startPy) 拖动到 (endPx, endPy)。
        /// </summary>
        /// <param name="calibration">九点仿射标定矩阵</param>
        /// <param name="dragDeltaPixelX">拖拽像素位移 X = endX - startX</param>
        /// <param name="dragDeltaPixelY">拖拽像素位移 Y = endY - startY</param>
        /// <param name="currentWorldX">当前平台物理 X 坐标 (mm)</param>
        /// <param name="currentWorldY">当前平台物理 Y 坐标 (mm)</param>
        /// <param name="mountMode">相机安装模式</param>
        public static VisualAlignmentResult CalculateDragMove(
            NinePointCalibration calibration,
            double dragDeltaPixelX,
            double dragDeltaPixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            return CalculateDragMove(
                RequirePixelToWorld(calibration),
                dragDeltaPixelX, dragDeltaPixelY,
                currentWorldX, currentWorldY,
                mountMode);
        }

        /// <summary>
        /// 计算鼠标拖拽微调的物理位移量（显式传入像素-&gt;世界齐次变换 T_{World←Pixel}）。
        /// </summary>
        public static VisualAlignmentResult CalculateDragMove(
            Transform2D pixelToWorld,
            double dragDeltaPixelX,
            double dragDeltaPixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            RequireInvertible(pixelToWorld, nameof(pixelToWorld));

            double dpx = ApplyMountModeSign(dragDeltaPixelX, mountMode);
            double dpy = ApplyMountModeSign(dragDeltaPixelY, mountMode);

            var (deltaWorldX, deltaWorldY) = pixelToWorld.TransformVector(dpx, dpy);

            return new VisualAlignmentResult(
                TargetWorldX: currentWorldX + deltaWorldX,
                TargetWorldY: currentWorldY + deltaWorldY,
                DeltaWorldX: deltaWorldX,
                DeltaWorldY: deltaWorldY,
                PixelDeltaX: dpx,
                PixelDeltaY: dpy);
        }

        /// <summary>
        /// 计算鼠标拖拽微调的物理位移量（由坐标系树查找 PixelFrame -&gt; WorldFrame 的复合变换）。
        /// </summary>
        /// <exception cref="InvalidOperationException">坐标系树中 PixelFrame 与 WorldFrame 不连通。</exception>
        public static VisualAlignmentResult CalculateDragMove(
            CoordinateFrameTree frameTree,
            double dragDeltaPixelX,
            double dragDeltaPixelY,
            double currentWorldX,
            double currentWorldY,
            CameraMountMode mountMode = CameraMountMode.EyeInHand)
        {
            if (frameTree == null) throw new ArgumentNullException(nameof(frameTree));

            return CalculateDragMove(
                frameTree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame),
                dragDeltaPixelX, dragDeltaPixelY,
                currentWorldX, currentWorldY,
                mountMode);
        }

        /// <summary>
        /// 点击像素点绝对定位（用于静止相机或大视场全景相机下，直接将像素映射到工作台物理目标位置）。
        /// </summary>
        public static (double TargetWorldX, double TargetWorldY) CalculateAbsolutePosition(
            NinePointCalibration calibration,
            double pixelX,
            double pixelY)
        {
            if (calibration == null) throw new ArgumentNullException(nameof(calibration));
            return calibration.Transform(pixelX, pixelY);
        }

        /// <summary>
        /// 点击像素点绝对定位（显式传入像素-&gt;世界齐次变换 T_{World←Pixel}）。
        /// </summary>
        public static (double TargetWorldX, double TargetWorldY) CalculateAbsolutePosition(
            Transform2D pixelToWorld,
            double pixelX,
            double pixelY)
        {
            RequireInvertible(pixelToWorld, nameof(pixelToWorld));
            var (x, y) = pixelToWorld.Transform(pixelX, pixelY);
            return (x, y);
        }

        /// <summary>
        /// 点击像素点绝对定位（由坐标系树查找 PixelFrame -&gt; WorldFrame 的复合变换）。
        /// </summary>
        /// <exception cref="InvalidOperationException">坐标系树中 PixelFrame 与 WorldFrame 不连通。</exception>
        public static (double TargetWorldX, double TargetWorldY) CalculateAbsolutePosition(
            CoordinateFrameTree frameTree,
            double pixelX,
            double pixelY)
        {
            if (frameTree == null) throw new ArgumentNullException(nameof(frameTree));
            return CalculateAbsolutePosition(
                frameTree.GetTransform(CoordinateFrame.PixelFrame, CoordinateFrame.WorldFrame),
                pixelX, pixelY);
        }

        /// <summary>
        /// 从九点标定提取 T_{World←Pixel}（保留 null 参数校验语义）。
        /// </summary>
        private static Transform2D RequirePixelToWorld(NinePointCalibration calibration)
        {
            if (calibration == null) throw new ArgumentNullException(nameof(calibration));
            return calibration.ToTransform2D();
        }

        /// <summary>
        /// 固定相机（Eye-to-Hand）模式下，工件移动方向与像素偏差方向相反。
        /// </summary>
        private static double ApplyMountModeSign(double delta, CameraMountMode mountMode) =>
            mountMode == CameraMountMode.EyeToHand ? -delta : delta;

        /// <summary>
        /// 校验像素-&gt;世界变换线性部分可逆（奇异变换会把所有位移增量静默压成 0）。
        /// </summary>
        private static void RequireInvertible(Transform2D pixelToWorld, string paramName)
        {
            if (!pixelToWorld.IsInvertible)
            {
                throw new ArgumentException("像素->世界变换线性部分奇异（行列式接近0），无法用于对位计算", paramName);
            }
        }
    }
}
