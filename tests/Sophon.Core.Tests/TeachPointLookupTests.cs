#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Sophon.Core.Flow.V2;
using Sophon.Core.Teach;
using Xunit;

namespace Sophon.Core.Tests
{
    public class TeachPointLookupTests : IDisposable
    {
        private readonly string _file;
        private readonly TeachPointStore _store;

        public TeachPointLookupTests()
        {
            _file = Path.Combine(Path.GetTempPath(), "sophon-teach-" + Guid.NewGuid().ToString("N") + ".json");
            _store = new TeachPointStore(_file);
            _store.SaveOrUpdatePoint(new TeachPoint
            {
                Name = "取料位",
                AxisPositions = new Dictionary<int, double> { [0] = 12.5, [1] = 40.0 }
            });
        }

        public void Dispose()
        {
            try { if (File.Exists(_file)) File.Delete(_file); } catch { }
        }

        [Fact]
        public void 按名称解析单轴坐标()
        {
            Assert.True(TeachPointLookup.TryGetAxisPosition("取料位", 0, out var x, _store));
            Assert.Equal(12.5, x);
            Assert.False(TeachPointLookup.TryGetAxisPosition("取料位", 9, out _, _store));
        }

        [Fact]
        public void 多轴按轴号取坐标()
        {
            var targets = TeachPointLookup.ResolveAxisTargets("取料位", new[] { 0, 1 }, _store);
            Assert.Equal(new[] { 12.5, 40.0 }, targets);
        }

        [Fact]
        public void 找不到点或缺轴则失败()
        {
            Assert.Throws<InvalidOperationException>(() =>
                TeachPointLookup.ResolveAxisTargets("没有这个点", new[] { 0 }, _store));
            Assert.Throws<InvalidOperationException>(() =>
                TeachPointLookup.ResolveAxisTargets("取料位", new[] { 0, 2 }, _store));
        }

        [Fact]
        public void AxisMove节点参数含示教点类型()
        {
            var node = new AxisMoveNode();
            Assert.Contains(node.ParameterSchemas, s => s.Name == "pointName" && s.Type == "point");
        }

        [Fact]
        public void MultiAxisInterp节点参数含示教点()
        {
            var node = new MultiAxisInterpNode();
            Assert.Contains(node.ParameterSchemas, s => s.Name == "pointName" && s.Type == "point");
        }
    }
}
