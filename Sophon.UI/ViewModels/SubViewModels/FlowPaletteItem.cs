using System.Collections.ObjectModel;

namespace Sophon.UI.ViewModels
{
    /// <summary>
    /// 侧边栏「流程编辑」节点面板的分类组（TreeView 的一级节点，默认折叠）。
    /// 分类本身不可点击添加，仅展开/折叠；双击叶子节点才会添加节点到画布。
    /// </summary>
    public class FlowPaletteGroup
    {
        public string Category { get; set; } = string.Empty;

        public ObservableCollection<FlowPaletteItem> Items { get; } = new();
    }

    /// <summary>
    /// 侧边栏「流程编辑」节点面板的叶子节点（TreeView 的二级节点）。
    /// NodeType 为 FlowNodeRegistry 注册名，双击时经 AddFlowNodeCommand 加入画布。
    /// </summary>
    public class FlowPaletteItem
    {
        public string NodeType { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }
}
