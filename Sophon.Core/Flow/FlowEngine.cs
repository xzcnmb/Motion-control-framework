#nullable enable
using Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core
{
    /// <summary>
    /// 流程引擎：单调度线程 + await 协作。
    /// 停止语义：取消令牌是唯一事实来源（无残留旗标）；Idle 状态调用 Stop 为无操作；
    /// 步骤失败统一抛出 StepExecuteException 交由调用方（WorkStation）决定状态。
    /// </summary>
    public class FlowEngine : IFlowEngine, IFlowController
    {
        /// <summary>
        /// 从 JSON 配置文件加载流程（兼容 v1 格式，经 FlowJsonMigrator 迁移到 v2 后由 Wave2-C 接管）。
        /// </summary>
        public FlowEngine(string flowName, IConfigManagerFactory configFactory)
        {
            FlowName = flowName;
            _configManager = configFactory.CreateConfigManager(ConfigType.json, FlowName, "FlowData");
            _steps = _configManager.LoadConfig<List<IFlowStep>>() ?? new List<IFlowStep>();
            _steps.RemoveAll(s => s == null);
        }

        /// <summary>
        /// 直接以步骤列表构造（单元测试/内存流程用，不经过配置文件）。
        /// </summary>
        public FlowEngine(string flowName, IReadOnlyList<IFlowStep> steps)
        {
            FlowName = flowName;
            _steps = steps?.Where(s => s != null).ToList() ?? new List<IFlowStep>();
        }

        public string FlowName { get; }

        public bool IsPaused => Volatile.Read(ref _paused) == 1;
        public bool IsRunning => Volatile.Read(ref _running) == 1;
        public bool IsStopped => Volatile.Read(ref _running) == 0;
        public int CurrentIndex => Volatile.Read(ref _currentIndex);

        private readonly List<IFlowStep> _steps;
        private IConfigManager? _configManager;

        private CancellationTokenSource? _cts;
        private int _running;   // 0/1
        private int _paused;    // 0/1
        private int _currentIndex;
        private readonly object _lock = new object();

        public async Task RunAsync(IFlowContext context, CancellationToken token)
        {
            // 启动互斥：只允许一个运行实例
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                context.Logger.Warn($"流程【{FlowName}】已经在运行中，忽略重复启动");
                return;
            }

            // CTS 所有权归引擎内部：链接调用方令牌，Stop/调用方取消统一生效
            lock (_lock)
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            var ct = _cts.Token;

            try
            {
                context.TotalSteps = _steps.Count;
                Interlocked.Exchange(ref _currentIndex, 0);
                context.NextStepIndex = 0;

                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        context.Logger.Info($"流程【{FlowName}】被停止");
                        break;
                    }

                    // 暂停：挂在步骤边界，等待恢复或停止
                    while (Volatile.Read(ref _paused) == 1 && !ct.IsCancellationRequested)
                    {
                        await Task.Delay(50, ct).ConfigureAwait(false);
                    }
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    int index = context.NextStepIndex;
                    if (index < 0 || index >= _steps.Count || _steps[index] == null)
                    {
                        break;
                    }
                    _currentIndex = index;

                    var step = _steps[index];
                    context.Logger.Info($"开始执行步骤【{step.StepName}】...");
                    var result = await step.AsyncExecuteStep(context, ct).ConfigureAwait(false);

                    if (result == null || (result.Status != StepStatus.Success && result.Status != StepStatus.Skipped))
                    {
                        string msg = $"步骤【{step.StepName}】执行{(result?.Status == StepStatus.Cancelled ? "被取消" : "失败")}：{result?.Message}";
                        context.Logger.Error(msg);
                        throw new StepExecuteException(msg);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                context.Logger.Info($"流程【{FlowName}】运行被取消");
            }
            catch (StepExecuteException e)
            {
                context.Logger.Error($"流程步骤失败：{e.Message}");
                throw;
            }
            catch (Exception e)
            {
                context.Logger.Error($"流程执行异常：{e}");
                throw;
            }
            finally
            {
                Interlocked.Exchange(ref _paused, 0);
                Interlocked.Exchange(ref _running, 0);
                lock (_lock)
                {
                    try { _cts?.Dispose(); } catch { }
                    _cts = null;
                }
                context.Logger.Info($"流程【{FlowName}】执行结束");
            }
        }

        public void Pause()
        {
            if (Volatile.Read(ref _running) == 1)
            {
                Interlocked.Exchange(ref _paused, 1);
            }
        }

        public void Resume()
        {
            Interlocked.Exchange(ref _paused, 0);
        }

        public void Stop()
        {
            // Idle（未运行）时无 CTS，天然成为无操作——修复原版 _isStopped 残留缺陷
            lock (_lock)
            {
                _cts?.Cancel();
            }
        }
    }
}