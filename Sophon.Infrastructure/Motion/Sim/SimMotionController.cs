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
            Streaming,
            /// <summary>Cat1 受控停车中（有速度衰减过程后清除）</summary>
            Stopping,
            /// <summary>急停/Abort 故障锁定位：禁止一切运动，直至 ResetAxis</summary>
            ErrorStop
        }

        /// <summary>
        /// 回零阶段状态机（有阶段/事件驱动，禁止只靠固定距离成功；每个阶段推进都有超时/行程兜底，不允许永久挂起）。
        /// </summary>
        private enum HomeStage
        {
            /// <summary>朝回零方向搜索原点信号</summary>
            Search,
            /// <summary>已检测到原点信号</summary>
            Detected,
            /// <summary>反向退离原点</summary>
            Escaping,
            /// <summary>退离后停稳确认</summary>
            Settling,
            /// <summary>当前位置置零</summary>
            Zeroing
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

            // 回零专用状态（有阶段/事件驱动状态机，禁止只靠固定距离成功、必须有超时）
            public HomeStage HomingStage { get; set; } = HomeStage.Search;
            public HomingMode HomeMode { get; set; }
            public HomeDirection HomeDir { get; set; }
            public double HomeSpeed { get; set; }
            public double HomeStartPosition { get; set; }
            public double HomeDetectedPosition { get; set; } // 触发原点瞬间的位置（反向退离参考点）
            public long HomeStartElapsedMs { get; set; }     // 回零起始时刻（来自 ITimeSource，用于超时判定）
            public double HomeSearchExtent { get; set; }     // 搜索行程上限（行进幅度参考，超行程即判未回零）
            public double HomeEscapeExtent { get; set; }     // 退离行程

            // 故障状态
            public bool PositiveHardLimitFault { get; set; }
            public bool NegativeHardLimitFault { get; set; }
            public bool DriveAlarmFault { get; set; }
            public bool FollowingErrorFault { get; set; }
            public bool ErrorStop { get; set; }
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
        /// <para>必须在<b>锁外</b>调用，避免持锁派发事件/唤醒等待方。</para>
        /// </summary>
        private void ReportDone(AxisDoneArgs args)
        {
            if (_pending.TryRemove(args.RequestId, out var tcs))
            {
                tcs.TrySetResult(args);
            }
            _ = Task.Run(() =>
            {
                try { AxisDone?.Invoke(args); }
                catch { /* 忽略订阅者内部异常 */ }
            });
        }

        private void ReportDone(Guid requestId, bool success, string reason, CommandCompletionStatus? status = null, int axisId = 0)
        {
            var st = status ?? (success ? CommandCompletionStatus.Done : CommandCompletionStatus.Error);
            ReportDone(new AxisDoneArgs(requestId, success, reason, st, axisId));
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

        /// <summary>轴是否处于故障/急停锁定（ErrorStop 或任一故障位）。调用方需持锁。</summary>
        private static bool IsAxisFaulted(SimAxisState s)
            => s.ErrorStop || s.PositiveHardLimitFault || s.NegativeHardLimitFault || s.DriveAlarmFault || s.FollowingErrorFault;

        /// <summary>清除单轴的运动/速度/流式关联状态（不动 Enabled/Homed/故障位）。调用方需持锁。</summary>
        private static void ClearAxisMotion_Locked(SimAxisState state)
        {
            state.ActualVelocity = 0;
            state.MotionType = AxisMotionType.None;
            state.CurrentRequestId = Guid.Empty;
            state.TargetSpeed = 0;
            state.JogDir = 0;
        }

        /// <summary>清除流式插补状态。调用方需持锁。</summary>
        private void ClearStreaming_Locked()
        {
            _streamActive = false;
            _streamRequestId = Guid.Empty;
            while (_streamQueue.TryDequeue(out _)) { }
        }

        /// <summary>回零搜索行程上限：优先用两软限位跨度；否则用一个保守常量。仅用于"走到头仍碰不到原点"的超时熔断，不影响真实到位判定。</summary>
        private static double GetHomeSearchExtent(AxisDefinition def)
        {
            double raw = (def.SoftLimitMax - def.SoftLimitMin) + 100.0;
            if (double.IsNaN(raw) || double.IsInfinity(raw) || raw <= 0) raw = 1000.0;
            return raw;
        }

        /// <summary>
        /// 异步运动/回零命令被外部取消：以 <see cref="CommandCompletionStatus.CommandAborted"/> 完结本请求（PLCopen 软取消），
        /// 若该轴正在执行本请求则平滑停下（Halt），不留悬挂 TCS，不升级为 ErrorStop。
        /// </summary>
        private void OnMotionCommandCanceled(Guid requestId, int axisId)
        {
            bool runningThis;
            lock (_lock)
            {
                runningThis = _axisStates.TryGetValue(axisId, out var s)
                    && s.MotionType != AxisMotionType.None
                    && s.CurrentRequestId == requestId;
            }

            if (runningThis)
            {
                // Halt 会以 CommandAborted 完结该请求（并移出 _pending）
                Halt(axisId);
            }

            // 兜底：请求尚未启动或已提前完结时，确保 TCS 被完成，避免悬挂
            CompletePendingAsAborted(requestId, axisId, "命令被取消 (CommandAborted)");
        }

        /// <summary>把仍处于 _pending 的请求以 CommandAborted 完结并派发 AxisDone（无锁）。</summary>
        private void CompletePendingAsAborted(Guid requestId, int axisId, string reason)
        {
            if (_pending.TryRemove(requestId, out var tcs))
            {
                var args = new AxisDoneArgs(requestId, false, reason, CommandCompletionStatus.CommandAborted, axisId);
                tcs.TrySetResult(args);
                _ = Task.Run(() => { try { AxisDone?.Invoke(args); } catch { } });
            }
        }

        // 流式位置环形/并发缓冲
        private Guid _streamRequestId = Guid.Empty;
        private int _streamAxisCount;
        private double _streamCycleMs = 1.0;
        private readonly ConcurrentQueue<double[]> _streamQueue = new();
        private volatile bool _streamActive;
        private long _lastElapsedMs;

        // 回零状态机专用常量
        private const double HomeTotalTimeoutMs = 5000;   // 任何回零阶段的总超时（ms）：禁止永久挂起
        private const double HomeEscapeSpeedRatio = 0.5;  // 检测到原点后反向退离速度占回零速度比例
        private const double HomeSettleFactor = 0.5;      // 停稳阶段速度衰减系数
        private const double HomeSettleVThreshold = 0.01; // 停稳确认的速度阈值
        private const double HomeDefaultEscapeMm = 1.0;  // 检测到原点后反向退离距离（Contracts 无此配置项，此处给唯一默认）

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
                    state.CurrentRequestId = Guid.Empty;
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
            Guid requestId = Guid.Empty;
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    requestId = state.CurrentRequestId;
                    state.Enabled = false;
                    state.ActualVelocity = 0;
                    state.MotionType = AxisMotionType.None;
                    state.CurrentRequestId = Guid.Empty;
                }
            }

            if (requestId != Guid.Empty)
            {
                ReportDone(requestId, false, "轴下使能，中止当前命令", CommandCompletionStatus.CommandAborted, axisId);
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

            var fail = TryBeginJog(requestId, axisId, dir, speed);
            if (fail != null)
            {
                ReportDone(fail);
            }
            return requestId;
        }

        /// <summary>在锁内尝试登记点动命令；返回 null 表示成功启动，否则返回快速失败回报（调用方在锁外派发）。</summary>
        private AxisDoneArgs? TryBeginJog(Guid requestId, int axisId, int dir, double speed)
        {
            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 不存在", CommandCompletionStatus.Error, axisId);

                if (!state.Enabled)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 未使能", CommandCompletionStatus.Error, axisId);

                if (IsAxisFaulted(state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 处于故障锁定状态，请先复位", CommandCompletionStatus.Error, axisId);

                if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 点动速度无效", CommandCompletionStatus.Error, axisId);

                if (state.MotionType != AxisMotionType.None)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 已有运动命令在执行", CommandCompletionStatus.CommandAborted, axisId);

                var def = state.Definition;
                double clampedSpeed = Math.Min(Math.Abs(speed), def.MaxSpeed);
                int normalizedDir = dir >= 0 ? 1 : -1;

                state.MotionType = AxisMotionType.Jog;
                state.CurrentRequestId = requestId;
                state.JogDir = normalizedDir;
                state.TargetSpeed = clampedSpeed;
                state.Accel = def.MaxAccel > 0 ? def.MaxAccel : 1000;
                state.Decel = def.MaxDecel > 0 ? def.MaxDecel : 1000;
                return null;
            }
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
                var reg = ct.Register(() => OnMotionCommandCanceled(requestId, axisId));
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }

            if (ct.IsCancellationRequested)
            {
                // 令牌在启动前已取消：不启动运动，直接以 CommandAborted 完结，避免悬挂
                CompletePendingAsAborted(requestId, axisId, "运动命令在启动前已被取消");
            }
            else
            {
                MoveAbsCore(requestId, axisId, target, speed, accel, decel, jerk);
            }
            return tcs.Task;
        }

        private void MoveAbsCore(Guid requestId, int axisId, double target, double speed, double accel, double decel, double jerk)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(requestId, false, "控制器未处于 Ready 状态");
                return;
            }

            var result = TryBeginMove(requestId, axisId, target, speed, accel, decel, out var limitEvt);
            if (limitEvt != null)
            {
                // 软限位事件在锁外派发
                _ = Task.Run(() => LimitTriggered?.Invoke(limitEvt));
            }
            if (result != null)
            {
                ReportDone(result);
            }
        }

        /// <summary>在锁内尝试登记绝对定位命令；返回 null 表示成功启动，否则返回快速失败回报（调用方在锁外派发）。</summary>
        private AxisDoneArgs? TryBeginMove(Guid requestId, int axisId, double target, double speed, double accel, double decel, out LimitTriggeredArgs? limitEvt)
        {
            limitEvt = null;
            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 不存在", CommandCompletionStatus.Error, axisId);

                if (!state.Enabled)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 未使能", CommandCompletionStatus.Error, axisId);

                if (IsAxisFaulted(state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 处于故障锁定状态，请先复位", CommandCompletionStatus.Error, axisId);

                if (state.MotionType != AxisMotionType.None)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 已有运动命令在执行", CommandCompletionStatus.CommandAborted, axisId);

                if (double.IsNaN(target) || double.IsInfinity(target) || double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 的目标或速度无效", CommandCompletionStatus.Error, axisId);

                var def = state.Definition;

                // 强制软限位校验：若越界则不发运动，立即回报失败 + LimitTriggered(软限位事件)
                if (def.SoftLimitEnabled)
                {
                    if (target > def.SoftLimitMax)
                    {
                        limitEvt = new LimitTriggeredArgs(axisId, "SoftLimitMax", true, false);
                        return new AxisDoneArgs(requestId, false, "目标超出正向软限位", CommandCompletionStatus.Error, axisId);
                    }
                    if (target < def.SoftLimitMin)
                    {
                        limitEvt = new LimitTriggeredArgs(axisId, "SoftLimitMin", false, false);
                        return new AxisDoneArgs(requestId, false, "目标超出负向软限位", CommandCompletionStatus.Error, axisId);
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
                return null;
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
                var reg = ct.Register(() => OnMotionCommandCanceled(requestId, axisId));
                tcs.Task.ContinueWith(_ => reg.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
            }

            if (ct.IsCancellationRequested)
            {
                CompletePendingAsAborted(requestId, axisId, "回零命令在启动前已被取消");
            }
            else
            {
                HomeCore(requestId, axisId, mode, dir, speed);
            }
            return tcs.Task;
        }

        /// <summary>
        /// 回零状态机（Search → Detected → Escaping → Settling → Zeroing → 完成）。所有推进由仿真循环按事件/阶段驱动，
        /// 每个阶段有超时与行程兜底，禁止只靠固定距离成功、也禁止永久挂起。初始校验失败在锁外回报。
        /// </summary>
        private void HomeCore(Guid requestId, int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(requestId, false, "控制器未处于 Ready 状态");
                return;
            }

            var fail = TryBeginHome(requestId, axisId, mode, dir, speed);
            if (fail != null)
            {
                ReportDone(fail);
            }
        }

        private AxisDoneArgs? TryBeginHome(Guid requestId, int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            lock (_lock)
            {
                if (!_axisStates.TryGetValue(axisId, out var state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 不存在", CommandCompletionStatus.Error, axisId);

                if (!state.Enabled)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 未使能", CommandCompletionStatus.Error, axisId);

                if (IsAxisFaulted(state))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 处于故障锁定状态，请先复位", CommandCompletionStatus.Error, axisId);

                if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 回零速度无效", CommandCompletionStatus.Error, axisId);

                if (state.MotionType != AxisMotionType.None)
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 已有运动命令在执行", CommandCompletionStatus.CommandAborted, axisId);

                // 需要搜索原点信号的模式必须有回零输入点；CurrentPosition 模式不使用外部传感器，故不强制
                bool needsOriginSignal = state.Definition.HomeMode != HomingMode.CurrentPosition && mode != HomingMode.CurrentPosition;
                if (needsOriginSignal && string.IsNullOrWhiteSpace(state.Definition.HomeIoName))
                    return new AxisDoneArgs(requestId, false, $"轴 {axisId} 未配置回零输入点 (HomeIoName)", CommandCompletionStatus.Error, axisId);

                var def = state.Definition;
                double safeSpeed = speed > 0 ? Math.Min(speed, def.HomeSpeed > 0 ? def.HomeSpeed : speed) : (def.HomeSpeed > 0 ? def.HomeSpeed : 10);
                if (safeSpeed <= 0) safeSpeed = 10;

                state.MotionType = AxisMotionType.Homing;
                state.CurrentRequestId = requestId;
                state.HomeMode = mode;
                state.HomeDir = dir;
                state.HomeSpeed = safeSpeed;
                state.HomingStage = HomeStage.Search;
                state.HomeStartPosition = state.ActualPosition;
                state.HomeDetectedPosition = state.ActualPosition;
                state.HomeStartElapsedMs = _lastElapsedMs;
                state.HomeSearchExtent = GetHomeSearchExtent(def);
                state.HomeEscapeExtent = HomeDefaultEscapeMm;
                state.Accel = def.MaxAccel > 0 ? def.MaxAccel : 1000;
                state.Decel = def.MaxDecel > 0 ? def.MaxDecel : 1000;

                int dirSign = (dir == HomeDirection.Positive) ? 1 : -1;
                // 搜索目标位置仅用于可观测性；真实推进与熔断只看 HomeSearchExtent 行程（回零允许越过软限位搜索）
                state.JogDir = dirSign;
                state.TargetPosition = state.HomeStartPosition + dirSign * state.HomeSearchExtent;
                state.TargetSpeed = state.HomeSpeed;
                return null;
            }
        }

        // ---------- 停止与安全分层实现 (EN 60204-1 Stop Category 2/1/0 & PLCopen) ----------

        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)。
        /// 立即停稳并把运动状态清空为 <see cref="AxisMotionType.None"/>(Standstill，可立即重新运动)；
        /// 以 <see cref="CommandCompletionStatus.CommandAborted"/> 完结当前请求（软取消，不锁定、不进入 ErrorStop）。
        /// </summary>
        public void Halt(int axisId)
        {
            AxisDoneArgs? done = null;
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state) && state.MotionType != AxisMotionType.None)
                {
                    var reqId = state.CurrentRequestId;
                    done = new AxisDoneArgs(reqId, false, "工艺暂停中止 (MC_Halt)", CommandCompletionStatus.CommandAborted, axisId);
                    state.ActualVelocity = 0;
                    ClearAxisMotion_Locked(state);
                }
            }

            if (done != null) ReportDone(done);
        }

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)。
        /// 可观察差异：保留 <see cref="AxisMotionType.Stopping"/>(Cat1 停车中) 由仿真循环减速收敛后清除，
        /// 并把当前位置设为减速终点；以 <see cref="CommandCompletionStatus.CommandAborted"/> 完结请求（不进入 ErrorStop）。
        /// </summary>
        public void Stop(int axisId) => StopMotion(axisId);

        public void StopMotion(int axisId)
        {
            AxisDoneArgs? done = null;
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state) && state.MotionType != AxisMotionType.None)
                {
                    var reqId = state.CurrentRequestId;
                    done = new AxisDoneArgs(reqId, false, "受控减速停止 (MC_Stop)", CommandCompletionStatus.CommandAborted, axisId);

                    if (state.MotionType == AxisMotionType.Jog)
                    {
                        // Jog 无绝对目标，直接停稳
                        state.ActualVelocity = 0;
                        ClearAxisMotion_Locked(state);
                    }
                    else
                    {
                        // Cat1 受控停车：保留运动类型形成可观察差异，以当前位置为减速终点
                        state.MotionType = AxisMotionType.Stopping;
                        state.TargetPosition = state.ActualPosition;
                        state.TargetSpeed = 0;
                        state.JogDir = 0;
                    }
                }
            }

            if (done != null) ReportDone(done);
        }

        /// <summary>
        /// Level 3: 硬件安全急停 (Emergency Stop / Hard Abort / STO, Stop Cat 0/1)。
        /// 立即停止并进入 <see cref="AxisMotionType.ErrorStop"/> 锁定，直至 <see cref="ResetAxis"/>；
        /// 在途请求以 <see cref="CommandCompletionStatus.Error"/> 完结（不留悬挂 TCS），速度/流式状态一并清除。
        /// </summary>
        public void EmergencyStop(int axisId) => Abort(axisId);

        public void Abort(int axisId, double decelRatio = 0)
        {
            var doneList = new List<AxisDoneArgs>();
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    if (state.MotionType != AxisMotionType.None && state.CurrentRequestId != Guid.Empty)
                    {
                        doneList.Add(new AxisDoneArgs(state.CurrentRequestId, false,
                            "硬件安全急停 (EmergencyStop/STO)", CommandCompletionStatus.Error, axisId));
                    }
                    state.ErrorStop = true;
                    state.ActualVelocity = 0;
                    ClearAxisMotion_Locked(state);
                    state.MotionType = AxisMotionType.ErrorStop;
                }
            }

            foreach (var d in doneList) ReportDone(d);
        }

        public void EmergencyStopAll() => AbortAll();

        public void AbortAll()
        {
            var doneList = new List<AxisDoneArgs>();
            lock (_lock)
            {
                ClearStreaming_Locked(); // 流式插补一并急停
                foreach (var state in _axisStates.Values)
                {
                    if (state.MotionType != AxisMotionType.None && state.CurrentRequestId != Guid.Empty)
                    {
                        doneList.Add(new AxisDoneArgs(state.CurrentRequestId, false,
                            "全局急停中止 (Global EmergencyStop)", CommandCompletionStatus.Error, state.Definition.AxisId));
                    }
                    state.ErrorStop = true;
                    state.ActualVelocity = 0;
                    ClearAxisMotion_Locked(state);
                    state.MotionType = AxisMotionType.ErrorStop;
                }
            }

            foreach (var d in doneList) ReportDone(d);
        }

        /// <summary>
        /// 故障复位指令 (MC_Reset)。清除急停/故障锁定并恢复到 <see cref="AxisMotionType.None"/>(Standstill/Disabled 待命)，
        /// 在途命令以 CommandAborted 完结；位置与 Homed 状态保留。
        /// </summary>
        public void ResetAxis(int axisId)
        {
            var doneList = new List<AxisDoneArgs>();
            lock (_lock)
            {
                if (_axisStates.TryGetValue(axisId, out var state))
                {
                    if (state.MotionType != AxisMotionType.None && state.CurrentRequestId != Guid.Empty)
                    {
                        doneList.Add(new AxisDoneArgs(state.CurrentRequestId, false,
                            "轴复位，中止当前命令", CommandCompletionStatus.CommandAborted, axisId));
                    }
                    state.PositiveHardLimitFault = false;
                    state.NegativeHardLimitFault = false;
                    state.DriveAlarmFault = false;
                    state.FollowingErrorFault = false;
                    state.ErrorStop = false;
                    ClearAxisMotion_Locked(state);
                    state.HomingStage = HomeStage.Search;
                }
            }

            foreach (var d in doneList) ReportDone(d);
        }

        /// <summary>
        /// 故障注入（测试与演示关键能力）。
        /// </summary>
        /// <param name="kind">故障类型</param>
        /// <param name="axisId">目标轴</param>
        /// <param name="active">是否激活</param>
        public void InjectFault(FaultKind kind, int axisId, bool active)
        {
            var interrupted = new List<(Guid requestId, int axisId)>();

            lock (_lock)
            {
                if (kind == FaultKind.Disconnected)
                {
                    if (active)
                    {
                        State = ConnectionState.Fault;
                        foreach (var st in _axisStates.Values)
                        {
                            if (st.CurrentRequestId != Guid.Empty)
                            {
                                interrupted.Add((st.CurrentRequestId, st.Definition.AxisId));
                            }
                            st.ActualVelocity = 0;
                            st.MotionType = AxisMotionType.None;
                            st.CurrentRequestId = Guid.Empty;
                        }
                    }
                    else
                    {
                        State = ConnectionState.Ready;
                    }
                }
                else if (_axisStates.TryGetValue(axisId, out var state))
                {
                    switch (kind)
                    {
                        case FaultKind.PositiveHardLimit:
                            state.PositiveHardLimitFault = active;
                            if (active)
                            {
                                if (state.CurrentRequestId != Guid.Empty)
                                    interrupted.Add((state.CurrentRequestId, axisId));
                                state.CurrentRequestId = Guid.Empty;
                                state.ActualVelocity = 0;
                                state.MotionType = AxisMotionType.None;
                                state.ErrorStop = true;
                                string ptName = state.Definition.LimitPositiveIoName ?? $"Limit{axisId}+";
                                Task.Run(() =>
                                {
                                    try { LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, ptName, true, true)); } catch { }
                                    try { AxisFault?.Invoke(new AxisFaultArgs(axisId, "HARD_LIMIT_POS", $"轴 {axisId} 正向硬限位触发")); } catch { }
                                });
                            }
                            break;

                        case FaultKind.NegativeHardLimit:
                            state.NegativeHardLimitFault = active;
                            if (active)
                            {
                                if (state.CurrentRequestId != Guid.Empty)
                                    interrupted.Add((state.CurrentRequestId, axisId));
                                state.CurrentRequestId = Guid.Empty;
                                state.ActualVelocity = 0;
                                state.MotionType = AxisMotionType.None;
                                state.ErrorStop = true;
                                string ptName = state.Definition.LimitNegativeIoName ?? $"Limit{axisId}-";
                                Task.Run(() =>
                                {
                                    try { LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, ptName, false, true)); } catch { }
                                    try { AxisFault?.Invoke(new AxisFaultArgs(axisId, "HARD_LIMIT_NEG", $"轴 {axisId} 负向硬限位触发")); } catch { }
                                });
                            }
                            break;

                        case FaultKind.DriveAlarm:
                            state.DriveAlarmFault = active;
                            if (active)
                            {
                                if (state.CurrentRequestId != Guid.Empty)
                                    interrupted.Add((state.CurrentRequestId, axisId));
                                state.CurrentRequestId = Guid.Empty;
                                state.ActualVelocity = 0;
                                state.MotionType = AxisMotionType.None;
                                state.ErrorStop = true;
                                Task.Run(() =>
                                {
                                    try { AxisFault?.Invoke(new AxisFaultArgs(axisId, "DRIVE_ALARM", $"轴 {axisId} 驱动器报警")); } catch { }
                                });
                            }
                            break;

                        case FaultKind.FollowingError:
                            state.FollowingErrorFault = active;
                            if (active)
                            {
                                state.ErrorStop = true;
                                Task.Run(() =>
                                {
                                    try { AxisFault?.Invoke(new AxisFaultArgs(axisId, "FOLLOWING_ERROR", $"轴 {axisId} 跟随误差超差")); } catch { }
                                });
                            }
                            break;
                    }
                }
            }

            foreach (var item in interrupted)
            {
                ReportDone(item.requestId, false, "故障注入中止当前命令", CommandCompletionStatus.Error, item.axisId);
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

                // 由仿真循环统一推进单调时刻锚点（单位 ms），供回零等超时判定使用（无 Thread.Sleep 到调用线程）
                _lastElapsedMs = sw.ElapsedMilliseconds;

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
                        if (!state.Enabled || state.MotionType == AxisMotionType.None
                            || state.MotionType == AxisMotionType.ErrorStop)
                        {
                            // ErrorStop 为锁定态：不进积分、不运动，直到 ResetAxis
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

                            case AxisMotionType.Stopping:
                                // Cat1 受控停车：按当前位置减速收敛后清除 Stopping（AxisManager 到零速后清 Stopping）
                                StepStopping(state, dt);
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

        /// <summary>Cat1 受控停车减速收敛：速度必须与"停车态"同步清除，避免出现半速且仍 Stopping 的中间态。</summary>
        private void StepStopping(SimAxisState state, double dt)
        {
            double decelPerStep = Math.Max(state.Decel, 1.0) * dt;
            double magnitude = Math.Max(Math.Abs(state.ActualVelocity) - decelPerStep, 0.0);
            if (magnitude <= 0.0)
            {
                // 已停稳：同步清除速度与 Stopping 态
                state.ActualVelocity = 0;
                state.MotionType = AxisMotionType.None;
                return;
            }

            double sign = Math.Sign(state.ActualVelocity == 0 ? 1 : state.ActualVelocity);
            state.ActualVelocity = magnitude * sign;
            state.ActualPosition += state.ActualVelocity * dt;
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
            // 回零总超时熔断（任何阶段共用）：IEC 走停 + 限时保护，禁止永久挂起
            if (ElapsedSinceMs(state.HomeStartElapsedMs) > HomeTotalTimeoutMs)
            {
                FailHome_Locked(state, doneList, $"回零超时（{HomeTotalTimeoutMs} ms 内未完成 {state.HomingStage} 阶段）");
                return;
            }

            switch (state.HomingStage)
            {
                // Zeroing：当前位置置零模式（Preparing → Zeroing）
                case HomeStage.Search when state.HomeMode == HomingMode.CurrentPosition:
                    state.HomingStage = HomeStage.Zeroing;
                    break;

                // Search：朝回零方向搜索原点信号（IO 事件驱动，行程/耗时双兜底）
                case HomeStage.Search:
                    StepHomeSearch(state, dt, doneList);
                    break;

                // Detected：记录触发原点瞬间的位置
                case HomeStage.Detected:
                    state.HomeDetectedPosition = state.ActualPosition;
                    state.HomingStage = HomeStage.Escaping;
                    break;

                // Escaping：反向退离原点
                case HomeStage.Escaping:
                    StepHomeEscape(state, dt, doneList);
                    break;

                // Settling：退离后停稳确认
                case HomeStage.Settling:
                    StepHomeSettling(state, dt);
                    break;

                // Zeroing：置零并通过 AxisDone 完成（完成只走事件，不静默成功）
                case HomeStage.Zeroing:
                {
                    var reqId = state.CurrentRequestId;
                    state.ActualPosition = 0;
                    state.ActualVelocity = 0;
                    state.Homed = true;
                    state.MotionType = AxisMotionType.None;
                    state.CurrentRequestId = Guid.Empty;
                    state.HomingStage = HomeStage.Search;
                    doneList.Add(new AxisDoneArgs(reqId, true, "回零成功"));
                    break;
                }
            }
        }

        /// <summary>Search：沿回零方向搜索原点，命中即转 Detected；行程耗尽则明确失败（替代旧的固定 50mm 成功伪逻辑）。</summary>
        private void StepHomeSearch(SimAxisState state, double dt, List<AxisDoneArgs> doneList)
        {
            // 硬限位 / 故障 / 急停：失败可见，禁止静默继续寻零
            if (ShouldStopAxis_Locked(state))
            {
                FailHome_Locked(state, doneList, "回零失败：寻零过程中检测到硬限位/故障/急停");
                return;
            }

            bool trigger = false;
            if (_ioController != null && !string.IsNullOrWhiteSpace(state.Definition.HomeIoName))
            {
                trigger = _ioController.ReadDi(state.Definition.HomeIoName);
            }

            double homeV = state.JogDir * state.HomeSpeed;
            state.ActualVelocity = homeV;
            state.ActualPosition += homeV * dt;

            double traveled = Math.Abs(state.ActualPosition - state.HomeStartPosition);
            if (trigger)
            {
                state.HomingStage = HomeStage.Detected;
            }
            else if (traveled >= state.HomeSearchExtent)
            {
                FailHome_Locked(state, doneList, "回零失败：走满搜索行程仍未检测到原点信号");
            }
        }

        /// <summary>Escaping：反向退离原点到设定距离，保持速度交给 Settling 收敛。</summary>
        private void StepHomeEscape(SimAxisState state, double dt, List<AxisDoneArgs> doneList)
        {
            if (ShouldStopAxis_Locked(state))
            {
                FailHome_Locked(state, doneList, "回零失败：退离过程中检测到硬限位/故障/急停");
                return;
            }

            int escapeDir = -state.JogDir;
            double escapeDistance = Math.Max(state.HomeEscapeExtent, 0.05);
            state.ActualVelocity = escapeDir * state.HomeSpeed * HomeEscapeSpeedRatio;
            state.ActualPosition += state.ActualVelocity * dt;

            if (Math.Abs(state.ActualPosition - state.HomeDetectedPosition) >= escapeDistance)
            {
                state.ActualPosition = state.HomeDetectedPosition + escapeDir * escapeDistance;
                state.HomingStage = HomeStage.Settling;
            }
        }

        /// <summary>Settling：退离后速度收敛到阈值即认为停稳，转 Zeroing。</summary>
        private void StepHomeSettling(SimAxisState state, double dt)
        {
            state.ActualVelocity *= HomeSettleFactor;
            state.ActualPosition += state.ActualVelocity * dt;
            if (Math.Abs(state.ActualVelocity) <= HomeSettleVThreshold)
            {
                state.ActualVelocity = 0;
                state.HomingStage = HomeStage.Zeroing;
            }
        }

        /// <summary>相对当前仿真时刻已过去的毫秒数（锚点由 ITimeSource 校准的仿真循环更新，无 Thread.Sleep）。</summary>
        private long ElapsedSinceMs(long anchorMs) => _lastElapsedMs - anchorMs;

        /// <summary>该轴是否处于故障/急停锁定（回零过程中同样要失败可见）。调用方需持锁。</summary>
        private static bool ShouldStopAxis_Locked(SimAxisState state)
            => state.ErrorStop || state.PositiveHardLimitFault || state.NegativeHardLimitFault
                || state.DriveAlarmFault || state.FollowingErrorFault;

        /// <summary>回零失败：以 Error 完结 AxisDone 并停住当前位置（不置零、不标记 Homed）。调用方需持锁。</summary>
        private static void FailHome_Locked(SimAxisState state, List<AxisDoneArgs> doneList, string reason)
        {
            var reqId = state.CurrentRequestId;
            int axisId = state.Definition.AxisId;
            state.ActualVelocity = 0;
            state.MotionType = AxisMotionType.None;
            state.CurrentRequestId = Guid.Empty;
            state.HomingStage = HomeStage.Search;
            doneList.Add(new AxisDoneArgs(reqId, false, reason, CommandCompletionStatus.Error, axisId));
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
