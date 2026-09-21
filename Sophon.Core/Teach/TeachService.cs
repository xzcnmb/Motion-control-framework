#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.Core.Teach
{
    /// <summary>
    /// 示教服务。
    /// 职责：
    /// 1. CaptureCurrent：从 AxisManager 快照捕获当前各轴物理位置，并执行软限位安全校验；
    /// 2. RunToPoint：逐轴下发 MoveAbs，通过 await AxisDone 异步等待所有轴到位完成（禁止轮询位置）；
    /// 3. RunGroup：按点位组列表顺序依次调用 RunToPoint 执行示教轨迹；
    /// 4. 点位与点位组的 CRUD 委托给 TeachPointStore。
    /// </summary>
    public class TeachService
    {
        private readonly AxisManager _axisManager;
        private readonly TeachPointStore _store;
        private readonly IReadOnlyList<AxisDefinition> _axisDefinitions;

        /// <summary>示教点运行整体超时（ms）。防止某轴永不回报导致 WhenAll 永久挂起。</summary>
        private const int _runTimeoutMs = 60000;

        public TeachPointStore Store => _store;

        public TeachService(
            AxisManager axisManager,
            TeachPointStore? store = null,
            IReadOnlyList<AxisDefinition>? axisDefinitions = null)
        {
            _axisManager = axisManager ?? throw new ArgumentNullException(nameof(axisManager));
            _store = store ?? new TeachPointStore();
            _axisDefinitions = axisDefinitions ?? _axisManager.Controller?.Axes ?? new List<AxisDefinition>();
        }

        /// <summary>
        /// 从当前所有轴的快照中捕获坐标，并校验软限位范围。
        /// 若任一轴超出软限位，则抛出 InvalidOperationException 拒绝保存。
        /// </summary>
        /// <param name="name">点位名称</param>
        /// <param name="group">所属分组</param>
        /// <param name="speed">运行速度</param>
        /// <param name="description">描述</param>
        /// <returns>创建并持久化的示教点对象</returns>
        public TeachPoint CaptureCurrent(string name, string group = "Default", double speed = 20.0, string description = "")
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("点位名称不能为空", nameof(name));
            }

            var snapshots = _axisManager.GetSnapshots();
            if (snapshots == null || snapshots.Count == 0)
            {
                throw new InvalidOperationException("无法获取轴快照，请确保 AxisManager 正常运行");
            }

            var posDict = new Dictionary<int, double>();
            foreach (var s in snapshots)
            {
                // 软限位校验
                var def = _axisDefinitions.FirstOrDefault(d => d.AxisId == s.AxisId);
                if (def != null && def.SoftLimitEnabled)
                {
                    if (s.Position > def.SoftLimitMax || s.Position < def.SoftLimitMin)
                    {
                        throw new InvalidOperationException(
                            $"轴 {s.AxisId} 当前位置 {s.Position:F3} 超出软限位范围 [{def.SoftLimitMin:F3}, {def.SoftLimitMax:F3}]，拒绝保存示教点！");
                    }
                }

                posDict[s.AxisId] = s.Position;
            }

            var point = new TeachPoint
            {
                Name = name,
                Group = group,
                AxisPositions = posDict,
                Speed = speed,
                Description = description,
                CreateTime = DateTime.Now
            };

            _store.SaveOrUpdatePoint(point);
            return point;
        }

        /// <summary>
        /// 校验给定点位各轴是否都在软限位安全范围内。
        /// </summary>
        public bool ValidatePointLimits(TeachPoint point, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (point == null)
            {
                errorMessage = "点位对象为空";
                return false;
            }

            foreach (var kv in point.AxisPositions)
            {
                int axisId = kv.Key;
                double pos = kv.Value;

                var def = _axisDefinitions.FirstOrDefault(d => d.AxisId == axisId);
                if (def != null && def.SoftLimitEnabled)
                {
                    if (pos > def.SoftLimitMax || pos < def.SoftLimitMin)
                    {
                        errorMessage = $"轴 {axisId} 目标位置 {pos:F3} 超出软限位范围 [{def.SoftLimitMin:F3}, {def.SoftLimitMax:F3}]";
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// 运行至指定示教点。
        /// 严格遵守契约铁律：等待底层 AxisDone 事件判定到位，禁止轮询位置。
        /// </summary>
        /// <param name="point">目标点位</param>
        /// <param name="overrideSpeed">覆盖速度，若为 null 则使用点位自带的速度</param>
        /// <param name="ct">取消令牌</param>
        public async Task RunToPointAsync(TeachPoint point, double? overrideSpeed = null, CancellationToken ct = default)
        {
            if (point == null) throw new ArgumentNullException(nameof(point));

            if (!ValidatePointLimits(point, out var errMsg))
            {
                throw new InvalidOperationException($"示教点限位校验失败: {errMsg}");
            }

            double speed = overrideSpeed ?? point.Speed;
            if (speed <= 0) speed = 20.0;

            // 无竞态：AxisManager.MoveAbsAsync 转发底层控制器的异步完成回报（TCS 在下发前登记），
            // 快速失败/停止/断线都会以 Success=false 完成任务，禁止轮询位置。
            // WaitAsync 时限独占超时判定；外部取消经 ct 传播为 OperationCanceledException。
            var axisIds = new List<int>();
            var tasks = new List<Task<AxisDoneArgs>>();

            foreach (var kv in point.AxisPositions)
            {
                int axisId = kv.Key;
                double target = kv.Value;

                var def = _axisDefinitions.FirstOrDefault(d => d.AxisId == axisId);
                double accel = def?.MaxAccel ?? 500.0;
                double decel = def?.MaxDecel ?? 500.0;
                double jerk = def?.MaxJerk ?? 2000.0;

                axisIds.Add(axisId);
                tasks.Add(_axisManager.MoveAbsAsync(axisId, target, speed, accel, decel, jerk, ct));
            }

            try
            {
                var results = await Task.WhenAll(tasks)
                    .WaitAsync(TimeSpan.FromMilliseconds(_runTimeoutMs), ct);

                for (int i = 0; i < results.Length; i++)
                {
                    if (!results[i].Success)
                    {
                        throw new InvalidOperationException($"轴 {axisIds[i]} 运行到目标点失败: {results[i].Reason}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                foreach (var axisId in axisIds) { try { _axisManager.StopMotion(axisId); } catch { } }
                throw new OperationCanceledException($"示教点 '{point.Name}' 运行被取消");
            }
            catch (TimeoutException)
            {
                foreach (var axisId in axisIds) { try { _axisManager.StopMotion(axisId); } catch { } }
                throw new TimeoutException($"示教点 '{point.Name}' 运行未在 {_runTimeoutMs}ms 内全部到位");
            }
        }

        /// <summary>
        /// 运行指定点位组（多点连续轨迹）。
        /// </summary>
        public async Task RunGroupAsync(TeachPointGroup group, double? overrideSpeed = null, CancellationToken ct = default)
        {
            if (group == null) throw new ArgumentNullException(nameof(group));

            foreach (var pointId in group.PointIds)
            {
                ct.ThrowIfCancellationRequested();

                var point = _store.GetPoint(pointId);
                if (point == null)
                {
                    throw new KeyNotFoundException($"点位组 '{group.Name}' 中不存在点位 ID: {pointId}");
                }

                await RunToPointAsync(point, overrideSpeed, ct);
            }
        }
    }
}
