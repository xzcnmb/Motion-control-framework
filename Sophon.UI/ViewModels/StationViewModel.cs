#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using HandyControl.Controls;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Core;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels
{
    /// <summary>
    /// 工站页：工站是设备单元，流程图是配方。启动后循环跑绑定的配方，直到停止。
    /// </summary>
    public class StationViewModel : BindableBase, INavigationAware
    {
        private readonly IWorkStationFactory _workStationFactory;
        private readonly IWorkStationManager _workStationManager;
        private readonly WorkStationProfileStore _profileStore;

        private readonly Dictionary<IWorkStation, Action<WorkStationState>> _stateHandlers = new();
        private readonly Dictionary<IWorkStation, Action<int>> _cycleHandlers = new();

        public ObservableCollection<StationItemVm> Stations { get; } = new();
        public ObservableCollection<string> AvailableFlows { get; } = new();

        private StationItemVm? _selectedStation;
        public StationItemVm? SelectedStation
        {
            get => _selectedStation;
            set
            {
                if (SetProperty(ref _selectedStation, value))
                {
                    SyncBindSelection();
                    RaiseCanExecuteChanged();
                }
            }
        }

        private string? _selectedFlowToBind;
        public string? SelectedFlowToBind
        {
            get => _selectedFlowToBind;
            set => SetProperty(ref _selectedFlowToBind, value);
        }

        private string _newStationName = string.Empty;
        public string NewStationName
        {
            get => _newStationName;
            set => SetProperty(ref _newStationName, value);
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
        public DelegateCommand BindRecipeCommand { get; }
        public DelegateCommand AddStationCommand { get; }

        public StationViewModel(
            IWorkStationFactory workStationFactory,
            IWorkStationManager workStationManager,
            WorkStationProfileStore profileStore)
        {
            _workStationFactory = workStationFactory ?? throw new ArgumentNullException(nameof(workStationFactory));
            _workStationManager = workStationManager ?? throw new ArgumentNullException(nameof(workStationManager));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));

            RefreshCommand = new DelegateCommand(RefreshStations);
            StartCommand = new DelegateCommand(ExecuteStart, CanExecuteOnSelected);
            PauseCommand = new DelegateCommand(ExecutePause, CanExecuteOnSelected);
            ResumeCommand = new DelegateCommand(ExecuteResume, CanExecuteOnSelected);
            StopCommand = new DelegateCommand(ExecuteStop, CanExecuteOnSelected);
            BindRecipeCommand = new DelegateCommand(ExecuteBind, CanExecuteOnSelected);
            AddStationCommand = new DelegateCommand(ExecuteAddStation);
        }

        private bool CanExecuteOnSelected() => SelectedStation != null;

        private void RaiseCanExecuteChanged()
        {
            StartCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            BindRecipeCommand.RaiseCanExecuteChanged();
        }

        public void OnNavigatedTo(NavigationContext navigationContext) => RefreshStations();

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            UnsubscribeAll();
            Stations.Clear();
        }

        private void RefreshStations()
        {
            try
            {
                AvailableFlows.Clear();
                foreach (var flow in FlowGraphStore.ListFlowNames().OrderBy(n => n, StringComparer.Ordinal))
                {
                    AvailableFlows.Add(flow);
                }

                var profiles = _profileStore.LoadOrMigrateFromFlows();
                foreach (var p in profiles)
                {
                    var station = _workStationFactory.CreateWorkStation(p.StationName);
                    if (!string.IsNullOrWhiteSpace(p.BoundFlowName)
                        && !string.Equals(station.BoundFlowName, p.BoundFlowName, StringComparison.Ordinal))
                    {
                        try
                        {
                            station.BindRecipe(p.BoundFlowName);
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                }

                UnsubscribeAll();
                Stations.Clear();

                foreach (var kv in _workStationFactory.WorkStationCache.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var vm = new StationItemVm(kv.Value);
                    Stations.Add(vm);

                    Action<WorkStationState> onState = state => OnStationStateChanged(kv.Value, state);
                    Action<int> onCycle = n => OnCycleCompleted(kv.Value, n);
                    kv.Value.StateChanged += onState;
                    kv.Value.CycleCompleted += onCycle;
                    _stateHandlers[kv.Value] = onState;
                    _cycleHandlers[kv.Value] = onCycle;
                }

                StatusMessage = $"工站 {Stations.Count} 个，流程图 {AvailableFlows.Count} 张。启动后循环跑绑定配方，点停止才结束。";
                SyncBindSelection();
            }
            catch (Exception ex)
            {
                Growl.Error($"刷新工站列表失败: {ex.Message}");
                StatusMessage = $"刷新失败: {ex.Message}";
            }
        }

        private void SyncBindSelection()
        {
            if (SelectedStation == null)
            {
                return;
            }

            SelectedFlowToBind = AvailableFlows.FirstOrDefault(f =>
                string.Equals(f, SelectedStation.BoundFlowName, StringComparison.Ordinal))
                ?? SelectedStation.BoundFlowName;
        }

        private void UnsubscribeAll()
        {
            foreach (var kv in _stateHandlers)
            {
                kv.Key.StateChanged -= kv.Value;
            }
            foreach (var kv in _cycleHandlers)
            {
                kv.Key.CycleCompleted -= kv.Value;
            }
            _stateHandlers.Clear();
            _cycleHandlers.Clear();
        }

        private void OnStationStateChanged(IWorkStation station, WorkStationState state)
        {
            RunOnUi(() =>
            {
                Stations.FirstOrDefault(x => x.Name == station.WorkStationName)?.RefreshFrom(station);
                RaiseCanExecuteChanged();
            });
        }

        private void OnCycleCompleted(IWorkStation station, int cycle)
        {
            RunOnUi(() => Stations.FirstOrDefault(x => x.Name == station.WorkStationName)?.RefreshFrom(station));
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        private void ExecuteAddStation()
        {
            string name = (NewStationName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
            {
                Growl.Warning("请先填写工站名称");
                return;
            }

            if (_workStationFactory.WorkStationCache.ContainsKey(name))
            {
                Growl.Warning($"工站「{name}」已存在");
                return;
            }

            string flow = SelectedFlowToBind ?? AvailableFlows.FirstOrDefault() ?? string.Empty;
            _profileStore.Upsert(new WorkStationProfile
            {
                StationName = name,
                BoundFlowName = flow,
                LoopRecipe = true
            });
            _workStationFactory.CreateWorkStation(name);
            if (!string.IsNullOrEmpty(flow))
            {
                _workStationFactory.WorkStationCache[name].BindRecipe(flow);
            }
            NewStationName = string.Empty;
            RefreshStations();
            Growl.Success($"已添加工站「{name}」，配方：{(string.IsNullOrEmpty(flow) ? "未绑定" : flow)}");
        }

        private void ExecuteBind()
        {
            var item = SelectedStation;
            if (item == null)
            {
                return;
            }

            string flow = (SelectedFlowToBind ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(flow))
            {
                Growl.Warning("请选择要绑定的流程图");
                return;
            }

            try
            {
                if (!_workStationFactory.WorkStationCache.TryGetValue(item.Name, out var station))
                {
                    return;
                }

                station.BindRecipe(flow);
                _profileStore.Upsert(new WorkStationProfile
                {
                    StationName = station.WorkStationName,
                    BoundFlowName = flow,
                    LoopRecipe = true
                });
                item.RefreshFrom(station);
                Growl.Success($"工站「{item.Name}」已绑定配方「{flow}」。启动后将循环执行该流程。");
            }
            catch (Exception ex)
            {
                Growl.Error($"绑定失败: {ex.Message}");
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
                if (_workStationFactory.WorkStationCache.TryGetValue(name, out var station)
                    && string.IsNullOrWhiteSpace(station.BoundFlowName))
                {
                    Growl.Warning($"工站「{name}」还没有绑定流程图，请先选择配方。");
                    return;
                }

                _workStationManager.Start(name);
                Growl.Info($"工站「{name}」已启动，循环执行配方「{SelectedStation?.BoundFlowName}」，点停止结束。");
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
                Growl.Info($"工站「{name}」已暂停（节点边界）");
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
                Growl.Info($"工站「{name}」已继续循环");
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
                Growl.Info($"工站「{name}」已停止循环");
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
                selected.RefreshFrom(station);
            }
        }
    }

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

        private string _boundFlowName = string.Empty;
        public string BoundFlowName
        {
            get => _boundFlowName;
            private set
            {
                if (SetProperty(ref _boundFlowName, value))
                {
                    RaisePropertyChanged(nameof(RecipeText));
                }
            }
        }

        private int _cycleCount;
        public int CycleCount
        {
            get => _cycleCount;
            private set => SetProperty(ref _cycleCount, value);
        }

        public string RecipeText => string.IsNullOrWhiteSpace(BoundFlowName) ? "未绑定配方" : BoundFlowName;

        public string StateText => State switch
        {
            WorkStationState.Idle => "空闲",
            WorkStationState.Running => "循环运行",
            WorkStationState.Paused => "暂停",
            WorkStationState.Stopped => "已停止",
            WorkStationState.Alarm => "报警",
            _ => State.ToString()
        };

        public string StateColor => State switch
        {
            WorkStationState.Running => "#52C41A",
            WorkStationState.Paused => "#FAAD14",
            WorkStationState.Alarm => "#FF4D4F",
            WorkStationState.Stopped => "#8C8C8C",
            _ => "#8C8C8C"
        };

        public StationItemVm(IWorkStation station)
        {
            Name = station.WorkStationName;
            RefreshFrom(station);
        }

        public void RefreshFrom(IWorkStation station)
        {
            State = station.CurrentState;
            BoundFlowName = station.BoundFlowName;
            CycleCount = station.CycleCount;
        }
    }
}
