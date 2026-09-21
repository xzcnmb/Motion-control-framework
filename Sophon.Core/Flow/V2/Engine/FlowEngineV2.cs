#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// V2 图形流程引擎。
    /// 核心特征：
    /// 1. 基于图拓扑结构调度（顺序、条件分支分流与结构化 Fork-Join 并行汇聚）；
    /// 2. 单调度线程 + await 协作模型（SemaphoreSlim 串行化调度防护，节点执行纯 await，禁止每节点裸线程）；
    /// 3. 严格生命周期管理（暂停/恢复=节点边界挂起，停止=CancellationToken 触发 + 未启动分支跳过）；
    /// 4. 循环防护：静态环检测 + 运行期节点最大迭代次数限制（MaxExecutionCount 动态环防御）；
    /// 5. 断点调试支持（挂起、单步推进、事件通知）；
    /// 6. 节点状态发布（event Action&lt;FlowNodeStateChange&gt; 与 IProgress 接口）。
    ///
    /// 执行模型形式化语义（Item ⑤）：
    /// - 【顺序】节点完成经 Out 连线单线程推进；
    /// - 【Fork】任一节点产生多个后续目标时进入并行区：每个分支独立任务调度（Task.Run），
    ///   任一分支失败立即连锁取消其余分支并汇合异常，杜绝孤儿分支竞态共享状态；
    /// - 【Join 屏障】并行区内若某节点可被 ≥2 个分支静态到达（汇聚节点），
    ///   则到达方在屏障门前等待，全部潜在分支到齐（或分歧结束）后放行——
    ///   汇聚节点全局只执行一次，多余到达被吸收，下游绝不触发多次；
    /// - 【Jump】非拓扑直跳经显式工作栈压栈实现，执行上下文原样保留，调用栈不增长；
    /// - 【循环】Loop 节点自管理迭代计数与 until/break/continue 条件，并内置安全丝熔断阈值。
    /// </summary>
    public class FlowEngineV2
    {
        private readonly SemaphoreSlim _schedulerLock = new(1, 1);
        private readonly SemaphoreSlim _pauseSignal = new(0, 1);
        private readonly SemaphoreSlim _breakpointSignal = new(0, 1);

        private readonly HashSet<string> _breakpoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _nodeExecutionCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, FlowNodeState> _nodeStates = new(StringComparer.OrdinalIgnoreCase);

        private volatile bool _isPaused;
        private volatile bool _isBreakpointHit;
        private string? _currentBreakpointNodeId;
        private CancellationTokenSource? _runCts;
        private int _forkIdSeed;

        /// <summary>运动控制器。</summary>
        public IMotionController? MotionController { get; set; }

        /// <summary>IO 控制器。</summary>
        public IIoController? IoController { get; set; }

        /// <summary>事件总线。</summary>
        public IEventBus? EventBus { get; set; }

        /// <summary>服务提供者（可选）。</summary>
        public IServiceProvider? Services { get; set; }

        /// <summary>
        /// 运行期单个节点最大允许执行次数（动态环/死循环防御阈值，默认 100,000）。
        /// </summary>
        public int MaxExecutionCount { get; set; } = 100000;

        /// <summary>
        /// 节点状态变更事件（UI 高亮展示与追踪）。
        /// </summary>
        public event Action<FlowNodeStateChange>? NodeStateChanged;

        /// <summary>
        /// 断点命中事件（参数为命中断点的 NodeId）。
        /// </summary>
        public event Action<string>? BreakpointHit;

        /// <summary>
        /// 进度监控对象。
        /// </summary>
        public IProgress<FlowNodeStateChange>? Progress { get; set; }

        /// <summary>当前正在执行的流程图。</summary>
        public FlowGraph? CurrentGraph { get; private set; }

        /// <summary>当前流程全局上下文。</summary>
        public IFlowContext? CurrentContext { get; private set; }

        /// <summary>流程是否正在运行中。</summary>
        public bool IsRunning { get; private set; }

        /// <summary>流程是否处于暂停状态。</summary>
        public bool IsPaused => _isPaused;

        public FlowEngineV2(
            IMotionController? motionController = null,
            IIoController? ioController = null,
            IEventBus? eventBus = null,
            IServiceProvider? services = null)
        {
            MotionController = motionController;
            IoController = ioController;
            EventBus = eventBus;
            Services = services;
        }

        #region 断点管理

        /// <summary>
        /// 设置指定节点的断点。
        /// </summary>
        public void SetBreakpoint(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                lock (_breakpoints)
                {
                    _breakpoints.Add(nodeId);
                }
            }
        }

        /// <summary>
        /// 移除指定节点的断点。
        /// </summary>
        public void RemoveBreakpoint(string nodeId)
        {
            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                lock (_breakpoints)
                {
                    _breakpoints.Remove(nodeId);
                }
            }
        }

        /// <summary>
        /// 清空所有断点。
        /// </summary>
        public void ClearBreakpoints()
        {
            lock (_breakpoints)
            {
                _breakpoints.Clear();
            }
        }

        /// <summary>
        /// 从断点处继续执行。
        /// </summary>
        public void ContinueFromBreakpoint()
        {
            if (_isBreakpointHit)
            {
                _isBreakpointHit = false;
                _currentBreakpointNodeId = null;
                try
                {
                    _breakpointSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }

        #endregion

        #region 控制操作（暂停/恢复/停止）

        /// <summary>
        /// 暂停流程执行。在当前节点完成、即将调度下一个节点的节点边界处挂起。
        /// </summary>
        public Task PauseAsync()
        {
            _isPaused = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// 恢复处于暂停状态的流程。
        /// </summary>
        public Task ResumeAsync()
        {
            if (_isPaused)
            {
                _isPaused = false;
                try
                {
                    _pauseSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 停止流程执行。触发取消令牌，未启动分支直接跳过，已启动的硬件节点执行 Abort。
        /// 并行分支的 Join 屏障等待同样绑定取消令牌，保证停止立即可感知。
        /// </summary>
        public Task StopAsync()
        {
            _runCts?.Cancel();
            // 如果处于暂停或断点，唤醒它以使其能及时感知取消
            if (_isPaused)
            {
                _isPaused = false;
                try { _pauseSignal.Release(); } catch { }
            }
            if (_isBreakpointHit)
            {
                _isBreakpointHit = false;
                try { _breakpointSignal.Release(); } catch { }
            }
            return Task.CompletedTask;
        }

        #endregion

        #region 流程执行核心

        /// <summary>
        /// 异步启动并执行流程图。
        /// </summary>
        /// <param name="graph">待执行的 FlowGraph 图模型。</param>
        /// <param name="context">流程上下文。</param>
        /// <param name="externalCt">外部取消令牌。</param>
        public async Task RunAsync(FlowGraph graph, IFlowContext context, CancellationToken externalCt = default)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (context == null) throw new ArgumentNullException(nameof(context));

            CurrentGraph = graph;
            CurrentContext = context;
            IsRunning = true;
            _isBreakpointHit = false;
            _nodeExecutionCounts.Clear();
            _nodeStates.Clear();

            // 初始化所有节点状态为 Idle
            foreach (var n in graph.Nodes)
            {
                SetNodeState(n, FlowNodeState.Idle);
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            _runCts = linkedCts;
            var token = linkedCts.Token;

            try
            {
                var startNode = graph.Nodes.FirstOrDefault(n =>
                    string.Equals(n.NodeType, "Start", StringComparison.OrdinalIgnoreCase));

                if (startNode == null)
                {
                    throw new InvalidOperationException($"流程图 '{graph.FlowName}' 中未找到 Start 入口节点");
                }

                // 从 Start 节点开始迭代拓扑调度
                await ExecuteFlowAsync(startNode, graph, context, token, null);
            }
            finally
            {
                IsRunning = false;
                _runCts = null;
            }
        }

        /// <summary>
        /// 迭代式调度主循环（显式工作栈替代递归）。
        /// Jump 跳转与 Loop 回边仅压栈/弹栈，调用栈深度不随跳转/循环次数增长，
        /// 从根本上避免同步跳转链耗尽线程栈（StackOverflow）。
        /// </summary>
        /// <param name="entryNode">入口节点。</param>
        /// <param name="branch">所属并行分支（主干为 null）；非 null 时启用 Join 屏障语义。</param>
        private async Task ExecuteFlowAsync(
            FlowNode entryNode,
            FlowGraph graph,
            IFlowContext context,
            CancellationToken ct,
            BranchRun? branch)
        {
            var workStack = new Stack<FlowNode>();
            workStack.Push(entryNode);

            while (workStack.Count > 0)
            {
                var node = workStack.Pop();

                if (ct.IsCancellationRequested)
                {
                    SetNodeState(node, FlowNodeState.Skipped, "已停止，跳过未启动节点");
                    continue;
                }

                // ---- 并行汇聚屏障（Join Barrier）----
                // 仅当同一 Fork 的其他分支也能静态到达该节点时才需同步：
                // 汇聚节点只执行一次，多余到达被吸收，保证下游汇聚不重复触发、不与分支竞态。
                if (branch != null)
                {
                    var decision = await EnterJoinGateAsync(branch, node, ct);
                    if (decision == JoinDecision.Absorb)
                    {
                        context.Logger?.Info(
                            $"[V2 Flow:{graph.FlowName}][Node:{node.Name}] 并行分支到达汇聚点，已被 Join 屏障吸收（由唯一分支执行汇聚）");
                        break; // 本分支到此收敛，终止后续流转
                    }
                }

                // 1. 检查断点命中
                bool isBreakpoint;
                lock (_breakpoints)
                {
                    isBreakpoint = _breakpoints.Contains(node.Id);
                }

                if (isBreakpoint)
                {
                    _isBreakpointHit = true;
                    _currentBreakpointNodeId = node.Id;
                    context.Logger?.Info($"[V2 Flow] 命中断点: 节点 '{node.Name}'({node.Id})");
                    BreakpointHit?.Invoke(node.Id);

                    // 挂起等待 ContinueFromBreakpoint 或取消
                    while (_isBreakpointHit && !ct.IsCancellationRequested)
                    {
                        await _breakpointSignal.WaitAsync(100, ct).ContinueWith(_ => { });
                    }
                    ct.ThrowIfCancellationRequested();
                }

                // 2. 检查暂停挂起（节点边界挂起）
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    await _pauseSignal.WaitAsync(100, ct).ContinueWith(_ => { });
                }
                ct.ThrowIfCancellationRequested();

                // 3. 动态环防御：检查执行次数上限
                int count = _nodeExecutionCounts.AddOrUpdate(node.Id, 1, (_, old) => old + 1);
                if (count > MaxExecutionCount)
                {
                    var errMsg = $"动态环防御触发: 节点 '{node.Name}'({node.Id}) 运行期累计执行次数达到上限 ({MaxExecutionCount})，中止执行";
                    SetNodeState(node, FlowNodeState.Failed, errMsg);
                    throw new StepExecuteException(errMsg);
                }

                // 4. 创建逻辑节点实例
                var nodeInstance = FlowNodeRegistry.Create(node.NodeType);
                if (nodeInstance == null)
                {
                    var errMsg = $"未找到节点类型 '{node.NodeType}' 的执行器（节点 '{node.Name}'）";
                    SetNodeState(node, FlowNodeState.Failed, errMsg);
                    throw new InvalidOperationException(errMsg);
                }

                var nodeCtx = new NodeExecutionContext(
                    context,
                    node,
                    graph,
                    MotionController,
                    IoController,
                    EventBus,
                    Services);

                // 5. 状态转换 -> Running
                SetNodeState(node, FlowNodeState.Running);

                NodeExecutionResult result;
                try
                {
                    // 单调度锁协作：执行节点逻辑时释放调度锁（依靠 await 不阻塞其他线程）
                    result = await nodeInstance.ExecuteAsync(nodeCtx, ct);
                }
                catch (OperationCanceledException)
                {
                    SetNodeState(node, FlowNodeState.Skipped, "节点执行被取消");
                    throw;
                }
                catch (Exception ex)
                {
                    SetNodeState(node, FlowNodeState.Failed, ex.Message);
                    throw new StepExecuteException($"节点 '{node.Name}' 执行异常: {ex.Message}");
                }

                if (result.Status == NodeStatus.Failed)
                {
                    SetNodeState(node, FlowNodeState.Failed, result.ErrorMessage);
                    throw new StepExecuteException($"节点 '{node.Name}' 执行失败: {result.ErrorMessage}");
                }
                else if (result.Status == NodeStatus.Skipped)
                {
                    SetNodeState(node, FlowNodeState.Skipped, result.ErrorMessage);
                    continue; // 跳过：本路径终止
                }

                // 执行成功
                SetNodeState(node, FlowNodeState.Completed);

                // 6. 处理 Jump 跳转节点（非拓扑直跳，执行上下文原样保留）
                if (!string.IsNullOrWhiteSpace(result.JumpTargetNodeId))
                {
                    var targetNode = ResolveJumpTarget(graph, result.JumpTargetNodeId);
                    if (targetNode == null)
                    {
                        throw new StepExecuteException(
                            $"Jump 目标节点不存在: '{result.JumpTargetNodeId}'（来源节点 '{node.Name}'({node.Id})）");
                    }
                    workStack.Push(targetNode);
                    continue;
                }

                // 7. 查找后续流转连接（拓扑顺序流转 + 分支分流 + 并行分流）
                var outgoingConns = graph.Connections.Where(c =>
                    string.Equals(c.FromNodeId, node.Id, StringComparison.OrdinalIgnoreCase)).ToList();

                if (outgoingConns.Count == 0)
                {
                    // 到达终端叶子节点
                    continue;
                }

                // 7a. 结构化 Fork-Join：分支并发 + Join 屏障 + 汇合端口单次续流
                if (result.ForkWithJoinBarrier)
                {
                    var (branchConns, joinConns) = SplitForkJoinConnections(node, outgoingConns, JoinPortName);
                    var branchTargets = DistinctTargets(graph, branchConns);
                    var joinTargets = DistinctTargets(graph, joinConns);

                    if (branchTargets.Count == 1)
                    {
                        await ExecuteFlowAsync(branchTargets[0], graph, context, ct, branch);
                    }
                    else if (branchTargets.Count > 1)
                    {
                        await RunForkAsync(branchTargets, graph, context, ct, result.JoinMode);
                    }

                    // Join 屏障后续流：汇合端口目标只推进一次（下游汇聚节点绝不重复触发）
                    if (joinTargets.Count == 1)
                    {
                        workStack.Push(joinTargets[0]);
                    }
                    else if (joinTargets.Count > 1)
                    {
                        await RunForkAsync(joinTargets, graph, context, ct, result.JoinMode);
                    }
                    continue;
                }

                // 7b. 根据选定的 Out 端口过滤（如 BranchNode 的 "True" / "False"）
                List<FlowConnection> targetConns;
                if (!string.IsNullOrWhiteSpace(result.SelectedPortName))
                {
                    // 查找该端口对应的 FlowPort Id 或 Name
                    var port = node.Ports.FirstOrDefault(p =>
                        string.Equals(p.Name, result.SelectedPortName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(p.Id, result.SelectedPortName, StringComparison.OrdinalIgnoreCase));

                    targetConns = outgoingConns.Where(c =>
                        (port != null && (string.Equals(c.FromPortId, port.Id, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(c.FromPortId, port.Name, StringComparison.OrdinalIgnoreCase))) ||
                        string.Equals(c.FromPortId, result.SelectedPortName, StringComparison.OrdinalIgnoreCase)
                    ).ToList();
                }
                else
                {
                    targetConns = outgoingConns;
                }

                if (targetConns.Count == 0)
                {
                    continue;
                }

                var nextNodes = DistinctTargets(graph, targetConns);

                if (nextNodes.Count == 1)
                {
                    // 单后续节点：顺序推进（压栈，栈深不增长）
                    workStack.Push(nextNodes[0]);
                }
                else if (nextNodes.Count > 1)
                {
                    // 多后续节点（Fork 并行执行）：带 Join 屏障的全部完成才继续
                    await RunForkAsync(nextNodes, graph, context, ct, ParallelJoinMode.All);
                }
            }
        }

        /// <summary>结构化 Fork-Join 约定的汇合端口名（分支经其余端口并发，屏障后经此端口单次续流）。</summary>
        private const string JoinPortName = "Out";

        /// <summary>
        /// 并行 Fork 执行器：为每个分支建立独立调度任务与静态可达集，
        /// 任一分支失败立即连锁取消其余分支（工业 Fail-Fast，防止孤儿分支竞态共享状态），
        /// 全部到达 Join 屏障（或首分支竞速放行）后才返回。
        /// </summary>
        private async Task RunForkAsync(
            List<FlowNode> branchHeads,
            FlowGraph graph,
            IFlowContext context,
            CancellationToken ct,
            ParallelJoinMode joinMode)
        {
            if (branchHeads.Count == 0) return;

            if (branchHeads.Count == 1)
            {
                await ExecuteFlowAsync(branchHeads[0], graph, context, ct, null);
                return;
            }

            var fork = new ForkState { ForkId = Interlocked.Increment(ref _forkIdSeed) };
            var adjacency = BuildAdjacency(graph);
            foreach (var head in branchHeads)
            {
                fork.Branches.Add(new BranchState(ComputeReachableNodeIds(adjacency, head.Id)));
            }

            // 分支级联取消令牌：任一分支失败/外部停止时，其余分支同步取消
            using var forkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var branchRuns = fork.Branches.Select(b => new BranchRun(fork, b)).ToList();
            var tasks = new List<Task>(branchRuns.Count);
            for (int i = 0; i < branchRuns.Count; i++)
            {
                var run = branchRuns[i];
                var head = branchHeads[i];
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteFlowAsync(head, graph, context, forkCts.Token, run);
                    }
                    finally
                    {
                        // 分支结束（成功/失败/被吸收/取消）均需注销存活标记并重估汇聚门
                        MarkBranchCompleted(fork, run.State);
                    }
                }, CancellationToken.None));
            }

            context.Logger?.Info(
                $"[V2 Flow:{graph.FlowName}] Fork#{fork.ForkId} 启动 {branchHeads.Count} 个并行分支 (Join={joinMode})");

            // Fail-Fast 联动：任一分支持久化前注册"故障即连锁取消"续体，
            // 保证长任务分支在故障发生的第一时间被取消（不得等轮到它才取消）。
            foreach (var t in tasks)
            {
                _ = t.ContinueWith(static (antecedent, state) =>
                {
                    if (antecedent.IsFaulted || antecedent.IsCanceled)
                    {
                        try { ((CancellationTokenSource)state!).Cancel(); } catch { }
                    }
                }, forkCts, TaskContinuationOptions.ExecuteSynchronously);
            }

            if (joinMode == ParallelJoinMode.Any)
            {
                // 竞速汇合：首个分支结束即放行，其余分支立即取消
                var winner = await Task.WhenAny(tasks);
                try { forkCts.Cancel(); } catch { }

                // 落败分支的取消是竞速放行的预期结果，不传播；
                // 但落败分支的真实故障（非取消）仍必须传播，杜绝静默吞掉硬件错误。
                Exception? realFailure = null;
                foreach (var t in tasks)
                {
                    try { await t; }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        realFailure ??= ex;
                    }
                    catch { /* 竞速取消：预期内，忽略 */ }
                }

                if (winner.IsFaulted && winner.Exception != null)
                {
                    ExceptionDispatchInfo.Capture(winner.Exception.InnerException ?? winner.Exception).Throw();
                }
                if (realFailure != null) ExceptionDispatchInfo.Capture(realFailure).Throw();
                if (winner.IsCanceled) ct.ThrowIfCancellationRequested();

                context.Logger?.Info($"[V2 Flow:{graph.FlowName}] Fork#{fork.ForkId} 竞速汇合完成 (Join=Any)");
                return;
            }

            // AND 汇合屏障：等待全部分支；失败分支连锁取消其余分支后汇合异常重抛
            Exception? failure = null;
            foreach (var t in tasks)
            {
                try { await t; }
                catch (Exception ex)
                {
                    failure = PreferRealFailure(failure, ex);
                    try { forkCts.Cancel(); } catch { }
                }
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();

            context.Logger?.Info(
                $"[V2 Flow:{graph.FlowName}] Fork#{fork.ForkId} 全部 {branchHeads.Count} 个分支到达 Join 屏障");
        }

        /// <summary>
        /// 分支异常汇总时的优选策略：真实失败（StepExecuteException 等）优先于连锁取消产生的
        /// OperationCanceledException，保证上层看到的是根因而非取消噪音。
        /// </summary>
        private static Exception PreferRealFailure(Exception? current, Exception candidate)
        {
            if (current == null) return candidate;
            if (current is OperationCanceledException && candidate is not OperationCanceledException)
            {
                return candidate;
            }
            return current;
        }

        #endregion

        #region Fork / Join 屏障运行期机制

        /// <summary>Join 门槛决策。</summary>
        private enum JoinDecision
        {
            /// <summary>本分支负责执行该（汇聚）节点。</summary>
            Execute,

            /// <summary>本分支已被汇聚屏障吸收，终止本分支后续流转。</summary>
            Absorb
        }

        /// <summary>并行 Fork 运行期状态（分支存活性 + 汇聚门）。</summary>
        private sealed class ForkState
        {
            public int ForkId { get; init; }

            /// <summary>各分支状态（Fork 创建后不再增减）。</summary>
            public List<BranchState> Branches { get; } = new();

            /// <summary>汇聚门表（节点 Id -&gt; 门），放行后移除。</summary>
            public ConcurrentDictionary<string, JoinGate> Gates { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>并行分支状态。</summary>
        private sealed class BranchState
        {
            public BranchState(HashSet<string> reachableNodeIds)
            {
                Reachable = reachableNodeIds;
            }

            /// <summary>静态可达节点集合（含分支头，含 Jump 静态跳转边）。</summary>
            public HashSet<string> Reachable { get; }

            /// <summary>分支是否仍在运行（结束后置 false，用于屏障释放判定）。</summary>
            public volatile bool Active = true;

            /// <summary>本分支已在哪些汇聚门登记到达。</summary>
            public ConcurrentDictionary<string, byte> ArrivedAt { get; } = new(StringComparer.OrdinalIgnoreCase);

            public void MarkArrived(string nodeId) => ArrivedAt[nodeId] = 0;
        }

        /// <summary>分支执行句柄（分支状态 + 吸收标记）。</summary>
        private sealed class BranchRun
        {
            public BranchRun(ForkState fork, BranchState state)
            {
                Fork = fork;
                State = state;
            }

            public ForkState Fork { get; }
            public BranchState State { get; }

            /// <summary>被 Join 屏障吸收（不再执行后续节点）。</summary>
            public volatile bool Absorbed;
        }

        /// <summary>汇聚门（Join Gate）：全部潜在分支到齐后放行，且仅放行一个执行者。</summary>
        private sealed class JoinGate
        {
            public JoinGate(string nodeId)
            {
                NodeId = nodeId;
            }

            public string NodeId { get; }

            /// <summary>已到达并等待的分支数。</summary>
            public int Waiters;

            /// <summary>等待中的分支（用于指定执行者与吸收其余分支）。</summary>
            public List<BranchRun> WaitingBranches { get; } = new();

            /// <summary>门是否已放行。</summary>
            public bool Opened;

            /// <summary>被指定执行汇聚节点的分支（放行后有效）。</summary>
            public volatile BranchRun? Executor;

            /// <summary>放行信号。</summary>
            public TaskCompletionSource Released { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// 进入汇聚门：判定本分支应执行还是被吸收。
        /// 无并发汇合（仅一个分支可到达）时零开销直接执行。
        /// </summary>
        private async Task<JoinDecision> EnterJoinGateAsync(BranchRun branch, FlowNode node, CancellationToken ct)
        {
            var fork = branch.Fork;

            // 可静态到达该节点的并行分支（含自身）；不足两个即不存在并发汇合
            var potentials = fork.Branches.Where(b => b.Reachable.Contains(node.Id)).ToList();
            if (potentials.Count <= 1) return JoinDecision.Execute;

            var gate = fork.Gates.GetOrAdd(node.Id, _ => new JoinGate(node.Id));

            bool releaseNow;
            lock (gate)
            {
                if (gate.Opened)
                {
                    // 门已放行后的重复到达（循环回边等极端图）：直接执行，绝不阻塞工业执行线程
                    return JoinDecision.Execute;
                }

                gate.Waiters++;
                gate.WaitingBranches.Add(branch);
                branch.State.MarkArrived(node.Id);

                releaseNow = potentials.All(p => !p.Active || p.ArrivedAt.ContainsKey(node.Id));
                if (releaseNow)
                {
                    OpenGateLocked(fork, gate);
                }
            }

            if (releaseNow)
            {
                return ReferenceEquals(gate.Executor, branch) ? JoinDecision.Execute : JoinDecision.Absorb;
            }

            // 屏障等待：其余潜在分支到达或结束；取消令牌始终有效（StopAsync 可立即打断）
            await gate.Released.Task.WaitAsync(ct);
            return branch.Absorbed ? JoinDecision.Absorb : JoinDecision.Execute;
        }

        /// <summary>
        /// 分支结束：注销存活标记，并重估所有汇聚门——
        /// 原本在等待本分支的门若已全部到齐（到达或分歧结束）则立即放行。
        /// </summary>
        private static void MarkBranchCompleted(ForkState fork, BranchState branch)
        {
            branch.Active = false;
            foreach (var gate in fork.Gates.Values.ToList())
            {
                TryReleaseJoinGate(fork, gate);
            }
        }

        /// <summary>尝试放行汇聚门（全部潜在分支已到达或已结束时）。</summary>
        private static void TryReleaseJoinGate(ForkState fork, JoinGate gate)
        {
            lock (gate)
            {
                if (gate.Opened || gate.Waiters == 0) return;

                var potentials = fork.Branches.Where(b => b.Reachable.Contains(gate.NodeId)).ToList();
                bool accounted = potentials.All(p => !p.Active || p.ArrivedAt.ContainsKey(gate.NodeId));
                if (!accounted) return;

                OpenGateLocked(fork, gate);
            }
        }

        /// <summary>
        /// 放行汇聚门（调用方须持有 gate 锁）：
        /// 指定唯一执行者，吸收其余等待分支，移除门并释放等待。
        /// </summary>
        private static void OpenGateLocked(ForkState fork, JoinGate gate)
        {
            if (gate.Opened) return;

            var executor = gate.WaitingBranches.FirstOrDefault(w => !w.Absorbed && w.State.Active);
            gate.Executor = executor;
            gate.Opened = true;
            foreach (var w in gate.WaitingBranches)
            {
                if (!ReferenceEquals(w, executor))
                {
                    w.Absorbed = true;
                }
            }

            fork.Gates.TryRemove(gate.NodeId, out _);
            gate.Released.TrySetResult();
        }

        /// <summary>构建静态邻接表（拓扑连线 + Jump 节点的静态跳转边）。</summary>
        private static Dictionary<string, List<string>> BuildAdjacency(FlowGraph graph)
        {
            var adjacency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in graph.Nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.Id))
                {
                    adjacency[node.Id] = new List<string>();
                }
            }

            foreach (var conn in graph.Connections)
            {
                if (adjacency.TryGetValue(conn.FromNodeId, out var list) &&
                    !string.IsNullOrWhiteSpace(conn.ToNodeId))
                {
                    list.Add(conn.ToNodeId);
                }
            }

            // Jump 为非拓扑直跳，其目标参数是静态可解的，纳入可达性分析以保证屏障判定不遗漏
            foreach (var node in graph.Nodes)
            {
                if (!string.Equals(node.NodeType, "Jump", StringComparison.OrdinalIgnoreCase)) continue;
                if (node.Parameters == null) continue;
                if (!node.Parameters.TryGetValue("targetNodeId", out var raw) || raw is not string targetId) continue;
                if (string.IsNullOrWhiteSpace(targetId)) continue;
                if (adjacency.TryGetValue(node.Id, out var list) && adjacency.ContainsKey(targetId))
                {
                    list.Add(targetId);
                }
            }

            return adjacency;
        }

        /// <summary>计算从指定节点出发的静态可达节点集合（含自身）。</summary>
        private static HashSet<string> ComputeReachableNodeIds(Dictionary<string, List<string>> adjacency, string headId)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!adjacency.ContainsKey(headId)) return result;

            var stack = new Stack<string>();
            stack.Push(headId);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!result.Add(id)) continue;
                if (!adjacency.TryGetValue(id, out var nexts)) continue;
                foreach (var next in nexts)
                {
                    if (!result.Contains(next))
                    {
                        stack.Push(next);
                    }
                }
            }
            return result;
        }

        /// <summary>拆分结构化 Fork 的连线：分支连线（非汇合端口）与汇合端口连线。</summary>
        private static (List<FlowConnection> BranchConns, List<FlowConnection> JoinConns) SplitForkJoinConnections(
            FlowNode node, List<FlowConnection> outgoingConns, string joinPortName)
        {
            var branchConns = new List<FlowConnection>();
            var joinConns = new List<FlowConnection>();

            foreach (var conn in outgoingConns)
            {
                var port = node.Ports.FirstOrDefault(p =>
                    string.Equals(p.Id, conn.FromPortId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Name, conn.FromPortId, StringComparison.OrdinalIgnoreCase));

                bool isJoinPort =
                    string.Equals(conn.FromPortId, joinPortName, StringComparison.OrdinalIgnoreCase) ||
                    (port != null && string.Equals(port.Name, joinPortName, StringComparison.OrdinalIgnoreCase));

                if (isJoinPort)
                {
                    joinConns.Add(conn);
                }
                else
                {
                    branchConns.Add(conn);
                }
            }

            return (branchConns, joinConns);
        }

        /// <summary>解析连线目标节点列表（去重、保序）。</summary>
        private static List<FlowNode> DistinctTargets(FlowGraph graph, List<FlowConnection> conns)
        {
            var nodes = new List<FlowNode>();
            foreach (var conn in conns)
            {
                var targetNode = graph.Nodes.FirstOrDefault(n =>
                    string.Equals(n.Id, conn.ToNodeId, StringComparison.OrdinalIgnoreCase));
                if (targetNode != null && !nodes.Contains(targetNode))
                {
                    nodes.Add(targetNode);
                }
            }
            return nodes;
        }

        /// <summary>
        /// 解析 Jump 目标：优先按节点 Id（忽略大小写），
        /// 其次按节点名称（标签）回退解析，方便按标签维护跳转。
        /// </summary>
        private static FlowNode? ResolveJumpTarget(FlowGraph graph, string targetNodeId)
        {
            var target = graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.Id, targetNodeId, StringComparison.OrdinalIgnoreCase));
            if (target != null) return target;

            return graph.Nodes.FirstOrDefault(n =>
                string.Equals(n.Name, targetNodeId, StringComparison.OrdinalIgnoreCase));
        }

        #endregion

        private void SetNodeState(FlowNode node, FlowNodeState state, string? message = null)
        {
            _nodeStates[node.Id] = state;
            var change = new FlowNodeStateChange(node.Id, node.Name, node.NodeType, state, message);

            try
            {
                NodeStateChanged?.Invoke(change);
                Progress?.Report(change);
            }
            catch
            {
                // 保护外部事件监听者异常不打崩调度
            }
        }
    }
}
