#nullable enable
using System;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 端口流向。
    /// </summary>
    public enum FlowPortDirection
    {
        In,
        Out
    }

    /// <summary>
    /// 流程图节点端口。
    /// </summary>
    public class FlowPort
    {
        /// <summary>
        /// 端口唯一标识符。
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 端口名称（例如 "In", "Out", "Next", "True", "False"）。
        /// </summary>
        public string Name { get; set; } = "Out";

        /// <summary>
        /// 端口方向（输入/输出）。
        /// </summary>
        public FlowPortDirection Direction { get; set; } = FlowPortDirection.Out;

        public FlowPort() { }

        public FlowPort(string name, FlowPortDirection direction, string? id = null)
        {
            Name = name;
            Direction = direction;
            if (!string.IsNullOrWhiteSpace(id))
            {
                Id = id;
            }
        }
    }
}
