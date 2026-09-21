#nullable enable
using System;
using System.Windows.Media;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class FlowLogItemViewModel
    {
        public DateTime Timestamp { get; }
        public string NodeName { get; }
        public string NodeType { get; }
        public FlowNodeState State { get; }
        public string? Message { get; }

        public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

        public Brush StateBrush => State switch
        {
            FlowNodeState.Idle => new SolidColorBrush(Color.FromRgb(140, 140, 140)),
            FlowNodeState.Running => new SolidColorBrush(Color.FromRgb(24, 144, 255)),
            FlowNodeState.Completed => new SolidColorBrush(Color.FromRgb(82, 196, 26)),
            FlowNodeState.Failed => new SolidColorBrush(Color.FromRgb(245, 34, 45)),
            FlowNodeState.Skipped => new SolidColorBrush(Color.FromRgb(250, 173, 20)),
            _ => Brushes.Gray
        };

        public FlowLogItemViewModel(FlowNodeStateChange change)
        {
            Timestamp = change.Timestamp;
            NodeName = change.NodeName;
            NodeType = change.NodeType;
            State = change.State;
            Message = change.Message;
        }

        public FlowLogItemViewModel(string nodeName, string nodeType, FlowNodeState state, string? message = null)
        {
            Timestamp = DateTime.Now;
            NodeName = nodeName;
            NodeType = nodeType;
            State = state;
            Message = message;
        }
    }
}
