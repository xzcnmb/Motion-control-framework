#nullable enable
using System.Windows.Input;
using Prism.Commands;
using Prism.Mvvm;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class PendingConnectionViewModel : BindableBase
    {
        private readonly FlowEditorViewModel _editor;
        private FlowPortViewModel? _source;

        public ICommand StartCommand { get; }
        public ICommand FinishCommand { get; }

        public PendingConnectionViewModel(FlowEditorViewModel editor)
        {
            _editor = editor;
            StartCommand = new DelegateCommand<FlowPortViewModel>(OnStart);
            FinishCommand = new DelegateCommand<FlowPortViewModel>(OnFinish);
        }

        private void OnStart(FlowPortViewModel? source)
        {
            _source = source;
        }

        private void OnFinish(FlowPortViewModel? target)
        {
            if (_source != null && target != null)
            {
                _editor.ConnectPorts(_source, target);
            }
            _source = null;
        }
    }
}
