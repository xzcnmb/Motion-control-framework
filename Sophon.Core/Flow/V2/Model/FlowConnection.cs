#nullable enable
namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 流程图节点之间的连线。
    /// </summary>
    public class FlowConnection
    {
        /// <summary>
        /// 源节点 ID。
        /// </summary>
        public string FromNodeId { get; set; } = string.Empty;

        /// <summary>
        /// 源节点端口 ID。
        /// </summary>
        public string FromPortId { get; set; } = string.Empty;

        /// <summary>
        /// 目标节点 ID。
        /// </summary>
        public string ToNodeId { get; set; } = string.Empty;

        /// <summary>
        /// 目标节点端口 ID。
        /// </summary>
        public string ToPortId { get; set; } = string.Empty;

        public FlowConnection() { }

        public FlowConnection(string fromNodeId, string fromPortId, string toNodeId, string toPortId)
        {
            FromNodeId = fromNodeId;
            FromPortId = fromPortId;
            ToNodeId = toNodeId;
            ToPortId = toPortId;
        }
    }
}
