#nullable enable
using Sophon.Common;
using System;

namespace Sophon.Core
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class StateMachine : IStateMachine
    {
        private readonly object _sync = new object();
        private volatile WorkStationState _currentState;

        /// <summary>最近一次进入 Alarm 的来源描述。</summary>
        public string? LastAlarmSource { get; private set; }

        public WorkStationState CurrentState => _currentState;

        public event Action<WorkStationState>? StateChanged;

        public void SetState(WorkStationState state, string? alarmSource = null)
        {
            lock (_sync)
            {
                if (state == _currentState)
                {
                    return;
                }
                _currentState = state;
                if (state == WorkStationState.Alarm)
                {
                    LastAlarmSource = alarmSource;
                }
            }

            // 事件在锁外发布，避免监听者在事件回调内再入状态机造成死锁
            StateChanged?.Invoke(state);
        }

        public void Reset()
        {
            lock (_sync)
            {
                if (_currentState != WorkStationState.Alarm)
                {
                    return;
                }
                _currentState = WorkStationState.Idle;
                LastAlarmSource = null;
            }

            StateChanged?.Invoke(WorkStationState.Idle);
        }
    }

    public enum WorkStationState
    {
        Idle,
        Running,
        Paused,
        Stopped,
        Alarm,
    }
}