using Sophon.Core;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Sophon.Core.Tests
{
    public class StateMachineTests
    {
        [Fact]
        public void 状态变化_事件仅触发一次()
        {
            var sm = new StateMachine();
            int count = 0;
            WorkStationState last = WorkStationState.Idle;
            sm.StateChanged += s => { count++; last = s; };

            sm.SetState(WorkStationState.Running);
            sm.SetState(WorkStationState.Running); // 重复设置不触发

            Assert.Equal(1, count);
            Assert.Equal(WorkStationState.Running, last);
            Assert.Equal(WorkStationState.Running, sm.CurrentState);
        }

        [Fact]
        public void 复位_报警回空闲_其余状态无效()
        {
            var sm = new StateMachine();
            sm.SetState(WorkStationState.Running);
            sm.Reset();
            Assert.Equal(WorkStationState.Running, sm.CurrentState); // 非 Alarm 无效果

            sm.SetState(WorkStationState.Alarm, "限位触发");
            Assert.Equal("限位触发", sm.LastAlarmSource);
            sm.Reset();
            Assert.Equal(WorkStationState.Idle, sm.CurrentState);
            Assert.Null(sm.LastAlarmSource);
        }

        [Fact]
        public async Task 并发设置状态_无死锁无异常()
        {
            var sm = new StateMachine();
            var states = new[] { WorkStationState.Idle, WorkStationState.Running, WorkStationState.Paused, WorkStationState.Stopped, WorkStationState.Alarm };

            var tasks = new Task[8];
            for (int i = 0; i < tasks.Length; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < 200; j++)
                    {
                        sm.SetState(states[(idx + j) % states.Length]);
                    }
                });
            }
            await Task.WhenAll(tasks);

            Assert.Contains(sm.CurrentState, states);
        }
    }
}