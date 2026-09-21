#nullable enable
using Prism.Mvvm;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class FlowConnectionViewModel : BindableBase
    {
        public FlowPortViewModel Source { get; }
        public FlowPortViewModel Target { get; }

        public FlowConnectionViewModel(FlowPortViewModel source, FlowPortViewModel target)
        {
            Source = source;
            Target = target;
        }

        public FlowConnection ToModel()
        {
            return new FlowConnection(Source.Node.Id, Source.Id, Target.Node.Id, Target.Id);
        }
    }
}
