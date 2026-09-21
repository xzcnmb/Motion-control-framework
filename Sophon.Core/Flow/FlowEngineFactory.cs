#nullable enable
using System;
using Sophon.Common;
using Sophon.Contracts;
using Sophon.Core.Flow.V2;

namespace Sophon.Core
{
    /// <summary>
    /// 每次按流程图名新建 v2 宿主，不按工站名缓存（多个工站可绑同一张图，但引擎实例不能共享）。
    /// </summary>
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class FlowEngineFactory : IFlowEngineFactory
    {
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

            return new FlowEngineV2Host(flowName, _motion, _io, EventBus.GetInstance());
        }
    }
}
