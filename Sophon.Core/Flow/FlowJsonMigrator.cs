#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Sophon.Core.Flow.V2;

namespace Sophon.Core
{
    /// <summary>
    /// 流程文件版本。
    /// V1：旧版线性步骤列表（List&lt;IFlowStep&gt; 直接序列化的 JSON 数组，每项无类型描述）；
    /// V2：图结构（FlowGraph = Node + Connection，由 Wave2-C 定义）。
    /// </summary>
    public enum FlowJsonVersion
    {
        Unknown = 0,
        V1 = 1,
        V2 = 2,
    }

    /// <summary>
    /// 旧版流程 JSON（v1）→ 新版图形流程（v2）迁移器。
    /// v1 旧工程存量流程文件不得报废：检测版本 → 迁移为 v2 外壳。
    /// v2 实体（FlowGraph 等）由 Wave2-C 补齐后，本类中 V2 壳结构替换为正式实体。
    /// </summary>
    public static class FlowJsonMigrator
    {
        /// <summary>v2 外壳：Wave2-C 落地 FlowGraph 前的过渡结构。</summary>
        public class V2Shell
        {
            public int Version { get; set; } = 2;
            public string FlowName { get; set; } = string.Empty;
            public List<V2ShellNode> Nodes { get; set; } = new List<V2ShellNode>();
        }

        /// <summary>v2 外壳节点：保留 v1 步骤的类型名与原始属性。</summary>
        public class V2ShellNode
        {
            public string StepTypeName { get; set; } = string.Empty;
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public JsonElement Raw { get; set; }
        }

