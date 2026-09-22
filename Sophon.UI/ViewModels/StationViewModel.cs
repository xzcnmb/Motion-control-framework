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
    /// 工站页：工站 = 设备单元，流程图 = 配方，轴组 = 本组要停的那些轴。
    /// </summary>
    public class StationViewModel : BindableBase, INavigationAware
    {
        private readonly IWorkStationFactory _workStationFactory;
        private readonly IWorkStationManager _workStationManager;
        private readonly WorkStationProfileStore _profileStore;
        private readonly AxisGroupStore _axisGroupStore;

        private readonly Dictionary<IWorkStation, Action<WorkStationState>> _stateHandlers = new();
        private readonly Dictionary<IWorkStation, Action<int>> _cycleHandlers = new();

        public ObservableCollection<StationItemVm> Stations { get; } = new();
        public ObservableCollection<string> AvailableFlows { get; } = new();
        public ObservableCollection<string> AvailableGroups { get; } = new();

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

        private string? _selectedGroupToBind;
        public string? SelectedGroupToBind
        {
            get => _selectedGroupToBind;
            set => SetProperty(ref _selectedGroupToBind, value);
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
        public DelegateCommand BindAxisGroupCommand { get; }
        public DelegateCommand AddStationCommand { get; }
        public DelegateCommand ResetCommand { get; }

        public StationViewModel(
            IWorkStationFactory workStationFactory,
            IWorkStationManager workStationManager,
            WorkStationProfileStore profileStore,
            AxisGroupStore axisGroupStore)
        {
            _workStationFactory = workStationFactory ?? throw new ArgumentNullException(nameof(workStationFactory));
            _workStationManager = workStationManager ?? throw new ArgumentNullException(nameof(workStationManager));
            _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
            _axisGroupStore = axisGroupStore ?? throw new ArgumentNullException(nameof(axisGroupStore));

            RefreshCommand = new DelegateCommand(RefreshStations);
            StartCommand = new DelegateCommand(ExecuteStart, CanExecuteOnSelected);
            PauseCommand = new DelegateCommand(ExecutePause, CanExecuteOnSelected);
            ResumeCommand = new DelegateCommand(ExecuteResume, CanExecuteOnSelected);
            StopCommand = new DelegateCommand(ExecuteStop, CanExecuteOnSelected);
            BindRecipeCommand = new DelegateCommand(ExecuteBind, CanExecuteOnSelected);
            BindAxisGroupCommand = new DelegateCommand(ExecuteBindGroup, CanExecuteOnSelected);
            AddStationCommand = new DelegateCommand(ExecuteAddStation);
            ResetCommand = new DelegateCommand(ExecuteReset, CanExecuteOnSelected);
        }

        private bool CanExecuteOnSelected() => SelectedStation != null;

        private void RaiseCanExecuteChanged()
        {
            StartCommand.RaiseCanExecuteChanged();
            PauseCommand.RaiseCanExecuteChanged();
            ResumeCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
            BindRecipeCommand.RaiseCanExecuteChanged();
            BindAxisGroupCommand.RaiseCanExecuteChanged();
            ResetCommand.RaiseCanExecuteChanged();
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

                AvailableGroups.Clear();
                foreach (var g in _axisGroupStore.Load().OrderBy(x => x.GroupName, StringComparer.Ordinal))
                {
                    AvailableGroups.Add(g.GroupName);
                }

                var profiles = _profileStore.LoadOrMigrateFromFlows();
                foreach (var p in profiles)
                {
                    var station = _workStationFactory.CreateWorkStation(p.StationName);
                    if (!string.IsNullOrWhiteSpace(p.BoundFlowName)
                        && !string.Equals(station.BoundFlowName, p.BoundFlowName, StringComparison.Ordinal))
                    {
                        try { station.BindRecipe(p.BoundFlowName); }
                        catch (InvalidOperationException) { }
                    }
                    if (!string.IsNullOrWhiteSpace(p.AxisGroupName)
                        && !string.Equals(station.AxisGroupName, p.AxisGroupName, StringComparison.Ordinal))
                    {
                        try { station.BindAxisGroup(p.AxisGroupName); }
                        catch (InvalidOperationException) { }
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

                StatusMessage = $"工站 {Stations.Count} 个，流程图 {AvailableFlows.Count} 张，轴组 {AvailableGroups.Count} 个。停止只停本组轴。";
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
            if (SelectedStation == null) return;
            SelectedFlowToBind = AvailableFlows.FirstOrDefault(f =>
                string.Equals(f, SelectedStation.BoundFlowName, StringComparison.Ordinal))
                ?? SelectedStation.BoundFlowName;
            SelectedGroupToBind = AvailableGroups.FirstOrDefault(g =>
                string.Equals(g, SelectedStation.AxisGroupName, StringComparison.Ordinal))
                ?? SelectedStation.AxisGroupName;
        }

        private void UnsubscribeAll()
        {
            foreach (var kv in _stateHandlers) kv.Key.StateChanged -= kv.Value;
            foreach (var kv in _cycleHandlers) kv.Key.CycleCompleted -= kv.Value;
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
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        private void Persist(IWorkStation station)
        {
            _profileStore.Upsert(new WorkStationProfile
            {
                StationName = station.WorkStationName,
                BoundFlowName = station.BoundFlowName,
                LoopRecipe = true,
                AxisGroupName = station.AxisGroupName
            });
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
            string group = SelectedGroupToBind ?? string.Empty;
            _profileStore.Upsert(new WorkStationProfile
            {
                StationName = name,
                BoundFlowName = flow,
                LoopRecipe = true,
                AxisGroupName = group
            });
            var station = _workStationFactory.CreateWorkStation(name);
            if (!string.IsNullOrEmpty(flow)) station.BindRecipe(flow);
            if (!string.IsNullOrEmpty(group))
            {
                try { station.BindAxisGroup(group); }
                catch (InvalidOperationException ex) { Growl.Warning(ex.Message); }
            }
            NewStationName = string.Empty;
            RefreshStations();
            Growl.Success($"已添加工站「{name}」");
        }

        private void ExecuteBind()
        {
            var item = SelectedStation;
            if (item == null) return;
            string flow = (SelectedFlowToBind ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(flow))
            {
                Growl.Warning("请选择要绑定的流程图");
                return;
            }
            try
            {
                if (!_workStationFactory.WorkStationCache.TryGetValue(item.Name, out var station)) return;
                station.BindRecipe(flow);
                Persist(station);
                item.RefreshFrom(station);
                Growl.Success($"工站「{item.Name}」已绑定配方「{flow}」");
            }
            catch (Exception ex)
            {
                Growl.Error($"绑定失败: {ex.Message}");
            }
        }

        private void ExecuteBindGroup()
        {
            var item = SelectedStation;
            if (item == null) return;
            string group = (SelectedGroupToBind ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(group))
            {
                Growl.Warning("请选择要绑定的轴组");
                return;
            }
            try
            {
                if (!_workStationFactory.WorkStationCache.TryGetValue(item.Name, out var station)) return;
                station.BindAxisGroup(group);
                Persist(station);
                item.RefreshFrom(station);
                Growl.Success($"工站「{item.Name}」已绑定轴组「{group}」。停止时只停本组轴。");
            }
            catch (Exception ex)
            {
                Growl.Error($"绑定轴组失败: {ex.Message}");
            }
        }

        private void ExecuteStart()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                if (_workStationFactory.WorkStationCache.TryGetValue(name, out var station))
                {
                    if (string.IsNullOrWhiteSpace(station.BoundFlowName))
                    {
                        Growl.Warning($"工站「{name}」还没有绑定流程图");
                        return;
                    }
                    if (station.BoundAxisIds.Count == 0)
                    {
                        Growl.Warning($"工站「{name}」还没有绑定轴组（或轴组为空），停止时无法只停本组轴。");
                        return;
                    }
                }
                _workStationManager.Start(name);
                if (_workStationFactory.WorkStationCache.TryGetValue(name, out var after)
                    && after.CurrentState != WorkStationState.Running)
                {
                    Growl.Warning($"工站「{name}」未进入运行（当前 {after.CurrentState}）。暂停请点继续；上一轮未退出请稍后再启动。");
                    RefreshSelectedState();
                    return;
                }
                Growl.Info($"工站「{name}」已启动循环");
            }
            catch (InvalidOperationException ex) { Growl.Warning(ex.Message); }
            catch (Exception ex) { Growl.Error($"启动失败: {ex.Message}"); }
            RefreshSelectedState();
        }

        private void ExecutePause()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                if (SelectedStation!.State != WorkStationState.Running)
                {
                    Growl.Warning($"工站「{name}」未在运行");
                    return;
                }
                _workStationManager.Pause(name);
                Growl.Info($"工站「{name}」已暂停（节点边界）");
            }
            catch (Exception ex) { Growl.Error($"暂停失败: {ex.Message}"); }
            RefreshSelectedState();
        }

        private void ExecuteResume()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                _workStationManager.Resume(name);
                Growl.Info($"工站「{name}」已继续");
            }
            catch (Exception ex) { Growl.Error($"继续失败: {ex.Message}"); }
            RefreshSelectedState();
        }

        private void ExecuteStop()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                _workStationManager.Stop(name);
                Growl.Info($"工站「{name}」已停止（只停本组轴）");
            }
            catch (Exception ex) { Growl.Error($"停止失败: {ex.Message}"); }
            RefreshSelectedState();
        }

        private void ExecuteReset()
        {
            var name = SelectedStation?.Name;
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                _workStationManager.Reset(name);
                Growl.Info($"工站「{name}」已复位");
            }
            catch (Exception ex) { Growl.Error($"复位失败: {ex.Message}"); }
            RefreshSelectedState();
        }

        private void RefreshSelectedState()
        {
            var selected = SelectedStation;
            if (selected == null) return;
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
                    RaisePropertyChanged(nameof(RecipeText));
            }
        }

        private string _axisGroupName = string.Empty;
        public string AxisGroupName
        {
            get => _axisGroupName;
            private set
            {
                if (SetProperty(ref _axisGroupName, value))
                    RaisePropertyChanged(nameof(AxisGroupText));
            }
        }

        private int _cycleCount;
        public int CycleCount
        {
            get => _cycleCount;
            private set => SetProperty(ref _cycleCount, value);
        }

        public string RecipeText => string.IsNullOrWhiteSpace(BoundFlowName) ? "未绑定配方" : BoundFlowName;
        public string AxisGroupText => string.IsNullOrWhiteSpace(AxisGroupName) ? "未绑定轴组" : AxisGroupName;

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
            AxisGroupName = station.AxisGroupName;
            CycleCount = station.CycleCount;
        }
    }
}
