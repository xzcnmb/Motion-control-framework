using System;

namespace Sophon.Core
{
    public interface IWorkStation
    {
        string WorkStationName { get; }

        /// <summary>绑定的配方流程图名。空表示未绑定。</summary>
        string BoundFlowName { get; }

        /// <summary>true：Start 后循环执行配方直到 Stop（PackML Execute）。</summary>
        bool LoopRecipe { get; }

        /// <summary>本次启动后已完成的配方圈数。</summary>
        int CycleCount { get; }

        WorkStationState CurrentState { get; }
        event Action<WorkStationState> StateChanged;
        event Action<int> CycleCompleted;

        /// <summary>启动：Idle/Stopped/Alarm（已复位）→ Running，循环跑绑定配方。重复调用不叠加。</summary>
        void Start();

        void Pause();

        void Resume();

        /// <summary>停止循环：Running/Paused → Stopped；Idle 时无操作。</summary>
        void Stop();

        /// <summary>报警复位：Alarm → Idle。</summary>
        void Reset();

        /// <summary>绑定流程图。运行中拒绝。</summary>
        void BindRecipe(string flowName);
    }
}
