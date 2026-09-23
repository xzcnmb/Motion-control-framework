#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// V2 流程图文件持久化存储。
    /// 【契约铁律】JSON 单源，不写数据库；落盘路径默认为 SophonData\flows\{FlowName}.json，UTF-8 无 BOM，缩进。
    /// </summary>
    public static class FlowGraphStore
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

        /// <summary>
        /// 默认基础存储目录相对/绝对路径。
        /// </summary>
        public static string DefaultBaseDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "flows");

        /// <summary>
        /// 将流程名净化为安全的裸文件名（不含目录、不含盘符、不含路径穿越），用于拼装落盘路径。
        /// 保留 Unicode 字母/数字（中文名原样保留）以及 <c>_</c> 与 <c>-</c>。
        /// </summary>
        /// <exception cref="ArgumentException">入参为空/空白，或净化结果为空时抛出。</exception>
        public static string SanitizeFlowName(string flowName)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程名不能为空", nameof(flowName));
            }

            var name = flowName.Trim();

            // 彻底剥离目录分隔符与盘符冒号，杜绝任何路径穿越
            name = name.Replace("\\", string.Empty).Replace("/", string.Empty).Replace(":", string.Empty);

            // 折叠/移除 ".."（防止路径穿越）
            while (name.Contains("..", StringComparison.Ordinal))
            {
                name = name.Replace("..", string.Empty);
            }

            // 其余非法文件名字符（* ? " &lt; &gt; | 及控制字符）统一替换为下划线；保留中文等 Unicode 字母/数字、_、-
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }
            name = sb.ToString();

            // Windows 不允许文件名以点/空格结尾
            name = name.TrimEnd('.', ' ');

            // 限制长度（截断后可能出现新的结尾点/空格，再清一次）
            if (name.Length > 120)
            {
                name = name.Substring(0, 120);
            }
            name = name.TrimEnd('.', ' ');

            if (name.Length == 0)
            {
                throw new ArgumentException("流程名包含无法用于文件名的字符", nameof(flowName));
            }

            return name;
        }

        /// <summary>
        /// 获取流程文件的完整物理路径。
        /// 名称先经 <see cref="SanitizeFlowName"/> 净化，再以 full-path 前缀校验确保解析结果始终位于 base 目录内。
        /// </summary>
        public static string GetFilePath(string flowName, string? baseDirectory = null)
        {
            var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
            var safeName = SanitizeFlowName(flowName);
            var combined = Path.Combine(dir, $"{safeName}.json");

            // defense-in-depth：确认解析后的完整路径仍在 base 目录内，否则拒绝
            var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(combined);
            if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"流程名解析后的路径越出存储目录：{fullPath}");
            }

            return combined;
        }

        /// <summary>
        /// 保存流程图至 JSON 文件。
        /// </summary>
        /// <param name="graph">待保存的流程图对象。</param>
        /// <param name="baseDirectory">目标存储目录（为空则使用默认目录）。</param>
        /// <returns>保存成功生成的完整文件路径。</returns>
        public static string Save(FlowGraph graph, string? baseDirectory = null)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            // 净化流程名并回写，保证落盘文件名与内存显示一致
            graph.FlowName = SanitizeFlowName(graph.FlowName);

            var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var filePath = GetFilePath(graph.FlowName, dir);
            var json = graph.ToJson(indented: true);

            AtomicWrite(filePath, json);
            return filePath;
        }

        /// <summary>
        /// 崩溃安全写入：先写唯一命名 .tmp，再原子替换/移动到目标。替换时保留上一份为 .bak。
        /// 临时文件使用唯一名（目标 + Guid + .tmp）：并发保存（或保存与读取并发）不再争抢同一个
        /// 固定 .tmp，避免互相踩踏造成的 IOException。任一异常都尽力清理 .tmp 后抛出，
        /// 保证目标永远不会是截断/半成品文件。
        /// </summary>
        private static void AtomicWrite(string target, string content)
        {
            var tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, content, Utf8WithoutBom);
                if (File.Exists(target))
                {
                    File.Replace(tmp, target, target + ".bak");
                }
                else
                {
                    File.Move(tmp, target);
                }
            }
            catch
            {
                // 失败路径清理临时文件，保证目录里不留 .tmp 残骸
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>
        /// 带短重试的文件读取：吸收并发保存（File.Replace / File.Move）造成的瞬时共享冲突。
        /// 工站运行期间 <see cref="SubFlowNode"/> / <see cref="ParallelNode"/> / <see cref="FlowEngineV2Host"/>
        /// 都要读流程文件，与编辑器保存并发时偶发 IOException / UnauthorizedAccessException；
        /// 原样抛出会让节点无谓失败、工站周期中断，重试数次即可恢复。
        /// </summary>
        /// <param name="path">待读取文件路径。</param>
        /// <param name="maxRetries">最大尝试次数（含首次）。</param>
        /// <param name="delayMs">每次重试前的固定等待毫秒数。</param>
        public static string ReadAllTextResilient(string path, int maxRetries = 5, int delayMs = 30)
        {
            if (maxRetries < 1) maxRetries = 1;

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return File.ReadAllText(path);
                }
                catch (IOException) when (attempt < maxRetries)
                {
                    RetryDelay(delayMs);
                }
                catch (UnauthorizedAccessException) when (attempt < maxRetries)
                {
                    RetryDelay(delayMs);
                }
            }
        }

        private static void RetryDelay(int delayMs)
        {
            if (delayMs <= 0) return;
            try { Thread.Sleep(delayMs); }
            catch (ThreadInterruptedException) { /* 等待被中断时立即继续重试 */ }
        }

        /// <summary>
        /// 从 JSON 文件加载流程图。
        /// 读取经 <see cref="ReadAllTextResilient"/>：工站运行期与编辑器保存并发时，容忍瞬时共享冲突。
        /// </summary>
        /// <param name="flowName">流程名称。</param>
        /// <param name="baseDirectory">存储目录（为空则使用默认目录）。</param>
        /// <returns>反序列化得到的 FlowGraph 对象，若文件不存在则返回 null。</returns>
        public static FlowGraph? Load(string flowName, string? baseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(flowName))
            {
                return null;
            }

            var filePath = GetFilePath(flowName, baseDirectory);
            if (!File.Exists(filePath))
            {
                // 兼容升级前落盘的历史文件名：ListFlowNames 返回磁盘原始名（可能带尾随空格/点），
                // 而净化后的路径查不到这种文件（如 "flow .json" 会被查成 "flow.json"）。
                // 找不到时按原始名字回退精确查找一次，仍找不到才返回 null。
                var exactPath = TryGetExactNameFilePath(flowName, baseDirectory);
                if (exactPath == null || !File.Exists(exactPath))
                {
                    return null;
                }
                filePath = exactPath;
            }

            var json = ReadAllTextResilient(filePath);
            return FlowGraph.FromJson(json);
        }

        /// <summary>
        /// 按未净化的原始流程名拼装文件路径，仅供 <see cref="Load"/> 的回退查找使用。
        /// 与 <see cref="GetFilePath"/> 同样校验解析结果必须位于 base 目录内，拒绝路径穿越。
        /// </summary>
        private static string? TryGetExactNameFilePath(string flowName, string? baseDirectory)
        {
            try
            {
                var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
                var combined = Path.Combine(dir, flowName + ".json");
                var fullDir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(combined);
                if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return combined;
            }
            catch
            {
                // 名字含极端字符导致路径无法解析时，视为找不到，不让异常逃逸到工站执行路径
                return null;
            }
        }

        /// <summary>
        /// 判断指定流程文件是否存在。
        /// </summary>
        public static bool Exists(string flowName, string? baseDirectory = null)
        {
            var filePath = GetFilePath(flowName, baseDirectory);
            return File.Exists(filePath);
        }

        /// <summary>
        /// 删除指定流程文件。
        /// </summary>
        public static bool Delete(string flowName, string? baseDirectory = null)
        {
            var filePath = GetFilePath(flowName, baseDirectory);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 列出指定目录下所有保存的流程名称列表。
        /// </summary>
        public static List<string> ListFlowNames(string? baseDirectory = null)
        {
            var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
            var result = new List<string>();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    result.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            return result;
        }

        /// <summary>
        /// 导出工程文件（.sophonflow.json）。工站导入同一份配方。
        /// </summary>
        public static string ExportTo(FlowGraph graph, string filePath)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("路径不能为空", nameof(filePath));

            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            AtomicWrite(filePath, graph.ToJson(indented: true));
            return filePath;
        }

        /// <summary>
        /// 从工程文件导入到流程库，返回流程名。同名覆盖前由调用方确认。
        /// </summary>
        public static FlowGraph ImportFrom(string filePath, string? baseDirectory = null, bool save = true)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("找不到流程工程文件", filePath);
            }

            var json = File.ReadAllText(filePath, Utf8WithoutBom);
            var graph = FlowGraph.FromJson(json)
                        ?? throw new InvalidOperationException("无法解析流程工程文件");

            // 版本守卫优先：只接受 v1/v2，更高版本需升级软件（先于节点结构检查，避免高版本工程被误报成"没有节点"）
            if (graph.Version > 2)
            {
                throw new InvalidOperationException($"不支持的流程版本 v{graph.Version}，请升级软件后再导入。");
            }

            if (graph.Nodes == null || graph.Nodes.Count == 0)
            {
                throw new InvalidOperationException("工程文件没有节点，拒绝导入。");
            }

            // 节点类型为空的节点视为无效工程（引擎无法识别、无法调度），直接拒绝
            var emptyTypeCount = graph.Nodes.Count(n => string.IsNullOrWhiteSpace(n.NodeType));
            if (emptyTypeCount > 0)
            {
                throw new InvalidOperationException($"工程包含 {emptyTypeCount} 个未填写节点类型(NodeType)的节点，拒绝导入。");
            }

            // 未注册节点类型守卫：本系统无法识别的节点类型一律拒绝导入
            var unknownTypes = graph.Nodes
                .Select(n => n.NodeType)
                .Where(t => !string.IsNullOrWhiteSpace(t) && !FlowNodeRegistry.IsRegistered(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknownTypes.Count > 0)
            {
                throw new InvalidOperationException("工程包含本系统未注册的节点类型：" + string.Join("、", unknownTypes) + "。拒绝导入。");
            }

            if (string.IsNullOrWhiteSpace(graph.FlowName))
            {
                string file = Path.GetFileName(filePath);
                graph.FlowName = file.EndsWith(".sophonflow.json", StringComparison.OrdinalIgnoreCase)
                    ? file[..^".sophonflow.json".Length]
                    : Path.GetFileNameWithoutExtension(file);
            }

            // 净化流程名后再做结构校验与落盘
            graph.FlowName = SanitizeFlowName(graph.FlowName);

            if (!graph.Validate(out var errors) && errors.Count > 0)
            {
                throw new InvalidOperationException("工程文件校验失败：" + string.Join("；", errors));
            }

            if (save)
            {
                Save(graph, baseDirectory);
            }
            return graph;
        }
    }
}
