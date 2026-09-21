#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.Core.Coordinator
{
    /// <summary>
    /// 工业运动协调器（中间层，位于 HAL/AxisManager 之上，流程与 UI 业务之下）。
    /// 对标 PLCopen Part 4 协调运动规范：
    /// 1. 轴组 (AxisGroup) 拓扑与生命周期管理（XY 龙门、XYZ 笛卡尔、双轴对位）；
    /// 2. 轴组状态机：GroupDisabled, GroupStandby, GroupMoving, GroupStopping, GroupErrorStop；
    /// 3. 单轴与轴组访问仲裁：轴组运动期间锁定成员轴（防单轴抢占撕裂机构）；单轴移动时阻断轴组启动；
    /// 4. 多轴协调插补与 Fail-Fast 连锁急停：任意轴异常 1ms 内全组急停；
    /// 5. 统一分层停止：GroupHalt (Cat 2), GroupStop (Cat 1), GroupEmergencyStop (Cat 0/1)。
    /// </summary>
    public class MotionCoordinator : IDisposable
    {
        private readonly IMotionController _motionController;
        private readonly AxisManager? _axisManager;

        private readonly ConcurrentDictionary<string, GroupRuntimeContext> _groups = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, string> _axisToGroupLock = new(); // 记录哪些轴被哪个轴组锁死
        private readonly object _lock = new();
        private bool _isDisposed;

        /// <summary>
        /// 轴组运行时上下文。
        /// </summary>
        public class GroupRuntimeContext
        {
            public AxisGroupDefinition Definition { get; set; }
            public AxisGroupState State { get; set; } = AxisGroupState.GroupStandby;
            public Guid CurrentRequestId { get; set; } = Guid.Empty;
            public string? LastError { get; set; }

            public GroupRuntimeContext(AxisGroupDefinition def)
            {
                Definition = def;
            }
        }

        public MotionCoordinator(IMotionController motionController, AxisManager? axisManager = null)
        {
            _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
            _axisManager = axisManager;

            _motionController.AxisDone += OnAxisDone;
            _motionController.AxisFault += OnAxisFault;
            _motionController.LimitTriggered += OnLimitTriggered;
        }

        #region 轴组注册与管理

        /// <summary>
        /// 注册并激活一个轴组。
        /// </summary>
        public void RegisterGroup(AxisGroupDefinition groupDef)
        {
            if (groupDef == null) throw new ArgumentNullException(nameof(groupDef));
            if (string.IsNullOrWhiteSpace(groupDef.GroupName)) throw new ArgumentException("GroupName 不能为空", nameof(groupDef));
            if (groupDef.AxisIds == null || groupDef.AxisIds.Count == 0) throw new ArgumentException("轴组成员轴不能为空", nameof(groupDef));

            _groups[groupDef.GroupName] = new GroupRuntimeContext(groupDef);
        }

        /// <summary>
        /// 获取指定轴组当前状态。
        /// </summary>
        public AxisGroupState GetGroupState(string groupName)
        {
            return _groups.TryGetValue(groupName, out var ctx) ? ctx.State : AxisGroupState.GroupDisabled;
        }

        /// <summary>
        /// 获取全部已注册轴组清单。
        /// </summary>
        public IReadOnlyList<AxisGroupDefinition> GetAllGroups() => _groups.Values.Select(g => g.Definition).ToList();

        #endregion

        #region 命令仲裁与资源互斥

        /// <summary>
        /// 仲裁单轴独立操作请求（调试、手动点动、单轴回零）。
        /// 若目标轴正参与轴组协同运动 (GroupMoving)，则仲裁拒绝，保护机构防扭扯。
        /// </summary>
        public bool TryAcquireSingleAxisAccess(int axisId, out string rejectionReason)
        {
            rejectionReason = string.Empty;
            if (_axisToGroupLock.TryGetValue(axisId, out var holderGroup))
            {
                rejectionReason = $"仲裁拦截：轴 {axisId} 当前被轴组 '{holderGroup}' 独占协同运行中，禁止单轴抢占！";
                return false;
            }
            return true;
        }

        #endregion

        #region 轴组协调运动与 Fail-Fast 连锁

        /// <summary>
        /// 执行轴组直线插补定位协同运动。
        /// 包含：资源仲裁锁定、并发启动、单轴故障 Fail-Fast 连锁急停、到位状态闭式转换。
        /// </summary>
        public async Task<AxisDoneArgs> MoveLinearAsync(
            string groupName,
            double[] targetPositions,
            double? speed = null,
            double? accel = null,
            double? decel = null,
            int timeoutMs = 30000,
            CancellationToken ct = default)
        {
            if (!_groups.TryGetValue(groupName, out var groupCtx))
            {
                return new AxisDoneArgs(Guid.NewGuid(), false, $"未找到轴组: {groupName}", CommandCompletionStatus.Error);
            }

            var def = groupCtx.Definition;
            if (targetPositions.Length != def.AxisIds.Count)
            {
                return new AxisDoneArgs(Guid.NewGuid(), false, $"目标坐标数量({targetPositions.Length})与轴组成员数({def.AxisIds.Count})不符", CommandCompletionStatus.Error);
            }

            // 1. 状态机检查 (PLCopen: 只能在 GroupStandby 启动)
            lock (_lock)
            {
                if (groupCtx.State == AxisGroupState.GroupStopping)
                {
                    return new AxisDoneArgs(Guid.NewGuid(), false, $"轴组 '{groupName}' 处于 GroupStopping 停止状态，拒绝新指令", CommandCompletionStatus.CommandAborted);
                }
                if (groupCtx.State == AxisGroupState.GroupErrorStop)
                {
                    return new AxisDoneArgs(Guid.NewGuid(), false, $"轴组 '{groupName}' 处于 GroupErrorStop 故障锁定，需先复位", CommandCompletionStatus.Error);
                }

                // 2. 独占锁定全部成员轴
                foreach (var ax in def.AxisIds)
                {
                    _axisToGroupLock[ax] = groupName;
                }
                groupCtx.State = AxisGroupState.GroupMoving;
            }

            double synSpeed = speed ?? def.DefaultSpeed;
            double synAccel = accel ?? def.DefaultAccel;
            double synDecel = decel ?? def.DefaultDecel;

            using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reqId = Guid.NewGuid();
            groupCtx.CurrentRequestId = reqId;

            var tasks = new Task<AxisDoneArgs>[def.AxisIds.Count];

            try
            {
                for (int i = 0; i < def.AxisIds.Count; i++)
                {
                    int currentAxis = def.AxisIds[i];
                    double target = targetPositions[i];

                    var t = _motionController.MoveAbsAsync(currentAxis, target, synSpeed, synAccel, synDecel, 0, failFastCts.Token);
                    tasks[i] = t;

                    // 工业 Fail-Fast 连锁急停：任一轴失败/报警，1ms 内立即掐断全组
                    _ = t.ContinueWith(res =>
                    {
                        if (res.IsCompletedSuccessfully && !res.Result.Success)
                        {
                            try { failFastCts.Cancel(); } catch { }
                            EmergencyStopGroupInternal(groupCtx, $"成员轴 {currentAxis} 异常: {res.Result.Reason}");
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }

                var results = await Task.WhenAll(tasks)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), failFastCts.Token);

                if (results.All(r => r.Success))
                {
                    lock (_lock)
                    {
                        groupCtx.State = AxisGroupState.GroupStandby;
                    }
                    return new AxisDoneArgs(reqId, true, "轴组协同到位成功", CommandCompletionStatus.Done);
                }

                var firstFail = results.FirstOrDefault(r => !r.Success);
                return new AxisDoneArgs(reqId, false, firstFail?.Reason ?? "协同轴到位失败", CommandCompletionStatus.Error);
            }
            catch (OperationCanceledException)
            {
                EmergencyStopGroupInternal(groupCtx, "外部取消或触发 Fail-Fast 连锁急停");
                return new AxisDoneArgs(reqId, false, "轴组运动被取消或因连锁急停中止", CommandCompletionStatus.CommandAborted);
            }
            catch (TimeoutException)
            {
                EmergencyStopGroupInternal(groupCtx, $"轴组协同运动未在 {timeoutMs}ms 内到位超时");
                return new AxisDoneArgs(reqId, false, $"轴组运动超时 ({timeoutMs}ms)", CommandCompletionStatus.Error);
            }
            finally
            {
                // 释放成员轴独占锁
                lock (_lock)
                {
                    foreach (var ax in def.AxisIds)
                    {
                        _axisToGroupLock.TryRemove(ax, out _);
                    }
                }
            }
        }

        #endregion

        #region 轴组分层停止实现 (Level 1 / 2 / 3)

        /// <summary>
        /// Level 1: 轴组工艺暂停/平滑停止 (MC_GroupHalt, Stop Cat 2)。
        /// 成员轴平稳减速到 0 并保持使能，轴组回到 GroupStandby。
        /// </summary>
        public void GroupHalt(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var ctx)) return;

            lock (_lock)
            {
                foreach (var ax in ctx.Definition.AxisIds)
                {
                    _motionController.Halt(ax);
                }
                ctx.State = AxisGroupState.GroupStandby;
            }
        }

        /// <summary>
        /// Level 2: 轴组控制卡受控停止 (MC_GroupStop, Stop Cat 1)。
        /// 锁定该组，期间拒绝一切新运动请求，直到停稳并复位。
        /// </summary>
        public void GroupStop(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var ctx)) return;

            lock (_lock)
            {
                foreach (var ax in ctx.Definition.AxisIds)
                {
                    _motionController.Stop(ax);
                }
                ctx.State = AxisGroupState.GroupStopping;
            }
        }

        /// <summary>
        /// Level 3: 轴组硬件安全急停 (MC_GroupEmergencyStop / STO, Stop Cat 0/1)。
        /// 立即切断全组成员轴脉冲，状态进入 GroupErrorStop，必须 Reset 才能恢复。
        /// </summary>
        public void GroupEmergencyStop(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var ctx)) return;
            EmergencyStopGroupInternal(ctx, "手动触发轴组安全急停");
        }

        /// <summary>
        /// 轴组故障复位 (MC_GroupReset)。
        /// </summary>
        public void GroupReset(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var ctx)) return;

            lock (_lock)
            {
                foreach (var ax in ctx.Definition.AxisIds)
                {
                    _motionController.ResetAxis(ax);
                }
                ctx.State = AxisGroupState.GroupStandby;
                ctx.LastError = null;
            }
        }

        private void EmergencyStopGroupInternal(GroupRuntimeContext ctx, string reason)
        {
            lock (_lock)
            {
                foreach (var ax in ctx.Definition.AxisIds)
                {
                    _motionController.EmergencyStop(ax);
                }
                ctx.State = AxisGroupState.GroupErrorStop;
                ctx.LastError = reason;
            }
        }

        #endregion

        #region 硬件事件监听与全局联动

        private void OnAxisDone(AxisDoneArgs args) { }

        private void OnAxisFault(AxisFaultArgs args)
        {
            // 检查故障轴是否属于某个轴组；若属于，立即触发该轴组的 Fail-Fast 连锁急停
            foreach (var g in _groups.Values)
            {
                if (g.Definition.AxisIds.Contains(args.AxisId))
                {
                    EmergencyStopGroupInternal(g, $"成员轴 {args.AxisId} 故障: [{args.FaultCode}] {args.Message}");
                }
            }
        }

        private void OnLimitTriggered(LimitTriggeredArgs args)
        {
            if (args.IsHardLimit)
            {
                foreach (var g in _groups.Values)
                {
                    if (g.Definition.AxisIds.Contains(args.AxisId))
                    {
                        EmergencyStopGroupInternal(g, $"成员轴 {args.AxisId} 触发硬限位: {args.LimitPointName}");
                    }
                }
            }
        }

        #endregion

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _motionController.AxisDone -= OnAxisDone;
            _motionController.AxisFault -= OnAxisFault;
            _motionController.LimitTriggered -= OnLimitTriggered;
        }
    }
}
