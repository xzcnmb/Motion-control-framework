using System;
using System.Threading;

namespace Sophon.Contracts
{
    /// <summary>
    /// 显式运动控制命令实体（对标 PLCopen 体系）。
    /// 承载命令参数、优先级、缓冲模式及完整生命周期追踪。
    /// </summary>
    public class MotionCommand
    {
        /// <summary>命令唯一流水号。</summary>
        public Guid CommandId { get; set; } = Guid.NewGuid();

        /// <summary>目标轴编号（多轴插补或轴组时为主轴或 -1）。</summary>
        public int AxisId { get; set; }

        /// <summary>所属轴组名称（若属于轴组协调运动）。</summary>
        public string? GroupName { get; set; }

        /// <summary>命令指令类型。</summary>
        public MotionCommandType CommandType { get; set; } = MotionCommandType.MoveAbsolute;

        /// <summary>目标位置 (mm/deg)。</summary>
        public double TargetPosition { get; set; }

        /// <summary>目标运行速度 (mm/s 或 deg/s)。</summary>
        public double TargetSpeed { get; set; }

        /// <summary>加速度。</summary>
        public double Accel { get; set; }

        /// <summary>减速度。</summary>
        public double Decel { get; set; }

        /// <summary>平滑加加速度 (S曲线 Jerk，0 为梯形)。</summary>
        public double Jerk { get; set; }

        /// <summary>缓冲衔接模式（默认 Aborting 抢占）。</summary>
        public CommandBufferMode BufferMode { get; set; } = CommandBufferMode.Aborting;

        /// <summary>
        /// 优先级权重：数值越大优先级越高。
        /// 硬件急停: 1000 | 卡控停止: 800 | 软件暂停: 500 | 正常定位: 100
        /// </summary>
        public int Priority { get; set; } = 100;

        /// <summary>命令创建时间。</summary>
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;

        /// <summary>开始执行时间戳。</summary>
        public DateTime? StartTime { get; set; }

        /// <summary>结束或完成时间戳。</summary>
        public DateTime? FinishTime { get; set; }

        /// <summary>命令当前生命周期状态。</summary>
        public CommandLifecycleState State { get; set; } = CommandLifecycleState.Created;

        /// <summary>最终完成语义状态（Done, CommandAborted, Error）。</summary>
        public CommandCompletionStatus? CompletionStatus { get; set; }

        /// <summary>完成或失败详细信息说明。</summary>
        public string? StatusMessage { get; set; }

        /// <summary>
        /// 标记命令进入执行状态。
        /// </summary>
        public void MarkExecuting()
        {
            State = CommandLifecycleState.Executing;
            StartTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 标记命令已到达目标并成功完成。
        /// </summary>
        public void MarkDone(string message = "到位完成")
        {
            State = CommandLifecycleState.Completed;
            CompletionStatus = CommandCompletionStatus.Done;
            StatusMessage = message;
            FinishTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 标记命令被中止（被 Halt/Stop 打断或新指令抢占）。
        /// </summary>
        public void MarkAborted(string reason = "命令被中止")
        {
            State = CommandLifecycleState.Aborted;
            CompletionStatus = CommandCompletionStatus.CommandAborted;
            StatusMessage = reason;
            FinishTime = DateTime.UtcNow;
        }

        /// <summary>
        /// 标记命令发生错误转入故障态。
        /// </summary>
        public void MarkError(string error)
        {
            State = CommandLifecycleState.Faulted;
            CompletionStatus = CommandCompletionStatus.Error;
            StatusMessage = error;
            FinishTime = DateTime.UtcNow;
        }
    }
}
