using Prism.Events;

namespace Sophon.Core.Event
{
    /// <summary>
    /// 侧边栏「流程编辑」节点面板点击后发布，载荷为节点类型（NodeType）。
    /// FlowEditorViewModel 订阅后在流程画布上添加对应节点。
    /// </summary>
    public class AddFlowNodeEvent : PubSubEvent<string>
    {
    }
}
