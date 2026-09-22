using System;
using System.Collections.Generic;

namespace Sophon.Core
{
    public interface IWorkStation
    {
        string WorkStationName { get; }

        /// <summary>绑定的配方流程图名。空表示未绑定。</summary>
        string BoundFlowName { get; }

        /// <summary>绑定的逻辑轴组名。空表示未绑轴组。</summary>
        string AxisGroupName { get; }

        /// <summary>组内轴号。Stop 只对这些轴发 Cat1 受控停。</summary>
        IReadOnlyList<int> BoundAxisIds { get; }

        /// <summary>true：Start 后循环执行配方直到 Stop（PackML Execute）。</summary>
        bool LoopRecipe { get; }

        /// <summary>本次启动后已完成的配方圈数。</summary>
        int CycleCount { get; }

        WorkStationState CurrentState { get; }
        event Action<WorkStationState> StateChanged;
        event Action<int> CycleCompleted;

        /// <summary>仅 Idle / Stopped → Running。Alarm 须先 Reset；Paused 请用 Resume，不会另开任务。</summary>
        void Start();

        void Pause();

        void Resume();

        /// <summary>停止循环并对绑定轴组发 Cat1 受控停。急停必须硬接线，不走这里。</summary>
        void Stop();

        /// <summary>报警复位：Alarm → Idle。</summary>
        void Reset();

        /// <summary>绑定流程图。仅 Idle / Stopped 允许。</summary>
        void BindRecipe(string flowName);

        /// <summary>绑定逻辑轴组。仅 Idle / Stopped 允许。运行中的组不能与其他工站共享轴。</summary>
        void BindAxisGroup(string groupName);
    }
}
