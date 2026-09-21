#nullable enable
using System.Windows;
using System.Windows.Media;
using Prism.Mvvm;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class FlowPortViewModel : BindableBase
    {
        public string Id { get; }
        public string Name { get; set; }
        public FlowPortDirection Direction { get; }
        public FlowNodeViewModel Node { get; }

        private Point _anchor;
        public Point Anchor
        {
            get => _anchor;
            set => SetProperty(ref _anchor, value);
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public Brush PortBrush => Direction == FlowPortDirection.In
            ? new SolidColorBrush(Color.FromRgb(24, 144, 255))
            : new SolidColorBrush(Color.FromRgb(82, 196, 26));

        public FlowPortViewModel(FlowNodeViewModel node, FlowPort port)
        {
            Node = node;
            Id = string.IsNullOrWhiteSpace(port.Id) ? System.Guid.NewGuid().ToString("N") : port.Id;
            Name = port.Name;
            Direction = port.Direction;
        }

        public FlowPortViewModel(FlowNodeViewModel node, string name, FlowPortDirection direction, string? id = null)
        {
            Node = node;
            Id = string.IsNullOrWhiteSpace(id) ? System.Guid.NewGuid().ToString("N") : id;
            Name = name;
            Direction = direction;
        }

        public FlowPort ToModel()
        {
            return new FlowPort(Name, Direction, Id);
        }
    }
}
