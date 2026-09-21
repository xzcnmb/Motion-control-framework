#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Stream;

namespace Sophon.Infrastructure.Motion.Sim
{
    /// <summary>
    /// 仿真运动控制器实现。
    /// 实现 <see cref="IMotionController"/>、<see cref="IVerifiedDriver"/> 与 <see cref="IPositionStreamSink"/>。
    /// 内部包含高精度仿真线程（周期 1~5ms），负责各轴梯形速度积分、位置更新、到位事件（AxisDone）判定以及故障检测。
    /// </summary>
    public class SimMotionController : IMotionController, IVerifiedDriver, IPositionStreamSink
    {
        private enum AxisMotionType
        {
            None,
            Move,
            Jog,
            Homing,
            Streaming
        }

        private class SimAxisState
        {
            public AxisDefinition Definition { get; set; } = null!;
            public bool Enabled { get; set; }
            public bool Homed { get; set; }
            public double ActualPosition { get; set; }
            public double ActualVelocity { get; set; }

            // 运动命令状态
            public AxisMotionType MotionType { get; set; } = AxisMotionType.None;
            public Guid CurrentRequestId { get; set; }
            public double TargetPosition { get; set; }
            public double TargetSpeed { get; set; }
            public double Accel { get; set; }
            public double Decel { get; set; }
            public int JogDir { get; set; }

            // 回零专用状态
            public int HomingStage { get; set; } // 0: 寻零走位, 1: 碰原点, 2: 反向离开, 3: 完成
            public HomingMode HomeMode { get; set; }
            public HomeDirection HomeDir { get; set; }
            public double HomeSpeed { get; set; }
            public double HomeStartPosition { get; set; }

            // 故障状态
            public bool PositiveHardLimitFault { get; set; }
            public bool NegativeHardLimitFault { get; set; }
            public bool DriveAlarmFault { get; set; }
            public bool FollowingErrorFault { get; set; }
        }

        private readonly List<AxisDefinition> _axes;
        private readonly Dictionary<int, SimAxisState> _axisStates = new();
        private readonly object _lock = new();

        private ConnectionState _state = ConnectionState.Disconnected;
        private readonly ITimeSource _timeSource;
        private readonly Sophon.Contracts.IIoController? _ioController;

        private Thread? _simThread;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        /// <summary>
        /// 未完成请求注册表：异步完成回报 API（MoveAbsAsync/HomeAsync）在【下发命令前】先注册 TCS，
        /// 快速失败路径同步完成 TCS，从根本上消除"AxisDone 事件先于订阅"的竞态。
        /// </summary>
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AxisDoneArgs>> _pending = new();

        /// <summary>
        /// 统一完成回报：先同步完成对应 TCS（若为异步 API 请求），再异步派发 AxisDone 事件（UI 等订阅者）。
        /// 严格遵循 PLCopen 语义互斥区分 Done(到位成功)、CommandAborted(被中途叫停/抢占)、Error(故障/越界)。
        /// </summary>
        private void ReportDone(Guid requestId, bool success, string reason, CommandCompletionStatus? status = null, int axisId = 0)
        {
            var st = status ?? (success ? CommandCompletionStatus.Done : CommandCompletionStatus.Error);
            var args = new AxisDoneArgs(requestId, success, reason, st, axisId);
            if (_pending.TryRemove(requestId, out var tcs))
            {
                tcs.TrySetResult(args);
            }
            _ = Task.Run(() =>
            {
                try { AxisDone?.Invoke(args); }
                catch { /* 忽略订阅者内部异常 */ }
            });
        }

        /// <summary>
        /// 仿真线程锁外派发到位事件：同步完成 TCS + 同步派发事件（此处已无锁）。
        /// </summary>
        private void DispatchDone(AxisDoneArgs done)
        {
            if (_pending.TryRemove(done.RequestId, out var tcs))
            {
                tcs.TrySetResult(done);
            }
            try { AxisDone?.Invoke(done); }
            catch { /* 忽略订阅者内部异常 */ }
        }

        // 流式位置环形/并发缓冲
        private Guid _streamRequestId = Guid.Empty;
        private int _streamAxisCount;
        private double _streamCycleMs = 1.0;
        private readonly ConcurrentQueue<double[]> _streamQueue = new();
        private volatile bool _streamActive;

