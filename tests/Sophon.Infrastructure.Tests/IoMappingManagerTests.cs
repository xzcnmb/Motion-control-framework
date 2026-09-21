using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion.Axis;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    public class IoMappingManagerTests : IDisposable
    {
        private readonly string _tempFile;

        public IoMappingManagerTests()
        {
            _tempFile = Path.Combine(Path.GetTempPath(), $"io_points_test_{Guid.NewGuid():N}.json");
        }

        public void Dispose()
        {
            if (File.Exists(_tempFile))
            {
                try { File.Delete(_tempFile); } catch { }
            }
        }

        [Fact]
        public void IoPointConfigStore_保存与加载往返正确()
        {
            var store = new IoPointConfigStore(_tempFile);
            var defaults = IoPointConfigStore.SeedDefaults();
            Assert.NotEmpty(defaults);

            store.Save(defaults);

            var loaded = store.Load();
            Assert.Equal(defaults.Count, loaded.Count);
            Assert.Equal("DI_EMG_STOP", loaded[0].LogicalName);
            Assert.Equal(SwitchType.NormallyClose, loaded[0].Switch);
        }

        [Fact]
        public void 极性反转与常闭开关逻辑映射正确()
        {
            var store = new IoPointConfigStore(_tempFile);
            var points = new List<IoPointDefinition>
            {
                // 1. 普通常开NO，无反转：物理1 -> 逻辑1
                new() { LogicalName = "DI_NO_NORMAL", Direction = IoDirection.DI, CardNo = 0, ChannelBit = 0, Invert = false, Switch = SwitchType.NormallyOpen, FilterMs = 0 },
                // 2. 常开NO，极性反转：物理1 -> 逻辑0
                new() { LogicalName = "DI_NO_INVERT", Direction = IoDirection.DI, CardNo = 0, ChannelBit = 1, Invert = true, Switch = SwitchType.NormallyOpen, FilterMs = 0 },
                // 3. 常闭NC急停，无反转：平时闭合物理1 -> 逻辑0(安全无报警)；拍下断开物理0 -> 逻辑1(报警触发)
                new() { LogicalName = "DI_NC_EMG", Direction = IoDirection.DI, CardNo = 0, ChannelBit = 2, Invert = false, Switch = SwitchType.NormallyClose, FilterMs = 0 },
                // 4. DO 输出带极性反转
                new() { LogicalName = "DO_INVERT", Direction = IoDirection.DO, CardNo = 0, ChannelBit = 3, Invert = true }
            };
            store.Save(points);

            var rawDiState = new Dictionary<(int card, int bit), bool>();
            var rawDoState = new Dictionary<(int card, int bit), bool>();

            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => rawDiState.TryGetValue((card, bit), out var b) && b,
                rawDoWriter: (card, bit, val) => rawDoState[(card, bit)] = val);

            // 测试 1: 常开 NO
            rawDiState[(0, 0)] = true;
            Assert.True(manager.ReadDi("DI_NO_NORMAL"));
            rawDiState[(0, 0)] = false;
            Assert.False(manager.ReadDi("DI_NO_NORMAL"));

            // 测试 2: 常开 NO + 极性反转
            rawDiState[(0, 1)] = true;
            Assert.False(manager.ReadDi("DI_NO_INVERT"));
            rawDiState[(0, 1)] = false;
            Assert.True(manager.ReadDi("DI_NO_INVERT"));

            // 测试 3: 常闭 NC 急停开关
            // 平时未按下：物理开关闭合有电 (raw = true) -> 逻辑应为 false（正常安全）
            rawDiState[(0, 2)] = true;
            Assert.False(manager.ReadDi("DI_NC_EMG"));
            // 拍下急停或断线：物理开关断开 (raw = false) -> 逻辑应为 true（触发报警）
            rawDiState[(0, 2)] = false;
            Assert.True(manager.ReadDi("DI_NC_EMG"));

            // 测试 4: DO 输出带极性反转
            manager.WriteDo("DO_INVERT", true); // 逻辑输出 true
            Assert.False(rawDoState[(0, 3)]);   // 物理位由于 Invert=true 写入 false
        }

        [Fact]
        public void 防抖滤波时间窗_抖动干扰被有效滤除()
        {
            var store = new IoPointConfigStore(_tempFile);
            var points = new List<IoPointDefinition>
            {
                new()
                {
                    LogicalName = "DI_DEBOUNCE",
                    Direction = IoDirection.DI,
                    CardNo = 0,
                    ChannelBit = 0,
                    Invert = false,
                    Switch = SwitchType.NormallyOpen,
                    FilterMs = 60 // 60ms 滤波
                }
            };
            store.Save(points);

            bool currentPhysical = false;
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => currentPhysical,
                rawDoWriter: (c, b, v) => { });

            // 初始稳定为 false
            Assert.False(manager.ReadDi("DI_DEBOUNCE"));

            // 瞬间产生 10ms 的脉冲毛刺（干扰）
            currentPhysical = true;
            Assert.False(manager.ReadDi("DI_DEBOUNCE")); // 滤波未达 60ms，维持 false

            // 保持持续高电平超过 70ms
            Thread.Sleep(75);
            Assert.True(manager.ReadDi("DI_DEBOUNCE")); // 滤波时间满足，确认有效导通
        }
    }
}
