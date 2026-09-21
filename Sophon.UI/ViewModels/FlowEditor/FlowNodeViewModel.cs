#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Prism.Mvvm;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class FlowNodeViewModel : BindableBase
    {
        public string Id { get; }
        public string NodeType { get; }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        private Point _location;
        public Point Location
        {
            get => _location;
            set => SetProperty(ref _location, value);
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public string Category { get; }
        public Brush HeaderBrush { get; }

        public ObservableCollection<FlowPortViewModel> InputPorts { get; } = new();
        public ObservableCollection<FlowPortViewModel> OutputPorts { get; } = new();

        private FlowNodeState _executionState = FlowNodeState.Idle;
        public FlowNodeState ExecutionState
        {
            get => _executionState;
            set
            {
                if (SetProperty(ref _executionState, value))
                {
                    RaisePropertyChanged(nameof(StateText));
                    RaisePropertyChanged(nameof(StateBrush));
                }
            }
        }

        public string StateText => ExecutionState switch
        {
            FlowNodeState.Idle => "就绪",
            FlowNodeState.Running => "运行中",
            FlowNodeState.Completed => "已完成",
            FlowNodeState.Failed => "失败",
            FlowNodeState.Skipped => "已跳过",
            _ => "未知"
        };

        public Brush StateBrush => ExecutionState switch
        {
            FlowNodeState.Idle => new SolidColorBrush(Color.FromRgb(140, 140, 140)),       // 灰
            FlowNodeState.Running => new SolidColorBrush(Color.FromRgb(24, 144, 255)),     // 蓝
            FlowNodeState.Completed => new SolidColorBrush(Color.FromRgb(82, 196, 26)),    // 绿
            FlowNodeState.Failed => new SolidColorBrush(Color.FromRgb(245, 34, 45)),       // 红
            FlowNodeState.Skipped => new SolidColorBrush(Color.FromRgb(250, 173, 20)),     // 黄
            _ => Brushes.Gray
        };

        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ObservableCollection<ParameterEditorViewModel> ParameterEditors { get; } = new();

        public FlowNodeViewModel(string nodeType, string name, Point location, string? id = null)
        {
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            NodeType = nodeType;
            Name = name;
            Location = location;

            Category = GetCategoryForType(nodeType);
            HeaderBrush = GetBrushForCategory(Category);

            InitializeFromRegistry();
        }

        public FlowNodeViewModel(FlowNode model)
        {
            Id = model.Id;
            NodeType = model.NodeType;
            Name = string.IsNullOrWhiteSpace(model.Name) ? model.NodeType : model.Name;
            Location = new Point(model.Position.X, model.Position.Y);

            Category = GetCategoryForType(model.NodeType);
            HeaderBrush = GetBrushForCategory(Category);

            // 导入参数
            if (model.Parameters != null)
            {
                foreach (var kvp in model.Parameters)
                {
                    Parameters[kvp.Key] = kvp.Value;
                }
            }

            // 导入端口
            if (model.Ports != null && model.Ports.Count > 0)
            {
                foreach (var p in model.Ports)
                {
                    var portVm = new FlowPortViewModel(this, p);
                    if (p.Direction == FlowPortDirection.In)
                    {
                        InputPorts.Add(portVm);
                    }
                    else
                    {
                        OutputPorts.Add(portVm);
                    }
                }
            }
            else
            {
                // 若模型端口为空，从注册表读取默认端口
                var baseNode = FlowNodeRegistry.Create(model.NodeType);
                var defaultPorts = baseNode?.GetDefaultPorts() ?? new List<FlowPort> { new("In", FlowPortDirection.In), new("Out", FlowPortDirection.Out) };
                foreach (var p in defaultPorts)
                {
                    var portVm = new FlowPortViewModel(this, p);
                    if (p.Direction == FlowPortDirection.In) InputPorts.Add(portVm);
                    else OutputPorts.Add(portVm);
                }
            }

            // 生成参数属性编辑器
            BuildParameterEditors();
        }

        private void InitializeFromRegistry()
        {
            var baseNode = FlowNodeRegistry.Create(NodeType);
            if (baseNode != null)
            {
                var ports = baseNode.GetDefaultPorts();
                foreach (var p in ports)
                {
                    var portVm = new FlowPortViewModel(this, p);
                    if (p.Direction == FlowPortDirection.In)
                    {
                        InputPorts.Add(portVm);
                    }
                    else
                    {
                        OutputPorts.Add(portVm);
                    }
                }

                if (baseNode.ParameterSchemas != null)
                {
                    foreach (var s in baseNode.ParameterSchemas)
                    {
                        if (s.DefaultValue != null)
                        {
                            Parameters[s.Name] = s.DefaultValue;
                        }
                    }
                }
            }
            else
            {
                InputPorts.Add(new FlowPortViewModel(this, "In", FlowPortDirection.In));
                OutputPorts.Add(new FlowPortViewModel(this, "Out", FlowPortDirection.Out));
            }

            BuildParameterEditors();
        }

        private void BuildParameterEditors()
        {
            ParameterEditors.Clear();
            var baseNode = FlowNodeRegistry.Create(NodeType);
            if (baseNode?.ParameterSchemas != null)
            {
                foreach (var schema in baseNode.ParameterSchemas)
                {
                    Parameters.TryGetValue(schema.Name, out var initialVal);
                    ParameterEditors.Add(new ParameterEditorViewModel(this, schema, initialVal));
                }
            }
        }

        public void UpdateParameter(string name, object? value)
        {
            Parameters[name] = value;
        }

        public FlowNode ToModel()
        {
            var node = new FlowNode(NodeType, Name, Id)
            {
                Position = new FlowPosition(Location.X, Location.Y),
                Parameters = new Dictionary<string, object?>(Parameters, StringComparer.OrdinalIgnoreCase),
                Ports = new List<FlowPort>()
            };

            foreach (var inPort in InputPorts)
            {
                node.Ports.Add(inPort.ToModel());
            }
            foreach (var outPort in OutputPorts)
            {
                node.Ports.Add(outPort.ToModel());
            }

            return node;
        }

        public static string GetCategoryForType(string nodeType)
        {
            return nodeType switch
            {
                "Start" or "Delay" or "Variable" => "基础控制",
                "Branch" or "Loop" or "Parallel" or "Jump" or "SubFlow" => "逻辑流程",
                "AxisMove" or "AxisJog" or "AxisHome" or "MultiAxisInterp" => "运动控制",
                "DiWait" or "DoSet" => "数字与IO",
                "EventPublish" or "EventWait" => "事件协作",
                _ => "自定义扩展"
            };
        }

        public static Brush GetBrushForCategory(string category)
        {
            return category switch
            {
                "基础控制" => new SolidColorBrush(Color.FromRgb(114, 46, 209)),   // 紫
                "逻辑流程" => new SolidColorBrush(Color.FromRgb(250, 140, 22)),   // 橙
                "运动控制" => new SolidColorBrush(Color.FromRgb(24, 144, 255)),   // 蓝
                "数字与IO" => new SolidColorBrush(Color.FromRgb(19, 194, 194)),   // 青
                "事件协作" => new SolidColorBrush(Color.FromRgb(235, 47, 150)),   // 品红
                _ => new SolidColorBrush(Color.FromRgb(89, 89, 89))
            };
        }
    }
}
