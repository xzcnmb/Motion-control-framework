#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Drivers.Native;

namespace Sophon.Infrastructure.Motion.Drivers
{
    /// <summary>
    /// 固高 GTS / GTS-VB / GTHD 适配器骨架（gts.dll）。GEN / GE 总线主站不要走这里。
    /// 未经真机验证。DLL 缺失必须失败可见，禁止假装成功。
    /// </summary>
    public class GoogolGtsMotionController : IMotionController, IVerifiedDriver
    {
        private readonly List<AxisDefinition> _axes;
        private readonly ConcurrentDictionary<int, bool> _enabled = new();
        private readonly ConcurrentDictionary<int, bool> _homed = new();
        private readonly ConcurrentDictionary<int, Guid> _activeRequests = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<AxisDoneArgs>> _pending = new();
        private readonly ConcurrentDictionary<int, bool> _readFaultNotified = new();

        private ConnectionState _state = ConnectionState.Disconnected;
        private Thread? _monitorThread;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public DriverKind Kind => DriverKind.GoogolGts;

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
            MotionCapability.BufferedSegments |
            MotionCapability.HardLimitInput |
            MotionCapability.ContinuousVelocityBlending |
            MotionCapability.HardwareCompareOutput |
            MotionCapability.BacklashCompensation;

        public IReadOnlyList<AxisDefinition> Axes => _axes.AsReadOnly();

        /// <summary>
        /// 标注驱动是否经过真机验证。真卡骨架实现固定返回 false。
        /// </summary>
        public bool IsFieldVerified => false;

        public event Action<AxisDoneArgs>? AxisDone;
        public event Action<AxisFaultArgs>? AxisFault;
        public event Action<LimitTriggeredArgs>? LimitTriggered;

        public GoogolGtsMotionController(IEnumerable<AxisDefinition>? axes = null)
        {
            _axes = axes?.ToList() ?? new List<AxisDefinition>();
            foreach (var ax in _axes)
            {
                _enabled[ax.AxisId] = false;
                _homed[ax.AxisId] = false;
            }

            _monitorThread = new Thread(MonitorLoop)
            {
                Name = "GoogolGts_MonitorLoop",
                IsBackground = true
            };
            _monitorThread.Start();
        }

