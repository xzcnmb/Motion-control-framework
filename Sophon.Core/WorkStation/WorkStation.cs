#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core
{
    /// <summary>
    /// 工站（ISA-88 Unit / PackML 设备）：绑定一张配方图 + 一个逻辑轴组。
    /// Start 进入 Execute 循环直到 Stop。暂停挂在节点边界，不停轴。
    /// 停止 = 工站 CTS + 仅对本组轴 Cat1 受控停。急停必须硬接线。
    /// </summary>
    public class WorkStation : IWorkStation
    {
        public WorkStation(
            string workStationName,
            IFlowEngineFactory flowEngineFactory,
            IFlowContextFactory flowContextFactory,
            IStateMachine stateMachine,
            WorkStationOptions? options = null,
            IMotionController? motion = null,
            AxisGroupLease? axisLease = null,
            AxisGroupStore? axisGroupStore = null)
        {
            WorkStationName = workStationName;
            _flowEngineFactory = flowEngineFactory;
            _options = options ?? WorkStationOptions.Cyclic(workStationName);
            BoundFlowName = string.IsNullOrWhiteSpace(_options.BoundFlowName)
                ? workStationName
                : _options.BoundFlowName!;
            LoopRecipe = _options.LoopRecipe;
            AxisGroupName = _options.AxisGroupName ?? string.Empty;
            BoundAxisIds = Array.Empty<int>();
            _flowEngine = flowEngineFactory.CreateFlowEngine(BoundFlowName);
            _flowController = _flowEngine as IFlowController;
            _flowContext = flowContextFactory.CreateFlowContext(WorkStationName);
            _stateMachine = stateMachine;
            _motion = motion;
            _axisLease = axisLease;
            _axisGroupStore = axisGroupStore;
            ApplyStoredGroup();
            _stateMachine.StateChanged += OnStateChanged;
        }

        public string WorkStationName { get; }
        public string BoundFlowName { get; private set; }
        public string AxisGroupName { get; private set; }
        public IReadOnlyList<int> BoundAxisIds { get; private set; }
        public bool LoopRecipe { get; }
        public int CycleCount { get; private set; }
        public WorkStationState CurrentState => _stateMachine.CurrentState;
        public event Action<WorkStationState>? StateChanged;
        public event Action<int>? CycleCompleted;

        private readonly IFlowEngineFactory _flowEngineFactory;
        private readonly WorkStationOptions _options;
        private readonly IMotionController? _motion;
        private readonly AxisGroupLease? _axisLease;
        private readonly AxisGroupStore? _axisGroupStore;
        private IFlowEngine _flowEngine;
        private IFlowController? _flowController;
        private readonly IFlowContext _flowContext;
        private readonly IStateMachine _stateMachine;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private int _generation;
        private readonly object _lock = new object();

        public void BindRecipe(string flowName)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程图名不能为空", nameof(flowName));
            }

            lock (_lock)
            {
                EnsureIdleOrStoppedForRebind("配方");
                BoundFlowName = flowName.Trim();
                _flowEngine = _flowEngineFactory.CreateFlowEngine(BoundFlowName);
                _flowController = _flowEngine as IFlowController;
                _flowContext.ClearData();
                _flowContext.Logger.Info($"工站{WorkStationName} 绑定配方「{BoundFlowName}」");
            }
        }

        public void BindAxisGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                throw new ArgumentException("轴组名不能为空", nameof(groupName));
            }

            lock (_lock)
            {
                EnsureIdleOrStoppedForRebind("轴组");
                if (_axisGroupStore == null)
                {
                    throw new InvalidOperationException("轴组档案未注入，无法按名绑定。");
                }

                var group = _axisGroupStore.Find(groupName.Trim());
                if (group == null)
                {
                    throw new InvalidOperationException($"找不到轴组「{groupName}」。请先在轴组配置里创建。");
                }

                AxisGroupName = group.GroupName;
                BoundAxisIds = group.DistinctAxisIds();
                _flowContext.Logger.Info($"工站{WorkStationName} 绑定轴组「{AxisGroupName}」轴 [{string.Join(",", BoundAxisIds)}]");
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                var s = _stateMachine.CurrentState;
                if (s == WorkStationState.Running)
                {
                    _flowContext.Logger.Info($"工站{WorkStationName}已经在运行中，忽略重复启动");
                    return;
                }

                if (s == WorkStationState.Paused)
                {
                    _flowContext.Logger.Warn($"工站{WorkStationName}已暂停，请点继续，不会另开循环任务");
                    return;
                }

                if (s == WorkStationState.Alarm)
                {
                    throw new InvalidOperationException(
                        $"工站「{WorkStationName}」处于报警，必须先复位再启动。");
                }

                if (_runTask != null && !_runTask.IsCompleted)
                {
                    _flowContext.Logger.Warn($"工站{WorkStationName}上一轮循环尚未退出，拒绝启动（PackML 同一时刻只允许一个 Execute）");
                    return;
                }

                if (_axisLease != null && BoundAxisIds.Count > 0)
                {
                    var acquired = _axisLease.TryAcquire(WorkStationName, BoundAxisIds, out var conflict);
                    if (!string.IsNullOrEmpty(conflict) || acquired.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"工站「{WorkStationName}」无法占用轴组：{conflict ?? "占用失败"}");
                    }
                }

                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                CycleCount = 0;
                int generation = ++_generation;
                var cts = _cts;
                _runTask = Task.Run(() => RunWorkAsync(cts, generation));
            }

            ApplyState(WorkStationState.Running);
        }

        private async Task RunWorkAsync(CancellationTokenSource cts, int generation)
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

                    try
                    {
                        CycleCompleted?.Invoke(n);
                    }
                    catch (Exception ex)
                    {
                        _flowContext.Logger.Warn($"工站{WorkStationName}圈数回调异常：{ex.Message}");
                    }

                    if (!LoopRecipe)
                    {
                        break;
                    }
                }

                bool goIdle = false;
                lock (_lock)
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                    var s = _stateMachine.CurrentState;
                    if (s == WorkStationState.Running || s == WorkStationState.Paused)
                    {
                        goIdle = true;
                    }
                }
                if (goIdle)
                {
                    ReleaseAxes();
                    ApplyState(WorkStationState.Idle);
                }
            }
            catch (OperationCanceledException)
            {
                ReleaseAxes();
                _flowContext.Logger.Info($"工站{WorkStationName}流程被取消");
                bool goStopped = false;
                lock (_lock)
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                    if (_stateMachine.CurrentState == WorkStationState.Running)
                    {
                        goStopped = true;
                    }
                }
                if (goStopped)
                {
                    ApplyState(WorkStationState.Stopped);
                }
            }
            catch (Exception e)
            {
                _flowContext.Logger.Error($"工站{WorkStationName}运行异常：{e.Message}");
                bool goAlarm = false;
                lock (_lock)
                {
                    if (generation != _generation)
                    {
                        return;
                    }
                    if (_stateMachine.CurrentState != WorkStationState.Stopped)
                    {
                        goAlarm = true;
                    }
                }
                if (goAlarm)
                {
                    ReleaseAxes();
                    ApplyState(WorkStationState.Alarm, e.Message);
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
            }
            ApplyState(WorkStationState.Paused);
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
            }
            ApplyState(WorkStationState.Running);
        }

        public void Stop()
        {
            bool stopped = false;
            lock (_lock)
            {
                _flowController?.Stop();
                if (_stateMachine.CurrentState == WorkStationState.Running || _stateMachine.CurrentState == WorkStationState.Paused)
                {
                    _cts?.Cancel();
                    StopAxesControlled();
                    stopped = true;
                }
            }
            if (stopped)
            {
                ReleaseAxes();
                ApplyState(WorkStationState.Stopped);
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                if (_stateMachine.CurrentState != WorkStationState.Alarm)
                {
                    return;
                }
            }
            _stateMachine.Reset();
        }

        private void ApplyState(WorkStationState state, string? alarmSource = null)
        {
            _stateMachine.SetState(state, alarmSource);
        }

        private void EnsureIdleOrStoppedForRebind(string what)
        {
            var s = _stateMachine.CurrentState;
            if (s != WorkStationState.Idle && s != WorkStationState.Stopped)
            {
                throw new InvalidOperationException(
                    $"工站「{WorkStationName}」处于 {s}，不能更换{what}。");
            }

            if (_runTask != null && !_runTask.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"工站「{WorkStationName}」上一轮尚未退出，不能更换{what}。");
            }
        }

        private void ApplyStoredGroup()
        {
            if (!string.IsNullOrWhiteSpace(AxisGroupName) && _axisGroupStore != null)
            {
                var group = _axisGroupStore.Find(AxisGroupName);
                if (group != null)
                {
                    BoundAxisIds = group.DistinctAxisIds();
                    return;
                }
            }

            BoundAxisIds = (_options.AxisIds ?? Array.Empty<int>()).Distinct().ToArray();
        }

        /// <summary>PackML Stop = Cat1 受控停，只打绑定轴组。急停不走这里。</summary>
        private void StopAxesControlled()
        {
            var motion = _motion;
            if (motion == null || BoundAxisIds.Count == 0)
            {
                return;
            }

            try
            {
                foreach (int axisId in BoundAxisIds)
                {
                    motion.Stop(axisId);
                }
            }
            catch (Exception ex)
            {
                _flowContext.Logger.Warn($"工站{WorkStationName}受控停轴失败：{ex.Message}");
            }
        }

        private void ReleaseAxes()
        {
            try
            {
                _axisLease?.Release(WorkStationName);
            }
            catch (Exception ex)
            {
                _flowContext.Logger.Warn($"工站{WorkStationName}释放轴组失败：{ex.Message}");
            }
        }

        private void OnStateChanged(WorkStationState state)
        {
            _flowContext.Logger.Info($"工站{WorkStationName}状态切换：{state}");
            StateChanged?.Invoke(state);
        }
    }
}
