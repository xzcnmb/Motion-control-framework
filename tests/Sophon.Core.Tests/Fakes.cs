using Common;
using Sophon.Core;
using System.Collections.Generic;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 测试用日志假实现（无操作，仅满足依赖）。
    /// </summary>
    public sealed class FakeLoggerManager : ILoggerManager
    {
        public void Trace(string msg) { }
        public void Debug(string msg) { }
        public void Info(string msg) { }
        public void Warn(string msg) { }
        public void Error(string msg) { }
        public void Fatal(string msg) { }
    }

    public sealed class FakeLoggerFactory : ILoggerFactory
    {
        public ILoggerManager CreateLogger(string loggername) => new FakeLoggerManager();
    }

    /// <summary>
    /// 流程引擎工厂假实现：绕开 JSON 配置，直接用步骤列表造引擎。
    /// </summary>
    public sealed class FakeFlowEngineFactory : IFlowEngineFactory
    {
        private readonly IReadOnlyList<IFlowStep> _steps;

        public FakeFlowEngineFactory(IReadOnlyList<IFlowStep> steps)
        {
            _steps = steps;
        }

        public IFlowEngine CreateFlowEngine(string flowName) => new FlowEngine(flowName, _steps);
    }

    /// <summary>
    /// 流程上下文工厂假实现。
    /// </summary>
    public sealed class FakeFlowContextFactory : IFlowContextFactory
    {
        public IFlowContext CreateFlowContext(string flowName) => new FlowContext(flowName, new FakeLoggerFactory());
    }
}