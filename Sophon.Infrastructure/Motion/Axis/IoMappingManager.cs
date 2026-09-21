#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;

namespace Sophon.Infrastructure.Motion.Axis
{
    /// <summary>
    /// 工业 IO 映射管理控制器（实现 IIoController）。
    /// 核心职责：
    /// 1. 将应用层工艺逻辑点名（如 "DI_AIR_PRESSURE_OK", "DO_GRIP_OPEN"）
    ///    按配置表映射到物理硬件（卡号 + 物理通道位）；
    /// 2. 处理电气极性硬件反转 (Invert) 与 常开/常闭 (NO/NC) 逻辑反相，确保无论现场传感器是 NPN/PNP/NO/NC，上层应用逻辑值一致（1=有效，0=无效）；
    /// 3. 执行软件防抖滤波时间窗；
    /// 4. 包装底层硬件 IO 驱动（Sim/固高/雷赛），支持动态热更新点位表。
    /// </summary>
    public class IoMappingManager : Sophon.Contracts.IIoController
    {
        private readonly IoPointConfigStore _store;
        private readonly Func<int, int, bool> _rawDiReader;
        private readonly Action<int, int, bool> _rawDoWriter;

        private readonly ConcurrentDictionary<string, IoPointDefinition> _pointMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _doStateCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (bool state, long lastChangeTick)> _debounceCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public event Action<string, bool>? DiChanged;

        public IReadOnlyList<string> DiPointNames =>
            _pointMap.Values.Where(p => p.Direction == IoDirection.DI).Select(p => p.LogicalName).ToList();

        public IReadOnlyList<string> DoPointNames =>
            _pointMap.Values.Where(p => p.Direction == IoDirection.DO).Select(p => p.LogicalName).ToList();

        public IoMappingManager(
            IoPointConfigStore? store = null,
            Func<int, int, bool>? rawDiReader = null,
            Action<int, int, bool>? rawDoWriter = null)
        {
            _store = store ?? new IoPointConfigStore();
            // 缺省回退到内存缓存（无真实卡时）
            _rawDiReader = rawDiReader ?? ((card, bit) => false);
            _rawDoWriter = rawDoWriter ?? ((card, bit, val) => { });

            Reload();
        }

        /// <summary>
        /// 重新加载持久化 IO 映射表。
        /// </summary>
        public void Reload()
        {
            lock (_lock)
            {
                _pointMap.Clear();
                var list = _store.Load();
                if (list.Count == 0)
                {
                    list = IoPointConfigStore.SeedDefaults();
                    _store.Save(list);
                }

                foreach (var p in list)
                {
                    _pointMap[p.LogicalName] = p;
                }
            }
        }

        /// <summary>
        /// 注册或更新单个点位映射。
        /// </summary>
        public void SetPoint(IoPointDefinition def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));
            _pointMap[def.LogicalName] = def;
        }

        /// <summary>
        /// 读数字输入点（逻辑语义：true=信号有效/触发，false=未触发）。
        /// 自动完成 物理电平 -> 极性反转 -> 常闭取反 -> 滤波防抖。
        /// </summary>
        public bool ReadDi(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return false;

            if (_pointMap.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DI)
            {
                // 1. 读取底层物理位电平
                bool raw = _rawDiReader(def.CardNo, def.ChannelBit);

                // 2. 极性反转与常闭(NC)取反计算：
                //    常闭NC在未触发时物理导通(raw=1)，逻辑应为 false；动作断开(raw=0)时，逻辑为 true。
                bool logical = raw ^ def.Invert ^ (def.Switch == SwitchType.NormallyClose);

                // 3. 软件防抖滤波
                if (def.FilterMs > 0)
                {
                    long now = Environment.TickCount64;
                    var entry = _debounceCache.GetOrAdd(pointName, _ => (logical, now));
                    if (entry.state != logical)
                    {
                        // 发生翻转，检查是否维持超过 FilterMs
                        if (now - entry.lastChangeTick >= def.FilterMs)
                        {
                            _debounceCache[pointName] = (logical, now);
                            return logical;
                        }
                        // 未超过滤波时间，维持上一稳定状态
                        return entry.state;
                    }
                    else
                    {
                        _debounceCache[pointName] = (logical, now);
                    }
                }

                return logical;
            }

            // 点名未配置时直接返回 false
            return false;
        }

        /// <summary>
        /// 写数字输出点（逻辑语义：true=输出有效，false=输出无效）。
        /// 自动完成 逻辑电平 -> 极性反转 -> 硬件物理位输出。
        /// </summary>
        public void WriteDo(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;

            if (_pointMap.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DO)
            {
                // 极性反转
                bool physicalOut = value ^ def.Invert;

                _rawDoWriter(def.CardNo, def.ChannelBit, physicalOut);
                _doStateCache[pointName] = value;
            }
            else
            {
                _doStateCache[pointName] = value;
            }
        }

        /// <summary>
        /// 模拟手动或外部触发输入点电平变化（测试或仿真用）。
        /// </summary>
        public void SetVirtualDi(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;
            DiChanged?.Invoke(pointName, value);
        }

        public IReadOnlyDictionary<string, bool> SnapshotDi()
        {
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _pointMap.Values.Where(p => p.Direction == IoDirection.DI))
            {
                dict[p.LogicalName] = ReadDi(p.LogicalName);
            }
            return dict;
        }

        public IReadOnlyDictionary<string, bool> SnapshotDo()
        {
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _pointMap.Values.Where(p => p.Direction == IoDirection.DO))
            {
                dict[p.LogicalName] = _doStateCache.TryGetValue(p.LogicalName, out var val) && val;
            }
            return dict;
        }
    }
}