        /// <summary>
        /// 统一完成回报：先同步完成 TCS（异步 API 无订阅竞态），再异步派发 AxisDone 事件。
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
                catch { }
            });
        }

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            State = ConnectionState.Connecting;
            await Task.Yield();

            try
            {
                // [未经真机验证] 固高卡开卡与复位初始化。
                // DLL 缺失一律视为连接失败——绝不静默进入 Ready 模拟运行。
                short ret = GoogolGtsNative.GT_Open(0, 1);
                if (ret == 0)
                {
                    GoogolGtsNative.GT_Reset();
                }

                if (ret != 0)
                {
                    State = ConnectionState.Fault;
                    throw new InvalidOperationException($"固高 GTS 开卡失败，错误码: {ret} (未经真机验证)");
                }

                State = ConnectionState.Ready;
            }
            catch (DllNotFoundException ex)
            {
                State = ConnectionState.Fault;
                throw new InvalidOperationException("未找到固高 GTS 驱动 DLL(gts.dll)，无法连接真卡 (未经真机验证)", ex);
            }
            catch
            {
                State = ConnectionState.Fault;
                throw;
            }
        }

        public Task DisconnectAsync()
        {
            try
            {
                // [未经真机验证] 关闭固高控制器
                try
                {
                    GoogolGtsNative.GT_Close();
                }
                catch (DllNotFoundException) { }
            }
            catch
            {
                // 忽略关闭异常
            }

            State = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public void EnableAxis(int axisId)
        {
            try
            {
                // [未经真机验证] 固高轴使能；DLL 缺失/失败时不置位使能标志
                GoogolGtsNative.GT_AxisOn((short)(axisId + 1));
                _enabled[axisId] = true;
            }
            catch (Exception ex)
            {
                _enabled[axisId] = false;
                AxisFault?.Invoke(new AxisFaultArgs(axisId, "ENABLE_FAIL", $"使能失败: {ex.Message} (未经真机验证)"));
            }
        }

        public void DisableAxis(int axisId)
        {
            try
            {
                // [未经真机验证] 固高轴下使能
                try
                {
                    GoogolGtsNative.GT_AxisOff((short)(axisId + 1));
                }
                catch (DllNotFoundException) { }
                _enabled[axisId] = false;
            }
            catch (Exception ex)
            {
                AxisFault?.Invoke(new AxisFaultArgs(axisId, "DISABLE_FAIL", $"下使能失败: {ex.Message} (未经真机验证)"));
            }
        }

        public bool IsAxisEnabled(int axisId) => _enabled.TryGetValue(axisId, out var en) && en;

        public bool IsAxisHomed(int axisId) => _homed.TryGetValue(axisId, out var h) && h;

        public double GetPosition(int axisId)
        {
            try
            {
                // [未经真机验证] 读规划位置并换算为物理单位。
                // 读取失败返回 NaN 并报 AxisFault，禁止回退缓存值（诚实性铁律）。
                if (GoogolGtsNative.GT_GetPos((short)(axisId + 1), out int pPos) == 0)
                {
                    var def = _axes.FirstOrDefault(a => a.AxisId == axisId);
                    double ppu = def?.PulsePerUnit ?? 1000.0;
                    if (ppu > 0) return pPos / ppu;
                }
            }
            catch (Exception ex)
            {
                NotifyReadFaultOnce(axisId, ex.Message);
                return double.NaN;
            }

            NotifyReadFaultOnce(axisId, "GT_GetPos 返回失败");
            return double.NaN;
        }

        public double GetVelocity(int axisId)
        {
            try
            {
                // [未经真机验证] 读规划速度
                if (GoogolGtsNative.GT_GetVel((short)(axisId + 1), out double pVel) == 0)
                {
                    var def = _axes.FirstOrDefault(a => a.AxisId == axisId);
                    double ppu = def?.PulsePerUnit ?? 1000.0;
                    if (ppu > 0) return pVel / ppu;
                }
            }
            catch (Exception ex)
            {
                NotifyReadFaultOnce(axisId, ex.Message);
                return double.NaN;
            }

            NotifyReadFaultOnce(axisId, "GT_GetVel 返回失败");
            return double.NaN;
        }

        private void NotifyReadFaultOnce(int axisId, string detail)
        {
            if (_readFaultNotified.TryAdd(axisId, true))
            {
                AxisFault?.Invoke(new AxisFaultArgs(axisId, "READ_FAIL", $"位置/速度读取失败: {detail} (未经真机验证)"));
            }
        }

        public Guid Jog(int axisId, int dir, double speed)
        {
            var req = Guid.NewGuid();
            if (State != ConnectionState.Ready)
            {
                ReportDone(req, false, "控制器未连接 (未经真机验证)");
                return req;
            }

            try
            {
                var def = _axes.FirstOrDefault(a => a.AxisId == axisId);
                double clampedSpeed = def != null ? Math.Min(Math.Abs(speed), def.MaxSpeed) : Math.Abs(speed);
                double ppu = def?.PulsePerUnit ?? 1000.0;
                double pulseVel = clampedSpeed * ppu * (dir >= 0 ? 1 : -1);

                // [未经真机验证] 固高点动运动
                short ax = (short)(axisId + 1);
                GoogolGtsNative.GT_PrfTrap(ax);
                var prm = new GoogolGtsNative.TTrapPrm
                {
                    acc = (def?.MaxAccel ?? 1000) * ppu,
                    dec = (def?.MaxDecel ?? 1000) * ppu,
                    velStart = 0,
                    smoothTime = 20
                };
                GoogolGtsNative.GT_SetTrapPrm(ax, ref prm);
                GoogolGtsNative.GT_SetVel(ax, Math.Abs(pulseVel));
                GoogolGtsNative.GT_Update(1 << axisId);

                _activeRequests[axisId] = req;
            }
            catch (Exception ex)
            {
                ReportDone(req, false, $"Jog失败: {ex.Message} (未经真机验证)");
            }

            return req;
        }

        public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0)
        {
            var req = Guid.NewGuid();
            MoveAbsCore(req, axisId, target, speed, accel, decel, jerk);
            return req;
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

        private void MoveAbsCore(Guid req, int axisId, double target, double speed, double accel, double decel, double jerk)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(req, false, "控制器未连接 (未经真机验证)");
                return;
            }

            var def = _axes.FirstOrDefault(a => a.AxisId == axisId);
            if (def != null && def.SoftLimitEnabled)
            {
                if (target > def.SoftLimitMax)
                {
                    _ = Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, "SoftLimitMax", true, false)));
                    ReportDone(req, false, "目标超出正向软限位 (未经真机验证)");
                    return;
                }
                if (target < def.SoftLimitMin)
                {
                    _ = Task.Run(() => LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, "SoftLimitMin", false, false)));
                    ReportDone(req, false, "目标超出负向软限位 (未经真机验证)");
                    return;
                }
            }

            try
            {
                double ppu = def?.PulsePerUnit ?? 1000.0;
                int targetPulse = (int)(target * ppu);
                double clampedSpeed = def != null ? Math.Min(Math.Abs(speed), def.MaxSpeed) : Math.Abs(speed);

                // [未经真机验证] 固高点位绝对运动。DLL 缺失/调用失败一律回报失败，绝不模拟到位。
                short ax = (short)(axisId + 1);
                GoogolGtsNative.GT_PrfTrap(ax);
                var prm = new GoogolGtsNative.TTrapPrm
                {
                    acc = (accel > 0 ? accel : (def?.MaxAccel ?? 1000)) * ppu,
                    dec = (decel > 0 ? decel : (def?.MaxDecel ?? 1000)) * ppu,
                    velStart = 0,
                    smoothTime = 20
                };
                GoogolGtsNative.GT_SetTrapPrm(ax, ref prm);
                GoogolGtsNative.GT_SetPos(ax, targetPulse);
                GoogolGtsNative.GT_SetVel(ax, clampedSpeed * ppu);
                GoogolGtsNative.GT_Update(1 << axisId);

                _activeRequests[axisId] = req;

                // [未经真机验证] 骨架未实现卡内到位中断映射：
                // 真机联调前，运动完成/失败不会自动回报 AxisDone（上位机需依赖超时保护）。
                // 真机实现时应订阅卡内运动完成中断并调用 ReportDone。
            }
            catch (Exception ex)
            {
                ReportDone(req, false, $"MoveAbs失败: {ex.Message} (未经真机验证)");
            }
        }

        public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0)
        {
            double cur = GetPosition(axisId);
            if (double.IsNaN(cur))
            {
                var req = Guid.NewGuid();
                ReportDone(req, false, $"轴 {axisId} 当前位置读取失败，无法相对定位 (未经真机验证)");
                return req;
            }
            return MoveAbs(axisId, cur + delta, speed, accel, decel, jerk);
        }

        public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            var req = Guid.NewGuid();
            HomeCore(req, axisId, mode, dir, speed);
            return req;
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

        private void HomeCore(Guid req, int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            if (State != ConnectionState.Ready)
            {
                ReportDone(req, false, "控制器未连接 (未经真机验证)");
                return;
            }

            // [未经真机验证] 骨架未实现回零流程：如实回报失败，真机按原点/限位信号时序实现后
            // 再置位 _homed 并通过 ReportDone(req, true, ...) 回报。
            ReportDone(req, false, "回零流程未实现 (固高GTS骨架，未经真机验证)");
        }

        // ---------- 停止与安全分层实现 (EN 60204-1 Stop Category 2/1/0 & PLCopen) ----------

        /// <summary>
        /// Level 1: 软件工艺暂停/平滑停止 (MC_Halt, Stop Cat 2)。
        /// </summary>
        public void Halt(int axisId)
        {
            try
            {
                try
                {
                    GoogolGtsNative.GT_Stop(1 << axisId, 0);
                }
                catch (DllNotFoundException) { }

                if (_activeRequests.TryRemove(axisId, out var rId))
                {
                    ReportDone(rId, false, "工艺暂停中止 (MC_Halt, 未经真机验证)", CommandCompletionStatus.CommandAborted, axisId);
                }
            }
            catch { }
        }

        /// <summary>
        /// Level 2: 控制卡受控停止 (MC_Stop, Stop Cat 1)。
        /// </summary>
        public void Stop(int axisId) => StopMotion(axisId);

        public void StopMotion(int axisId)
        {
            try
            {
                try
                {
                    GoogolGtsNative.GT_Stop(1 << axisId, 0);
                }
                catch (DllNotFoundException) { }

                if (_activeRequests.TryRemove(axisId, out var rId))
                {
                    ReportDone(rId, false, "受控减速停止 (MC_Stop, 未经真机验证)", CommandCompletionStatus.CommandAborted, axisId);
                }
            }
            catch { }
        }

        /// <summary>
        /// Level 3: 硬件安全急停 (Emergency Stop / Hard Abort / STO, Stop Cat 0/1)。
        /// </summary>
        public void EmergencyStop(int axisId) => Abort(axisId);

        public void Abort(int axisId, double decelRatio = 0)
        {
            try
            {
                try
                {
                    GoogolGtsNative.GT_Stop(1 << axisId, 1);
                }
                catch (DllNotFoundException) { }

                if (_activeRequests.TryRemove(axisId, out var rId))
                {
                    ReportDone(rId, false, "轴硬件急停中止 (MC_EStop, 未经真机验证)", CommandCompletionStatus.Error, axisId);
                }
            }
            catch { }
        }

        public void EmergencyStopAll() => AbortAll();

        public void AbortAll()
        {
            try
            {
                try
                {
                    GoogolGtsNative.GT_Stop(0xFFFF, 1);
                }
                catch (DllNotFoundException) { }

                foreach (var ax in _axes)
                {
                    if (_activeRequests.TryRemove(ax.AxisId, out var rId))
                    {
                        ReportDone(rId, false, "全局急停中止 (Global EmergencyStop, 未经真机验证)", CommandCompletionStatus.Error, ax.AxisId);
                    }
                }
            }
            catch { }
        }

        public void ResetAxis(int axisId)
        {
            try
            {
                try
                {
                    GoogolGtsNative.GT_ClrSts((short)axisId, 1);
                }
                catch (DllNotFoundException) { }
            }
            catch { }
        }

        private void MonitorLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (State == ConnectionState.Ready)
                    {
                        foreach (var ax in _axes)
                        {
                            // [未经真机验证] 轮询状态字检查报警和限位
                            try
                            {
                                if (GoogolGtsNative.GT_GetSts((short)(ax.AxisId + 1), out int sts) == 0)
                                {
                                    // 检查报警标志位 (位1: 驱动报警, 位5: 正限位, 位6: 负限位)
                                    if ((sts & 0x02) != 0)
                                    {
                                        AxisFault?.Invoke(new AxisFaultArgs(ax.AxisId, "ALARM", "驱动报警 (未经真机验证)"));
                                    }
                                    if ((sts & 0x20) != 0)
                                    {
                                        LimitTriggered?.Invoke(new LimitTriggeredArgs(ax.AxisId, ax.LimitPositiveIoName ?? "PosLimit", true, true));
                                    }
                                    if ((sts & 0x40) != 0)
                                    {
                                        LimitTriggered?.Invoke(new LimitTriggeredArgs(ax.AxisId, ax.LimitNegativeIoName ?? "NegLimit", false, true));
                                    }
                                }
                            }
                            catch (DllNotFoundException) { }
                        }
                    }
                }
                catch { }

                Thread.Sleep(50);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            if (_monitorThread != null && _monitorThread.IsAlive)
            {
                _monitorThread.Join(500);
            }
            _cts.Dispose();
        }
    }
}