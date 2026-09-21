#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 工站用的 v2 宿主：把 ISA-88 单元（工站）接到配方阶段（FlowGraph 节点）。
    /// Pause 挂在节点边界，Stop 走取消令牌；每次 Start 重新读盘，编辑器保存后立刻生效。
    /// 不再回退 v1 线性 IFlowStep。
    /// </summary>
    public sealed class FlowEngineV2Host : IFlowEngine, IFlowController
    {
        private readonly IMotionController? _motion;
        private readonly IIoController? _io;
        private readonly IEventBus? _eventBus;
        private readonly IServiceProvider? _services;
        private readonly string? _graphDirectory;
        private readonly object _lock = new object();
        private FlowEngineV2? _active;
        private int _running;

        public FlowEngineV2Host(
            string flowName,
            IMotionController? motion = null,
            IIoController? io = null,
            IEventBus? eventBus = null,
            IServiceProvider? services = null,
            string? graphDirectory = null)
        {
            FlowName = flowName ?? throw new ArgumentNullException(nameof(flowName));
            _motion = motion;
            _io = io;
            _eventBus = eventBus;
            _services = services;
            _graphDirectory = graphDirectory;
        }

        public string FlowName { get; }

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    return _active?.IsPaused == true;
                }
            }
        }

        public bool IsRunning => Volatile.Read(ref _running) == 1;

        public bool IsStopped => !IsRunning;

        public int CurrentIndex => 0;

        public async Task RunAsync(IFlowContext context, CancellationToken token)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var graph = FlowGraphStore.Load(FlowName, _graphDirectory);
            if (graph == null || graph.Nodes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"工站「{FlowName}」没有 v2 流程图（SophonData/flows/{FlowName}.json）。请在流程编辑器保存后再启动。工站只执行节点图，不再跑 v1 线性步骤。");
            }

            if (!graph.Validate(out var errors) && errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"工站「{FlowName}」流程图不合法：{string.Join("；", errors)}");
            }

            var engine = new FlowEngineV2(_motion, _io, _eventBus, _services);
            lock (_lock)
            {
                _active = engine;
            }
            Interlocked.Exchange(ref _running, 1);

            try
            {
                context.Logger.Info($"工站「{FlowName}」按 v2 节点图启动（{graph.Nodes.Count} 个节点）");
                await engine.RunAsync(graph, context, token).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
                lock (_lock)
                {
                    if (ReferenceEquals(_active, engine))
                    {
                        _active = null;
                    }
                }
            }
        }

        public void Pause()
        {
            FlowEngineV2? engine;
            lock (_lock)
            {
                engine = _active;
            }
            engine?.PauseAsync();
        }

        public void Resume()
        {
            FlowEngineV2? engine;
            lock (_lock)
            {
                engine = _active;
            }
            engine?.ResumeAsync();
        }

        public void Stop()
        {
            FlowEngineV2? engine;
            lock (_lock)
            {
                engine = _active;
            }
            engine?.StopAsync();
        }
    }
}
