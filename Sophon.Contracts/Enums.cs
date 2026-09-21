namespace Sophon.Contracts
{
    /// <summary>
    /// 驱动类型。由配置决定加载哪套硬件实现；Simulated 为内置虚拟控制器。
    /// </summary>
    public enum DriverKind
    {
        Simulated,
        GoogolGts,
        LeadShineDmc,
        ZmotionZmc,
    }

    /// <summary>
    /// 加减速参数语义。不同平台差异：固高/正运动用“加速度值”(unit/s²)，雷赛 DMC 传统脉冲卡用“加速时间”(s)。
    /// 上层 AxisDefinition 统一存加速度值；适配层按此语义换算。
    /// </summary>
    public enum AccelParamKind
    {
        /// <summary>加速度值（unit/s²）——固高 GTS、正运动 ZMC。</summary>
        AccelerationValue,
        /// <summary>加速时间（s）——雷赛 DMC 传统脉冲卡（Tacc/Tdec）。</summary>
        AccelerationTime,
    }

    /// <summary>
    /// 相机厂商 / 采集后端。Simulated 为内置仿真（目录/内存图源）。
    /// </summary>
    public enum CameraVendor
    {
        /// <summary>仿真图源（目录轮播 / 内存注入），无需任何相机 SDK。</summary>
        Simulated,
        /// <summary>海康机器视觉 MVS（MvCamCtrl.NET）。</summary>
        HikvisionMvs,
        /// <summary>大华 / 华睿等其他 GenICam 兼容后端（预留）。</summary>
        GenICam,
    }

    /// <summary>
    /// 相机触发模式。
    /// </summary>
    public enum CameraTriggerMode
    {
        /// <summary>连续采集（自由运行）。</summary>
        Continuous,
        /// <summary>软触发（软件命令取一帧）。</summary>
        Software,
        /// <summary>硬触发（外部 IO 信号）。</summary>
        Hardware,
    }

    /// <summary>
    /// 电磁阀类型。决定气缸/夹爪的输出与断电行为。
    /// </summary>
    public enum ValveType
    {
        /// <summary>单电控（一位五通/弹簧复位）：单 DO，得电动作、失电弹簧复位。</summary>
        SingleCoil,
        /// <summary>双电控（二位五通/双线圈）：双 DO，脉冲换向、断电保位。严禁两线圈同时得电。</summary>
        DoubleCoil,
    }

    /// <summary>
    /// 气缸/夹爪位置状态。
    /// </summary>
    public enum CylinderPosition
    {
        /// <summary>位置未知（两到位信号都无效或都有效，异常）。</summary>
        Unknown,
        /// <summary>已缩回/夹紧到位。</summary>
        Home,
        /// <summary>已伸出/张开到位。</summary>
        Work,
    }

    /// <summary>
    /// 外设通讯传输方式。
    /// </summary>
    public enum DeviceTransport
    {
        /// <summary>Modbus RTU over RS485/RS232 串口。</summary>
        ModbusRtu,
        /// <summary>Modbus TCP over 以太网。</summary>
        ModbusTcp,
        /// <summary>裸串口 ASCII 文本协议（扫码枪等）。</summary>
        SerialAscii,
    }

    /// <summary>
    /// Modbus 寄存器区（契约层自含，避免依赖 Infrastructure 的同名枚举，由设备层映射）。
    /// </summary>
    public enum ModbusArea
    {
        Coil,
        DiscreteInput,
        InputRegister,
        HoldingRegister,
    }

    /// <summary>
    /// 寄存器点位数据类型（解析用）。
    /// </summary>
    public enum RegisterDataType
    {
        /// <summary>16 位无符号。</summary>
        UInt16,
        /// <summary>16 位有符号。</summary>
        Int16,
        /// <summary>32 位无符号（2 寄存器）。</summary>
        UInt32,
        /// <summary>32 位有符号（2 寄存器）。</summary>
        Int32,
        /// <summary>32 位浮点（2 寄存器，IEEE-754）。</summary>
        Float32,
        /// <summary>布尔（线圈/离散输入）。</summary>
        Bool,
    }

    /// <summary>
    /// 数字 IO 方向。
    /// </summary>
    public enum IoDirection
    {
        /// <summary>数字量输入（传感器、磁性开关、按钮、报警输入）。</summary>
        DI,
        /// <summary>数字量输出（电磁阀、继电器、指示灯、蜂鸣器）。</summary>
        DO,
    }

    /// <summary>
    /// 开关常态极性类型。
    /// </summary>
    public enum SwitchType
    {
        /// <summary>常开（Normally Open，NO）：未触发时断开(0)，触发时导通(1)。</summary>
        NormallyOpen,
        /// <summary>常闭（Normally Closed，NC）：未触发时导通(1)，触发或断线时断开(0)。安全回路推荐。</summary>
        NormallyClose,
    }

    /// <summary>
    /// 控制器连接状态（连接状态机属于驱动接口成员）。
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Ready,
        Fault,
        Reconnecting,
    }

    /// <summary>
    /// 控制器能力位。面向功能特性 (Feature/Capability) 查询，彻底解耦厂商差异泄漏。
    /// 业务层与协调器只针对能力标志位判定，不针对厂商名称做分支判断。
    /// </summary>
    [System.Flags]
    public enum MotionCapability
    {
        None = 0,
        /// <summary>支持整段缓冲下发（卡内连续插补）</summary>
        BufferedSegments = 1,
        /// <summary>支持上位机周期插补（仅 Sim/离线，真卡一般不启用）</summary>
        HostInterpolation = 2,
        /// <summary>卡内硬件限位输入（微秒级卡内快速急停）</summary>
        HardLimitInput = 4,
        /// <summary>支持 PVT 型值点流式下发</summary>
        PvTStreaming = 8,
        /// <summary>支持高速硬件位置比较输出 (HW CMP / 飞拍)</summary>
        HardwareCompareOutput = 16,
        /// <summary>支持机械反向间隙补偿 (Backlash Compensation)</summary>
        BacklashCompensation = 32,
        /// <summary>支持连续轨迹前瞻与平滑过渡 (Continuous Velocity Blending)</summary>
        ContinuousVelocityBlending = 64,
        /// <summary>加减速参数基于时间 (Acceleration Time / 秒) 而非加速度物理量</summary>
        TimeBasedAcceleration = 128,
        /// <summary>支持卡载专用硬件急停信号输入 (EMG Input)</summary>
        HardwareEStopInput = 256,
    }

    /// <summary>
    /// PLCopen 标准命令完成状态语义。
    /// 互斥区分：成功达成(Done)、被叫停/抢占(CommandAborted)、报错/限位(Error)。
    /// </summary>
    public enum CommandCompletionStatus
    {
        /// <summary>命令成功执行到位并静止进入 Standstill (PLCopen: Done)</summary>
        Done,
        /// <summary>命令被中止（被 MC_Stop / MC_Halt 停下，或被后续指令抢占，或被取消）(PLCopen: CommandAborted)</summary>
        CommandAborted,
        /// <summary>执行过程中发生错误或报警，轴转入 ErrorStop (PLCopen: Error)</summary>
        Error,
    }

    /// <summary>
    /// 运动控制命令类型（PLCopen 体系）。
    /// </summary>
    public enum MotionCommandType
    {
        MoveAbsolute,
        MoveRelative,
        MoveVelocity,
        Home,
        Halt,
        Stop,
        EmergencyStop,
    }

    /// <summary>
    /// PLCopen 连续运动缓冲模式。
    /// </summary>
    public enum CommandBufferMode
    {
        /// <summary>立即打断前一命令（前一命令置 CommandAborted）</summary>
        Aborting,
        /// <summary>在前一命令完成后才开始执行（前一命令正常达成置 Done）</summary>
        Buffered,
        /// <summary>连续平滑过渡（低速穿过）</summary>
        BlendingLow,
        /// <summary>连续平滑过渡（高速穿过）</summary>
        BlendingHigh,
    }

    /// <summary>
    /// 命令生命周期状态。
    /// </summary>
    public enum CommandLifecycleState
    {
        Created,
        Queued,
        Executing,
        Completed,
        Aborted,
        Faulted,
    }

    /// <summary>
    /// 工业停止分级类别（对标 EN 60204-1 停止类别与 PLCopen 规范）。
    /// </summary>
    public enum StopCategoryLevel
    {
        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)
        /// 正常平稳按减速度减速到 0，轴保持使能状态 (Standstill)，不脱离轨迹坐标，解除后可直接继续运行。
        /// </summary>
        SoftwareHalt,

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)
        /// 触发底层卡级受控减速停，轴转入 Stopping 状态并闭锁，期间拒绝一切新运动指令，直到停稳并复位。
        /// </summary>
        CardControlledStop,

        /// <summary>
        /// Level 3: 硬件安全急停 (Emergency Stop / Hard Abort / STO, Stop Cat 0/1)
        /// 立即切断脉冲或触发 STO 硬件安全力矩关断，伺服下使能或抱闸，轴强制转入 ErrorStop，必须显式 Reset 才能恢复。
        /// </summary>
        HardwareEmergencyStop,
    }

    /// <summary>
    /// 轴组 (AxisGroup) 运行状态机（对标 PLCopen Part 4 协调运动规范）。
    /// </summary>
    public enum AxisGroupState
    {
        GroupDisabled,
        GroupStandby,
        GroupMoving,
        GroupStopping,
        GroupErrorStop,
    }

    /// <summary>
    /// 回零方式。
    /// </summary>
    public enum HomingMode
    {
        /// <summary>限位开关回零</summary>
        LimitSwitch,
        /// <summary>原点信号回零</summary>
        OriginSignal,
        /// <summary>Z 相回零</summary>
        ZPhase,
        /// <summary>当前位置定零</summary>
        CurrentPosition,
    }

    /// <summary>
    /// 回零方向。
    /// </summary>
    public enum HomeDirection
    {
        Positive,
        Negative,
    }
}