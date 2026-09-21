#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Alarm;

namespace Sophon.Core.Device
{
    /// <summary>
    /// 工业气缸 / 夹爪控制服务。
    /// 承载工业可靠性五道防线：
    /// 1. 启动前置条件校验（安全门、气压信号等）
    /// 2. 气缸组互锁保护（防止同组多气缸同时伸出干涉碰撞）
    /// 3. 双电控防烧脉冲输出（严禁双线圈同时导通，脉冲换向后及时复位）/ 单电控电平自保持
    /// 4. 双到位磁性开关确认（等真实反馈到达，禁止盲信命令）
    /// 5. 动作超时保护与异常报警
    ///
    /// 安全语义（Item ⑧）——故障自动愈合（Auto-Heal）安全边界：
    /// * 超时 / 输出异常 / 取消等故障时，必须释放互锁组占用（_groupActiveWork），避免同组其他气缸被永久锁死；
    /// * 严禁盲目驱动反向线圈 / DO（可能压伤工件或与人员、刀具碰撞）：
    ///   双电控脉冲结束后双线圈本已断电（阀机械保位），故障时仅做防御性断电，不发出反向指令；
    ///   单电控故障时撤除驱动电平（失电即弹簧复位到设计安全位），同样不发出反向指令；
    /// * 故障必须登记正式的安全故障事件 / 报警（AlarmCenter），等待人工干预，禁止静默恢复；
    /// * 提供 ForceReleaseInterlock / GetInterlockHolder 供人工干预与诊断复位。
    /// </summary>
    public class CylinderService
    {
        private readonly IIoController _ioController;
        private readonly AlarmCenter? _alarmCenter;
        private readonly ConcurrentDictionary<string, CylinderPosition> _positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _groupActiveWork = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        // 气缸安全故障报警代码（按气缸名实例化，避免多气缸故障相互覆盖 / 单次 Clear 误清其他气缸）
        private const string AlarmCodeTimeout = "CYLINDER_TIMEOUT";
        private const string AlarmCodeOutputFault = "CYLINDER_OUTPUT_FAULT";
        private const string AlarmCodeInterrupted = "CYLINDER_FAULT";

        public CylinderService(IIoController ioController, AlarmCenter? alarmCenter = null)
        {
            _ioController = ioController ?? throw new ArgumentNullException(nameof(ioController));
            _alarmCenter = alarmCenter;
        }

        /// <summary>
        /// 动作执行结果
        /// </summary>
        public record MoveResult(bool Success, string Message, CylinderPosition FinalPosition);

        /// <summary>
        /// 驱动气缸运动至目标位置（Work=伸出/张开，Home=缩回/夹紧）。
        /// </summary>
        public async Task<MoveResult> MoveToAsync(CylinderDefinition c, CylinderPosition target, CancellationToken ct = default)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            if (target == CylinderPosition.Unknown)
            {
                return new MoveResult(false, "目标位置不可为 Unknown", GetPosition(c));
            }

            // 1. 检查前置安全条件（如气压正常、安全门关闭）
            if (c.EnableConditionDiNames != null)
            {
                foreach (var diName in c.EnableConditionDiNames)
                {
                    if (!string.IsNullOrWhiteSpace(diName))
                    {
                        if (!_ioController.ReadDi(diName))
                        {
                            return new MoveResult(false, $"前置条件未满足: 输入点 '{diName}' 为 False，禁止气缸动作", GetPosition(c));
                        }
                    }
                }
            }

