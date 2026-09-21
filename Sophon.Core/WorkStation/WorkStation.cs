#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core
{
    /// <summary>
    /// 工站：流程引擎 + 状态机的宿主。
    /// 生命周期设计要点（评审修复）：
    /// - 异常绝不逃逸到后台线程：RunWork 内部全捕获，状态统一在 await 之后设置；
    /// - 步骤失败 → Alarm（且 Alarm 具备 Reset 复位路径）；正常完成 → Idle；
    /// - Stop 由工站持有的 CTS 单点取消，Idle 态 Stop 为无操作；
    /// - 不再向外部暴露裸 CTS。
    /// </summary>
    public class WorkStation : IWorkStation
    {
        public WorkStation(string workStationName, IFlowEngineFactory flowEngineFactory, IFlowContextFactory flowContextFactory, IStateMachine stateMachine)
        {
            WorkStationName = workStationName;
            _flowEngine = flowEngineFactory.CreateFlowEngine(WorkStationName);
            _flowController = _flowEngine as IFlowController;
            _flowContext = flowContextFactory.CreateFlowContext(WorkStationName);
            _stateMachine = stateMachine;
            _stateMachine.StateChanged += OnStateChanged;
        }

        public string WorkStationName { get; }
        public WorkStationState CurrentState => _stateMachine.CurrentState;
        public event Action<WorkStationState>? StateChanged;

        private readonly IFlowEngine _flowEngine;
        private readonly IFlowController? _flowController;
        private readonly IFlowContext _flowContext;
        private readonly IStateMachine _stateMachine;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new object();

        public void Start()
        {
            lock (_lock)
            {
                if (_stateMachine.CurrentState == WorkStationState.Running)
                {
                    _flowContext.Logger.Info($"工站{WorkStationName}已经在运行中，忽略重复启动");
                    return;
                }
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _stateMachine.SetState(WorkStationState.Running);
            }

            var cts = _cts;
            // 显式调度到线程池：引擎执行期间可能出现同步阻塞步骤（如等待门控/IO），
            // 绝不能占用调用线程（UI 线程或测试线程），否则调用方无法再发出 Pause/Stop
            _ = Task.Run(async () => await RunWorkAsync(cts!).ConfigureAwait(false));
        }

        private async Task RunWorkAsync(CancellationTokenSource cts)
        {
            try
            {
                await _flowEngine.RunAsync(_flowContext, cts.Token).ConfigureAwait(false);

                // 流程结束（正常完成或被停止）：仅在仍处于 Running/Paused 时回到 Idle，
                // 不覆盖 Stop 后的 Stopped 或失败后的 Alarm
                lock (_lock)
                {
                    var s = _stateMachine.CurrentState;
                    if (s == WorkStationState.Running || s == WorkStationState.Paused)
                    {
                        _stateMachine.SetState(WorkStationState.Idle);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _flowContext.Logger.Info($"工站{WorkStationName}流程被取消");
            }
            catch (Exception e)
            {
                // 步骤失败/引擎异常的唯一下落：Alarm（保留可复位路径）
                _flowContext.Logger.Error($"工站{WorkStationName}运行异常：{e.Message}");
                lock (_lock)
                {
                    if (_stateMachine.CurrentState != WorkStationState.Stopped)
                    {
                        _stateMachine.SetState(WorkStationState.Alarm, e.Message);
                    }
                }
            }
        }

        public void Pause()
        {
            lock (_lock)
            {
                if (_stateMachine.CurrentState != WorkStationState.Running)
                {
                    return;
                }
                _flowController?.Pause();
                _stateMachine.SetState(WorkStationState.Paused);
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                if (_stateMachine.CurrentState != WorkStationState.Paused)
                {
                    return;
                }
                _flowController?.Resume();
                _stateMachine.SetState(WorkStationState.Running);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _flowController?.Stop();
                if (_stateMachine.CurrentState == WorkStationState.Running || _stateMachine.CurrentState == WorkStationState.Paused)
                {
                    _cts?.Cancel();
                    _stateMachine.SetState(WorkStationState.Stopped);
                }
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_stateMachine.CurrentState == WorkStationState.Alarm)
                {
                    _stateMachine.Reset();
                }
            }
        }

        private void OnStateChanged(WorkStationState state)
        {
            _flowContext.Logger.Info($"工站{WorkStationName}状态切换：{state}");
            StateChanged?.Invoke(state);
        }
    }
}