namespace Sophon.Contracts
{
    /// <summary>
    /// 轴定义：物理单位（mm/deg）+ 传动参数。脉冲换算（PulsePerUnit 等）只在驱动适配器内部使用。
    /// </summary>
    public class AxisDefinition
    {
        /// <summary>轴编号（卡内索引，0 起）。</summary>
        public int AxisId { get; set; }

        /// <summary>轴名称（界面显示用）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>单位："mm" 或 "deg"。</summary>
        public string Unit { get; set; } = "mm";

        /// <summary>脉冲当量：脉冲/物理单位（含丝杆导程与减速比）。</summary>
        public double PulsePerUnit { get; set; } = 1000;

        /// <summary>方向取反。</summary>
        public bool DirectionInvert { get; set; }

        // ---------- 限位（限位安全铁律：软限位由规划器执行提前减速；硬限位优先卡内硬件处理） ----------
        public bool SoftLimitEnabled { get; set; } = true;
        public double SoftLimitMin { get; set; }
        public double SoftLimitMax { get; set; } = 100;
        public bool HardLimitEnabled { get; set; }
        /// <summary>正方向硬限位 IO 点名（IIoController 虚拟点名）。</summary>
        public string? LimitPositiveIoName { get; set; }
        /// <summary>负方向硬限位 IO 点名。</summary>
        public string? LimitNegativeIoName { get; set; }
        /// <summary>原点信号 IO 点名。</summary>
        public string? HomeIoName { get; set; }

        // ---------- 动力学约束 ----------
        public double MaxSpeed { get; set; } = 100;
        public double MaxAccel { get; set; } = 500;
        public double MaxDecel { get; set; } = 500;
        public double MaxJerk { get; set; } = 2000;

        // ---------- 回零配置 ----------
        public HomingMode HomeMode { get; set; } = HomingMode.LimitSwitch;
        public HomeDirection HomeDir { get; set; } = HomeDirection.Negative;
        public double HomeSpeed { get; set; } = 10;
    }
}