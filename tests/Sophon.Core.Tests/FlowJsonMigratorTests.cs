using Sophon.Core;
using System.Linq;
using Xunit;

namespace Sophon.Core.Tests
{
    public class FlowJsonMigratorTests
    {
        private const string V1Sample = @"[
            { ""StepName"": ""延时1"", ""__type"": ""FlowStep_Delay"", ""_delayTime_ms"": 500 },
            { ""StepName"": ""回零X轴"", ""__type"": ""FlowStep_AxisHome"" }
        ]";

        private const string V2Sample = @"{ ""version"": 2, ""flowName"": ""demo"", ""nodes"": [] }";

        [Fact]
        public void 检测版本_数组为V1_对象为V2_其他未知()
        {
            Assert.Equal(FlowJsonVersion.V1, FlowJsonMigrator.DetectVersion(V1Sample));
            Assert.Equal(FlowJsonVersion.V2, FlowJsonMigrator.DetectVersion(V2Sample));
            Assert.Equal(FlowJsonVersion.Unknown, FlowJsonMigrator.DetectVersion(@"""just a string"""));
            Assert.Equal(FlowJsonVersion.Unknown, FlowJsonMigrator.DetectVersion(string.Empty));
        }

        [Fact]
        public void 迁移V1到V2_保留步骤名与顺序()
        {
            var shell = FlowJsonMigrator.MigrateV1ToV2(V1Sample, "迁移测试");

            Assert.NotNull(shell);
            Assert.Equal(2, shell!.Version);
            Assert.Equal("迁移测试", shell.FlowName);
            Assert.Equal(2, shell.Nodes.Count);
            Assert.Equal(new[] { "延时1", "回零X轴" }, shell.Nodes.Select(n => n.StepTypeName).ToArray());
            Assert.Equal(shell.Nodes.Select(n => n.Id).Distinct().Count(), shell.Nodes.Count); // Id 唯一
        }

        [Fact]
        public void 非V1文档_迁移返回空()
        {
            Assert.Null(FlowJsonMigrator.MigrateV1ToV2(V2Sample, "x"));
            Assert.Null(FlowJsonMigrator.MigrateV1ToV2("{}", "x")); // 空对象 = Unknown
        }

        [Fact]
        public void 缺失步骤名_迁移抛异常()
        {
            const string broken = @"[ { ""foo"": 1 } ]";
            Assert.Throws<System.InvalidOperationException>(() => FlowJsonMigrator.MigrateV1ToV2(broken, "坏样本"));
        }
    }
}