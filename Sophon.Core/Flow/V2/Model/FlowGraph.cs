#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// V2 图形流程模型（DAG / 图执行拓扑）。
    /// </summary>
    public class FlowGraph
    {
        private static readonly JsonSerializerOptions DefaultJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// 流程规范版本，固定为 2。
        /// </summary>
        public int Version { get; set; } = 2;

        /// <summary>
        /// 流程名称。
        /// </summary>
        public string FlowName { get; set; } = string.Empty;

        /// <summary>
        /// 流程节点列表。
        /// </summary>
        public List<FlowNode> Nodes { get; set; } = new List<FlowNode>();

        /// <summary>
        /// 节点连线列表。
        /// </summary>
        public List<FlowConnection> Connections { get; set; } = new List<FlowConnection>();

        /// <summary>
        /// 校验图结构的合法性。
        /// 规则：
        /// 1. Start 节点唯一且存在；
        /// 2. 每个非 Start 节点至少有一条入边；
        /// 3. 无悬空连接（源/目标节点或端口不存在）；
        /// 4. 静态环检测（DFS 发现回路时报告完整环路路径）。
        /// </summary>
        /// <param name="errors">校验失败的错误列表。</param>
        /// <returns>若无错误返回 true，否则返回 false。</returns>
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();

            // 1. Start 节点唯一且存在
            var startNodes = Nodes.Where(n => string.Equals(n.NodeType, "Start", StringComparison.OrdinalIgnoreCase)).ToList();
            if (startNodes.Count == 0)
            {
                errors.Add("流程图中缺失 Start 节点（必须有且仅有一个 Start 节点）");
            }
            else if (startNodes.Count > 1)
            {
                errors.Add($"流程图中存在多个 Start 节点（共 {startNodes.Count} 个），Start 节点必须唯一");
            }

            var nodeDict = new Dictionary<string, FlowNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                if (string.IsNullOrWhiteSpace(node.Id))
                {
                    errors.Add($"存在未指定 Id 的节点: {node.Name}");
                    continue;
                }
                if (nodeDict.ContainsKey(node.Id))
                {
                    errors.Add($"存在重复的节点 Id: {node.Id}（节点名称: {node.Name}）");
                }
                else
                {
                    nodeDict[node.Id] = node;
                }
            }

            // 2. 悬空连接检测
            var incomingNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Connections.Count; i++)
            {
                var conn = Connections[i];
                bool fromValid = true;
                bool toValid = true;

                if (!nodeDict.TryGetValue(conn.FromNodeId, out var fromNode))
                {
                    errors.Add($"连线[{i}]源节点不存在: FromNodeId='{conn.FromNodeId}'");
                    fromValid = false;
                }
                else if (!string.IsNullOrWhiteSpace(conn.FromPortId))
                {
                    if (!fromNode.Ports.Any(p => string.Equals(p.Id, conn.FromPortId, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(p.Name, conn.FromPortId, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"连线[{i}]源端口不存在: 节点 '{fromNode.Name}'({fromNode.Id}) 无端口 '{conn.FromPortId}'");
                    }
                }

                if (!nodeDict.TryGetValue(conn.ToNodeId, out var toNode))
                {
                    errors.Add($"连线[{i}]目标节点不存在: ToNodeId='{conn.ToNodeId}'");
                    toValid = false;
                }
                else if (!string.IsNullOrWhiteSpace(conn.ToPortId))
                {
                    if (!toNode.Ports.Any(p => string.Equals(p.Id, conn.ToPortId, StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(p.Name, conn.ToPortId, StringComparison.OrdinalIgnoreCase)))
                    {
                        errors.Add($"连线[{i}]目标端口不存在: 节点 '{toNode.Name}'({toNode.Id}) 无端口 '{conn.ToPortId}'");
                    }
                }

                if (fromValid && toValid)
                {
                    incomingNodes.Add(conn.ToNodeId);
                }
            }

            // 3. 每个非 Start 节点必须有入边
            foreach (var node in Nodes)
            {
                if (string.Equals(node.NodeType, "Start", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!incomingNodes.Contains(node.Id))
                {
                    errors.Add($"节点 '{node.Name}'({node.Id}, 类型: {node.NodeType}) 无任何入边，无法被执行调度");
                }
            }

            // 4. 静态环检测（DFS 拓扑遍历）
            //    合法回边：环路中若包含 Loop / Jump 等控制流节点，则属于有意的循环/跳转结构，
            //    运行期引擎已有迭代次数上限保护，不判为错误；仅拦截不含控制流节点的意外死环。
            var loopControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Loop", "Jump" };
            var cycleIdPaths = DetectCyclesRaw();
            foreach (var cycleIds in cycleIdPaths)
            {
                bool hasLoopControl = cycleIds.Any(id =>
                    nodeDict.TryGetValue(id, out var n) && loopControlTypes.Contains(n.NodeType));
                if (hasLoopControl)
                {
                    continue; // 合法的循环/跳转回边
                }

                var display = cycleIds.Select(id =>
                    nodeDict.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n.Name)
                        ? $"{n.Name}({id})"
                        : id);
                errors.Add($"静态环检测失败: 检测到非法循环依赖路径 [{string.Join(" -> ", display)}]（如需循环请使用 Loop/Jump 节点）");
            }

            return errors.Count == 0;
        }

        /// <summary>
        /// 静态环检测（返回原始节点 Id 路径，含闭合节点），供 Validate 判定环中是否含控制流节点。
        /// </summary>
        public List<List<string>> DetectCyclesRaw()
        {
            var results = new List<List<string>>();

            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.Id))
                {
                    adj[node.Id] = new List<string>();
                }
            }
            foreach (var conn in Connections)
            {
                if (adj.ContainsKey(conn.FromNodeId) && adj.ContainsKey(conn.ToNodeId))
                {
                    adj[conn.FromNodeId].Add(conn.ToNodeId);
                }
            }

            var visitState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var nodeId in adj.Keys)
            {
                visitState[nodeId] = 0;
            }

            var currentPath = new List<string>();

            void Dfs(string u)
            {
                visitState[u] = 1;
                currentPath.Add(u);

                foreach (var v in adj[u])
                {
                    if (visitState.TryGetValue(v, out int state))
                    {
                        if (state == 1)
                        {
                            int startIndex = currentPath.IndexOf(v);
                            if (startIndex >= 0)
                            {
                                var cycle = currentPath.Skip(startIndex).ToList();
                                cycle.Add(v); // 闭合
                                results.Add(cycle);
                            }
                        }
                        else if (state == 0)
                        {
                            Dfs(v);
                        }
                    }
                }

                currentPath.RemoveAt(currentPath.Count - 1);
                visitState[u] = 2;
            }

            foreach (var nodeId in Nodes.Select(n => n.Id))
            {
                if (!string.IsNullOrWhiteSpace(nodeId) && visitState.TryGetValue(nodeId, out int state) && state == 0)
                {
                    Dfs(nodeId);
                }
            }

            return results;
        }

        /// <summary>
        /// 基于深度优先遍历（DFS）的静态环检测，查找图中所有的有向环路。
        /// </summary>
        /// <returns>检测到的所有环路节点名称/ID路径列表。</returns>
        public List<List<string>> DetectCycles()
        {
            var results = new List<List<string>>();
            var nodeDict = Nodes.ToDictionary(n => n.Id, n => n, StringComparer.OrdinalIgnoreCase);

            // 构建邻接表
            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in Nodes)
            {
                adj[node.Id] = new List<string>();
            }
            foreach (var conn in Connections)
            {
                if (adj.ContainsKey(conn.FromNodeId) && adj.ContainsKey(conn.ToNodeId))
                {
                    adj[conn.FromNodeId].Add(conn.ToNodeId);
                }
            }

            // 0: 未访问, 1: 访问中（在当前搜索递归栈中）, 2: 已完全访问
            var visitState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var nodeId in adj.Keys)
            {
                visitState[nodeId] = 0;
            }

            var currentPath = new List<string>();

            void Dfs(string u)
            {
                visitState[u] = 1;
                currentPath.Add(u);

                foreach (var v in adj[u])
                {
                    if (visitState.TryGetValue(v, out int state))
                    {
                        if (state == 1)
                        {
                            // 发现反向边，形成环
                            int startIndex = currentPath.IndexOf(v);
                            if (startIndex >= 0)
                            {
                                var cycle = currentPath.Skip(startIndex).Select(id =>
                                    nodeDict.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n.Name)
                                        ? $"{n.Name}({id})"
                                        : id).ToList();
                                // 闭合环表示
                                var targetDisplay = nodeDict.TryGetValue(v, out var vn) && !string.IsNullOrWhiteSpace(vn.Name)
                                    ? $"{vn.Name}({v})"
                                    : v;
                                cycle.Add(targetDisplay);
                                results.Add(cycle);
                            }
                        }
                        else if (state == 0)
                        {
                            Dfs(v);
                        }
                    }
                }

                currentPath.RemoveAt(currentPath.Count - 1);
                visitState[u] = 2;
            }

            foreach (var nodeId in Nodes.Select(n => n.Id))
            {
                if (visitState.TryGetValue(nodeId, out int state) && state == 0)
                {
                    Dfs(nodeId);
                }
            }

            return results;
        }

        /// <summary>
        /// 序列化为 JSON 字符串。
        /// </summary>
        public string ToJson(bool indented = true)
        {
            if (indented)
            {
                return JsonSerializer.Serialize(this, DefaultJsonOptions);
            }
            var options = new JsonSerializerOptions(DefaultJsonOptions) { WriteIndented = false };
            return JsonSerializer.Serialize(this, options);
        }

        /// <summary>
        /// 从 JSON 字符串反序列化为 FlowGraph。
        /// </summary>
        public static FlowGraph? FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            return JsonSerializer.Deserialize<FlowGraph>(json, DefaultJsonOptions);
        }
    }
}
