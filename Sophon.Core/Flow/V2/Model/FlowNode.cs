#nullable enable
using System;
using System.Collections.Generic;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 流程图节点定义。
    /// </summary>
    public class FlowNode
    {
        /// <summary>
        /// 节点唯一标识符（Guid 字符串）。
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 节点类型标识（如 "Start", "Delay", "AxisMove" 等）。
        /// </summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>
        /// 节点显示名称。
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 节点在画布上的坐标位置。
        /// </summary>
        public FlowPosition Position { get; set; } = new FlowPosition();

        /// <summary>
        /// 节点业务参数字典。
        /// </summary>
        public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 节点端口列表。
        /// </summary>
        public List<FlowPort> Ports { get; set; } = new List<FlowPort>();

        public FlowNode() { }

        public FlowNode(string nodeType, string name, string? id = null)
        {
            NodeType = nodeType;
            Name = name;
            if (!string.IsNullOrWhiteSpace(id))
            {
                Id = id;
            }
        }
    }
}
