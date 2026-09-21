#nullable enable
using System.Windows.Media;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class ToolboxItemViewModel
    {
        public string NodeType { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Brush CategoryBrush { get; set; } = Brushes.Gray;
    }
}
