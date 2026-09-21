using Sophon.Common;
using System.Collections.Concurrent;

namespace Sophon.Core
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class WorkStationFactory : IWorkStationFactory
    {
        public WorkStationFactory(IFlowEngineFactory flowEngineFactory, IFlowContextFactory flowContextFactory)
        {
            _flowEngineFactory = flowEngineFactory;
            _flowContextFactory = flowContextFactory;
            WorkStationCache = new ConcurrentDictionary<string, IWorkStation>();
        }

        public ConcurrentDictionary<string, IWorkStation> WorkStationCache { get; }

        private readonly IFlowEngineFactory _flowEngineFactory;
        private readonly IFlowContextFactory _flowContextFactory;

        public IWorkStation CreateWorkStation(string workStationName)
        {
            return WorkStationCache.GetOrAdd(workStationName, name =>
                new WorkStation(name, _flowEngineFactory, _flowContextFactory, new StateMachine()));
        }
    }
}