        /// <summary>
        /// 检测 JSON 文档的版本：
        /// - 根为对象且含 version==2 → V2；
        /// - 根为数组（元素含 StepName/stepName）→ V1；
        /// - 其余 → Unknown。
        /// </summary>
        public static FlowJsonVersion DetectVersion(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return FlowJsonVersion.Unknown;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var v) && v.TryGetInt32(out var ver) && ver == 2)
            {
                return FlowJsonVersion.V2;
            }
            if (root.ValueKind == JsonValueKind.Array)
            {
                return FlowJsonVersion.V1;
            }
            return FlowJsonVersion.Unknown;
        }

        /// <summary>
        /// v1 → v2 外壳迁移（保留旧版兼容）。v1 每项步骤按顺序成为 v2 的线性链（连线关系由 Wave2-C 消费
        /// "Nodes 顺序即执行顺序"约定补齐）。任何一项缺失 StepName 都视为迁移失败。
        /// </summary>
        /// <param name="json">v1 流程 JSON（数组）。</param>
        /// <param name="flowName">流程名。</param>
        /// <returns>v2 外壳；json 非 v1 时返回 null。</returns>
        public static V2Shell? MigrateV1ToV2(string json, string flowName)
        {
            if (DetectVersion(json) != FlowJsonVersion.V1)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var nodes = new List<V2ShellNode>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var stepName = TryGetStepName(element);
                if (stepName == null)
                {
                    throw new InvalidOperationException("v1 流程中存在缺失 StepName 的步骤，迁移中止");
                }
                nodes.Add(new V2ShellNode { StepTypeName = stepName, Raw = element.Clone() });
            }

            return new V2Shell { FlowName = flowName, Nodes = nodes };
        }

        /// <summary>
        /// 将 v1 流程 JSON 直接迁移为正式的 V2 图形拓扑 FlowGraph 实体。
        /// 自动生成 Start 入口节点，并将 v1 线性步骤链转换为带有 In/Out 端口连线的 FlowNode 链。
        /// </summary>
        /// <param name="json">v1 流程 JSON（步骤对象数组）。</param>
        /// <param name="flowName">流程名称。</param>
        /// <returns>构建完成的 FlowGraph 拓扑图，非 v1 格式时返回 null。</returns>
        public static FlowGraph? MigrateV1ToFlowGraph(string json, string flowName)
        {
            if (DetectVersion(json) != FlowJsonVersion.V1)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var graph = new FlowGraph
            {
                Version = 2,
                FlowName = flowName,
                Nodes = new List<FlowNode>(),
                Connections = new List<FlowConnection>()
            };

            // 1. 创建唯一的 Start 入口节点
            var startNode = new FlowNode("Start", "起始")
            {
                Position = new FlowPosition(100, 100),
                Ports = new List<FlowPort>
                {
                    new FlowPort("Out", FlowPortDirection.Out, "Out")
                }
            };
            graph.Nodes.Add(startNode);

            var previousNode = startNode;
            int stepIndex = 0;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var stepName = TryGetStepName(element);
                if (stepName == null)
                {
                    throw new InvalidOperationException("v1 流程中存在缺失 StepName 的步骤，迁移中止");
                }

                string rawType = TryGetTypeName(element) ?? "";
                string nodeType = MapStepTypeToNodeType(rawType, stepName);

                var flowNode = new FlowNode(nodeType, stepName)
                {
                    Position = new FlowPosition(100 + (stepIndex + 1) * 200, 100),
                    Ports = new List<FlowPort>
                    {
                        new FlowPort("In", FlowPortDirection.In, "In"),
                        new FlowPort("Out", FlowPortDirection.Out, "Out")
                    }
                };

                // 复制参数
                foreach (var prop in element.EnumerateObject())
                {
                    flowNode.Parameters[prop.Name] = prop.Value.Clone();
                }

                // 兼容参数名映射
                if (element.TryGetProperty("_delayTime_ms", out var delayElem) && delayElem.TryGetInt32(out var delayVal))
                {
                    flowNode.Parameters["delayMs"] = delayVal;
                }

                graph.Nodes.Add(flowNode);

                // 建立从上一个节点 Out 到当前节点 In 的顺序连接
                graph.Connections.Add(new FlowConnection(
                    fromNodeId: previousNode.Id,
                    fromPortId: "Out",
                    toNodeId: flowNode.Id,
                    toPortId: "In"
                ));

                previousNode = flowNode;
                stepIndex++;
            }

            return graph;
        }

        private static string MapStepTypeToNodeType(string rawType, string stepName)
        {
            if (rawType.Contains("Delay", StringComparison.OrdinalIgnoreCase) || stepName.Contains("延时"))
                return "Delay";
            if (rawType.Contains("Home", StringComparison.OrdinalIgnoreCase) || stepName.Contains("回零"))
                return "AxisHome";
            if (rawType.Contains("Move", StringComparison.OrdinalIgnoreCase) || stepName.Contains("定位") || stepName.Contains("移动"))
                return "AxisMove";
            if (rawType.Contains("DiWait", StringComparison.OrdinalIgnoreCase) || stepName.Contains("等待输入"))
                return "DiWait";
            if (rawType.Contains("DoSet", StringComparison.OrdinalIgnoreCase) || stepName.Contains("设置输出"))
                return "DoSet";
            if (rawType.Contains("EventPublish", StringComparison.OrdinalIgnoreCase) || stepName.Contains("发布事件"))
                return "EventPublish";
            if (rawType.Contains("EventSubscribe", StringComparison.OrdinalIgnoreCase) || rawType.Contains("EventWait", StringComparison.OrdinalIgnoreCase) || stepName.Contains("订阅事件") || stepName.Contains("等待事件"))
                return "EventWait";
            if (rawType.Contains("Loop", StringComparison.OrdinalIgnoreCase) || stepName.Contains("循环"))
                return "Loop";
            if (rawType.Contains("Parallel", StringComparison.OrdinalIgnoreCase) || stepName.Contains("并行"))
                return "Parallel";
            if (rawType.Contains("Jump", StringComparison.OrdinalIgnoreCase) || stepName.Contains("跳转"))
                return "Jump";

            return "Delay"; // 默认安全兜底
        }

        private static string? TryGetTypeName(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            foreach (var candidate in new[] { "__type", "Type", "type", "$type" })
            {
                if (element.TryGetProperty(candidate, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    return p.GetString();
                }
            }
            return null;
        }

        private static string? TryGetStepName(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (var candidate in new[] { "StepName", "stepName" })
            {
                if (element.TryGetProperty(candidate, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    return p.GetString();
                }
            }
            return null;
        }
    }
}