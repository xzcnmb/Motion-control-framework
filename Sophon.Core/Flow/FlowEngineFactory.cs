#nullable enable
using System;
using Sophon.Common;
using Sophon.Contracts;
using Sophon.Core.Flow.V2;

namespace Sophon.Core
{
    /// <summary>
    /// 每次按流程图名新建 v2 宿主。必须把 IServiceProvider 传下去，视觉/外设节点才能从容器取依赖。
    /// </summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class FlowEngineFactory : IFlowEngineFactory
    {
        private readonly IMotionController? _motion;
        private readonly IIoController? _io;
        private readonly IServiceProvider? _services;

        public FlowEngineFactory(
            IMotionController? motionController = null,
            IIoController? ioController = null,
            IServiceProvider? services = null)
        {
            _motion = motionController;
            _io = ioController;
            _services = services;
        }

        public IFlowEngine CreateFlowEngine(string flowName)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程名不能为空", nameof(flowName));
            }

            return new FlowEngineV2Host(flowName, _motion, _io, EventBus.GetInstance(), _services);
        }
    }
}
