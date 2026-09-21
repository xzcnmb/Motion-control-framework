using System;

namespace Sophon.Vision.Coordinates
{
    /// <summary>
    /// 视觉系统标准坐标系（Coordinate Frames）。
    ///
    /// 工业视觉中一条典型的坐标链为：
    /// 像素坐标系 (Pixel) -&gt; 相机坐标系 (Camera) -&gt; 工具/TCP 坐标系 (Tool) -&gt; 机器世界坐标系 (World)。
    /// 相邻坐标系之间的 2D 齐次变换由 <see cref="Transform2D"/> 描述，
    /// 并由 <see cref="CoordinateFrameTree"/> 统一注册、查找与复合。
    /// </summary>
    public enum CoordinateFrame
    {
        /// <summary>
        /// 图像像素坐标系：原点在图像左上角，u 轴向右，v 轴向下，单位为像素 (px)。
        /// 视觉算法（blob 检测、模板匹配、九点标定输入）直接输出的坐标空间。
        /// </summary>
        PixelFrame = 0,

        /// <summary>
        /// 相机光心坐标系：原点位于相机光学中心（光轴与成像基准交点），
        /// 采用光学惯例（成像面 X 右、Y 下，随安装姿态可差一个旋转），单位为毫米 (mm)。
        /// 九点标定矩阵的线性部分即包含 像素-&gt;相机 的缩放与旋转分量。
        /// </summary>
        CameraFrame = 1,

        /// <summary>
        /// 工具坐标系（TCP，Tool Center Point）：原点位于执行末端有效工作点，
        /// 例如点胶针头尖端、吸嘴/夹爪中心，随运动轴末端一起移动，单位为毫米 (mm)。
        /// </summary>
        ToolFrame = 2,

        /// <summary>
        /// 机器世界坐标系：运动平台/机台的绝对物理坐标系（标准右手笛卡尔坐标系），
        /// 单位为毫米 (mm)。运动控制指令（绝对定位）即在该坐标系下给出。
        /// </summary>
        WorldFrame = 3
    }

    /// <summary>
    /// 坐标系的描述信息（轴向约定、单位），供日志、UI 与异常信息展示使用。
    /// </summary>
    public static class CoordinateFrameInfo
    {
        /// <summary>
        /// 获取坐标系的单位字符串（px / mm）。
        /// </summary>
        public static string GetUnit(CoordinateFrame frame) => frame switch
        {
            CoordinateFrame.PixelFrame => "px",
            CoordinateFrame.CameraFrame => "mm",
            CoordinateFrame.ToolFrame => "mm",
            CoordinateFrame.WorldFrame => "mm",
            _ => "?"
        };

        /// <summary>
        /// 获取坐标系的中文描述（原点与轴向约定）。
        /// </summary>
        public static string GetDescription(CoordinateFrame frame) => frame switch
        {
            CoordinateFrame.PixelFrame => "图像像素坐标系（左上角原点，u右v下，px）",
            CoordinateFrame.CameraFrame => "相机光心坐标系（光学中心原点，mm）",
            CoordinateFrame.ToolFrame => "工具/TCP坐标系（针尖/吸嘴原点，mm）",
            CoordinateFrame.WorldFrame => "机器世界坐标系（机台绝对物理坐标，mm）",
            _ => "未知坐标系"
        };
    }
}
