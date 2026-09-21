namespace Sophon.Contracts
{
    /// <summary>
    /// 相机 / 图像采集配置（平台无关）。
    /// 海康 MVS 等真实相机与仿真图源都对此模型编程；采集后端由 <see cref="Vendor"/> 决定。
    /// </summary>
    public class CameraConfig
    {
        /// <summary>配置名称（多相机切换、界面显示用）。</summary>
        public string Name { get; set; } = "相机1";

        /// <summary>采集后端厂商。</summary>
        public CameraVendor Vendor { get; set; } = CameraVendor.Simulated;

        /// <summary>
        /// 设备标识：
        /// 海康 MVS 用序列号或用户自定义名（UserDefinedName）或枚举索引；
        /// 仿真图源用图片目录路径。
        /// </summary>
        public string DeviceKey { get; set; } = string.Empty;

        /// <summary>触发模式。</summary>
        public CameraTriggerMode TriggerMode { get; set; } = CameraTriggerMode.Software;

        /// <summary>曝光时间（微秒）。0 表示不设置、沿用相机当前值。</summary>
        public double ExposureUs { get; set; }

        /// <summary>增益（dB）。0 表示不设置。</summary>
        public double Gain { get; set; }

        /// <summary>
        /// GigE 网口相机是否自动优化包大小（GevSCPSPacketSize）。USB 相机忽略。
        /// </summary>
        public bool OptimizePacketSize { get; set; } = true;

        /// <summary>取一帧的超时（毫秒），主动取帧模式用。</summary>
        public int GrabTimeoutMs { get; set; } = 1000;

        /// <summary>
        /// 是否翻转 Y 轴（部分相机坐标系与机械坐标系 Y 方向相反，标定前统一）。
        /// </summary>
        public bool FlipY { get; set; }
    }
}
