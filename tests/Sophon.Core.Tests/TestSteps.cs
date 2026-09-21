using Sophon.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 每测试实例独立的执行记录器（并行测试互不干扰）。
    /// </summary>
    public sealed class ExecutionRecorder
    {
        private readonly object _sync = new object();
        private readonly List<string> _names = new List<string>();

        public void Record(string stepName)
        {
            lock (_sync) { _names.Add(stepName); }
        }

        public string[] Names
        {
            get { lock (_sync) { return _names.ToArray(); } }
        }

        public bool IsEmpty
        {
            get { lock (_sync) { return _names.Count == 0; } }
        }

        public void Clear()
        {
            lock (_sync) { _names.Clear(); }
        }
    }

    /// <summary>
    /// 测试步骤族：计数/延时/失败/门控。
    /// </summary>
    public static class TestSteps
    {
        /// <summary>计数步骤：执行即记名，步进+1。</summary>
        public sealed class CountingStep : IFlowStep
        {
            public CountingStep(string name, ExecutionRecorder recorder)
            {
                StepName = name;
                _recorder = recorder;
            }
            public string StepName { get; }
            private readonly ExecutionRecorder _recorder;

            public Task<StepResult> AsyncExecuteStep(IFlowContext context, CancellationToken token)
            {
                _recorder.Record(StepName);
                context.NextStepIndex++;
                return Task.FromResult(StepResult.Success());
            }
        }

        /// <summary>延时步骤。</summary>
        public sealed class DelayStep : IFlowStep
        {
            public DelayStep(string name, int delayMs) { StepName = name; _delayMs = delayMs; }
            public string StepName { get; }
            private readonly int _delayMs;

            public async Task<StepResult> AsyncExecuteStep(IFlowContext context, CancellationToken token)
            {
                await Task.Delay(_delayMs, token);
                context.NextStepIndex++;
                return StepResult.Success();
            }
        }

        /// <summary>失败步骤：返回 Failure，引擎应抛 StepExecuteException。</summary>
        public sealed class FailStep : IFlowStep
        {
            public FailStep(string name) => StepName = name;
            public string StepName { get; }

            public Task<StepResult> AsyncExecuteStep(IFlowContext context, CancellationToken token)
            {
                context.NextStepIndex++;
                return Task.FromResult(StepResult.Failure($"{StepName} 故意失败"));
            }
        }

        /// <summary>
        /// 门控步骤：等待信号；用来确定性地测试"暂停挂在步骤边界"。
        /// </summary>
        public sealed class GateStep : IFlowStep
        {
            public GateStep(string name, ManualResetEventSlim gate, ManualResetEventSlim entered)
            {
                StepName = name;
                _gate = gate;
                _entered = entered;
            }
            public string StepName { get; }
            private readonly ManualResetEventSlim _gate;
            private readonly ManualResetEventSlim _entered;

            public Task<StepResult> AsyncExecuteStep(IFlowContext context, CancellationToken token)
            {
                _entered.Set();
                _gate.Wait(token);
                context.NextStepIndex++;
                return Task.FromResult(StepResult.Success());
            }
        }
    }

    /// <summary>测试公共辅助。</summary>
    public static class TestHelper
    {
        public static async Task<bool> WaitUntilAsync(System.Func<bool> condition, int timeoutMs = 3000, int intervalMs = 20)
        {
            var deadline = System.Environment.TickCount64 + timeoutMs;
            while (System.Environment.TickCount64 < deadline)
            {
                if (condition())
                {
                    return true;
                }
                await Task.Delay(intervalMs);
            }
            return condition();
        }
    }
}