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
    }
}
