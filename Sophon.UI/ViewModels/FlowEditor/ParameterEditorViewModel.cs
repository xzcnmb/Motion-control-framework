#nullable enable
using System;
using System.Text.Json;
using Prism.Mvvm;
using Sophon.Core.Flow.V2;

namespace Sophon.UI.ViewModels.FlowEditor
{
    public class ParameterEditorViewModel : BindableBase
    {
        private readonly FlowNodeViewModel _node;
        public ParameterSchema Schema { get; }

        public string Name => Schema.Name;
        public string DisplayName => string.IsNullOrWhiteSpace(Schema.DisplayName) ? Schema.Name : Schema.DisplayName;
        public string Type => (Schema.Type ?? "string").ToLowerInvariant();
        public string? Description => Schema.Description;
        public bool IsRequired => Schema.IsRequired;
        public string[]? Options => Schema.Options;

        public bool IsBool => Type == "bool";
        public bool IsInt => Type == "int" || Type == "axis";
        public bool IsDouble => Type == "double";
        public bool IsEnum => Type == "enum" && Options != null && Options.Length > 0;
        public bool IsJson => Type == "json";
        public bool IsString => !IsBool && !IsInt && !IsDouble && !IsEnum && !IsJson;

        private object? _value;
        public object? Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                {
                    _node.UpdateParameter(Name, value);
                    RaisePropertyChanged(nameof(StringValue));
                    RaisePropertyChanged(nameof(IntValue));
                    RaisePropertyChanged(nameof(DoubleValue));
                    RaisePropertyChanged(nameof(BoolValue));
                    RaisePropertyChanged(nameof(EnumValue));
                    RaisePropertyChanged(nameof(JsonValue));
                }
            }
        }

        public string StringValue
        {
            get => Value?.ToString() ?? "";
            set => Value = value;
        }

        public int IntValue
        {
            get
            {
                if (Value is int i) return i;
                if (Value is JsonElement je && je.TryGetInt32(out var jInt)) return jInt;
                if (int.TryParse(Value?.ToString(), out var parsed)) return parsed;
                return 0;
            }
            set => Value = value;
        }

        public double DoubleValue
        {
            get
            {
                if (Value is double d) return d;
                if (Value is float f) return f;
                if (Value is int i) return i;
                if (Value is JsonElement je && je.TryGetDouble(out var jDbl)) return jDbl;
                if (double.TryParse(Value?.ToString(), out var parsed)) return parsed;
                return 0.0;
            }
            set => Value = value;
        }

        public bool BoolValue
        {
            get
            {
                if (Value is bool b) return b;
                if (Value is JsonElement je && je.ValueKind == JsonValueKind.True) return true;
                if (bool.TryParse(Value?.ToString(), out var parsed)) return parsed;
                return false;
            }
            set => Value = value;
        }

        public string EnumValue
        {
            get
            {
                if (Value != null && Options != null)
                {
                    if (Value is int idx && idx >= 0 && idx < Options.Length)
                    {
                        return Options[idx];
                    }
                    string str = Value.ToString() ?? "";
                    foreach (var opt in Options)
                    {
                        if (string.Equals(opt, str, StringComparison.OrdinalIgnoreCase)) return opt;
                    }
                }
                return Options != null && Options.Length > 0 ? Options[0] : "";
            }
            set => Value = value;
        }

        public string JsonValue
        {
            get
            {
                if (Value is JsonElement je) return je.ToString();
                return Value?.ToString() ?? "";
            }
            set => Value = value;
        }

        public ParameterEditorViewModel(FlowNodeViewModel node, ParameterSchema schema, object? initialValue)
        {
            _node = node;
            Schema = schema;
            _value = initialValue ?? schema.DefaultValue;
        }
    }
}
