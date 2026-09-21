#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
        /// 获取流程文件的完整物理路径。
        /// </summary>
        public static string GetFilePath(string flowName, string? baseDirectory = null)
        {
            var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
            return Path.Combine(dir, $"{flowName}.json");
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
            if (string.IsNullOrWhiteSpace(graph.FlowName))
            {
                throw new ArgumentException("FlowName 不能为空", nameof(graph));
            }

            var dir = !string.IsNullOrWhiteSpace(baseDirectory) ? baseDirectory : DefaultBaseDirectory;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var filePath = GetFilePath(graph.FlowName, dir);
            var json = graph.ToJson(indented: true);

            File.WriteAllText(filePath, json, Utf8WithoutBom);
            return filePath;
        }

        /// <summary>
        /// 从 JSON 文件加载流程图。
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
                return null;
            }

            var json = File.ReadAllText(filePath, Utf8WithoutBom);
            return FlowGraph.FromJson(json);
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
    }
}
