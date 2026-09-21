#nullable enable
using System;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 节点状态变更事件参数，用于 UI 高亮、运行跟踪与进度监控。
    /// </summary>
    public class FlowNodeStateChange
    {
        /// <summary>节点唯一标识符。</summary>
        public string NodeId { get; }

        /// <summary>节点名称。</summary>
        public string NodeName { get; }

        /// <summary>节点类型。</summary>
        public string NodeType { get; }

        /// <summary>变更后的节点状态。</summary>
        public FlowNodeState State { get; }

        /// <summary>状态发生变更的时间戳。</summary>
        public DateTime Timestamp { get; }

        /// <summary>附加消息（如失败错误信息、耗时等）。</summary>
        public string? Message { get; }

        public FlowNodeStateChange(string nodeId, string nodeName, string nodeType, FlowNodeState state, string? message = null)
        {
            NodeId = nodeId;
            NodeName = nodeName;
            NodeType = nodeType;
            State = state;
            Timestamp = DateTime.UtcNow;
            Message = message;
        }

        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss.fff}] Node '{NodeName}'({NodeId}) -> {State} {(Message != null ? $"({Message})" : "")}";
    }
}
