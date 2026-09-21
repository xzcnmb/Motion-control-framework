#nullable enable
using System;
using Sophon.Application;
using Sophon.Core.Alarm;

namespace Sophon.Application.Services.Alarm
{
    /// <summary>
    /// AlarmCenter 与现有 IAlarmRepository 的桥接服务。
    /// 报警发生时同步调用 IAlarmRepository.Alarm(code) 让旧 UI 列表可见；
    /// 报警清除时同步调用 ClearAlarm(code)。（只调用，不改动已有 Repository 实现）。
    /// </summary>
    public class AlarmRepositoryBridge : IDisposable
    {
        private readonly AlarmCenter _alarmCenter;
        private readonly IAlarmRepository? _alarmRepository;
        private bool _disposed;

        public AlarmRepositoryBridge(AlarmCenter alarmCenter, IAlarmRepository? alarmRepository = null)
        {
            _alarmCenter = alarmCenter ?? throw new ArgumentNullException(nameof(alarmCenter));
            _alarmRepository = alarmRepository;

            _alarmCenter.AlarmRaised += OnAlarmRaised;
            _alarmCenter.AlarmCleared += OnAlarmCleared;
        }

        private void OnAlarmRaised(ActiveAlarm alarm)
        {
            if (_alarmRepository == null) return;

            try
            {
                _alarmRepository.Alarm(alarm.Code);
            }
            catch { }
        }

        private void OnAlarmCleared(string code)
        {
            if (_alarmRepository == null) return;

            try
            {
                _alarmRepository.ClearAlarm(code);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _alarmCenter.AlarmRaised -= OnAlarmRaised;
            _alarmCenter.AlarmCleared -= OnAlarmCleared;
        }
    }
}
