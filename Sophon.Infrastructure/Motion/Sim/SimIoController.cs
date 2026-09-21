#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Motion.Sim
{
    /// <summary>
    /// 仿真 IO 控制器实现。
    /// 线程安全，支持点名访问、快照、模拟置位触发 DiChanged 事件。
    /// </summary>
    public class SimIoController : Sophon.Contracts.IIoController
    {
        private readonly ConcurrentDictionary<string, bool> _diPoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _doPoints = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// DI 状态变化事件（点名 -> 新值）。
        /// </summary>
        public event Action<string, bool>? DiChanged;

        /// <summary>
        /// 已声明的 DI 点名清单。
        /// </summary>
        public IReadOnlyList<string> DiPointNames => _diPoints.Keys.ToList();

        /// <summary>
        /// 已声明的 DO 点名清单。
        /// </summary>
        public IReadOnlyList<string> DoPointNames => _doPoints.Keys.ToList();

        /// <summary>
        /// 默认构造函数：装配常用仿真点名。
        /// </summary>
        public SimIoController()
            : this(
                initialDi: new Dictionary<string, bool>
                {
                    ["EStopButton"] = false,
                    ["HomeX"] = false,
                    ["HomeY"] = false,
                    ["LimitX+"] = false,
                    ["LimitX-"] = false,
                    ["LimitY+"] = false,
                    ["LimitY-"] = false,
                    ["LimitZ+"] = false,
                    ["LimitZ-"] = false
                },
                diPointNames: null,
                doPointNames: new[] { "GreenLight", "RedLight", "Buzzer", "CylinderPush", "VacuumSuck" })
        {
        }

        /// <summary>
        /// 自定义点名和初值构造。
        /// </summary>
        /// <param name="initialDi">初始 DI 键值对</param>
        /// <param name="diPointNames">额外声明的 DI 点名清单（初始值为 false）</param>
        /// <param name="doPointNames">声明的 DO 点名清单（初始值为 false）</param>
        public SimIoController(
            IReadOnlyDictionary<string, bool>? initialDi,
            IEnumerable<string>? diPointNames = null,
            IEnumerable<string>? doPointNames = null)
        {
            if (initialDi != null)
            {
                foreach (var kv in initialDi)
                {
                    _diPoints[kv.Key] = kv.Value;
                }
            }

            if (diPointNames != null)
            {
                foreach (var name in diPointNames)
                {
                    if (!_diPoints.ContainsKey(name))
                    {
                        _diPoints[name] = false;
                    }
                }
            }

            if (doPointNames != null)
            {
                foreach (var name in doPointNames)
                {
                    _doPoints[name] = false;
                }
            }
        }

        /// <summary>
        /// 读取指定虚拟点名的 DI 值。未声明的点名默认返回 false。
        /// </summary>
        public bool ReadDi(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName))
                return false;

            return _diPoints.TryGetValue(pointName, out var val) && val;
        }

        /// <summary>
        /// 写入指定虚拟点名的 DO 值。
        /// </summary>
        public void WriteDo(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName))
                return;

            _doPoints[pointName] = value;
        }

        /// <summary>
        /// 仿真端手动置位 DI（常用于限位开关、原点开关、急停按钮触发仿真）。
        /// 若状态发生变化，将触发 <see cref="DiChanged"/> 事件。
        /// </summary>
        /// <param name="pointName">点名</param>
        /// <param name="value">新值</param>
        public void SetDi(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName))
                return;

            bool changed = false;
            _diPoints.AddOrUpdate(
                pointName,
                _ =>
                {
                    changed = true;
                    return value;
                },
                (_, oldVal) =>
                {
                    if (oldVal != value)
                    {
                        changed = true;
                    }
                    return value;
                });

            if (changed)
            {
                try
                {
                    DiChanged?.Invoke(pointName, value);
                }
                catch
                {
                    // 保护调用方异常不打崩调度
                }
            }
        }

        /// <summary>
        /// 获取当前所有 DI 状态快照。
        /// </summary>
        public IReadOnlyDictionary<string, bool> SnapshotDi()
        {
            return new Dictionary<string, bool>(_diPoints, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取当前所有 DO 状态快照。
        /// </summary>
        public IReadOnlyDictionary<string, bool> SnapshotDo()
        {
            return new Dictionary<string, bool>(_doPoints, StringComparer.OrdinalIgnoreCase);
        }
    }
}
