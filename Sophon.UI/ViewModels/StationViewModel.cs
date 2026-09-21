#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Core;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels
{
    /// <summary>
    /// 工站运行概览页视图模型：列出所有已保存流程对应的工站，
    /// 支持启动 / 暂停 / 继续 / 停止，并实时订阅工站状态变更。
    /// </summary>
    public class StationViewModel : BindableBase, INavigationAware
    {
        private readonly IWorkStationFactory _workStationFactory;
        private readonly IWorkStationManager _workStationManager;

        /// <summary>已订阅状态事件的工站 → 处理器，用于退订防重复。</summary>
        private readonly Dictionary<IWorkStation, Action<WorkStationState>> _handlers = new();

        public ObservableCollection<StationItemVm> Stations { get; } = new();

        private StationItemVm? _selectedStation;
        public StationItemVm? SelectedStation
        {
            get => _selectedStation;
            set
            {
                if (SetProperty(ref _selectedStation, value))
                {
                    RaiseCanExecuteChanged();
                }
            }
        }

        private string _statusMessage = "就绪";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand StartCommand { get; }
        public DelegateCommand PauseCommand { get; }
        public DelegateCommand ResumeCommand { get; }
        public DelegateCommand StopCommand { get; }

        public StationViewModel(IWorkStationFactory workStationFactory, IWorkStationManager workStationManager)
        {
            _workStationFactory = workStationFactory ?? throw new ArgumentNullException(nameof(workStationFactory));
            _workStationManager = workStationManager ?? throw new ArgumentNullException(nameof(workStationManager));

            RefreshCommand = new DelegateCommand(ExecuteRefresh);
            StartCommand = new DelegateCommand(ExecuteStart, CanExecuteOnSelected);
            PauseCommand = new DelegateCommand(ExecutePause, CanExecuteOnSelected);
            ResumeCommand = new DelegateCommand(ExecuteResume, CanExecuteOnSelected);
            StopCommand = new DelegateCommand(ExecuteStop, CanExecuteOnSelected);
        }

        private bool CanExecuteOnSelected() => SelectedStation != null;

        private void RaiseCanExecuteChanged()
        {
            StartCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            RefreshStations();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            UnsubscribeAll();
            Stations.Clear();
        }

        private void ExecuteRefresh()
        {
            RefreshStations();
        }

        private void RefreshStations()
        {
            try
            {
                // 1. 从已保存的流程图取流程名
                var flowNames = FlowGraphStore.ListFlowNames();

                // 2. 确保每个流程都有对应工站（已存在则返回缓存）
                foreach (var name in flowNames)
                {
                    _workStationFactory.CreateWorkStation(name);
                }

                // 3. 先退订旧订阅，再重建列表，防止重复订阅
                UnsubscribeAll();
                Stations.Clear();

                // 4. 遍历工站缓存生成列表项，并订阅状态变更
                foreach (var kv in _workStationFactory.WorkStationCache.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var vm = new StationItemVm(kv.Key, kv.Value.CurrentState);
                    Stations.Add(vm);

                    Action<WorkStationState> handler = state => OnStationStateChanged(kv.Value, state);
                    kv.Value.StateChanged += handler;
                    _handlers[kv.Value] = handler;
                }

                StatusMessage = $"已加载 {Stations.Count} 个工站（流程来源：SophonData/flows）";
            }
            catch (Exception ex)
            {
                Growl.Error($"刷新工站列表失败: {ex.Message}");
                StatusMessage = $"刷新失败: {ex.Message}";
            }
        }

        private void UnsubscribeAll()
        {
            foreach (var kv in _handlers)
            {
                kv.Key.StateChanged -= kv.Value;
            }
            _handlers.Clear();
        }

        private void OnStationStateChanged(IWorkStation station, WorkStationState state)
        {
            var vm = Stations.FirstOrDefault(x => x.Name == station.WorkStationName);
            if (vm == null)
            {
                return;
            }

            // 工站状态变更可能来自线程池线程（流程完成/异常），必须切回 UI 线程更新
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                vm.UpdateState(state);
                RaiseCanExecuteChanged();
            }
            else
            {
                dispatcher.Invoke(() =>
                {
                    vm.UpdateState(state);
                    RaiseCanExecuteChanged();
                });
            }
        }

        private void ExecuteStart()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                _workStationManager.Start(name);
                Growl.Info($"工站「{name}」已启动");
            }
            catch (Exception ex)
            {
                Growl.Error($"启动工站「{name}」失败: {ex.Message}");
            }
            RefreshSelectedState();
        }

        private void ExecutePause()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                if (SelectedStation!.State != WorkStationState.Running)
                {
                    Growl.Warning($"工站「{name}」未处于运行状态，无法暂停");
                    return;
                }
                _workStationManager.Pause(name);
                Growl.Info($"工站「{name}」已暂停");
            }
            catch (Exception ex)
            {
                Growl.Error($"暂停工站「{name}」失败: {ex.Message}");
            }
            RefreshSelectedState();
        }

        private void ExecuteResume()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                _workStationManager.Resume(name);
                Growl.Info($"工站「{name}」已继续");
            }
            catch (Exception ex)
            {
                Growl.Error($"继续工站「{name}」失败: {ex.Message}");
            }
            RefreshSelectedState();
        }

        private void ExecuteStop()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            try
            {
                _workStationManager.Stop(name);
                Growl.Info($"工站「{name}」已停止");
            }
            catch (Exception ex)
            {
                Growl.Error($"停止工站「{name}」失败: {ex.Message}");
            }
            RefreshSelectedState();
        }

        private void RefreshSelectedState()
        {
            var selected = SelectedStation;
            if (selected == null)
            {
                return;
            }
            if (_workStationFactory.WorkStationCache.TryGetValue(selected.Name, out var station))
            {
                selected.UpdateState(station.CurrentState);
            }
        }
    }

    /// <summary>
    /// 工站列表项：名称 + 状态中文文案 + 状态颜色。
    /// </summary>
    public class StationItemVm : BindableBase
    {
        public string Name { get; }

        private WorkStationState _state;
        public WorkStationState State
        {
            get => _state;
            private set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(StateText));
                    RaisePropertyChanged(nameof(StateColor));
                }
            }
        }

        public string StateText => State switch
        {
            WorkStationState.Idle => "空闲",
            WorkStationState.Running => "运行",
            WorkStationState.Paused => "暂停",
            WorkStationState.Alarm => "报警",
            _ => State.ToString()
        };

        public string StateColor => State switch
        {
            WorkStationState.Running => "#52C41A",
            WorkStationState.Paused => "#FAAD14",
            WorkStationState.Alarm => "#FF4D4F",
            _ => "#8C8C8C"
        };

        public StationItemVm(string name, WorkStationState state = WorkStationState.Idle)
        {
            Name = name;
            _state = state;
        }

        public void UpdateState(WorkStationState state) => State = state;
    }
}
