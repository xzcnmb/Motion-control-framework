#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Motion.Axis
{
    /// <summary>
    /// 轴管理器。
    /// 负责轴注册表管理、轴状态机维护、配置 JSON 持久化（SophonData\axis_config.json）、
    /// 后台刷新线程（默认 20ms）聚合位置/速度快照、转发事件并聚合 LimitTriggered 为 GlobalLimitAlarm。
    /// </summary>
    public class AxisManager : IDisposable
    {
        private class AxisInternalContext
        {
            public AxisDefinition Definition { get; set; } = null!;
            public AxisState State { get; set; } = AxisState.Disabled;
            public double Position { get; set; }
            public double Velocity { get; set; }
            public bool Enabled { get; set; }
            public bool Homed { get; set; }
            public Guid LastRequestId { get; set; }
            public bool CommandInFlight { get; set; }
            public long StopRequestedTick { get; set; }
            public bool ReadFaultReported { get; set; }
        }

        private readonly IMotionController _controller;
        private readonly ConcurrentDictionary<int, AxisInternalContext> _axes = new();
        private readonly object _lock = new();

        private Thread? _refreshThread;
        private readonly CancellationTokenSource _cts = new();
        private readonly int _refreshIntervalMs;
        private bool _disposed;

        /// <summary>
        /// 轴状态快照更新事件（供 UI / 监视器周期订阅）。
        /// </summary>
        public event Action<IReadOnlyList<AxisSnapshot>>? SnapshotsUpdated;

        /// <summary>
        /// 单轴到位事件转发。
        /// </summary>
        public event Action<AxisDoneArgs>? AxisDone;

        /// <summary>
        /// 单轴故障事件转发。
        /// </summary>
        public event Action<AxisFaultArgs>? AxisFault;

        /// <summary>
        /// 全局限位报警事件（供 Wave3-F 报警中心消费）。
        /// </summary>
        public event Action<GlobalLimitAlarmArgs>? GlobalLimitAlarm;

        /// <summary>
        /// 底层控制器实例。
        /// </summary>
        public IMotionController Controller => _controller;

        /// <summary>
        /// 构造轴管理器。
        /// </summary>
        /// <param name="controller">运动控制器</param>
        /// <param name="refreshIntervalMs">后台刷新周期（毫秒，默认 20ms）</param>
        public AxisManager(IMotionController controller, int refreshIntervalMs = 20)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _refreshIntervalMs = refreshIntervalMs > 0 ? refreshIntervalMs : 20;

            // 注册轴
            if (_controller.Axes != null)
            {
                foreach (var ax in _controller.Axes)
                {
                    var initial = new AxisInternalContext
                    {
                        Definition = ax,
                        State = AxisState.Disabled,
                        Position = 0,
                        Velocity = 0,
                        Enabled = false,
                        Homed = false
                    };

                    // 在启动刷新线程前取得一次同步快照，避免刚创建管理器时示教/监控读到默认零值。
                    try
                    {
                        initial.Position = _controller.GetPosition(ax.AxisId);
                        initial.Velocity = _controller.GetVelocity(ax.AxisId);
                        initial.Enabled = _controller.IsAxisEnabled(ax.AxisId);
                        initial.Homed = _controller.IsAxisHomed(ax.AxisId);
                        initial.State = initial.Enabled ? AxisState.Standstill : AxisState.Disabled;
                    }
                    catch
                    {
                        initial.State = AxisState.ErrorStop;
                    }

                    _axes[ax.AxisId] = initial;
                }
            }

            // 挂接底层控制器事件
            _controller.AxisDone += OnControllerAxisDone;
            _controller.AxisFault += OnControllerAxisFault;
            _controller.LimitTriggered += OnControllerLimitTriggered;

            // 启动后台快照刷新线程
            _refreshThread = new Thread(RefreshLoop)
            {
                Name = "AxisManager_RefreshLoop",
                IsBackground = true
            };
            _refreshThread.Start();
        }

        private void OnControllerAxisDone(AxisDoneArgs args)
        {
            lock (_lock)
            {
                AxisInternalContext? ctx = _axes.Values.FirstOrDefault(candidate =>
                    candidate.LastRequestId == args.RequestId && args.RequestId != Guid.Empty);
                if (ctx == null && _axes.TryGetValue(args.AxisId, out var byAxis))
                {
                    ctx = byAxis;
                }

                if (ctx != null && ctx.LastRequestId == args.RequestId && args.RequestId != Guid.Empty)
                {
                    ctx.CommandInFlight = false;
                    if (args.Status == CommandCompletionStatus.Error)
                    {
                        ctx.State = AxisState.ErrorStop;
                    }
                    else if (ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Disabled)
                    {
                        // Done/CommandAborted 都表示当前命令已结束；受控停不应永久锁死轴。
                        ctx.State = AxisState.Standstill;
                    }
                }
            }

            try
            {
                AxisDone?.Invoke(args);
            }
            catch { }
        }

        private void OnControllerAxisFault(AxisFaultArgs args)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(args.AxisId, out var ctx))
                {
                    ctx.State = AxisState.ErrorStop;
                }
            }

            try
            {
                AxisFault?.Invoke(args);
            }
            catch { }
        }

        private void OnControllerLimitTriggered(LimitTriggeredArgs args)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(args.AxisId, out var ctx))
                {
                    if (args.IsHardLimit)
                    {
                        ctx.State = AxisState.ErrorStop;
                    }
                }
            }

            // 聚合为 GlobalLimitAlarm 事件派发
            try
            {
                GlobalLimitAlarm?.Invoke(new GlobalLimitAlarmArgs(
                    args.AxisId,
                    args.LimitPointName,
                    args.IsPositiveDirection,
                    args.IsHardLimit));
            }
            catch { }
        }

        /// <summary>
        /// 获取当前所有轴的只读快照列表（线程安全）。
        /// </summary>
        public IReadOnlyList<AxisSnapshot> GetSnapshots()
        {
            lock (_lock)
            {
                return _axes.Values.Select(ctx => new AxisSnapshot(
                    ctx.Definition.AxisId,
                    ctx.Position,
                    ctx.Velocity,
                    ctx.Enabled,
                    ctx.Homed,
                    ctx.State
                )).ToList();
            }
        }

        /// <summary>
        /// 获取单轴快照。
        /// </summary>
        public AxisSnapshot? GetSnapshot(int axisId)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    return new AxisSnapshot(
                        ctx.Definition.AxisId,
                        ctx.Position,
                        ctx.Velocity,
                        ctx.Enabled,
                        ctx.Homed,
                        ctx.State);
                }
                return null;
            }
        }

        /// <summary>
        /// 轴使能。
        /// </summary>
        public void EnableAxis(int axisId)
        {
            _controller.EnableAxis(axisId);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    ctx.Enabled = true;
                    if (ctx.State == AxisState.Disabled)
                    {
                        ctx.State = AxisState.Idle;
                    }
                }
            }
        }

        /// <summary>
        /// 轴下使能。
        /// </summary>
        public void DisableAxis(int axisId)
        {
            if (!_axes.ContainsKey(axisId))
            {
                throw new ArgumentOutOfRangeException(nameof(axisId), axisId, "未注册的轴号");
            }

            _controller.DisableAxis(axisId);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    ctx.Enabled = false;
                    ctx.CommandInFlight = false;
                    ctx.LastRequestId = Guid.Empty;
                    ctx.StopRequestedTick = 0;
                    ctx.State = AxisState.Disabled;
                }
            }
        }

        /// <summary>
        /// 使能全部轴。
        /// </summary>
        public void EnableAll()
        {
            foreach (var axisId in _axes.Keys)
            {
                EnableAxis(axisId);
            }
        }

        /// <summary>
        /// 下使能全部轴。
        /// </summary>
        public void DisableAll()
        {
            foreach (var axisId in _axes.Keys)
            {
                DisableAxis(axisId);
            }
        }

        /// <summary>
        /// 单轴绝对定位。
        /// </summary>
        public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var checkCtx))
                {
                    if (checkCtx.State == AxisState.Stopping)
                    {
                        throw new InvalidOperationException($"轴 {axisId} 正处于 Stopping 停止状态，拒绝接收新运动指令 (PLCopen规范)");
                    }
                    if (checkCtx.State == AxisState.ErrorStop)
                    {
                        throw new InvalidOperationException($"轴 {axisId} 处于 ErrorStop 故障锁定状态，请先执行 ResetAxis 复位后再试");
                    }
                }
            }

            var req = _controller.MoveAbs(axisId, target, speed, accel, decel, jerk);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    ctx.LastRequestId = req;
                    ctx.CommandInFlight = true;
                    if (ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Stopping && ctx.Enabled)
                    {
                        ctx.State = AxisState.DiscreteMotion;
                    }
                }
            }
            return req;
        }

        /// <summary>
        /// 单轴绝对定位（异步完成回报版）：转发控制器的 MoveAbsAsync，无订阅竞态。
        /// </summary>
        public Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var checkCtx))
                {
                    if (checkCtx.State == AxisState.Stopping)
                    {
                        return Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), false, $"轴 {axisId} 正处于 Stopping 停止状态，拒绝新指令", CommandCompletionStatus.CommandAborted, axisId));
                    }
                    if (checkCtx.State == AxisState.ErrorStop)
                    {
                        return Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), false, $"轴 {axisId} 处于 ErrorStop 故障锁定，需先复位", CommandCompletionStatus.Error, axisId));
                    }
                }
            }

            var task = _controller.MoveAbsAsync(axisId, target, speed, accel, decel, jerk, ct);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx) && ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Stopping && ctx.Enabled)
                {
                    ctx.LastRequestId = Guid.Empty;
                    ctx.CommandInFlight = true;
                    ctx.State = AxisState.DiscreteMotion;
                }
            }

            _ = task.ContinueWith(completed =>
            {
                if (!completed.IsCompletedSuccessfully) return;
                var args = completed.Result;
                lock (_lock)
                {
                    if (_axes.TryGetValue(args.AxisId, out var ctx) && ctx.CommandInFlight)
                    {
                        ctx.LastRequestId = args.RequestId;
                        ctx.CommandInFlight = false;
                        ctx.State = args.Status == CommandCompletionStatus.Error
                            ? AxisState.ErrorStop
                            : ctx.State == AxisState.Disabled ? AxisState.Disabled : AxisState.Standstill;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        /// <summary>
        /// 单轴点动。
        /// </summary>
        public Guid Jog(int axisId, int dir, double speed)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var checkCtx))
                {
                    if (checkCtx.State == AxisState.Stopping)
                    {
                        throw new InvalidOperationException($"轴 {axisId} 处于 Stopping 状态，拒绝点动");
                    }
                    if (checkCtx.State == AxisState.ErrorStop)
                    {
                        throw new InvalidOperationException($"轴 {axisId} 处于 ErrorStop 状态，需先复位");
                    }
                }
            }

            var req = _controller.Jog(axisId, dir, speed);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    ctx.LastRequestId = req;
                    if (ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Stopping && ctx.Enabled)
                    {
                        ctx.State = AxisState.ContinuousMotion;
                    }
                }
            }
            return req;
        }

        /// <summary>
        /// 单轴回零。
        /// </summary>
        public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var checkCtx))
                {
                    if (checkCtx.State == AxisState.Stopping || checkCtx.State == AxisState.ErrorStop)
                    {
                        throw new InvalidOperationException($"轴 {axisId} 处于非可回零状态: {checkCtx.State}");
                    }
                }
            }

            var req = _controller.Home(axisId, mode, dir, speed);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    ctx.LastRequestId = req;
                    if (ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Stopping && ctx.Enabled)
                    {
                        ctx.State = AxisState.Homing;
                    }
                }
            }
            return req;
        }

        /// <summary>
        /// 单轴回零（异步完成回报版）：转发控制器的 HomeAsync，无订阅竞态。
        /// </summary>
        public Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var checkCtx))
                {
                    if (checkCtx.State == AxisState.Stopping || checkCtx.State == AxisState.ErrorStop)
                    {
                        return Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), false, $"轴 {axisId} 处于非可回零状态: {checkCtx.State}", CommandCompletionStatus.Error, axisId));
                    }
                }
            }

            var task = _controller.HomeAsync(axisId, mode, dir, speed, ct);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx) && ctx.State != AxisState.ErrorStop && ctx.State != AxisState.Stopping && ctx.Enabled)
                {
                    ctx.LastRequestId = Guid.Empty;
                    ctx.CommandInFlight = true;
                    ctx.State = AxisState.Homing;
                }
            }

            _ = task.ContinueWith(completed =>
            {
                if (!completed.IsCompletedSuccessfully) return;
                var args = completed.Result;
                lock (_lock)
                {
                    if (_axes.TryGetValue(args.AxisId, out var ctx) && ctx.CommandInFlight)
                    {
                        ctx.LastRequestId = args.RequestId;
                        ctx.CommandInFlight = false;
                        ctx.State = args.Status == CommandCompletionStatus.Error
                            ? AxisState.ErrorStop
                            : ctx.State == AxisState.Disabled ? AxisState.Disabled : AxisState.Standstill;
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }

        /// <summary>
        /// 全部轴回零。
        /// </summary>
        public IReadOnlyList<Guid> HomeAll()
        {
            var list = new List<Guid>();
            foreach (var ctx in _axes.Values)
            {
                var def = ctx.Definition;
                var req = Home(def.AxisId, def.HomeMode, def.HomeDir, def.HomeSpeed);
                list.Add(req);
            }
            return list;
        }

        // ---------- 停止与安全分层实现 (EN 60204-1 Stop Category 2/1/0 & PLCopen) ----------

        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)。
        /// 正常平稳按减速度减速至 0，轴保持使能状态 (Standstill)，不脱离轨迹坐标，解除后可直接继续运行。
        /// </summary>
        public void Halt(int axisId)
        {
            _controller.Halt(axisId);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx) && ctx.State != AxisState.Disabled && ctx.State != AxisState.ErrorStop)
                {
                    ctx.State = AxisState.Standstill;
                }
            }
        }

        /// <summary>
        /// Level 1: 全部轴软件工艺暂停/平滑停止。
        /// </summary>
        public void HaltAll()
        {
            foreach (var axisId in _axes.Keys)
            {
                Halt(axisId);
            }
        }

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)。
        /// 触发底层卡级受控减速停，轴转入 Stopping 状态并闭锁，期间拒绝新运动请求，直到停稳并复位。
        /// </summary>
        public void Stop(int axisId)
        {
            bool shouldStop;
            lock (_lock)
            {
                if (!_axes.TryGetValue(axisId, out var ctx))
                {
                    throw new ArgumentOutOfRangeException(nameof(axisId), axisId, "未注册的轴号");
                }

                if (ctx.State == AxisState.Disabled || ctx.State == AxisState.ErrorStop)
                {
                    return;
                }

                shouldStop = ctx.CommandInFlight || Math.Abs(ctx.Velocity) > 0.0001;
                if (shouldStop)
                {
                    ctx.State = AxisState.Stopping;
                    ctx.StopRequestedTick = Environment.TickCount64;
                }
                else
                {
                    ctx.State = AxisState.Standstill;
                    ctx.StopRequestedTick = 0;
                }
            }

            // 不持有 AxisManager 锁调用驱动，避免驱动回调反向进入管理器造成死锁。
            _controller.Stop(axisId);
        }

        public void StopMotion(int axisId) => Stop(axisId);

        /// <summary>
        /// Level 2: 全部轴控制卡受控停止。
        /// </summary>
        public void StopAll()
        {
            foreach (var axisId in _axes.Keys)
            {
                Stop(axisId);
            }
        }

        /// <summary>
        /// Level 3: 硬件安全急停 / STO 安全力矩关断 (MC_EmergencyStop / Hard Abort, Stop Cat 0/1)。
        /// 立即切断脉冲或关断驱动器使能，轴强制转入 ErrorStop，必须显式 ResetAxis 才能恢复。
        /// </summary>
        public void EmergencyStop(int axisId)
        {
            _controller.EmergencyStop(axisId);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx) && ctx.State != AxisState.Disabled)
                {
                    ctx.State = AxisState.ErrorStop;
                }
            }
        }

        public void Abort(int axisId) => EmergencyStop(axisId);

        /// <summary>
        /// Level 3: 全局硬件安全急停。
        /// </summary>
        public void EmergencyStopAll()
        {
            _controller.EmergencyStopAll();
            lock (_lock)
            {
                foreach (var ctx in _axes.Values)
                {
                    if (ctx.State != AxisState.Disabled)
                    {
                        ctx.State = AxisState.ErrorStop;
                    }
                }
            }
        }

        public void AbortAll() => EmergencyStopAll();

        /// <summary>
        /// 故障复位指令 (MC_Reset)：将处于 ErrorStop 的轴复位回到 Standstill（若使能有效）或 Disabled。
        /// </summary>
        public void ResetAxis(int axisId)
        {
            if (!_axes.ContainsKey(axisId))
            {
                throw new ArgumentOutOfRangeException(nameof(axisId), axisId, "未注册的轴号");
            }

            _controller.ResetAxis(axisId);
            lock (_lock)
            {
                if (_axes.TryGetValue(axisId, out var ctx))
                {
                    if (ctx.State == AxisState.ErrorStop || ctx.State == AxisState.Stopping)
                    {
                        ctx.CommandInFlight = false;
                        ctx.LastRequestId = Guid.Empty;
                        ctx.StopRequestedTick = 0;
                        ctx.State = ctx.Enabled ? AxisState.Standstill : AxisState.Disabled;
                    }
                }
            }
        }

        /// <summary>
        /// 复位全部轴的故障状态。
        /// </summary>
        public void ResetAll()
        {
            foreach (var axisId in _axes.Keys)
            {
                ResetAxis(axisId);
            }
        }

        /// <summary>
        /// 将当前轴配置保存到 JSON 文件（默认运行目录 SophonData\axis_config.json）。
        /// </summary>
        /// <param name="filePath">自定义路径；若为空则保存至 AppDomain.CurrentDomain.BaseDirectory 下的 SophonData\axis_config.json</param>
        public void SaveConfiguration(string? filePath = null)
        {
            string path = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "axis_config.json");
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var definitions = _axes.Values.Select(c => c.Definition).ToList();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(definitions, options);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// 从 JSON 文件加载轴配置（默认运行目录 SophonData\axis_config.json）。
        /// </summary>
        /// <param name="filePath">配置文件路径</param>
        /// <returns>加载出的轴定义集合</returns>
        public static List<AxisDefinition> LoadConfiguration(string? filePath = null)
        {
            string path = filePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SophonData", "axis_config.json");
            if (!File.Exists(path))
            {
                return new List<AxisDefinition>();
            }

            string json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<List<AxisDefinition>>(json);
            return result ?? new List<AxisDefinition>();
        }

        private void RefreshLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    List<int> axisIds;
                    lock (_lock)
                    {
                        axisIds = _axes.Keys.ToList();
                    }

                    var readings = new Dictionary<int, (double position, double velocity, bool enabled, bool homed)>();
                    foreach (int axisId in axisIds)
                    {
                        try
                        {
                            readings[axisId] = (
                                _controller.GetPosition(axisId),
                                _controller.GetVelocity(axisId),
                                _controller.IsAxisEnabled(axisId),
                                _controller.IsAxisHomed(axisId));
                        }
                        catch (Exception ex)
                        {
                            lock (_lock)
                            {
                                if (_axes.TryGetValue(axisId, out var faultCtx) && !faultCtx.ReadFaultReported)
                                {
                                    faultCtx.ReadFaultReported = true;
                                    faultCtx.State = AxisState.ErrorStop;
                                }
                            }

                            try
                            {
                                AxisFault?.Invoke(new AxisFaultArgs(axisId, "AXIS_READ_FAILED", ex.Message));
                            }
                            catch { }
                        }
                    }

                    List<AxisSnapshot> snapshots;
                    lock (_lock)
                    {
                        foreach (var reading in readings)
                        {
                            if (!_axes.TryGetValue(reading.Key, out var ctx)) continue;

                            ctx.Position = reading.Value.position;
                            ctx.Velocity = reading.Value.velocity;
                            ctx.Enabled = reading.Value.enabled;
                            ctx.Homed = reading.Value.homed;

                            bool invalidReading = double.IsNaN(ctx.Position) || double.IsInfinity(ctx.Position) ||
                                                   double.IsNaN(ctx.Velocity) || double.IsInfinity(ctx.Velocity);
                            if (invalidReading)
                            {
                                ctx.State = AxisState.ErrorStop;
                                continue;
                            }

                            if (!ctx.Enabled)
                            {
                                ctx.State = AxisState.Disabled;
                                ctx.CommandInFlight = false;
                            }
                            else if (ctx.State == AxisState.Disabled)
                            {
                                ctx.State = AxisState.Standstill;
                            }
                            else if ((ctx.State == AxisState.ContinuousMotion || ctx.State == AxisState.Jogging ||
                                      ctx.State == AxisState.DiscreteMotion || ctx.State == AxisState.Moving ||
                                      ctx.State == AxisState.Homing) &&
                                     Math.Abs(ctx.Velocity) < 0.0001 && !ctx.CommandInFlight)
                            {
                                ctx.State = AxisState.Standstill;
                            }
                            else if (ctx.State == AxisState.Stopping && Math.Abs(ctx.Velocity) < 0.0001)
                            {
                                ctx.State = AxisState.Standstill;
                                ctx.StopRequestedTick = 0;
                            }
                            else if (ctx.State == AxisState.Stopping &&
                                     ctx.StopRequestedTick > 0 &&
                                     Environment.TickCount64 - ctx.StopRequestedTick > 5000)
                            {
                                ctx.State = AxisState.ErrorStop;
                                ctx.StopRequestedTick = 0;
                            }
                        }

                        snapshots = _axes.Values.Select(ctx => new AxisSnapshot(
                            ctx.Definition.AxisId,
                            ctx.Position,
                            ctx.Velocity,
                            ctx.Enabled,
                            ctx.Homed,
                            ctx.State
                        )).ToList();
                    }

                    try
                    {
                        SnapshotsUpdated?.Invoke(snapshots);
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        AxisFault?.Invoke(new AxisFaultArgs(-1, "AXIS_REFRESH_FAILED", ex.Message));
                    }
                    catch { }
                }

                if (_cts.Token.WaitHandle.WaitOne(_refreshIntervalMs)) break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _controller.AxisDone -= OnControllerAxisDone;
            _controller.AxisFault -= OnControllerAxisFault;
            _controller.LimitTriggered -= OnControllerLimitTriggered;

            _cts.Cancel();
            if (_refreshThread != null && _refreshThread.IsAlive)
            {
                _refreshThread.Join(500);
            }
            _cts.Dispose();
        }
    }
}
