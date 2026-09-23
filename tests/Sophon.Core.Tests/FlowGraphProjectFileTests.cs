using System;
using System.Collections.Generic;
using System.IO;
using Sophon.Core.Flow.V2;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowGraphProjectFileTests : IDisposable
    {
        private readonly string _dir;

        public FlowGraphProjectFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sophon-flow-proj-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void 导出再导入_流程名和节点数不变()
        {
            var graph = new FlowGraph
            {
                FlowName = "搬运配方",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };
            string file = Path.Combine(_dir, "搬运配方.sophonflow.json");
            FlowGraphStore.ExportTo(graph, file);
            Assert.True(File.Exists(file));

            var loaded = FlowGraphStore.ImportFrom(file, _dir);
            Assert.Equal("搬运配方", loaded.FlowName);
            Assert.Single(loaded.Nodes);
            Assert.Contains("搬运配方", FlowGraphStore.ListFlowNames(_dir));
        }

        [Fact]
        public void 空图导入拒绝()
        {
            string file = Path.Combine(_dir, "空.sophonflow.json");
            File.WriteAllText(file, "{\"flowName\":\"空\",\"nodes\":[]}");
            Assert.Throws<InvalidOperationException>(() => FlowGraphStore.ImportFrom(file, _dir));
        }

        [Fact]
        public void SanitizeFlowName_净化穿越与非法字符_中文名原样保留且不越出目录()
        {
            // ..\..\evil → 剥离目录分隔符与 ".."，得到裸文件名
            var evil = FlowGraphStore.SanitizeFlowName("..\\..\\evil");
            Assert.DoesNotContain("..", evil);
            Assert.DoesNotContain("\\", evil);
            Assert.DoesNotContain("/", evil);
            Assert.DoesNotContain(":", evil);
            Assert.Contains("evil", evil);

            // / \ : * ? 全部被处理成安全字符
            var mixed = FlowGraphStore.SanitizeFlowName("a/b\\c:d*e?f");
            foreach (var bad in new[] { '/', '\\', ':', '*', '?' })
            {
                Assert.DoesNotContain(bad, mixed);
            }

            // 中文名与下划线原样保留（向后兼容存量 v2 流程）
            Assert.Equal("示例工站_搬运demo", FlowGraphStore.SanitizeFlowName("示例工站_搬运demo"));

            // 空/空白与净化后为空的输入一律拒绝
            Assert.Throws<ArgumentException>(() => FlowGraphStore.SanitizeFlowName("   "));
            Assert.Throws<ArgumentException>(() => FlowGraphStore.SanitizeFlowName(".."));

            // 净化后的名字拼出的路径始终位于 base 目录内，绝不越出
            string path = FlowGraphStore.GetFilePath(evil, _dir);
            Assert.StartsWith(Path.GetFullPath(_dir), Path.GetFullPath(path));
        }

        [Fact]
        public void 原子保存_覆盖旧文件_生成bak_目标为合法JSON_无残留tmp()
        {
            var graph = new FlowGraph
            {
                FlowName = "原子保存测试",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };

            // 第一次保存：新建文件，不应有 .bak
            string path1 = FlowGraphStore.Save(graph, _dir);
            Assert.True(File.Exists(path1));
            Assert.False(File.Exists(path1 + ".tmp"));
            Assert.False(File.Exists(path1 + ".bak"));

            // 第二次保存：覆盖，应保留上一份为 .bak，目标仍是合法 JSON，且无 .tmp 残留
            string path2 = FlowGraphStore.Save(graph, _dir);
            Assert.Equal(path1, path2);
            Assert.True(File.Exists(path2 + ".bak"));
            Assert.False(File.Exists(path2 + ".tmp"));

            string json = File.ReadAllText(path2);
            var reloaded = FlowGraph.FromJson(json);
            Assert.NotNull(reloaded);
            Assert.Equal("原子保存测试", reloaded!.FlowName);
        }

        [Fact]
        public void 导入_版本过高_拒绝()
        {
            var graph = new FlowGraph
            {
                Version = 99,
                FlowName = "未来版本",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };
            string file = Path.Combine(_dir, "v99.json");
            File.WriteAllText(file, graph.ToJson());

            var ex = Assert.Throws<InvalidOperationException>(() => FlowGraphStore.ImportFrom(file, _dir, save: false));
            Assert.Contains("v99", ex.Message);
        }

        [Fact]
        public void 导入_未注册节点类型_拒绝()
        {
            var graph = new FlowGraph
            {
                Version = 2,
                FlowName = "未知节点工程",
                Nodes = new List<FlowNode> { new("NoSuchNode", "神秘节点") }
            };
            string file = Path.Combine(_dir, "unknown.json");
            File.WriteAllText(file, graph.ToJson());

            var ex = Assert.Throws<InvalidOperationException>(() => FlowGraphStore.ImportFrom(file, _dir, save: false));
            Assert.Contains("NoSuchNode", ex.Message);
        }

        [Fact]
        public void 导入_合法v2工程_成功()
        {
            var graph = new FlowGraph
            {
                Version = 2,
                FlowName = "合法工程",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };
            string file = Path.Combine(_dir, "合法工程.sophonflow.json");
            FlowGraphStore.ExportTo(graph, file);

            var loaded = FlowGraphStore.ImportFrom(file, _dir, save: false);
            Assert.Equal("合法工程", loaded.FlowName);
        }

        [Fact]
        public void 重复保存_仅保留一份bak_目标为合法JSON_无tmp残留()
        {
            var graph = new FlowGraph
            {
                FlowName = "重复保存测试",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };

            // 连续两次覆盖保存：目标始终可读、.bak 有且仅有一份、目录里不留任何 .tmp 残骸
            string path = FlowGraphStore.Save(graph, _dir);
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

            path = FlowGraphStore.Save(graph, _dir);
            Assert.True(File.Exists(path));
            Assert.Single(Directory.GetFiles(_dir, "*.bak"));
            Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

            var reloaded = FlowGraph.FromJson(File.ReadAllText(path));
            Assert.NotNull(reloaded);
            Assert.Equal("重复保存测试", reloaded!.FlowName);
        }

        [Fact]
        public void Load_历史文件带尾随空格_按原始名回退查找()
        {
            // 升级前落盘的文件名带尾随空格："flow .json"。ListFlowNames 原样返回 "flow "，
            // 而净化后的路径 "flow.json" 并不存在，Load 必须回退按原始名精确查找，
            // 否则下拉里看得见、点开却报"未找到流程文件"。
            var graph = new FlowGraph
            {
                FlowName = "flow ",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始") { Ports = new List<FlowPort> { new("Out", FlowPortDirection.Out, "Out") } }
                }
            };

            // ExportTo 按给定路径原样落盘（不净化文件名），模拟升级前遗留的 "flow .json"
            string legacyFile = Path.Combine(_dir, "flow .json");
            FlowGraphStore.ExportTo(graph, legacyFile);
            Assert.True(File.Exists(legacyFile));
            Assert.False(File.Exists(Path.Combine(_dir, "flow.json")));
            Assert.Contains("flow ", FlowGraphStore.ListFlowNames(_dir));

            var loaded = FlowGraphStore.Load("flow ", _dir);
            Assert.NotNull(loaded);
            Assert.Equal("flow ", loaded!.FlowName);
        }

        [Fact]
        public void 导入_节点类型为空_拒绝()
        {
            var graph = new FlowGraph
            {
                Version = 2,
                FlowName = "空类型工程",
                Nodes = new List<FlowNode>
                {
                    new("Start", "起始"),
                    new FlowNode { NodeType = "", Name = "无类型节点" }
                }
            };
            string file = Path.Combine(_dir, "emptytype.json");
            File.WriteAllText(file, graph.ToJson());

            var ex = Assert.Throws<InvalidOperationException>(() => FlowGraphStore.ImportFrom(file, _dir, save: false));
            Assert.Contains("NodeType", ex.Message);
        }
    }
}
