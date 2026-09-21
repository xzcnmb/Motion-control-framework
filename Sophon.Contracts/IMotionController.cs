using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Contracts
{
    /// <summary>
    /// 统一运动控制器接口（轴运动语义，不含 IO）。
    /// 所有位置/速度一律使用物理单位（mm/deg），脉冲换算只在驱动适配器内部。
    /// 上位机不得用轮询 GetPosition 判定到位，一律订阅 AxisDone。
    /// </summary>
    public interface IMotionController : IDisposable
    {
        /// <summary>驱动类型标识（不得在运行期改变）。</summary>
        DriverKind Kind { get; }

        /// <summary>连接状态（连接状态机是接口契约的一部分）。</summary>
        ConnectionState State { get; }

        /// <summary>连接状态变化事件。</summary>
        event Action<ConnectionState>? StateChanged;

        /// <summary>能力位：BufferedSegments/HostInterpolation/PvTStreaming/HardLimitInput。</summary>
        MotionCapability Capabilities { get; }

        /// <summary>轴配置快照（只读）。</summary>
        IReadOnlyList<AxisDefinition> Axes { get; }

        /// <summary>建立连接；返回时连接状态为 Ready 或抛出异常。</summary>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>断开连接。</summary>
        Task DisconnectAsync();

        // ---------- 轴使能与状态 ----------
        void EnableAxis(int axisId);
        void DisableAxis(int axisId);
        bool IsAxisEnabled(int axisId);
        bool IsAxisHomed(int axisId);
        double GetPosition(int axisId);
        double GetVelocity(int axisId);

        // ---------- 运动命令（返回 requestId，完成以 AxisDone 事件回报） ----------
        /// <summary>点动。dir=+1/-1。</summary>
        Guid Jog(int axisId, int dir, double speed);

        /// <summary>绝对定位。</summary>
        Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0);

        /// <summary>
        /// 绝对定位（异步完成回报版）。返回的任务在本次运动结束时完成：
        /// 快速失败（未连接/未使能/超软限位）也会以 Success=false 的结果完成，
        /// **不存在"AxisDone 事件先于订阅"的竞态**，流程/示教等业务请优先使用本 API。
        /// </summary>
        Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default);

        /// <summary>相对定位。</summary>
        Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0);

        /// <summary>单轴回零。完成后 AxisDone 回报，且 IsAxisHomed 置位。</summary>
        Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed);

        /// <summary>
        /// 单轴回零（异步完成回报版）。语义同 <see cref="MoveAbsAsync"/>：无订阅竞态。
        /// </summary>
        Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default);

        // ---------- 停止与安全分层语义 (对标 EN 60204-1 Stop Category 2/1/0 & PLCopen) ----------
        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)。
        /// 正常平稳按设定减速度减速至 0，轴保持使能状态 (Standstill)，不脱离轨迹坐标，解除后可直接继续运行。
        /// </summary>
        void Halt(int axisId);

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)。
        /// 触发底层卡级受控减速停，轴转入 Stopping 状态并闭锁，期间拒绝接收新运动指令，直到轴停稳并复位。
        /// </summary>
        void Stop(int axisId);

        /// <summary>
        /// 兼容保留：按 profile 减速停止（等同于 Level 2 Stop）。
        /// </summary>
        void StopMotion(int axisId);

        /// <summary>
        /// Level 3: 硬件安全急停 / STO 安全力矩关断 (MC_EmergencyStop / Hard Abort, Stop Cat 0/1)。
        /// 立即切断脉冲输出或触发驱动器 STO，伺服下使能或抱闸，轴强制转入 ErrorStop，必须显式 Reset 才能恢复。
        /// </summary>
        void EmergencyStop(int axisId);

        /// <summary>
        /// 兼容保留：单轴急停。
        /// </summary>
        void Abort(int axisId, double decelRatio = 0);

        /// <summary>
        /// 最高优先级全局急停：旁路一切逻辑，立即切断全部轴（急停按钮/安全门/硬限位联动使用）。
        /// </summary>
        void EmergencyStopAll();

        /// <summary>
        /// 兼容保留：全部轴急停。
        /// </summary>
        void AbortAll();

        /// <summary>
        /// 故障复位指令 (MC_Reset)：将处于 ErrorStop 状态的轴复位回到 Standstill（若使能有效）或 Disabled。
        /// </summary>
        void ResetAxis(int axisId);

        // ---------- 事件 ----------
        event Action<AxisDoneArgs>? AxisDone;
        event Action<AxisFaultArgs>? AxisFault;
        event Action<LimitTriggeredArgs>? LimitTriggered;
    }

    /// <summary>
    /// 标注驱动是否经过真机验证。真卡骨架实现返回 false（"未经真机验证"），Sim 返回 true。
    /// </summary>
    public interface IVerifiedDriver
    {
        bool IsFieldVerified { get; }
    }
}