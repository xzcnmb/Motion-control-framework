#nullable enable
using System;
using System.Text.RegularExpressions;
using Sophon.Core;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 默认运动报警联动处理器。
    /// StopAxis=按 AxisId 停该轴、StopAllAxes=AbortAll、EStopAll=AbortAll + 置 WorkStation Alarm。
    /// </summary>
    public class MotionAlarmLinkageHandler : IAlarmLinkageHandler
    {
        private readonly AxisManager? _axisManager;
        private readonly IWorkStation? _workStation;
        private readonly IStateMachine? _stateMachine;

        public MotionAlarmLinkageHandler(
            AxisManager? axisManager = null,
            IWorkStation? workStation = null,
            IStateMachine? stateMachine = null)
        {
            _axisManager = axisManager;
            _workStation = workStation;
            _stateMachine = stateMachine;
        }

        public virtual void Handle(AlarmDefinition def, string detail)
        {
            switch (def.LinkageMode)
            {
                case LinkageMode.None:
                    break;

                case LinkageMode.StopAxis:
                    int? axisId = ParseAxisId(detail);
                    if (axisId.HasValue && _axisManager != null)
                    {
                        _axisManager.StopMotion(axisId.Value);
                    }
                    else
                    {
                        // 若未解析出单轴，降级安全减速停所有轴
                        _axisManager?.StopAll();
                    }
                    break;

                case LinkageMode.StopAllAxes:
                    _axisManager?.AbortAll();
                    break;

                case LinkageMode.StopFlow:
                    _workStation?.Stop();
                    break;

                case LinkageMode.EStopAll:
                    _axisManager?.AbortAll();
                    if (_stateMachine != null)
                    {
                        _stateMachine.SetState(WorkStationState.Alarm, $"{def.Code}: {def.Message} [{detail}]");
                    }
                    else if (_workStation != null)
                    {
                        _workStation.Stop();
                    }
                    break;
            }
        }

        private static int? ParseAxisId(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return null;

            // 匹配 "Axis: 0", "AxisId=1", "轴0", "0" 等
            var match = Regex.Match(detail, @"(?:Axis(?:Id)?[:=\s]*|轴\s*)(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
            {
                return id;
            }

            if (int.TryParse(detail.Trim(), out int directId))
            {
                return directId;
            }

            return null;
        }
    }
}
