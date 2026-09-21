using System.Collections.Concurrent;
using System.Linq;
using Sophon.Common;

namespace Sophon.Core
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class WorkStationFactory : IWorkStationFactory
    {
        public WorkStationFactory(
            IFlowEngineFactory flowEngineFactory,
            IFlowContextFactory flowContextFactory,
            WorkStationProfileStore? profileStore = null)
        {
            _flowEngineFactory = flowEngineFactory;
            _flowContextFactory = flowContextFactory;
            _profileStore = profileStore;
            WorkStationCache = new ConcurrentDictionary<string, IWorkStation>();
        }

        public ConcurrentDictionary<string, IWorkStation> WorkStationCache { get; }

        private readonly IFlowEngineFactory _flowEngineFactory;
        private readonly IFlowContextFactory _flowContextFactory;
        private readonly WorkStationProfileStore? _profileStore;

        public IWorkStation CreateWorkStation(string workStationName)
        {
            return WorkStationCache.GetOrAdd(workStationName, name =>
            {
                var profile = _profileStore?.Load()
                    .FirstOrDefault(p => string.Equals(p.StationName, name, System.StringComparison.Ordinal));
                var options = profile == null
                    ? WorkStationOptions.Cyclic(name)
                    : new WorkStationOptions
                    {
                        BoundFlowName = string.IsNullOrWhiteSpace(profile.BoundFlowName) ? name : profile.BoundFlowName,
                        LoopRecipe = profile.LoopRecipe
                    };
                return new WorkStation(name, _flowEngineFactory, _flowContextFactory, new StateMachine(), options);
            });
        }
    }
}
