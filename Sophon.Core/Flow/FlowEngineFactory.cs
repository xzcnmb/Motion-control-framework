#nullable enable
using System;
using System.Collections.Concurrent;
using Sophon.Common;
using Sophon.Contracts;
using Sophon.Core.Flow.V2;

namespace Sophon.Core
{
    /// <summary>
    /// 工站流程引擎工厂。生产路径只创建 v2 宿主（读 FlowGraphStore）。
    /// 单元测试仍可通过 FakeFlowEngineFactory 注入 v1 线性步骤。
    /// </summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class FlowEngineFactory : IFlowEngineFactory
    {
        private readonly ConcurrentDictionary<string, IFlowEngine> _flowEngineCache = new(StringComparer.Ordinal);
        private readonly IMotionController? _motion;
        private readonly IIoController? _io;

        public FlowEngineFactory(
            IMotionController? motionController = null,
            IIoController? ioController = null)
        {
            _motion = motionController;
            _io = ioController;
        }

        public IFlowEngine CreateFlowEngine(string flowName)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程名不能为空", nameof(flowName));
            }

            return _flowEngineCache.GetOrAdd(flowName, name =>
                new FlowEngineV2Host(name, _motion, _io, EventBus.GetInstance()));
        }
    }
}
