#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core
{
    /// <summary>
    /// 工站（ISA-88 Unit / PackML 设备）：绑定一张配方图，Start 进入 Execute 循环，直到 Stop。
    /// 暂停挂在节点/步骤边界；停止 = 工站 CTS；异常进 Alarm，不继续下一圈。
    /// </summary>
    public class WorkStation : IWorkStation
    {
        public WorkStation(
            string workStationName,
            IFlowEngineFactory flowEngineFactory,
            IFlowContextFactory flowContextFactory,
            IStateMachine stateMachine,
            WorkStationOptions? options = null)
        {
            WorkStationName = workStationName;
            _flowEngineFactory = flowEngineFactory;
            _options = options ?? WorkStationOptions.Cyclic(workStationName);
            BoundFlowName = string.IsNullOrWhiteSpace(_options.BoundFlowName)
                ? workStationName
                : _options.BoundFlowName!;
            LoopRecipe = _options.LoopRecipe;
            _flowEngine = flowEngineFactory.CreateFlowEngine(BoundFlowName);
            _flowController = _flowEngine as IFlowController;
            _flowContext = flowContextFactory.CreateFlowContext(WorkStationName);
            _stateMachine = stateMachine;
            _stateMachine.StateChanged += OnStateChanged;
        }

        public string WorkStationName { get; }
        public string BoundFlowName { get; private set; }
        public bool LoopRecipe { get; }
        public int CycleCount { get; private set; }
        public WorkStationState CurrentState => _stateMachine.CurrentState;
        public event Action<WorkStationState>? StateChanged;
        public event Action<int>? CycleCompleted;

        private readonly IFlowEngineFactory _flowEngineFactory;
        private readonly WorkStationOptions _options;
        private IFlowEngine _flowEngine;
        private IFlowController? _flowController;
        private readonly IFlowContext _flowContext;
        private readonly IStateMachine _stateMachine;
        private CancellationTokenSource? _cts;
        private readonly object _lock = new object();

        public void BindRecipe(string flowName)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程图名不能为空", nameof(flowName));
            }

            lock (_lock)
            {
                var s = _stateMachine.CurrentState;
                if (s == WorkStationState.Running || s == WorkStationState.Paused)
                {
                    throw new InvalidOperationException($"工站「{WorkStationName}」正在运行，不能更换配方。请先停止。");
                }

                BoundFlowName = flowName.Trim();
                _flowEngine = _flowEngineFactory.CreateFlowEngine(BoundFlowName);
                _flowController = _flowEngine as IFlowController;
                _flowContext.Logger.Info($"工站{WorkStationName} 绑定配方「{BoundFlowName}」");
            }
        }

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
                CycleCount = 0;
                _stateMachine.SetState(WorkStationState.Running);
            }

            var cts = _cts;
            _ = Task.Run(async () => await RunWorkAsync(cts!).ConfigureAwait(false));
        }

        private async Task RunWorkAsync(CancellationTokenSource cts)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(BoundFlowName))
                {
                    throw new InvalidOperationException(
                        $"工站「{WorkStationName}」未绑定流程图。请在工站页选择配方后再启动。");
                }

                while (!cts.IsCancellationRequested)
                {
                    _flowContext.Logger.Info(
                        $"工站{WorkStationName} 第 {CycleCount + 1} 次执行配方「{BoundFlowName}」");
                    await _flowEngine.RunAsync(_flowContext, cts.Token).ConfigureAwait(false);

                    if (cts.IsCancellationRequested)
                    {
                        break;
                    }

                    int n;
                    lock (_lock)
                    {
                        CycleCount++;
                        n = CycleCount;
                    }
                    CycleCompleted?.Invoke(n);

                    if (!LoopRecipe)
                    {
                        break;
                    }
                }

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
