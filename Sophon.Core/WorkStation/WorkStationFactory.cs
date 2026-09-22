#nullable enable
using System.Collections.Concurrent;
using System.Linq;
using Sophon.Common;
using Sophon.Contracts;

namespace Sophon.Core
{
    [InjectableAttribute(DependencyLifetime.Singleton)]
    public class WorkStationFactory : IWorkStationFactory
    {
        public WorkStationFactory(
            IFlowEngineFactory flowEngineFactory,
            IFlowContextFactory flowContextFactory,
            WorkStationProfileStore? profileStore = null,
            IMotionController? motion = null,
            AxisGroupLease? axisLease = null,
            AxisGroupStore? axisGroupStore = null)
        {
            _flowEngineFactory = flowEngineFactory;
            _flowContextFactory = flowContextFactory;
            _profileStore = profileStore;
            _motion = motion;
            _axisLease = axisLease;
            _axisGroupStore = axisGroupStore;
            WorkStationCache = new ConcurrentDictionary<string, IWorkStation>();
        }

        public ConcurrentDictionary<string, IWorkStation> WorkStationCache { get; }

        private readonly IFlowEngineFactory _flowEngineFactory;
        private readonly IFlowContextFactory _flowContextFactory;
        private readonly WorkStationProfileStore? _profileStore;
        private readonly IMotionController? _motion;
        private readonly AxisGroupLease? _axisLease;
        private readonly AxisGroupStore? _axisGroupStore;

        public IWorkStation CreateWorkStation(string workStationName)
        {
            return WorkStationCache.GetOrAdd(workStationName, name =>
            {
                var profile = _profileStore?.Load()
                    .FirstOrDefault(p => string.Equals(p.StationName, name, System.StringComparison.Ordinal));
                var options = profile == null
                    ? new WorkStationOptions { LoopRecipe = true, BoundFlowName = string.Empty }
                    : new WorkStationOptions
                    {
                        BoundFlowName = profile.BoundFlowName,
                        LoopRecipe = profile.LoopRecipe,
                        AxisGroupName = profile.AxisGroupName
                    };
                return new WorkStation(
                    name,
                    _flowEngineFactory,
                    _flowContextFactory,
                    new StateMachine(),
                    options,
                    _motion,
                    _axisLease,
                    _axisGroupStore);
            });
        }
    }
}
