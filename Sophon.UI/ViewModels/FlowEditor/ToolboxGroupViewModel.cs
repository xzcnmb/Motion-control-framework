#nullable enable
using System.Collections.ObjectModel;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class ToolboxGroupViewModel
    {
        public string CategoryName { get; set; } = string.Empty;
        public ObservableCollection<ToolboxItemViewModel> Items { get; } = new();

        public ToolboxGroupViewModel() { }

        public ToolboxGroupViewModel(string categoryName)
        {
            CategoryName = categoryName;
        }
    }
}