        public DriverKind Kind => DriverKind.Simulated;

        public ConnectionState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    StateChanged?.Invoke(_state);
                }
            }
        }

        public event Action<ConnectionState>? StateChanged;

        public MotionCapability Capabilities =>
            MotionCapability.HostInterpolation |
            MotionCapability.BufferedSegments |
            MotionCapability.HardLimitInput |
            MotionCapability.ContinuousVelocityBlending;

        public IReadOnlyList<AxisDefinition> Axes => _axes.AsReadOnly();

        public bool IsFieldVerified => true;

        public event Action<AxisDoneArgs>? AxisDone;
        public event Action<AxisFaultArgs>? AxisFault;
        public event Action<LimitTriggeredArgs>? LimitTriggered;
        public event Action<double[]>? PositionAccepted;

        /// <summary>
        /// 构造仿真控制器。
        /// </summary>
        /// <param name="axes">轴定义集合</param>
        /// <param name="timeSource">时间源（可注入测试用时间源，默认 StopwatchTimeSource）</param>
        /// <param name="ioController">关联的 IO 控制器（用于回零与限位联动，可选）</param>
        public SimMotionController(
            IEnumerable<AxisDefinition>? axes = null,
            ITimeSource? timeSource = null,
            Sophon.Contracts.IIoController? ioController = null)
        {
            _timeSource = timeSource ?? new StopwatchTimeSource();
            _ioController = ioController;

            if (axes != null && axes.Any())
            {
                _axes = axes.Select(CloneAxisDefinition).ToList();
            }
            else
            {
                _axes = new List<AxisDefinition>
                {
                    new()
                    {
                        AxisId = 0,
                        Name = "X",
                        Unit = "mm",
                        PulsePerUnit = 1000,
                        SoftLimitEnabled = true,
                        SoftLimitMin = 0,
                        SoftLimitMax = 1000,
                        MaxSpeed = 500,
                        MaxAccel = 2000,
                        MaxDecel = 2000,
                        HomeSpeed = 50,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Negative,
                        HomeIoName = "HomeX",
                        LimitPositiveIoName = "LimitX+",
                        LimitNegativeIoName = "LimitX-"
                    },
                    new()
                    {
                        AxisId = 1,
                        Name = "Y",
                        Unit = "mm",
                        PulsePerUnit = 1000,
                        SoftLimitEnabled = true,
                        SoftLimitMin = 0,
                        SoftLimitMax = 800,
                        MaxSpeed = 400,
                        MaxAccel = 1500,
                        MaxDecel = 1500,
                        HomeSpeed = 40,
                        HomeMode = HomingMode.OriginSignal,
                        HomeDir = HomeDirection.Negative,
                        HomeIoName = "HomeY",
                        LimitPositiveIoName = "LimitY+",
                        LimitNegativeIoName = "LimitY-"
                    }
                };
            }

            foreach (var ax in _axes)
            {
                _axisStates[ax.AxisId] = new SimAxisState
                {
                    Definition = ax,
                    Enabled = false,
                    Homed = false,
                    ActualPosition = 0,
                    ActualVelocity = 0
                };
            }

            // 启动后台仿真线程
            _simThread = new Thread(SimLoop)
            {
                Name = "SimMotionController_Loop",
                IsBackground = true
            };
            _simThread.Start();
        }

        private static AxisDefinition CloneAxisDefinition(AxisDefinition src)
        {
            return new AxisDefinition
            {
                AxisId = src.AxisId,
                Name = src.Name,
                Unit = src.Unit,
                PulsePerUnit = src.PulsePerUnit,
                DirectionInvert = src.DirectionInvert,
                SoftLimitEnabled = src.SoftLimitEnabled,
                SoftLimitMin = src.SoftLimitMin,
                SoftLimitMax = src.SoftLimitMax,
                HardLimitEnabled = src.HardLimitEnabled,
                LimitPositiveIoName = src.LimitPositiveIoName,
                LimitNegativeIoName = src.LimitNegativeIoName,
                HomeIoName = src.HomeIoName,
                MaxSpeed = src.MaxSpeed,
                MaxAccel = src.MaxAccel,
                MaxDecel = src.MaxDecel,
                MaxJerk = src.MaxJerk,
                HomeMode = src.HomeMode,
                HomeDir = src.HomeDir,
                HomeSpeed = src.HomeSpeed
            };
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            if (State == ConnectionState.Ready)
                return;

            State = ConnectionState.Connecting;
            await Task.Delay(20, ct); // 模拟握手耗时
            State = ConnectionState.Ready;
        }

        public Task DisconnectAsync()
        {
            List<Guid> active = new();
            lock (_lock)
            {
                // 停止所有轴并收集未完成请求
                foreach (var state in _axisStates.Values)
                {
                    if (state.MotionType != AxisMotionType.None && state.CurrentRequestId != Guid.Empty)
                    {
                        active.Add(state.CurrentRequestId);
                    }
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                }
            }

            foreach (var req in active)
            {
                ReportDone(req, false, "连接断开");
            }

            State = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public void EnableAxis(int axisId)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    state.Enabled = true;
                }
            }
        }

        public void DisableAxis(int axisId)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    state.Enabled = false;
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                }
            }
        }

        public bool IsAxisEnabled(int axisId)
        {
            lock (_lock)
            {
                return _axisStates.TryGetValue(axisId, out var state) && state.Enabled;
            }
        }

        public bool IsAxisHomed(int axisId)
        {
            lock (_lock)
            {
                return _axisStates.TryGetValue(axisId, out var state) && state.Homed;
            }
        }

        public double GetPosition(int axisId)
        {
            lock (_lock)
            {
                return _axisStates.TryGetValue(axisId, out var state) ? state.ActualPosition : 0.0;
            }
        }

        public double GetVelocity(int axisId)
        {
            lock (_lock)
            {
                return _axisStates.TryGetValue(axisId, out var state) ? state.ActualVelocity : 0.0;
            }
        }

        public Guid Jog(int axisId, int dir, double speed)
        {
            var requestId = Guid.NewGuid();

            // 未连接时命令返回失败事件
            if (State != ConnectionState.Ready)
            {
                ReportDone(requestId, false, "控制器未处于 Ready 状态");
                return requestId;
            }

            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                {
                    ReportDone(requestId, false, $"轴 {axisId} 不存在");
                    return requestId;
                }

                if (!state.Enabled)
                {
                    ReportDone(requestId, false, $"轴 {axisId} 未使能");
                    return requestId;
                }

                var def = state.Definition;
                double clampedSpeed = Math.Min(Math.Abs(speed), def.MaxSpeed);
                int normalizedDir = dir >= 0 ? 1 : -1;

                state.MotionType = AxisMotionType.Jog;
                state.CurrentRequestId = requestId;
                state.JogDir = normalizedDir;
                state.TargetSpeed = clampedSpeed;
                state.Accel = def.MaxAccel > 0 ? def.MaxAccel : 1000;
                state.Decel = def.MaxDecel > 0 ? def.MaxDecel : 1000;
            }

            return requestId;
        }

        public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0)
        {
            var requestId = Guid.NewGuid();
            MoveAbsCore(requestId, axisId, target, speed, accel, decel, jerk);
            return requestId;
        }

        public Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = Guid.NewGuid();
            _pending[requestId] = tcs;
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() =>
                {
                    if (_pending.TryRemove(requestId, out var pending))
                    {
                        pending.TrySetCanceled(ct);
                    }
                    Abort(axisId);
                });
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }
            MoveAbsCore(requestId, axisId, target, speed, accel, decel, jerk);
            return tcs.Task;
        }

        private void MoveAbsCore(Guid requestId, int axisId, double target, double speed, double accel, double decel, double jerk)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(requestId, false, "控制器未处于 Ready 状态");
                return;
            }

            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                {
                    ReportDone(requestId, false, $"轴 {axisId} 不存在");
                    return;
                }

                if (!state.Enabled)
                {
                    ReportDone(requestId, false, $"轴 {axisId} 未使能");
                    return;
                }

                var def = state.Definition;

                // 强制软限位校验：若越界则不发运动，立即回报失败 + LimitTriggered(软限位事件)
                if (def.SoftLimitEnabled)
                {
                    if (target > def.SoftLimitMax)
                    {
                        _ = Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, "SoftLimitMax", true, false)));
                        ReportDone(requestId, false, "目标超出正向软限位");
                        return;
                    }
                    if (target < def.SoftLimitMin)
                    {
                        _ = Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, "SoftLimitMin", false, false)));
                        ReportDone(requestId, false, "目标超出负向软限位");
                        return;
                    }
                }

                // 速度钳制
                double clampedSpeed = Math.Min(Math.Abs(speed), def.MaxSpeed);
                double safeAccel = accel > 0 ? Math.Min(accel, def.MaxAccel) : def.MaxAccel;
                double safeDecel = decel > 0 ? Math.Min(decel, def.MaxDecel) : def.MaxDecel;

                state.MotionType = AxisMotionType.Move;
                state.CurrentRequestId = requestId;
                state.TargetPosition = target;
                state.TargetSpeed = clampedSpeed;
                state.Accel = safeAccel > 0 ? safeAccel : 1000;
                state.Decel = safeDecel > 0 ? safeDecel : 1000;
            }
        }

        public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    double target = state.ActualPosition + delta;
                    return MoveAbs(axisId, target, speed, accel, decel, jerk);
                }
            }

            var req = Guid.NewGuid();
            ReportDone(req, false, $"轴 {axisId} 不存在");
            return req;
        }

        public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            var requestId = Guid.NewGuid();
            HomeCore(requestId, axisId, mode, dir, speed);
            return requestId;
        }

        public Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestId = Guid.NewGuid();
            _pending[requestId] = tcs;
            if (ct.CanBeCanceled)
            {
                var reg = ct.Register(() =>
                {
                    if (_pending.TryRemove(requestId, out var pending))
                    {
                        pending.TrySetCanceled(ct);
                    }
                    Abort(axisId);
                });
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }
            HomeCore(requestId, axisId, mode, dir, speed);
            return tcs.Task;
        }

        private void HomeCore(Guid requestId, int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(requestId, false, "控制器未处于 Ready 状态");
                return;
            }

            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                {
                    ReportDone(requestId, false, $"轴 {axisId} 不存在");
                    return;
                }

                if (!state.Enabled)
                {
                    ReportDone(requestId, false, $"轴 {axisId} 未使能");
                    return;
                }

                var def = state.Definition;
                double safeSpeed = speed > 0 ? Math.Min(speed, def.HomeSpeed) : def.HomeSpeed;
                if (safeSpeed <= 0) safeSpeed = 10;

                state.MotionType = AxisMotionType.Homing;
                state.CurrentRequestId = requestId;
                state.HomeMode = mode;
                state.HomeDir = dir;
                state.HomeSpeed = safeSpeed;
                state.HomingStage = 0;
                state.HomeStartPosition = state.ActualPosition;
                state.Accel = def.MaxAccel > 0 ? def.MaxAccel : 1000;
                state.Decel = def.MaxDecel > 0 ? def.MaxDecel : 1000;
            }
        }

        // ---------- 停止与安全分层实现 (EN 60204-1 Stop Category 2/1/0 & PLCopen) ----------

        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)。
        /// </summary>
        public void Halt(int axisId)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    if (state.MotionType != AxisMotionType.None)
                    {
                        var reqId = state.CurrentRequestId;
                        state.ActualVelocity = 0;
                        state.MotionType = AxisMotionType.None;
                        ReportDone(reqId, false, "工艺暂停中止 (MC_Halt)", CommandCompletionStatus.CommandAborted, axisId);
                    }
                }
            }
        }

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)。
        /// </summary>
        public void Stop(int axisId) => StopMotion(axisId);

        public void StopMotion(int axisId)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    if (state.MotionType != AxisMotionType.None)
                    {
                        var reqId = state.CurrentRequestId;
                        state.ActualVelocity = 0;
                        state.MotionType = AxisMotionType.None;
                        ReportDone(reqId, false, "受控减速停止 (MC_Stop)", CommandCompletionStatus.CommandAborted, axisId);
                    }
                }
            }
        }

        /// <summary>
        /// Level 3: 硬件安全急停 (Emergency Stop / Hard Abort / STO, Stop Cat 0/1)。
        /// </summary>
        public void EmergencyStop(int axisId) => Abort(axisId);

        public void Abort(int axisId, double decelRatio = 0)
        {
            Guid reqId = Guid.Empty;
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    reqId = state.CurrentRequestId;
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                }
            }

            if (reqId != Guid.Empty)
            {
                ReportDone(reqId, false, "硬件安全急停 (EmergencyStop/STO)", CommandCompletionStatus.Error, axisId);
            }
        }

        public void EmergencyStopAll() => AbortAll();

        public void AbortAll()
        {
            List<(Guid req, int ax)> abortReqs = new();
            lock (_lock)
            {
                foreach (var state in _axisStates.Values)
                {
                    if (state.MotionType != AxisMotionType.None && state.CurrentRequestId != Guid.Empty)
                    {
                        abortReqs.Add((state.CurrentRequestId, state.Definition.AxisId));
                    }
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                }
            }

            foreach (var item in abortReqs)
            {
                ReportDone(item.req, false, "全局急停中止 (Global EmergencyStop)", CommandCompletionStatus.Error, item.ax);
            }
        }

        /// <summary>
        /// 故障复位指令 (MC_Reset)。
        /// </summary>
        public void ResetAxis(int axisId)
        {
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                }
            }
        }

        /// <summary>
        /// 故障注入（测试与演示关键能力）。
        /// </summary>
        /// <param name="kind">故障类型</param>
        /// <param name="axisId">目标轴</param>
        /// <param name="active">是否激活</param>
        public void InjectFault(FaultKind kind, int axisId, bool active)
        {
            lock (_lock)
            {
                if (kind == FaultKind.Disconnected)
                {
                    if (active)
                    {
                        State = ConnectionState.Fault;
                        // 停止所有轴
                        foreach (var st in _axisStates.Values)
                        {
                            st.ActualVelocity = 0;
                            st.MotionType = AxisMotionType.None;
                        }
                    }
                    else
                    {
                        State = ConnectionState.Ready;
                    }
                    return;
                }

                if (!_axisStates.TryGetValue(axisId, out var state))
                    return;

                switch (kind)
                {
                    case FaultKind.PositiveHardLimit:
                        state.PositiveHardLimitFault = active;
                        if (active)
                        {
                            state.ActualVelocity = 0;
                            state.MotionType = AxisMotionType.None;
                            string ptName = state.Definition.LimitPositiveIoName ?? $"Limit{axisId}+";
                            Task.Run(() =>
                            {
                                LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, ptName, true, true));
                                AxisFault?.Invoke(new AxisFaultArgs(axisId, "HARD_LIMIT_POS", $"轴 {axisId} 正向硬限位触发"));
                            });
                        }
                        break;

                    case FaultKind.NegativeHardLimit:
                        state.NegativeHardLimitFault = active;
                        if (active)
                        {
                            state.ActualVelocity = 0;
                            state.MotionType = AxisMotionType.None;
                            string ptName = state.Definition.LimitNegativeIoName ?? $"Limit{axisId}-";
                            Task.Run(() =>
                            {
                                LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, ptName, false, true));
                                AxisFault?.Invoke(new AxisFaultArgs(axisId, "HARD_LIMIT_NEG", $"轴 {axisId} 负向硬限位触发"));
                            });
                        }
                        break;

                    case FaultKind.DriveAlarm:
                        state.DriveAlarmFault = active;
                        if (active)
                        {
                            state.ActualVelocity = 0;
                            state.MotionType = AxisMotionType.None;
                            Task.Run(() => AxisFault?.Invoke(new AxisFaultArgs(axisId, "DRIVE_ALARM", $"轴 {axisId} 驱动器报警")));
                        }
                        break;

                    case FaultKind.FollowingError:
                        state.FollowingErrorFault = active;
                        if (active)
                        {
                            Task.Run(() => AxisFault?.Invoke(new AxisFaultArgs(axisId, "FOLLOWING_ERROR", $"轴 {axisId} 跟随误差超差")));
                        }
                        break;
                }
            }
        }

        #region IPositionStreamSink 实现

        public void Begin(Guid requestId, int axisCount, double cycleMs)
        {
            _streamRequestId = requestId;
            _streamAxisCount = axisCount;
            _streamCycleMs = cycleMs > 0 ? cycleMs : 1.0;
            while (_streamQueue.TryDequeue(out _)) { }
            _streamActive = true;
        }

        public void Submit(double[] positions)
        {
            if (!_streamActive || positions == null)
                return;

            _streamQueue.Enqueue((double[])positions.Clone());
        }

        public void Complete()
        {
            _streamActive = false;
        }

        #endregion

        private void SimLoop()
        {
            var sw = Stopwatch.StartNew();
            long lastTicks = sw.ElapsedTicks;
            double tickFrequency = Stopwatch.Frequency;

            while (!_cts.IsCancellationRequested)
            {
                long currentTicks = sw.ElapsedTicks;
                double dt = (currentTicks - lastTicks) / tickFrequency;
                if (dt <= 0.0001)
                {
                    Thread.Sleep(1);
                    continue;
                }
                lastTicks = currentTicks;

                // 限制单步最大积分步长（防止调试中断恢复后位置突变）
                if (dt > 0.05) dt = 0.05;

                List<AxisDoneArgs> doneList = new();

                lock (_lock)
                {
                    // 1. 处理流式插补缓冲（如果处于 Ready 且队列有点）
                    if (State == ConnectionState.Ready && _streamQueue.TryDequeue(out var streamPos))
                    {
                        for (int i = 0; i < streamPos.Length && i < _axes.Count; i++)
                        {
                            int axId = _axes[i].AxisId;
                            if (_axisStates.TryGetValue(axId, out var axState) && axState.Enabled)
                            {
                                axState.ActualPosition = streamPos[i];
                            }
                        }
                        PositionAccepted?.Invoke(streamPos);
                    }

                    // 2. 处理各轴常规运动状态机积分
                    foreach (var kv in _axisStates)
                    {
                        var state = kv.Value;
                        if (!state.Enabled || state.MotionType == AxisMotionType.None)
                        {
                            state.ActualVelocity = 0;
                            continue;
                        }

                        // 限位硬件触发阻止运动
                        if ((state.ActualVelocity > 0 && state.PositiveHardLimitFault) ||
                            (state.ActualVelocity < 0 && state.NegativeHardLimitFault))
                        {
                            state.ActualVelocity = 0;
                            state.MotionType = AxisMotionType.None;
                            continue;
                        }

                        switch (state.MotionType)
                        {
                            case AxisMotionType.Move:
                                StepMove(state, dt, doneList);
                                break;

                            case AxisMotionType.Jog:
                                StepJog(state, dt, doneList);
                                break;

                            case AxisMotionType.Homing:
                                StepHoming(state, dt, doneList);
                                break;
                        }
                    }
                }

                // 在锁外分发到位事件
                foreach (var done in doneList)
                {
                    DispatchDone(done);
                }

                Thread.Sleep(2);
            }
        }

        private void StepMove(SimAxisState state, double dt, List<AxisDoneArgs> doneList)
        {
            double dist = state.TargetPosition - state.ActualPosition;
            double absDist = Math.Abs(dist);

            // 到位判断死区：小于 0.001 且速度较低
            if (absDist < 0.001 && Math.Abs(state.ActualVelocity) < 0.01)
            {
                state.ActualPosition = state.TargetPosition;
                state.ActualVelocity = 0;
                state.MotionType = AxisMotionType.None;
                doneList.Add(new AxisDoneArgs(state.CurrentRequestId, true, "到位"));
                return;
            }

            int dir = dist >= 0 ? 1 : -1;
            double decelDist = (state.ActualVelocity * state.ActualVelocity) / (2.0 * Math.Max(state.Decel, 1.0));

            double targetV;
            if (absDist <= decelDist)
            {
                // 减速段
                targetV = 0;
            }
            else
            {
                // 加速/匀速段
                targetV = dir * state.TargetSpeed;
            }

            // 更新速度
            if (state.ActualVelocity < targetV)
            {
                state.ActualVelocity = Math.Min(state.ActualVelocity + state.Accel * dt, targetV);
            }
            else if (state.ActualVelocity > targetV)
            {
                state.ActualVelocity = Math.Max(state.ActualVelocity - state.Decel * dt, targetV);
            }

            // 积分位置
            double step = state.ActualVelocity * dt;
            if (Math.Abs(step) > absDist)
            {
                state.ActualPosition = state.TargetPosition;
                state.ActualVelocity = 0;
                state.MotionType = AxisMotionType.None;
                doneList.Add(new AxisDoneArgs(state.CurrentRequestId, true, "到位"));
            }
            else
            {
                state.ActualPosition += step;
            }
        }

        private void StepJog(SimAxisState state, double dt, List<AxisDoneArgs> doneList)
        {
            double targetV = state.JogDir * state.TargetSpeed;
            if (state.ActualVelocity < targetV)
            {
                state.ActualVelocity = Math.Min(state.ActualVelocity + state.Accel * dt, targetV);
            }
            else if (state.ActualVelocity > targetV)
            {
                state.ActualVelocity = Math.Max(state.ActualVelocity - state.Decel * dt, targetV);
            }

            state.ActualPosition += state.ActualVelocity * dt;

            // 软限位越界保护
            var def = state.Definition;
            if (def.SoftLimitEnabled)
            {
                if (state.ActualPosition >= def.SoftLimitMax && state.ActualVelocity > 0)
                {
                    state.ActualPosition = def.SoftLimitMax;
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                    Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(def.AxisId, "SoftLimitMax", true, false)));
                    doneList.Add(new AxisDoneArgs(state.CurrentRequestId, false, "点动触碰正向软限位"));
                }
                else if (state.ActualPosition <= def.SoftLimitMin && state.ActualVelocity < 0)
                {
                    state.ActualPosition = def.SoftLimitMin;
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                    Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(def.AxisId, "SoftLimitMin", false, false)));
                    doneList.Add(new AxisDoneArgs(state.CurrentRequestId, false, "点动触碰负向软限位"));
                }
            }
        }

        private void StepHoming(SimAxisState state, double dt, List<AxisDoneArgs> doneList)
        {
            // 回零模式支持：CurrentPosition 或 仿真寻零（碰原点IO/限位再归零）
            if (state.HomeMode == HomingMode.CurrentPosition)
            {
                state.ActualPosition = 0;
                state.ActualVelocity = 0;
                state.Homed = true;
                state.MotionType = AxisMotionType.None;
                doneList.Add(new AxisDoneArgs(state.CurrentRequestId, true, "当前位置回零成功"));
                return;
            }

            int dir = state.HomeDir == HomeDirection.Positive ? 1 : -1;
            double homeV = dir * state.HomeSpeed;

            switch (state.HomingStage)
            {
                case 0:
                    // 阶段 0：朝回零方向运动，寻找原点信号或走固定距离
                    state.ActualVelocity = homeV;
                    state.ActualPosition += state.ActualVelocity * dt;

                    bool trigger = false;
                    if (_ioController != null && !string.IsNullOrWhiteSpace(state.Definition.HomeIoName))
                    {
                        trigger = _ioController.ReadDi(state.Definition.HomeIoName);
                    }

                    // 仿真环境：若没有外部 IO 触发，移动一定距离（例如 50mm）后模拟碰触原点
                    if (trigger || Math.Abs(state.ActualPosition - state.HomeStartPosition) >= 50.0)
                    {
                        state.HomingStage = 1;
                    }
                    break;

                case 1:
                    // 阶段 1：模拟碰原点后反向微调脱离
                    state.ActualVelocity = -homeV * 0.5;
                    state.ActualPosition += state.ActualVelocity * dt;

                    // 反向脱离移动 5mm
                    if (Math.Abs(state.ActualPosition) >= 5.0)
                    {
                        state.ActualPosition = 0;
                        state.ActualVelocity = 0;
                        state.Homed = true;
                        state.MotionType = AxisMotionType.None;
                        doneList.Add(new AxisDoneArgs(state.CurrentRequestId, true, "回零完成"));
                    }
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            if (_simThread != null && _simThread.IsAlive)
            {
                _simThread.Join(500);
            }
            _cts.Dispose();
        }
    }
}
