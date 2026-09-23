using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                rawDoWriter: (c, b, v) => { },
                startPolling: false);

            // 初始稳定为 false
            Assert.False(manager.ReadDi("DI_DEBOUNCE"));

            // 瞬间产生 10ms 的脉冲毛刺（干扰）
            currentPhysical = true;
            Assert.False(manager.ReadDi("DI_DEBOUNCE")); // 滤波未达 60ms，维持 false

            // 保持持续高电平超过 70ms
            Thread.Sleep(75);
            Assert.True(manager.ReadDi("DI_DEBOUNCE")); // 滤波时间满足，确认有效导通
        }

        private static IoPointDefinition DiPoint(
            string name, int card = 0, int bit = 0, bool invert = false,
            SwitchType sw = SwitchType.NormallyOpen, int filterMs = 0)
            => new()
            {
                LogicalName = name,
                Direction = IoDirection.DI,
                CardNo = card,
                ChannelBit = bit,
                Invert = invert,
                Switch = sw,
                FilterMs = filterMs
            };

        private static IoPointDefinition DoPoint(string name, int card = 0, int bit = 0, bool invert = false)
            => new()
            {
                LogicalName = name,
                Direction = IoDirection.DO,
                CardNo = card,
                ChannelBit = bit,
                Invert = invert
            };

        /// <summary>有界等待，避免用固定 Thread.Sleep 赌调度（后台轮询测试专用）。</summary>
        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(5);
            }
            return condition();
        }

        /// <summary>后台轮询诊断串：断言失败时直接说明轮询停在哪一步。</summary>
        private static string PollDiag(IoMappingManager manager)
            => $"PollCycleCount={manager.PollCycleCount}, IsPolling={manager.IsPolling}, " +
               $"LastError={manager.LastError?.GetType().Name}: {manager.LastError?.Message}";

        [Fact]
        public void SetVirtualDi后_ReadDi与快照一致并经极性与常闭映射()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition>
            {
                DiPoint("DI_NC", bit: 0, sw: SwitchType.NormallyClose),
                DiPoint("DI_INVERT", bit: 1, invert: true)
            });

            var events = new ConcurrentQueue<(string Name, bool Value)>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => true, // 常闭点物理闭合 = 正常未触发
                rawDoWriter: (card, bit, val) => { },
                startPolling: false);
            using (manager)
            {
                manager.DiChanged += (n, v) => events.Enqueue((n, v));

                // 未注入时跟随真实 reader
                Assert.False(manager.ReadDi("DI_NC"));

                // 常闭 NC：注入物理 raw=true -> 逻辑 false（正常未触发）
                manager.SetVirtualDi("DI_NC", true);
                Assert.False(manager.ReadDi("DI_NC"));
                Assert.False(manager.SnapshotDi()["DI_NC"]);

                // 常闭 NC：注入物理 raw=false -> 逻辑 true（触发/断线）
                manager.SetVirtualDi("DI_NC", false);
                Assert.True(manager.ReadDi("DI_NC"));
                Assert.True(manager.SnapshotDi()["DI_NC"]);

                // 极性反转：注入物理 raw=false -> 逻辑 true
                manager.SetVirtualDi("DI_INVERT", false);
                Assert.True(manager.ReadDi("DI_INVERT"));
                Assert.True(manager.SnapshotDi()["DI_INVERT"]);

                // 发布出去的事件值必须与读值一致（走同一条映射链路，不是绕过 mapping 的第二套值）
                Assert.Contains(events, e => e.Name == "DI_NC" && e.Value);
                Assert.Contains(events, e => e.Name == "DI_INVERT" && e.Value);

                // 清除注入后回到真实 reader
                manager.ClearVirtualDi("DI_NC");
                manager.ClearVirtualDi("DI_INVERT");
                Assert.False(manager.ReadDi("DI_NC"));
                Assert.False(manager.ReadDi("DI_INVERT"));
                Assert.DoesNotContain(manager.SnapshotDi(), kv => kv.Value);
            }
        }

        [Fact]
        public void 防抖_候选未稳定不发布_稳定后只发布一次()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition> { DiPoint("DI_FILTER", filterMs: 200) });

            var events = new ConcurrentQueue<(string Name, bool Value)>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => false,
                rawDoWriter: (card, bit, val) => { },
                startPolling: false);
            using (manager)
            {
                manager.DiChanged += (n, v) => events.Enqueue((n, v));

                // 初次采样只建立稳定值，不发布伪边沿
                Assert.False(manager.ReadDi("DI_FILTER"));
                Assert.True(events.IsEmpty);

                // 候选翻转但尚未保持到 FilterMs：不发事件，读值维持上一稳定值
                manager.SetVirtualDi("DI_FILTER", true);
                Assert.False(manager.ReadDi("DI_FILTER"));
                Assert.True(events.IsEmpty);

                // 保持超过 FilterMs 后再采样：只发布一次
                Thread.Sleep(260);
                manager.PollDiOnce();
                Assert.True(manager.ReadDi("DI_FILTER"));
                Assert.Single(events);
                Assert.Contains(events, e => e.Name == "DI_FILTER" && e.Value);

                // 相同稳定值反复采样不重复发布
                manager.PollDiOnce();
                manager.PollDiOnce();
                Assert.Single(events);
                Assert.True(manager.ReadDi("DI_FILTER"));
                Assert.Single(events);

                // 候选值回落（没稳定过的那次翻转）同样不产生事件
                manager.SetVirtualDi("DI_FILTER", false);
                Assert.True(manager.ReadDi("DI_FILTER"));
                Assert.Single(events);
            }
        }

        [Fact]
        public void 后台轮询发现真实reader边沿并按滤波防抖()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition>
            {
                DiPoint("DI_NOFILTER", bit: 0, filterMs: 0),
                DiPoint("DI_FILTERED", bit: 1, filterMs: 1000)
            });

            bool raw0 = false;
            bool raw1 = false;
            var events = new ConcurrentQueue<(string Name, bool Value)>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => bit == 0 ? Volatile.Read(ref raw0) : Volatile.Read(ref raw1),
                rawDoWriter: (card, bit, val) => { },
                pollIntervalMs: 5);
            using (manager)
            {
                manager.DiChanged += (n, v) => events.Enqueue((n, v));
                Assert.True(WaitUntil(() => manager.IsPolling, 5000), PollDiag(manager));
                // 等首个轮询周期建立稳定值，避免把初始采样当成"已存在的电平"
                Assert.True(WaitUntil(() => manager.PollCycleCount >= 1, 5000), PollDiag(manager));

                // 无滤波点：后台轮询自己发现真实 reader 的边沿并发布
                Volatile.Write(ref raw0, true);
                Assert.True(WaitUntil(() => events.Count > 0, 5000), PollDiag(manager));
                Assert.True(manager.ReadDi("DI_NOFILTER"));
                Assert.Contains(events, e => e.Name == "DI_NOFILTER" && e.Value);

                // 带滤波点：毛刺短于 FilterMs 时只算候选，后台轮询不发布
                Volatile.Write(ref raw1, true);
                manager.PollDiOnce();
                Assert.DoesNotContain(events, e => e.Name == "DI_FILTERED");
                Assert.False(manager.ReadDi("DI_FILTERED"));

                // 候选回落：没稳定过的候选不产生事件
                Volatile.Write(ref raw1, false);
                manager.PollDiOnce();
                manager.PollDiOnce();
                Assert.DoesNotContain(events, e => e.Name == "DI_FILTERED");
                Assert.False(manager.ReadDi("DI_FILTERED"));
            }
        }

        [Fact]
        public async Task Reload期间读快照不出现空映射且能换上新表()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition> { DiPoint("DI_A"), DoPoint("DO_A") });

            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => false,
                rawDoWriter: (card, bit, val) => { },
                startPolling: false);
            using (manager)
            {
                var stop = false;
                var violations = new ConcurrentQueue<string>();
                var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
                {
                    while (!Volatile.Read(ref stop))
                    {
                        var di = manager.SnapshotDi();
                        var doSnap = manager.SnapshotDo();
                        var diNames = manager.DiPointNames;
                        var doNames = manager.DoPointNames;

                        if (diNames.Count == 0) violations.Enqueue("DiPointNames 出现过空表");
                        if (doNames.Count == 0) violations.Enqueue("DoPointNames 出现过空表");
                        if (!di.ContainsKey("DI_A")) violations.Enqueue("SnapshotDi 丢过 DI_A");
                        if (!doSnap.ContainsKey("DO_A")) violations.Enqueue("SnapshotDo 丢过 DO_A");

                        manager.ReadDi("DI_A");
                        manager.PollDiOnce();
                    }
                })).ToArray();

                // 一边 Reload 一边读：读到的快照必须始终完整
                store.Save(new List<IoPointDefinition> { DiPoint("DI_A"), DiPoint("DI_B", bit: 1), DoPoint("DO_A") });
                for (int i = 0; i < 40; i++)
                {
                    manager.Reload();
                }
                Volatile.Write(ref stop, true);
                await Task.WhenAll(readers);

                Assert.Empty(violations);
                // Reload 之后确实换上了新表（而不是旧的空表）
                Assert.Contains("DI_B", manager.DiPointNames);
                Assert.Equal(2, manager.DiPointNames.Count);
                Assert.Contains("DI_A", manager.SnapshotDi().Keys);
            }
        }

        [Fact]
        public void Dispose后后台轮询停止_同步读仍可用()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition> { DiPoint("DI_POLL", filterMs: 0) });

            bool raw = false;
            var events = new ConcurrentQueue<(string Name, bool Value)>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => Volatile.Read(ref raw),
                rawDoWriter: (card, bit, val) => { },
                pollIntervalMs: 5);
            using (manager)
            {
                manager.DiChanged += (n, v) => events.Enqueue((n, v));
                Assert.True(WaitUntil(() => manager.IsPolling, 5000), PollDiag(manager));
                // 等首个轮询周期建立稳定值，避免把初始采样当成"已存在的电平"
                Assert.True(WaitUntil(() => manager.PollCycleCount >= 1, 5000), PollDiag(manager));

                Volatile.Write(ref raw, true);
                Assert.True(WaitUntil(() => events.Count > 0, 5000), PollDiag(manager));

                manager.Dispose();
                Assert.True(WaitUntil(() => !manager.IsPolling, 5000), PollDiag(manager));

                // 轮询次数停止增长
                long stopped = manager.PollCycleCount;
                Thread.Sleep(80);
                Assert.Equal(stopped, manager.PollCycleCount);

                // Dispose 之后真实 reader 再边沿也不发布
                Volatile.Write(ref raw, false);
                Thread.Sleep(80);
                Assert.Single(events);
                Assert.Contains(events, e => e.Name == "DI_POLL" && e.Value);

                // Dispose 之后同步读仍然可用（不抛 ObjectDisposedException），并补发这次稳定值变化
                Assert.False(manager.ReadDi("DI_POLL"));
                Assert.Equal(2, events.Count);

                // 重复 Dispose 安全
                manager.Dispose();
            }
        }

        [Fact]
        public void SnapshotDo反映已写逻辑值并做极性反转()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition>
            {
                DoPoint("DO_PLAIN", bit: 0),
                DoPoint("DO_INV", bit: 1, invert: true),
                DoPoint("DO_NEVER", bit: 2),
                DiPoint("DI_A")
            });

            var written = new Dictionary<(int Card, int Bit), bool>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => false,
                rawDoWriter: (card, bit, val) => written[(card, bit)] = val,
                startPolling: false);
            using (manager)
            {
                // 未写入过的输出点给出 false，而不是缺失键
                var initial = manager.SnapshotDo();
                Assert.False(initial["DO_NEVER"]);
                Assert.Equal(3, initial.Count);

                manager.WriteDo("DO_PLAIN", true);
                manager.WriteDo("DO_INV", true);

                var snapshot = manager.SnapshotDo();
                Assert.True(snapshot["DO_PLAIN"]);
                Assert.True(snapshot["DO_INV"]);       // 逻辑值
                Assert.True(written[(0, 0)]);           // 物理位不变
                Assert.False(written[(0, 1)]);          // 物理位被极性反转

                manager.WriteDo("DO_PLAIN", false);
                Assert.False(manager.SnapshotDo()["DO_PLAIN"]);
                Assert.False(written[(0, 0)]);
            }
        }

        [Fact]
        public void 未配置点名不伪成功_留下诊断且异常不从后台逃逸()
        {
            var store = new IoPointConfigStore(_tempFile);
            store.Save(new List<IoPointDefinition> { DiPoint("DI_A"), DoPoint("DO_A") });

            var errors = new ConcurrentQueue<Exception>();
            var manager = new IoMappingManager(
                store,
                rawDiReader: (card, bit) => throw new InvalidOperationException("控制卡不在线"),
                rawDoWriter: (card, bit, val) => { },
                pollIntervalMs: 5);
            using (manager)
            {
                manager.IoError += ex => errors.Enqueue(ex);

                // 未配置 DI：兼容返回 false，但必须留下诊断痕迹
                Assert.False(manager.ReadDi("DI_NOT_EXIST"));
                Assert.IsType<InvalidOperationException>(manager.LastError);

                // 未配置 DO：兼容写入，但不进入快照，同样留痕
                manager.WriteDo("DO_NOT_EXIST", true);
                Assert.DoesNotContain("DO_NOT_EXIST", manager.SnapshotDo().Keys);

                // 未配置点名的虚拟注入：保留旧行为广播一次，同时也留痕
                var injected = new ConcurrentQueue<(string Name, bool Value)>();
                manager.DiChanged += (n, v) => injected.Enqueue((n, v));
                manager.SetVirtualDi("DI_NOT_EXIST", true);
                Assert.Contains(injected, e => e.Name == "DI_NOT_EXIST" && e.Value);

                // 底层一直抛异常的后台轮询：异常不外逃，轮询线程仍然活着
                Assert.True(WaitUntil(() => errors.Count >= 2, 2000));
                Assert.True(WaitUntil(() => manager.PollCycleCount >= 3, 2000));

                // 同步读仍旧把底层失败暴露出来（不伪装成稳定值）
                Assert.Throws<InvalidOperationException>(() => manager.ReadDi("DI_A"));
            }
        }
    }
}
