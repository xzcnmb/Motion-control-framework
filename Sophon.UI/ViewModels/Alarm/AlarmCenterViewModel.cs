#nullable enable
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using Prism.Commands;
using Prism.Mvvm;
using Sophon.Core.Alarm;
using WpfApplication = System.Windows.Application;

namespace Sophon.UI.ViewModels.Alarm
{
    public class AlarmItemVm : BindableBase
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public AlarmSeverity Severity { get; set; }
        public LinkageMode LinkageMode { get; set; }
        public string Detail { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime TriggerTime { get; set; }

        public string SeverityText => Severity switch
        {
            AlarmSeverity.Info => "提示",
            AlarmSeverity.Warning => "警告",
            AlarmSeverity.Error => "错误",
            AlarmSeverity.Stop => "停机",
            AlarmSeverity.EStop => "急停",
            _ => "未知"
        };

        public string SeverityColor => Severity switch
        {
            AlarmSeverity.Info => "#1890FF",      // 蓝色
            AlarmSeverity.Warning => "#FAAD14",   // 黄色
            AlarmSeverity.Error => "#FF4D4F",     // 红色
            AlarmSeverity.Stop => "#D4380D",      // 深橙红
            AlarmSeverity.EStop => "#780650",     // 紫红
            _ => "#8C8C8C"
        };
    }

    public class AlarmCenterViewModel : BindableBase, Prism.Navigation.Regions.INavigationAware
    {
        private readonly AlarmCenter _alarmCenter;
        private bool _isSubscribed;

        public ObservableCollection<AlarmItemVm> ActiveAlarms { get; } = new();
        public ObservableCollection<AlarmHistoryRecord> HistoryRecords { get; } = new();

        private AlarmItemVm? _selectedAlarm;
        public AlarmItemVm? SelectedAlarm
        {
            get => _selectedAlarm;
            set => SetProperty(ref _selectedAlarm, value);
        }

        private DateTime? _startDate;
        public DateTime? StartDate
        {
            get => _startDate;
            set => SetProperty(ref _startDate, value);
        }

        private DateTime? _endDate;
        public DateTime? EndDate
        {
            get => _endDate;
            set => SetProperty(ref _endDate, value);
        }

        public DelegateCommand ClearSelectedAlarmCommand { get; }
        public DelegateCommand ClearAllAlarmsCommand { get; }
        public DelegateCommand QueryHistoryCommand { get; }
        public DelegateCommand ClearHistoryCommand { get; }

        public AlarmCenterViewModel(AlarmCenter alarmCenter)
        {
            _alarmCenter = alarmCenter ?? throw new ArgumentNullException(nameof(alarmCenter));

            ClearSelectedAlarmCommand = new DelegateCommand(OnClearSelectedAlarm, () => SelectedAlarm != null)
                .ObservesProperty(() => SelectedAlarm);

            ClearAllAlarmsCommand = new DelegateCommand(OnClearAllAlarms);
            QueryHistoryCommand = new DelegateCommand(OnQueryHistory);
            ClearHistoryCommand = new DelegateCommand(OnClearHistory);

            Subscribe();
            RefreshActiveAlarms();
            OnQueryHistory();
        }

        private void Subscribe()
        {
            if (_isSubscribed) return;
            _isSubscribed = true;
            _alarmCenter.AlarmRaised += OnAlarmRaised;
            _alarmCenter.AlarmCleared += OnAlarmCleared;
        }

        private void Unsubscribe()
        {
            if (!_isSubscribed) return;
            _isSubscribed = false;
            _alarmCenter.AlarmRaised -= OnAlarmRaised;
            _alarmCenter.AlarmCleared -= OnAlarmCleared;
        }

        public void OnNavigatedTo(Prism.Navigation.Regions.NavigationContext navigationContext)
        {
            Subscribe();
            RefreshActiveAlarms();
        }

        public bool IsNavigationTarget(Prism.Navigation.Regions.NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(Prism.Navigation.Regions.NavigationContext navigationContext)
        {
            Unsubscribe();
        }

        private void OnAlarmRaised(ActiveAlarm alarm)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                var existing = ActiveAlarms.FirstOrDefault(a => a.Code == alarm.Code);
                if (existing != null)
                {
                    ActiveAlarms.Remove(existing);
                }

                ActiveAlarms.Insert(0, new AlarmItemVm
                {
                    Code = alarm.Code,
                    Message = alarm.Message,
                    Severity = alarm.Severity,
                    LinkageMode = alarm.LinkageMode,
                    Detail = alarm.Detail,
                    Source = alarm.Source,
                    TriggerTime = alarm.TriggerTime
                });
            });
        }

        private void OnAlarmCleared(string code)
        {
            WpfApplication.Current?.Dispatcher?.Invoke(() =>
            {
                var existing = ActiveAlarms.FirstOrDefault(a => a.Code == code);
                if (existing != null)
                {
                    ActiveAlarms.Remove(existing);
                }
            });
        }

        private void RefreshActiveAlarms()
        {
            ActiveAlarms.Clear();
            foreach (var a in _alarmCenter.ActiveAlarms.OrderByDescending(a => a.TriggerTime))
            {
                ActiveAlarms.Add(new AlarmItemVm
                {
                    Code = a.Code,
                    Message = a.Message,
                    Severity = a.Severity,
                    LinkageMode = a.LinkageMode,
                    Detail = a.Detail,
                    Source = a.Source,
                    TriggerTime = a.TriggerTime
                });
            }
        }

        private void OnClearSelectedAlarm()
        {
            if (SelectedAlarm != null)
            {
                _alarmCenter.Clear(SelectedAlarm.Code);
            }
        }

        private void OnClearAllAlarms()
        {
            var codes = ActiveAlarms.Select(a => a.Code).ToList();
            foreach (var code in codes)
            {
                _alarmCenter.Clear(code);
            }
        }

        private void OnQueryHistory()
        {
            HistoryRecords.Clear();
            var list = _alarmCenter.GetHistory(StartDate, EndDate);
            foreach (var r in list.OrderByDescending(x => x.TriggerTime))
            {
                HistoryRecords.Add(r);
            }
        }

        private void OnClearHistory()
        {
            _alarmCenter.ClearHistory();
            HistoryRecords.Clear();
        }
    }
}
