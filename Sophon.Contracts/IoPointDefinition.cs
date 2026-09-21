using System;

namespace Sophon.Contracts
{
    /// <summary>
    /// 数字量 IO 点位定义（平台无关的工业点位配置模型）。
    /// 承载工业三层点位映射：物理硬件层（卡号+物理位） → 配置映射层（极性反转+常开常闭+滤波） → 逻辑应用层（工艺点名）。
    /// </summary>
    public class IoPointDefinition
    {
        /// <summary>逻辑点名（应用层唯一标识，如 "DI_AIR_OK", "DO_CYL1_EXTEND"）。</summary>
        public string LogicalName { get; set; } = string.Empty;

        /// <summary>IO 方向：DI（输入）或 DO（输出）。</summary>
        public IoDirection Direction { get; set; } = IoDirection.DI;

        /// <summary>硬件卡号（板卡编号/主板序号，从 0 开始）。</summary>
        public int CardNo { get; set; }

        /// <summary>物理通道位号（卡载或模块端口通道，如 0~31）。</summary>
        public int ChannelBit { get; set; }

        /// <summary>电平极性硬件反转：true 表示对底层物理电平取反。</summary>
        public bool Invert { get; set; }

        /// <summary>开关电气常态类型（常开 NO / 常闭 NC）。常闭开关动作时读取为 0，经映射后转为逻辑有效 1。</summary>
        public SwitchType Switch { get; set; } = SwitchType.NormallyOpen;

        /// <summary>防抖滤波时间（毫秒，0 表示不滤波）。针对机械接点抖动做时间窗滤波。</summary>
        public int FilterMs { get; set; } = 20;

        /// <summary>业务功能分类（如 "气缸机构", "安全防护", "硬件限位", "供料传感器", "系统指示"）。</summary>
        public string Category { get; set; } = "通用";

        /// <summary>点位中文功能描述（如 "工站1顶升气缸伸出到位磁性开关"）。</summary>
        public string Description { get; set; } = string.Empty;
    }
}
