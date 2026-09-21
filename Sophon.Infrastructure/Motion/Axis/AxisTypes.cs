namespace Sophon.Infrastructure.Motion.Axis
{
    /// <summary>
    /// 轴运行状态枚举（严格对标 PLCopen Motion Control Part 1 标准八态规范）。
    /// 状态机迁移受严格工业语义约束：
    /// 1. 任何时刻轴处于且仅处于一个定义状态；
    /// 2. Stopping 状态期间闭锁，拒绝接收任何新运动指令；
    /// 3. ErrorStop 为最高故障优先级，仅接受 Reset 指令复位返回 Standstill。
    /// </summary>
    public enum AxisState
    {
        /// <summary>未使能 (PLCopen: Disabled)</summary>
        Disabled = 0,

        /// <summary>已使能、静止就绪 (PLCopen: Standstill)</summary>
        Standstill = 1,
        /// <summary>兼容别名：空闲就绪</summary>
        Idle = 1,

        /// <summary>回零运动中 (PLCopen: Homing)</summary>
        Homing = 2,

        /// <summary>离散定位运动中 (PLCopen: DiscreteMotion / 绝对或相对定位)</summary>
        DiscreteMotion = 3,
        /// <summary>兼容别名：定位运动中</summary>
        Moving = 3,

        /// <summary>连续运动中 (PLCopen: ContinuousMotion / 点动Jog或速度控制)</summary>
        ContinuousMotion = 4,
        /// <summary>兼容别名：点动运行中</summary>
        Jogging = 4,

        /// <summary>同步/多轴联动运动中 (PLCopen: SynchronizedMotion / 轴组成员或从轴)</summary>
        SynchronizedMotion = 5,

        /// <summary>受控停止锁定中 (PLCopen: Stopping / 执行 MC_Stop 减速中，此状态拒绝一切新运动请求)</summary>
        Stopping = 6,

        /// <summary>故障停止锁定中 (PLCopen: ErrorStop / 需执行 MC_Reset 复位)</summary>
        ErrorStop = 7,
        /// <summary>兼容别名：故障错误</summary>
        Error = 7
    }

    /// <summary>
    /// 轴状态快照（不可变只读记录，保障线程安全）。
    /// </summary>
    /// <param name="AxisId">轴编号</param>
    /// <param name="Position">当前物理位置</param>
    /// <param name="Velocity">当前物理速度</param>
    /// <param name="Enabled">是否已使能</param>
    /// <param name="Homed">是否已回零</param>
    /// <param name="State">轴状态机状态</param>
    public record AxisSnapshot(
        int AxisId,
        double Position,
        double Velocity,
        bool Enabled,
        bool Homed,
        AxisState State);

    /// <summary>
    /// 全局限位报警参数（供 Wave3-F 报警中心消费）。
    /// </summary>
    /// <param name="AxisId">触发轴 ID</param>
    /// <param name="LimitPointName">限位点名</param>
    /// <param name="IsPositiveDirection">是否正向</param>
    /// <param name="IsHardLimit">true 为硬限位，false 为软限位</param>
    public record GlobalLimitAlarmArgs(
        int AxisId,
        string LimitPointName,
        bool IsPositiveDirection,
        bool IsHardLimit);
}
