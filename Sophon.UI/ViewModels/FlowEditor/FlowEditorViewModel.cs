#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Common;
using HandyControl.Controls;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Navigation.Regions;
using Sophon.Contracts;
using Sophon.Core;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class FlowEditorViewModel : BindableBase, IDisposable, INavigationAware
    {
        private readonly IMotionController? _motionController;
        private readonly IIoController? _ioController;
        private readonly IEventBus? _eventBus;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly IServiceProvider? _services;

        private FlowEngineV2? _engine;
        private CancellationTokenSource? _runCts;
        private bool _isDisposed;

        private string _flowName = "未命名流程";
        public string FlowName
        {
            get => _flowName;
            set => SetProperty(ref _flowName, value);
        }

        public ObservableCollection<FlowNodeViewModel> Nodes { get; } = new();
        public ObservableCollection<FlowConnectionViewModel> Connections { get; } = new();
        public PendingConnectionViewModel PendingConnection { get; }

        public ObservableCollection<ToolboxGroupViewModel> ToolboxGroups { get; } = new();
        public ObservableCollection<string> SavedFlowNames { get; } = new();
        public ObservableCollection<FlowLogItemViewModel> RunLogs { get; } = new();

        private string? _selectedSavedFlow;
        public string? SelectedSavedFlow
        {
            get => _selectedSavedFlow;
            set
            {
                if (SetProperty(ref _selectedSavedFlow, value) && !string.IsNullOrWhiteSpace(value))
                {
                    OpenSavedFlow(value);
                }
            }
        }

        private FlowNodeViewModel? _selectedNode;
        public FlowNodeViewModel? SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    foreach (var n in Nodes)
                    {
                        n.IsSelected = ReferenceEquals(n, value);
                    }
                    value?.ReloadPointParameters();
                    RaisePropertyChanged(nameof(HasSelectedNode));
                }
            }
        }

        public bool HasSelectedNode => SelectedNode != null;

        private string _statusHint = "选中节点后可在右侧改参数，改完点「保存」写入流程图。";
        public string StatusHint
        {
            get => _statusHint;
            set => SetProperty(ref _statusHint, value);
        }

        private Point _viewportLocation;
        public Point ViewportLocation
        {
            get => _viewportLocation;
            set => SetProperty(ref _viewportLocation, value);
        }

        private double _viewportZoom = 1.0;
        public double ViewportZoom
        {
            get => _viewportZoom;
            set => SetProperty(ref _viewportZoom, value);
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    RaisePropertyChanged(nameof(CanRun));
                    RaisePropertyChanged(nameof(CanPause));
                    RaisePropertyChanged(nameof(CanResume));
                    RaisePropertyChanged(nameof(CanStop));
                }
            }
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (SetProperty(ref _isPaused, value))
                {
                    RaisePropertyChanged(nameof(CanPause));
                    RaisePropertyChanged(nameof(CanResume));
                }
            }
        }

        public bool CanRun => !IsRunning;
        public bool CanPause => IsRunning && !IsPaused;
        public bool CanResume => IsRunning && IsPaused;
        public bool CanStop => IsRunning;

        // 命令
        public DelegateCommand NewFlowCommand { get; }
        public DelegateCommand OpenFlowCommand { get; }
        public DelegateCommand SaveFlowCommand { get; }
        public DelegateCommand ValidateFlowCommand { get; }
        public DelegateCommand ImportV1Command { get; }
        public DelegateCommand<ToolboxItemViewModel> AddNodeCommand { get; }
        public DelegateCommand DeleteSelectedCommand { get; }
        public DelegateCommand RunFlowCommand { get; }
        public DelegateCommand PauseFlowCommand { get; }
        public DelegateCommand ResumeFlowCommand { get; }
        public DelegateCommand StopFlowCommand { get; }
        public DelegateCommand ClearLogsCommand { get; }

        public FlowEditorViewModel(
            IMotionController? motionController = null,
            IIoController? ioController = null,
            IEventBus? eventBus = null,
            ILoggerFactory? loggerFactory = null,
            IServiceProvider? services = null)
        {
            _motionController = motionController;
            _ioController = ioController;
            _eventBus = eventBus;
            _loggerFactory = loggerFactory;
            _services = services;

            PendingConnection = new PendingConnectionViewModel(this);

            NewFlowCommand = new DelegateCommand(ExecuteNewFlow);
            OpenFlowCommand = new DelegateCommand(ExecuteOpenFlow);
            SaveFlowCommand = new DelegateCommand(ExecuteSaveFlow);
            ValidateFlowCommand = new DelegateCommand(ExecuteValidateFlow);
            ImportV1Command = new DelegateCommand(ExecuteImportV1);
            AddNodeCommand = new DelegateCommand<ToolboxItemViewModel>(ExecuteAddNode);
            DeleteSelectedCommand = new DelegateCommand(ExecuteDeleteSelected);
            RunFlowCommand = new DelegateCommand(async () => await ExecuteRunFlowAsync(), () => CanRun).ObservesProperty(() => CanRun);
            PauseFlowCommand = new DelegateCommand(async () => await ExecutePauseFlowAsync(), () => CanPause).ObservesProperty(() => CanPause);
            ResumeFlowCommand = new DelegateCommand(async () => await ExecuteResumeFlowAsync(), () => CanResume).ObservesProperty(() => CanResume);
            StopFlowCommand = new DelegateCommand(async () => await ExecuteStopFlowAsync(), () => CanStop).ObservesProperty(() => CanStop);
            ClearLogsCommand = new DelegateCommand(() => RunLogs.Clear());

            InitializeToolbox();
            RefreshSavedFlows();
            ExecuteNewFlow();
        }

        private void InitializeToolbox()
        {
            ToolboxGroups.Clear();

            var categoryDict = new Dictionary<string, ToolboxGroupViewModel>(StringComparer.OrdinalIgnoreCase)
            {
                ["基础控制"] = new("基础控制"),
                ["逻辑流程"] = new("逻辑流程"),
                ["运动控制"] = new("运动控制"),
                ["数字与IO"] = new("数字与IO"),
                ["事件协作"] = new("事件协作")
            };

            foreach (var group in categoryDict.Values)
            {
                ToolboxGroups.Add(group);
            }

            // 动态读取 FlowNodeRegistry 全部内建与扩展节点
            foreach (var nodeType in FlowNodeRegistry.RegisteredTypes)
            {
                var baseNode = FlowNodeRegistry.Create(nodeType);
                if (baseNode == null) continue;

                string category = FlowNodeViewModel.GetCategoryForType(nodeType);
                if (!categoryDict.TryGetValue(category, out var group))
                {
                    group = new ToolboxGroupViewModel(category);
                    categoryDict[category] = group;
                    ToolboxGroups.Add(group);
                }

                group.Items.Add(new ToolboxItemViewModel
                {
                    NodeType = nodeType,
                    Name = baseNode.Name,
                    Category = category,
                    Description = $"{baseNode.Name} ({nodeType})",
                    CategoryBrush = FlowNodeViewModel.GetBrushForCategory(category)
                });
            }
        }

        public void RefreshSavedFlows()
        {
            SavedFlowNames.Clear();
            try
            {
                var names = FlowGraphStore.ListFlowNames();
                foreach (var name in names)
                {
                    SavedFlowNames.Add(name);
                }
            }
            catch { }
        }

        public void ConnectPorts(FlowPortViewModel source, FlowPortViewModel target)
        {
            // 端口连线校验：源必须为 Out，目标必须为 In，不能连接同节点，避免重复连线
            if (source.Direction != FlowPortDirection.Out || target.Direction != FlowPortDirection.In)
            {
                // 若反向拖拽，自动调换
                if (source.Direction == FlowPortDirection.In && target.Direction == FlowPortDirection.Out)
                {
                    (source, target) = (target, source);
                }
                else
                {
                    Growl.Warning("连线失败：必须从输出端口 (Out) 连向输入端口 (In)");
                    return;
                }
            }

            if (source.Node == target.Node)
            {
                Growl.Warning("连线失败：不支持连接同一节点自身");
                return;
            }

            // 检查重复连线
            bool exists = Connections.Any(c => c.Source == source && c.Target == target);
            if (exists)
            {
                return;
            }

            var conn = new FlowConnectionViewModel(source, target);
            Connections.Add(conn);
            source.IsConnected = true;
            target.IsConnected = true;
        }

        public void DisconnectConnection(FlowConnectionViewModel conn)
        {
            Connections.Remove(conn);
            conn.Source.IsConnected = Connections.Any(c => c.Source == conn.Source);
            conn.Target.IsConnected = Connections.Any(c => c.Target == conn.Target);
        }

        private void AttachNode(FlowNodeViewModel node)
        {
            node.OnSelected = n => SelectedNode = n;
        }

        private void ExecuteNewFlow()
        {
            Nodes.Clear();
            Connections.Clear();
            RunLogs.Clear();
            FlowName = "新建流程_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            // 自动添加初始 Start 节点
            var startNode = new FlowNodeViewModel("Start", "起始", new Point(120, 150));
            AttachNode(startNode);
            Nodes.Add(startNode);
            SelectedNode = startNode;
            startNode.IsSelected = true;
        }

        private void ExecuteAddNode(ToolboxItemViewModel? item)
        {
            if (item == null) return;

            double x = 300;
            double y = 150;
            if (Nodes.Count > 0)
            {
                var lastNode = Nodes.Last();
                x = lastNode.Location.X + 220;
                y = lastNode.Location.Y;
                if (x > 1200)
                {
                    x = 120;
                    y += 180;
                }
            }

            var nodeVm = new FlowNodeViewModel(item.NodeType, item.Name, new Point(x, y));
            AttachNode(nodeVm);
            Nodes.Add(nodeVm);

            foreach (var n in Nodes) n.IsSelected = false;
            nodeVm.IsSelected = true;
            SelectedNode = nodeVm;
        }

        private void ExecuteDeleteSelected()
        {
            var selectedList = Nodes.Where(n => n.IsSelected).ToList();
            if (selectedList.Count == 0 && SelectedNode != null)
            {
                selectedList.Add(SelectedNode);
            }

            foreach (var node in selectedList)
            {
                // 移除关联连线
                var attachedConns = Connections.Where(c => c.Source.Node == node || c.Target.Node == node).ToList();
                foreach (var conn in attachedConns)
                {
                    Connections.Remove(conn);
                }
                Nodes.Remove(node);
            }

            SelectedNode = Nodes.FirstOrDefault();
            if (SelectedNode != null) SelectedNode.IsSelected = true;
        }

        public FlowGraph BuildGraph()
        {
            var graph = new FlowGraph
            {
                Version = 2,
                FlowName = FlowName,
                Nodes = new List<FlowNode>(),
                Connections = new List<FlowConnection>()
            };

            foreach (var nodeVm in Nodes)
            {
                graph.Nodes.Add(nodeVm.ToModel());
            }

            foreach (var connVm in Connections)
            {
                graph.Connections.Add(connVm.ToModel());
            }

            return graph;
        }

        public void LoadGraph(FlowGraph graph)
        {
            Nodes.Clear();
            Connections.Clear();
            RunLogs.Clear();

            FlowName = graph.FlowName;

            var nodeDict = new Dictionary<string, FlowNodeViewModel>(StringComparer.OrdinalIgnoreCase);
            var portDict = new Dictionary<string, FlowPortViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in graph.Nodes)
            {
                var nodeVm = new FlowNodeViewModel(node);
                AttachNode(nodeVm);
                Nodes.Add(nodeVm);
                nodeDict[nodeVm.Id] = nodeVm;

                foreach (var p in nodeVm.InputPorts) portDict[$"{nodeVm.Id}:{p.Id}"] = p;
                foreach (var p in nodeVm.OutputPorts) portDict[$"{nodeVm.Id}:{p.Id}"] = p;
                // 兼容端口名匹配
                foreach (var p in nodeVm.InputPorts) portDict[$"{nodeVm.Id}:{p.Name}"] = p;
                foreach (var p in nodeVm.OutputPorts) portDict[$"{nodeVm.Id}:{p.Name}"] = p;
            }

            foreach (var conn in graph.Connections)
            {
                FlowPortViewModel? sourcePort = null;
                FlowPortViewModel? targetPort = null;

                portDict.TryGetValue($"{conn.FromNodeId}:{conn.FromPortId}", out sourcePort);
                portDict.TryGetValue($"{conn.ToNodeId}:{conn.ToPortId}", out targetPort);

                if (sourcePort != null && targetPort != null)
                {
                    var connVm = new FlowConnectionViewModel(sourcePort, targetPort);
                    Connections.Add(connVm);
                    sourcePort.IsConnected = true;
                    targetPort.IsConnected = true;
                }
            }

            SelectedNode = Nodes.FirstOrDefault();
            if (SelectedNode != null) SelectedNode.IsSelected = true;
        }

        private void ExecuteSaveFlow()
        {
            if (string.IsNullOrWhiteSpace(FlowName))
            {
                Growl.Warning("保存失败：流程名称不能为空");
                return;
            }

            var graph = BuildGraph();
            bool isValid = graph.Validate(out var errors);

            if (!isValid)
            {
                string msg = "流程图存在以下校验问题：\n" + string.Join("\n• ", errors) + "\n\n是否仍要保存为草稿？";
                var result = System.Windows.MessageBox.Show(msg, "流程校验提示", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            try
            {
                string path = FlowGraphStore.Save(graph);
                RefreshSavedFlows();
                Growl.Success($"已保存流程「{graph.FlowName}」（{graph.Nodes.Count} 个节点）\n{path}");
                StatusHint = $"已保存 {DateTime.Now:HH:mm:ss}  {path}";
            }
            catch (Exception ex)
            {
                Growl.Error($"保存流程失败: {ex.Message}");
            }
        }

        private void OpenSavedFlow(string flowName)
        {
            try
            {
                var graph = FlowGraphStore.Load(flowName);
                if (graph != null)
                {
                    LoadGraph(graph);
                    Growl.Success($"已打开流程: {flowName}");
                }
                else
                {
                    Growl.Warning($"未找到流程文件: {flowName}");
                }
            }
            catch (Exception ex)
            {
                Growl.Error($"加载流程失败: {ex.Message}");
            }
        }

        private void ExecuteOpenFlow()
        {
            var dlg = new OpenFileDialog
            {
                Title = "打开流程图 JSON 文件",
                Filter = "流程图文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = FlowGraphStore.DefaultBaseDirectory
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dlg.FileName);
                    var ver = FlowJsonMigrator.DetectVersion(json);
                    if (ver == FlowJsonVersion.V2)
                    {
                        var graph = FlowGraph.FromJson(json);
                        if (graph != null)
                        {
                            LoadGraph(graph);
                            Growl.Success($"已打开流程: {graph.FlowName}");
                        }
                    }
                    else if (ver == FlowJsonVersion.V1)
                    {
                        string defaultName = Path.GetFileNameWithoutExtension(dlg.FileName);
                        var graph = FlowJsonMigrator.MigrateV1ToFlowGraph(json, defaultName);
                        if (graph != null)
                        {
                            LoadGraph(graph);
                            Growl.Success($"已成功导入并升级旧版 V1 流程: {defaultName}");
                        }
                    }
                    else
                    {
                        Growl.Warning("无法识别该 JSON 文件的流程版本");
                    }
                }
                catch (Exception ex)
                {
                    Growl.Error($"打开流程失败: {ex.Message}");
                }
            }
        }

        private void ExecuteValidateFlow()
        {
            var graph = BuildGraph();
            bool isValid = graph.Validate(out var errors);

            if (isValid)
            {
                Growl.Success("流程图校验通过！拓扑结构完整合规。");
            }
            else
            {
                string msg = "流程图校验发现 " + errors.Count + " 处问题：\n• " + string.Join("\n• ", errors);
                System.Windows.MessageBox.Show(msg, "流程校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExecuteImportV1()
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入旧版 V1 流程 JSON",
                Filter = "V1 流程文件 (*.json)|*.json|所有文件 (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string json = File.ReadAllText(dlg.FileName);
                    string defaultName = Path.GetFileNameWithoutExtension(dlg.FileName);
                    var graph = FlowJsonMigrator.MigrateV1ToFlowGraph(json, defaultName);
                    if (graph != null)
                    {
                        LoadGraph(graph);
                        Growl.Success($"已成功将旧版 V1 流程升级为图形流程: {defaultName}");
                    }
                    else
                    {
                        Growl.Warning("该文件不是合法的旧版 V1 流程 JSON");
                    }
                }
                catch (Exception ex)
                {
                    Growl.Error($"导入 V1 流程失败: {ex.Message}");
                }
            }
        }

        private async Task ExecuteRunFlowAsync()
        {
            var graph = BuildGraph();
            if (!graph.Validate(out var errors))
            {
                Growl.Error("无法启动运行：流程图校验未通过！\n" + string.Join("\n", errors.Take(3)));
                return;
            }

            IsRunning = true;
            IsPaused = false;
            _runCts = new CancellationTokenSource();

            // 重置所有节点状态为 Idle
            foreach (var n in Nodes)
            {
                n.ExecutionState = FlowNodeState.Idle;
            }

            RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Running, $"启动执行流程: {FlowName}"));

            _engine = new FlowEngineV2(_motionController, _ioController, _eventBus, _services);
            _engine.NodeStateChanged += OnNodeStateChanged;

            var context = new FlowContext(FlowName, _loggerFactory ?? new NullLoggerFactory());

            try
            {
                await Task.Run(async () =>
                {
                    await _engine.RunAsync(graph, context, _runCts.Token);
                }, _runCts.Token);

                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Completed, "流程全部执行完成"));
                Growl.Success("流程执行顺利完成！");
            }
            catch (OperationCanceledException)
            {
                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Skipped, "流程已手动停止"));
                Growl.Warning("流程已停止");
            }
            catch (Exception ex)
            {
                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Failed, $"流程执行异常: {ex.Message}"));
                Growl.Error($"流程执行失败: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
                if (_engine != null)
                {
                    _engine.NodeStateChanged -= OnNodeStateChanged;
                }
            }
        }

        private async Task ExecutePauseFlowAsync()
        {
            if (_engine != null && IsRunning)
            {
                await _engine.PauseAsync();
                IsPaused = true;
                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Running, "流程已请求暂停"));
                Growl.Info("流程已暂停");
            }
        }

        private async Task ExecuteResumeFlowAsync()
        {
            if (_engine != null && IsRunning && IsPaused)
            {
                await _engine.ResumeAsync();
                IsPaused = false;
                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Running, "流程已恢复继续执行"));
                Growl.Info("流程继续执行");
            }
        }

        private async Task ExecuteStopFlowAsync()
        {
            if (_engine != null && IsRunning)
            {
                await _engine.StopAsync();
                _runCts?.Cancel();
                RunLogs.Add(new FlowLogItemViewModel("流程引擎", "Engine", FlowNodeState.Failed, "流程正在请求停止..."));
            }
        }

        private void OnNodeStateChanged(FlowNodeStateChange change)
        {
            if (_isDisposed) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isDisposed) return;

                var node = Nodes.FirstOrDefault(n => string.Equals(n.Id, change.NodeId, StringComparison.OrdinalIgnoreCase));
                if (node != null)
                {
                    node.ExecutionState = change.State;
                }

                RunLogs.Add(new FlowLogItemViewModel(change));
            }));
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            RefreshSavedFlows();
            SelectedNode?.ReloadPointParameters();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext) => true;

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _runCts?.Cancel();
                if (_engine != null)
                {
                    _engine.NodeStateChanged -= OnNodeStateChanged;
                }
            }
        }

        private class NullLoggerFactory : ILoggerFactory
        {
            public ILoggerManager CreateLogger(string name) => new NullLoggerManager();
        }

        private class NullLoggerManager : ILoggerManager
        {
            public void Trace(string msg) { }
            public void Debug(string msg) { }
            public void Info(string msg) { }
            public void Warn(string msg) { }
            public void Error(string msg) { }
            public void Fatal(string msg) { }
        }
    }
}
