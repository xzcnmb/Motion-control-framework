#nullable enable
using System;

namespace Sophon.Core
{
    public interface IStateMachine
    {
        /// <summary>
        /// 当前状态（线程安全读取）。
        /// </summary>
        WorkStationState CurrentState { get; }

        /// <summary>
        /// 状态变化事件（线程安全发布）。
        /// </summary>
        event Action<WorkStationState>? StateChanged;

        /// <summary>
        /// 设置状态。
        /// </summary>
        /// <param name="state">目标状态</param>
        /// <param name="alarmSource">进入 Alarm 状态时的报警来源描述</param>
        void SetState(WorkStationState state, string? alarmSource = null);

        /// <summary>
        /// 报警复位：Alarm → Idle（限位复位/故障恢复路径）。其余状态调用无效果。
        /// </summary>
        void Reset();
    }
}