            // 2. 组互锁校验：若进入 Work 且同组已有其他气缸处于 Work，则拒绝动作
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(c.InterlockGroup))
                {
                    if (target == CylinderPosition.Work)
                    {
                        if (_groupActiveWork.TryGetValue(c.InterlockGroup, out var currentHolder) &&
                            !string.Equals(currentHolder, c.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            return new MoveResult(false, $"互锁保护拦截：同组 '{c.InterlockGroup}' 中已有气缸 '{currentHolder}' 处于工作位置，禁止同时伸出", GetPosition(c));
                        }
                        _groupActiveWork[c.InterlockGroup] = c.Name;
                    }
                    else if (target == CylinderPosition.Home)
                    {
                        if (_groupActiveWork.TryGetValue(c.InterlockGroup, out var currentHolder) &&
                            string.Equals(currentHolder, c.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            _groupActiveWork.TryRemove(c.InterlockGroup, out _);
                        }
                    }
                }
            }

            // 3. 电磁阀动作输出
            try
            {
                int pulseMs = c.PulseWidthMs > 0 ? c.PulseWidthMs : 200;

                if (c.Valve == ValveType.DoubleCoil)
                {
                    // 双电控：先断开反向线圈（互锁），再给目标线圈施加短暂脉冲（防烧线圈）
                    if (target == CylinderPosition.Work)
                    {
                        if (!string.IsNullOrWhiteSpace(c.HomeDoName))
                        {
                            _ioController.WriteDo(c.HomeDoName, false);
                        }
                        if (!string.IsNullOrWhiteSpace(c.WorkDoName))
                        {
                            _ioController.WriteDo(c.WorkDoName, true);
                            await Task.Delay(pulseMs, ct);
                            _ioController.WriteDo(c.WorkDoName, false);
                        }
                    }
                    else // Home
                    {
                        if (!string.IsNullOrWhiteSpace(c.WorkDoName))
                        {
                            _ioController.WriteDo(c.WorkDoName, false);
                        }
                        if (!string.IsNullOrWhiteSpace(c.HomeDoName))
                        {
                            _ioController.WriteDo(c.HomeDoName, true);
                            await Task.Delay(pulseMs, ct);
                            _ioController.WriteDo(c.HomeDoName, false);
                        }
                    }
                }
                else
                {
                    // 单电控：得电 Work，失电 Home（弹簧自复位）
                    if (!string.IsNullOrWhiteSpace(c.WorkDoName))
                    {
                        _ioController.WriteDo(c.WorkDoName, target == CylinderPosition.Work);
                    }
                }
            }
            catch (Exception ex)
            {
                // 安全边界：输出异常时释放互锁占用并登记安全故障，严禁反向盲动
                SafeRecoverFromFault(c, target, AlarmCodeOutputFault, $"电磁阀输出异常: {ex.Message}");
                return new MoveResult(false, $"电磁阀输出异常: {ex.Message}", GetPosition(c));
            }

            // 4. 到位确认与超时检测
            string? targetSensor = target == CylinderPosition.Work ? c.WorkSensorDiName : c.HomeSensorDiName;
            if (string.IsNullOrWhiteSpace(targetSensor))
            {
                // 无传感器反馈配置：等待额定时间后认定完成（记录警告）
                int fallbackWait = c.PulseWidthMs > 0 ? c.PulseWidthMs : 200;
                await Task.Delay(fallbackWait, ct);
                _positions[c.Name] = target;
                return new MoveResult(true, "动作已下发（未配置对应磁性开关，无传感器闭环反馈）", target);
            }

            // 闭环轮询磁性开关到位
            int timeoutMs = c.ConfirmTimeoutMs > 0 ? c.ConfirmTimeoutMs : 1500;
            var startTicks = Environment.TickCount64;

            try
            {
                while (Environment.TickCount64 - startTicks < timeoutMs)
                {
                    ct.ThrowIfCancellationRequested();

                    if (_ioController.ReadDi(targetSensor))
                    {
                        _positions[c.Name] = target;
                        return new MoveResult(true, $"到位确认成功: 磁性开关 '{targetSensor}' 已接通", target);
                    }

                    await Task.Delay(15, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // 安全边界：动作被取消且到位状态未确认 —— 释放互锁、撤除驱动、登记故障，防止永久锁死与失控动作
                SafeRecoverFromFault(c, target, AlarmCodeInterrupted, "动作被取消，且到位信号未确认");
                throw;
            }

            // 超时未到位：执行安全边界的自动愈合（释放互锁 + 撤除驱动 + 登记安全故障），严禁盲目反向驱动
            SafeRecoverFromFault(c, target, AlarmCodeTimeout,
                $"到位超时：在 {timeoutMs}ms 内磁性开关 '{targetSensor}' 未触发，请检查气源气压或传感器位置");

            return new MoveResult(false, $"到位超时报警：在 {timeoutMs}ms 内磁性开关 '{targetSensor}' 未触发，请检查气源气压或传感器位置", GetPosition(c));
        }

        /// <summary>
        /// 安全边界的自动愈合（Auto-Heal Safe Recovery）。
        /// 故障（超时 / 输出异常 / 取消）时统一执行：
        /// 1) 释放互锁组占用（_groupActiveWork），避免同组其他气缸被永久锁死；
        /// 2) 撤除本气缸的全部驱动输出（双线圈本就已脉冲断电，此处防御性补断；单电控撤除驱动电平，
        ///    失电回到弹簧复位的设计安全位）—— 严禁驱动反向线圈 / DO，防止压伤工件或碰撞人员刀具；
        /// 3) 向 AlarmCenter 登记正式的安全故障事件（按气缸名实例化），等待人工干预，禁止静默恢复。
        /// </summary>
        private void SafeRecoverFromFault(CylinderDefinition c, CylinderPosition target, string alarmCode, string reason)
        {
            // 1) 释放互锁组占用（若本气缸仍持有该组）
            if (!string.IsNullOrWhiteSpace(c.InterlockGroup))
            {
                lock (_lock)
                {
                    if (_groupActiveWork.TryGetValue(c.InterlockGroup, out var holder) &&
                        string.Equals(holder, c.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _groupActiveWork.TryRemove(c.InterlockGroup, out _);
                    }
                }
            }

            // 2) 撤除驱动输出（fail-safe：失电为安全态；绝不发出反向指令）
            try
            {
                if (!string.IsNullOrWhiteSpace(c.WorkDoName))
                {
                    _ioController.WriteDo(c.WorkDoName, false);
                }
                if (!string.IsNullOrWhiteSpace(c.HomeDoName))
                {
                    _ioController.WriteDo(c.HomeDoName, false);
                }
            }
            catch
            {
                // 撤除驱动失败不阻断故障登记流程（IO 层自身故障由上位监控捕获）
            }

            // 3) 登记正式安全故障报警（Severity=Error, Linkage=StopFlow，等待人工干预）
            string code = $"{alarmCode}#{c.Name}";
            string message = $"气缸 '{c.Name}' 安全故障（{reason}），已释放互锁并撤除驱动，等待人工干预";
            try
            {
                _alarmCenter?.Register(new AlarmDefinition(code, message, AlarmSeverity.Error, LinkageMode.StopFlow));
                _alarmCenter?.Raise(code, $"Cylinder:{c.Name}, Target:{target}, Reason:{reason}", source: "CylinderService");
            }
            catch
            {
                // 报警登记失败不影响安全恢复动作本身
            }
        }

        /// <summary>
        /// 人工干预 / 诊断复位：强制释放指定互锁组的占用（不动作任何气缸输出）。
        /// 用于故障后人工确认现场安全、解除组互锁锁死的场景。
        /// </summary>
        /// <param name="groupName">互锁组名</param>
        /// <returns>该组此前是否被占用（true=已释放）</returns>
        public bool ForceReleaseInterlock(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return false;

            lock (_lock)
            {
                return _groupActiveWork.TryRemove(groupName, out _);
            }
        }

        /// <summary>
        /// 查询指定互锁组当前的工作位占用者（气缸名）。
        /// </summary>
        /// <param name="groupName">互锁组名</param>
        /// <returns>占用气缸名；无占用或组不存在时返回 null</returns>
        public string? GetInterlockHolder(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName)) return null;

            lock (_lock)
            {
                return _groupActiveWork.TryGetValue(groupName, out var holder) ? holder : null;
            }
        }

        /// <summary>
        /// 获取气缸当前真实物理位置（基于磁性开关 DI 双到位状态）。
        /// </summary>
        public CylinderPosition GetPosition(CylinderDefinition c)
        {
            if (c == null) return CylinderPosition.Unknown;

            bool hasWorkSensor = !string.IsNullOrWhiteSpace(c.WorkSensorDiName);
            bool hasHomeSensor = !string.IsNullOrWhiteSpace(c.HomeSensorDiName);

            if (hasWorkSensor && hasHomeSensor)
            {
                bool w = _ioController.ReadDi(c.WorkSensorDiName!);
                bool h = _ioController.ReadDi(c.HomeSensorDiName!);

                if (w && !h) return CylinderPosition.Work;
                if (h && !w) return CylinderPosition.Home;
                return CylinderPosition.Unknown;
            }

            if (hasWorkSensor)
            {
                return _ioController.ReadDi(c.WorkSensorDiName!) ? CylinderPosition.Work : CylinderPosition.Unknown;
            }

            if (hasHomeSensor)
            {
                return _ioController.ReadDi(c.HomeSensorDiName!) ? CylinderPosition.Home : CylinderPosition.Unknown;
            }

            return _positions.TryGetValue(c.Name, out var p) ? p : CylinderPosition.Unknown;
        }

        /// <summary>
        /// 上电初始复位（若配置了 ResetToHomeOnStart，则归回 Home）。
        /// </summary>
        public async Task<MoveResult> InitializeCylinderAsync(CylinderDefinition c, CancellationToken ct = default)
        {
            if (c.ResetToHomeOnStart)
            {
                return await MoveToAsync(c, CylinderPosition.Home, ct);
            }
            return new MoveResult(true, "保持当前状态", GetPosition(c));
        }
    }
}
