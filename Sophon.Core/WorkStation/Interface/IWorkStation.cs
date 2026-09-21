using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core
{
    public interface IWorkStation
    {
        string WorkStationName { get; }
        WorkStationState CurrentState { get; }
        event Action<WorkStationState> StateChanged;

        /// <summary>启动：Idle/Alarm（已复位）→ Running。重复调用不产生实例叠加。</summary>
        void Start();

        void Pause();

        void Resume();

        /// <summary>停止：Running/Paused → Stopped；Idle 时无操作。</summary>
        void Stop();

        /// <summary>报警复位：Alarm → Idle（限位/故障处理完毕后的恢复入口）。</summary>
        void Reset();
    }
}