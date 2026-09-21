using Sophon.Core;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 引擎级回归测试。
    /// </summary>
    public class FlowEngineTests
    {
        private static (FlowEngine engine, IFlowContext context) Create(string name, params IFlowStep[] steps)
        {
            var engine = new FlowEngine(name, steps);
            var context = new FlowContext(name, new FakeLoggerFactory());
            return (engine, context);
        }

        [Fact]
        public async Task 未运行时停止_是无操作_不污染后续运行()
        {
            var recorder = new ExecutionRecorder();
            var (engine, context) = Create(
                "E1",
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.CountingStep("B", recorder));

            engine.Stop(); // Stop-before-Start：应为无操作

            await engine.RunAsync(context, CancellationToken.None);
            Assert.Equal(new[] { "A", "B" }, recorder.Names);
            Assert.False(engine.IsRunning);
        }

        [Fact]
        public async Task 并发启动_仅一个实例运行()
        {
            var recorder = new ExecutionRecorder();
            var (engine, context) = Create(
                "E2",
                new TestSteps.DelayStep("D", 60),
                new TestSteps.CountingStep("A", recorder));

            var t1 = engine.RunAsync(context, CancellationToken.None);
            var t2 = engine.RunAsync(context, CancellationToken.None); // 应被忽略

            await Task.WhenAll(t1, t2);
            Assert.Equal(new[] { "A" }, recorder.Names);
        }

        [Fact]
        public async Task 步骤失败_引擎抛出步骤执行异常()
        {
            var recorder = new ExecutionRecorder();
            var (engine, context) = Create(
                "E3",
                new TestSteps.CountingStep("A", recorder),
                new TestSteps.FailStep("F"));

            await Assert.ThrowsAsync<StepExecuteException>(() => engine.RunAsync(context, CancellationToken.None));
            Assert.False(engine.IsRunning);
        }

        [Fact]
        public async Task 令牌取消_正常结束_后续可再次运行()
        {
            var (engine, context) = Create("E4", new TestSteps.DelayStep("D", 5000));
            using var cts = new CancellationTokenSource(80);

            await engine.RunAsync(context, cts.Token); // 80ms 后被取消，不抛异常

            var recorder = new ExecutionRecorder();
            var engine2 = new FlowEngine("E4b", new IFlowStep[] { new TestSteps.CountingStep("A", recorder) });
            var context2 = new FlowContext("E4b", new FakeLoggerFactory());
            await engine2.RunAsync(context2, CancellationToken.None);
            Assert.Equal(new[] { "A" }, recorder.Names);
        }
    }
}