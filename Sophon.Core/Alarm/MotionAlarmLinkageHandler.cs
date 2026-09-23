#nullable enable
using System;
using System.Text.RegularExpressions;
using Sophon.Core;
using Sophon.Infrastructure.Motion.Axis;

namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 运动报警联动。EStopAll / StopFlow 必须走工站管理器停所有工站，不能打到未注册的单工站或幽灵状态机。
    /// 急停本身仍必须硬接线；这里只停软件循环 + AbortAll。
    /// </summary>
    public class MotionAlarmLinkageHandler : IAlarmLinkageHandler
    {
        private readonly AxisManager? _axisManager;
        private readonly IWorkStationManager? _workStationManager;

        public MotionAlarmLinkageHandler(
            AxisManager? axisManager = null,
            IWorkStationManager? workStationManager = null,
            IWorkStation? workStation = null,
            IStateMachine? stateMachine = null)
        {
            _axisManager = axisManager;
            _workStationManager = workStationManager;
            _ = workStation;
            _ = stateMachine;
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
                        _axisManager?.StopAll();
                    }
                    break;

                case LinkageMode.StopAllAxes:
                    // StopAllAxes 是 Cat1 受控停；只有 EStopAll 才允许进入 AbortAll/ErrorStop。
                    _axisManager?.StopAll();
                    break;

                case LinkageMode.StopFlow:
                    _workStationManager?.StopAll();
                    break;

                case LinkageMode.EStopAll:
                    _axisManager?.AbortAll();
                    _workStationManager?.StopAll();
                    break;
            }
        }

        private static int? ParseAxisId(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return null;

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
