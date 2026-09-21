using System.Collections.Generic;

namespace Sophon.Contracts
{
    /// <summary>
    /// 气缸 / 夹爪设备定义（平台无关）。
    /// 通过 IIoController 的虚拟点名驱动电磁阀 DO 与到位磁性开关 DI，
    /// 承载现场可靠性五道防线：双电控互锁 + 脉冲输出 + 双到位确认 + 超时保护 + 初始状态。
    /// </summary>
    public class CylinderDefinition
    {
        /// <summary>设备名称（如 "夹爪1" / "顶升气缸"）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>电磁阀类型。</summary>
        public ValveType Valve { get; set; } = ValveType.DoubleCoil;

        /// <summary>
        /// 工作方向（伸出/张开）DO 点名。
        /// 单电控：得电=Work，失电=Home；双电控：脉冲触发换向到 Work。
        /// </summary>
        public string WorkDoName { get; set; } = string.Empty;

        /// <summary>
        /// 复位方向（缩回/夹紧）DO 点名。
        /// 单电控可留空（失电即复位）；双电控必填。
        /// </summary>
        public string? HomeDoName { get; set; }

        /// <summary>工作到位（伸出/张开到位）磁性开关 DI 点名。可选但强烈建议。</summary>
        public string? WorkSensorDiName { get; set; }

        /// <summary>复位到位（缩回/夹紧到位）磁性开关 DI 点名。可选但强烈建议。</summary>
        public string? HomeSensorDiName { get; set; }

        /// <summary>
        /// 双电控脉冲宽度（ms）。双线圈阀只需 150~250ms 脉冲，禁止长期 ON（否则烧线圈）。
        /// 单电控忽略（持续保持电平）。
        /// </summary>
        public int PulseWidthMs { get; set; } = 200;

        /// <summary>
        /// 到位确认超时（ms）。发出指令后在此时间内未收到目标到位 DI 即判超时报警。
        /// 无到位传感器时退化为纯延时（等待 PulseWidthMs 后视为完成，但会给出无反馈警告）。
        /// </summary>
        public int ConfirmTimeoutMs { get; set; } = 1500;

        /// <summary>
        /// 互锁组名。同组内的气缸不允许同时处于 Work（空间/时序互锁），留空表示无互锁。
        /// </summary>
        public string? InterlockGroup { get; set; }

        /// <summary>上电初始动作：true=上电复位到 Home。</summary>
        public bool ResetToHomeOnStart { get; set; } = true;

        /// <summary>启动前置条件所需的 DI 点名（如安全门、气压开关），全部为 true 才允许动作。可空。</summary>
        public List<string> EnableConditionDiNames { get; set; } = new();
    }
}
