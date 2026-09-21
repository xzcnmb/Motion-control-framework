#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Sophon.Contracts;
using Sophon.Core;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 全局限位联动监视器（后台线程，周期可配默认 20ms，带看门狗与瞬态协议）。
    /// 规则：
    /// 1. 订阅 AxisManager.GlobalLimitAlarm（硬限位）→ AlarmCenter.Raise(Severity=Stop/EStop, Linkage=StopAllAxes 或 EStopAll)。
    /// 2. 订阅 SnapshotsUpdated（软限位越界检测）→ 超出 AxisDefinition 软限位判软限位触发 → Severity=Error + StopAxis。
    /// 3. 看门狗：AxisManager 快照停止更新超过 1s → Raise("WATCHDOG_TIMEOUT", Severity=Error, Linkage=StopAllAxes)，
    ///    并执行 Fail-Safe IO 关断钩子：FailSafeDoResetList 中的 DO（如气动电磁阀）立即强制置 FALSE，
    ///    防止气动执行器在失去监控的情况下继续运动。
    /// 4. 退离限位"瞬态协议"：硬限位触发后置位 Flag 禁止同方向运动；离开限位电平后允许手动 Reset（TryResetLimit），复位经 AlarmCenter.Clear + WorkStation.Reset。
    /// </summary>
    public class GlobalLimitMonitor : IDisposable
    {
        private readonly AxisManager _axisManager;
        private readonly AlarmCenter _alarmCenter;
        private readonly IIoController? _ioController;
        private readonly IWorkStation? _workStation;
        private readonly IStateMachine? _stateMachine;
        private readonly IReadOnlyList<AxisDefinition> _axisDefinitions;
        private readonly List<string> _failSafeDoResetList = new();

        private readonly ConcurrentDictionary<int, LimitStateRecord> _limitStates = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _monitorThread;
        private readonly int _intervalMs;
        private readonly TimeSpan _watchdogTimeout;

        private long _lastSnapshotTicks;
        private bool _disposed;
        private volatile bool _watchdogAlarmActive;
        private volatile bool _failSafeTriggered;

        public class LimitStateRecord
        {
            public int AxisId { get; set; }
            public bool HardLimitTriggered { get; set; }
            public bool SoftLimitTriggered { get; set; }
            public string? ActiveLimitPoint { get; set; }
            public bool IsPositiveDirection { get; set; }
            public bool MotionInForbiddenDirectionProhibited { get; set; }
        }

        public IReadOnlyDictionary<int, LimitStateRecord> LimitStates => _limitStates;

        /// <summary>
        /// Fail-Safe DO 复位清单：看门狗超时（通信丢失）时被立即强制置 FALSE 的 DO 点名
        /// （如气动电磁阀），防止气动执行器在失去监控的情况下继续运动。
        /// </summary>
        public IReadOnlyList<string> FailSafeDoResetList => _failSafeDoResetList;

        /// <summary>
        /// 看门狗 Fail-Safe 是否已触发过（ latch 锁存，直到显式调用 ResetFailSafeIoShutdown 复位）。
        /// </summary>
        public bool FailSafeTriggered => _failSafeTriggered;

        /// <summary>
        /// 本次 Fail-Safe 动作中实际被强制置 FALSE 的 DO 点名（诊断追溯用）。
        /// </summary>
        public IReadOnlyList<string> FailSafeResetDoPoints { get; private set; } = Array.Empty<string>();

        /// <summary>按轴组合报警代码，使每个轴拥有独立的报警实例（如 SOFT_LIMIT_ERROR#2）。</summary>
        private static string AxisCode(string baseCode, int axisId) => $"{baseCode}#{axisId}";

        public GlobalLimitMonitor(
            AxisManager axisManager,
            AlarmCenter alarmCenter,
            IReadOnlyList<AxisDefinition>? axisDefinitions = null,
            IIoController? ioController = null,
            IWorkStation? workStation = null,
            IStateMachine? stateMachine = null,
            int intervalMs = 20,
            TimeSpan? watchdogTimeout = null,
            IEnumerable<string>? failSafeDoResetList = null)
        {
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));
            _alarmCenter = alarmCenter ?? throw new ArgumentNullException(nameof(alarmCenter));
            _ioController = ioController;
            _workStation = workStation;
            _stateMachine = stateMachine;
            _intervalMs = intervalMs > 0 ? intervalMs : 20;
            _watchdogTimeout = watchdogTimeout ?? TimeSpan.FromSeconds(1.0);

            if (failSafeDoResetList != null)
            {
                _failSafeDoResetList.AddRange(failSafeDoResetList);
            }

            _axisDefinitions = axisDefinitions ?? _axisManager.Controller?.Axes ?? new List<AxisDefinition>();

            foreach (var def in _axisDefinitions)
            {
                _limitStates[def.AxisId] = new LimitStateRecord { AxisId = def.AxisId };
            }

            _lastSnapshotTicks = DateTime.UtcNow.Ticks;

            // 看门狗为全局单实例报警
            _alarmCenter.Register(new AlarmDefinition("WATCHDOG_TIMEOUT", "AxisManager 快照看门狗超时", AlarmSeverity.Error, LinkageMode.StopAllAxes));

            // 限位报警按轴实例化注册，避免多轴同时越界时相互覆盖 / 单次 Clear 误清其他轴。
            foreach (var def in _axisDefinitions)
            {
                _alarmCenter.Register(new AlarmDefinition(AxisCode("HARD_LIMIT_ESTOP", def.AxisId), $"轴 {def.AxisId} 硬限位触发（紧急停止）", AlarmSeverity.EStop, LinkageMode.EStopAll));
                _alarmCenter.Register(new AlarmDefinition(AxisCode("HARD_LIMIT_STOP", def.AxisId), $"轴 {def.AxisId} 硬限位触发（停止所有轴）", AlarmSeverity.Stop, LinkageMode.StopAllAxes));
                _alarmCenter.Register(new AlarmDefinition(AxisCode("SOFT_LIMIT_ERROR", def.AxisId), $"轴 {def.AxisId} 软限位越界", AlarmSeverity.Error, LinkageMode.StopAxis));
            }

            // 订阅 AxisManager 事件
            _axisManager.GlobalLimitAlarm += OnGlobalLimitAlarm;
            _axisManager.SnapshotsUpdated += OnSnapshotsUpdated;

            _monitorThread = new Thread(MonitorLoop)
            {
                Name = "GlobalLimitMonitor_Loop",
                IsBackground = true
            };
            _monitorThread.Start();
        }

        /// <summary>
        /// 响应 AxisManager 的硬限位事件。
        /// </summary>
        public void OnGlobalLimitAlarm(GlobalLimitAlarmArgs args)
        {
            var record = _limitStates.GetOrAdd(args.AxisId, id => new LimitStateRecord { AxisId = id });
            record.HardLimitTriggered = true;
            record.ActiveLimitPoint = args.LimitPointName;
            record.IsPositiveDirection = args.IsPositiveDirection;
            record.MotionInForbiddenDirectionProhibited = true; // 瞬态协议：禁止同方向运动

            string code = AxisCode(args.IsHardLimit ? "HARD_LIMIT_ESTOP" : "SOFT_LIMIT_ERROR", args.AxisId);
            string detail = $"Axis:{args.AxisId}, Point:{args.LimitPointName}, Dir:{(args.IsPositiveDirection ? "Pos" : "Neg")}, Hard:{args.IsHardLimit}";
            _alarmCenter.Raise(code, detail, source: "LimitMonitor");
        }

        /// <summary>
        /// 响应 AxisManager 的快照更新（软限位越界检测 + 刷新看门狗时间戳）。
        /// </summary>
        public void OnSnapshotsUpdated(IReadOnlyList<AxisSnapshot> snapshots)
        {
            Interlocked.Exchange(ref _lastSnapshotTicks, DateTime.UtcNow.Ticks);

            if (_watchdogAlarmActive)
            {
                _alarmCenter.Clear("WATCHDOG_TIMEOUT");
                _watchdogAlarmActive = false;
            }

            if (snapshots == null) return;

            foreach (var snapshot in snapshots)
            {
                var def = _axisDefinitions.FirstOrDefault(d => d.AxisId == snapshot.AxisId);
                if (def == null || !def.SoftLimitEnabled) continue;

                var record = _limitStates.GetOrAdd(snapshot.AxisId, id => new LimitStateRecord { AxisId = id });

                // 软限位越界检测（按轴独立报警实例）
                string softCode = AxisCode("SOFT_LIMIT_ERROR", snapshot.AxisId);
                if (snapshot.Position > def.SoftLimitMax)
                {
                    if (!record.SoftLimitTriggered)
                    {
                        record.SoftLimitTriggered = true;
                        record.IsPositiveDirection = true;
                        string detail = $"Axis:{snapshot.AxisId}, Pos:{snapshot.Position:F3} > Max:{def.SoftLimitMax:F3}";
                        _alarmCenter.Raise(softCode, detail, source: "LimitMonitor");
                    }
                }
                else if (snapshot.Position < def.SoftLimitMin)
                {
                    if (!record.SoftLimitTriggered)
                    {
                        record.SoftLimitTriggered = true;
                        record.IsPositiveDirection = false;
                        string detail = $"Axis:{snapshot.AxisId}, Pos:{snapshot.Position:F3} < Min:{def.SoftLimitMin:F3}";
                        _alarmCenter.Raise(softCode, detail, source: "LimitMonitor");
                    }
                }
                else
                {
                    // 若已回到软限位范围之内，自动复位该轴软限位标记与报警
                    if (record.SoftLimitTriggered)
                    {
                        record.SoftLimitTriggered = false;
                        _alarmCenter.Clear(softCode);
                    }
                }
            }
        }

        /// <summary>
        /// 后台监视循环（看门狗超时监控）。
        /// </summary>
        private void MonitorLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    long lastTicks = Interlocked.Read(ref _lastSnapshotTicks);
                    var elapsed = DateTime.UtcNow - new DateTime(lastTicks, DateTimeKind.Utc);

                    if (elapsed > _watchdogTimeout && !_watchdogAlarmActive)
                    {
                        _watchdogAlarmActive = true;
                        _alarmCenter.Raise("WATCHDOG_TIMEOUT", $"AxisManager 快照停止更新超过 {elapsed.TotalMilliseconds:F0}ms", source: "Watchdog");

                        // Fail-Safe IO 关断钩子：通信丢失即视为设备状态不可信，
                        // 立即强制 FailSafeDoResetList 中的 DO（气动电磁阀等）置 FALSE，
                        // 防止气动执行器在失去监控的情况下继续运动（每次超时事件都重新强制执行，幂等）。
                        TriggerFailSafeIoShutdown($"WATCHDOG_TIMEOUT: 快照停止更新超过 {elapsed.TotalMilliseconds:F0}ms");
                    }

                    Thread.Sleep(_intervalMs);
                }
                catch (ThreadAbortException)
                {
                    break;
                }
                catch { }
            }
        }

        /// <summary>
        /// Fail-Safe IO 关断钩子：将 <see cref="FailSafeDoResetList"/> 中的 DO 立即强制置 FALSE。
        /// 看门狗超时（通信丢失）时由监视循环自动调用；也可由人工急停等场景显式调用。
        /// 单点写入失败不影响其余点位的关断（尽力而为，逐点 try/catch）。
        /// 注意：本动作只关断（置 FALSE），绝不会置 TRUE，不会驱动任何执行器动作。
        /// </summary>
        /// <param name="reason">触发原因（用于诊断追溯）</param>
        public void TriggerFailSafeIoShutdown(string reason)
        {
            var applied = new List<string>();

            if (_ioController != null)
            {
                foreach (var point in _failSafeDoResetList)
                {
                    if (string.IsNullOrWhiteSpace(point)) continue;
                    try
                    {
                        _ioController.WriteDo(point, false);
                        applied.Add(point);
                    }
                    catch
                    {
                        // 单点写入失败（IO 卡故障等）：记录跳过，继续关断其余点位
                    }
                }
            }

            FailSafeResetDoPoints = applied;
            _failSafeTriggered = true;
        }

        /// <summary>
        /// 复位 Fail-Safe 锁存标志（人工确认现场安全后的诊断复位）。
        /// 注意：仅清除锁存状态，【不会】重新点亮任何 DO —— 恢复输出必须由人工显式执行。
        /// </summary>
        public void ResetFailSafeIoShutdown()
        {
            _failSafeTriggered = false;
            FailSafeResetDoPoints = Array.Empty<string>();
        }

        /// <summary>
        /// 瞬态协议：检查当前轴是否被禁止向该方向运动。
        /// </summary>
        public bool IsDirectionProhibited(int axisId, int direction)
        {
            if (_limitStates.TryGetValue(axisId, out var record) && record.MotionInForbiddenDirectionProhibited)
            {
                bool isPos = direction > 0;
                if (record.IsPositiveDirection == isPos)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 瞬态协议：尝试手动复位限位状态。
        /// 需检测限位电平已离开（若提供了 IIoController 且配置了点名），否则拒绝复位。
        /// 复位成功后执行 AlarmCenter.Clear + WorkStation/StateMachine.Reset。
        /// </summary>
        /// <param name="axisId">轴 ID</param>
        /// <returns>是否复位成功</returns>
        public bool TryResetLimit(int axisId)
        {
            if (!_limitStates.TryGetValue(axisId, out var record))
            {
                return false;
            }

            // 若有 IO 控制器与限位点名，核查电平是否已离开（通常限位触发为 true，离开为 false）
            if (_ioController != null && !string.IsNullOrEmpty(record.ActiveLimitPoint))
            {
                try
                {
                    bool currentLevel = _ioController.ReadDi(record.ActiveLimitPoint);
                    if (currentLevel)
                    {
                        // 依然处于限位电平上，拒绝复位
                        return false;
                    }
                }
                catch { }
            }

            // 清除记录状态
            record.HardLimitTriggered = false;
            record.SoftLimitTriggered = false;
            record.MotionInForbiddenDirectionProhibited = false;
            record.ActiveLimitPoint = null;

            // 仅清除该轴自身的报警实例，不影响其他轴
            _alarmCenter.Clear(AxisCode("HARD_LIMIT_ESTOP", axisId));
            _alarmCenter.Clear(AxisCode("HARD_LIMIT_STOP", axisId));
            _alarmCenter.Clear(AxisCode("SOFT_LIMIT_ERROR", axisId));

            // 复位工站状态机
            try
            {
                _stateMachine?.Reset();
                _workStation?.Reset();
            }
            catch { }

            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _axisManager.GlobalLimitAlarm -= OnGlobalLimitAlarm;
            _axisManager.SnapshotsUpdated -= OnSnapshotsUpdated;

            _cts.Cancel();
            if (_monitorThread.IsAlive)
            {
                _monitorThread.Join(500);
            }
            _cts.Dispose();
        }
    }
}
