#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// V2 节点逻辑基类。定义节点类型名、参数 Schema 元数据及异步执行核心契约。
    /// </summary>
    public abstract class FlowNodeBase
    {
        /// <summary>节点类型唯一标识符（例如 "Start", "Delay", "AxisMove"）。</summary>
        public abstract string NodeType { get; }

        /// <summary>节点显示名称。</summary>
        public virtual string Name => NodeType;

        /// <summary>节点参数 Schema 元数据，用于属性面板生成与默认值约束。</summary>
        public abstract IReadOnlyList<ParameterSchema> ParameterSchemas { get; }

        /// <summary>
        /// 执行节点逻辑。
        /// </summary>
        /// <param name="ctx">节点执行上下文。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>节点执行结果，包含后续流向端口与状态。</returns>
        public abstract Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct);

        /// <summary>
        /// 获取该类节点的默认端口拓扑配置（用于编辑器新拖拽节点时的默认端口初始化）。
        /// </summary>
        public virtual List<FlowPort> GetDefaultPorts()
        {
            return new List<FlowPort>
            {
                new("In", FlowPortDirection.In, "In"),
                new("Out", FlowPortDirection.Out, "Out")
            };
        }
    }
}
