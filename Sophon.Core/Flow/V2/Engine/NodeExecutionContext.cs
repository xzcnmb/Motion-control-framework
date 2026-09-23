#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 节点执行运行期上下文，封装流程上下文、硬件控制器引用及参数解析辅助方法。
    /// </summary>
    public class NodeExecutionContext
    {
        public IFlowContext FlowContext { get; }
        public FlowNode Node { get; }
        public FlowGraph Graph { get; }
        public IServiceProvider? Services { get; }

        public IMotionController? MotionController { get; }
        public IIoController? IoController { get; }
        public IEventBus? EventBus { get; }
        public IReadOnlyCollection<string> FlowCallStack { get; }

        public NodeExecutionContext(
            IFlowContext flowContext,
            FlowNode node,
            FlowGraph graph,
            IMotionController? motionController = null,
            IIoController? ioController = null,
            IEventBus? eventBus = null,
            IServiceProvider? services = null,
            IReadOnlyCollection<string>? flowCallStack = null)
        {
            FlowContext = flowContext ?? throw new ArgumentNullException(nameof(flowContext));
            Node = node ?? throw new ArgumentNullException(nameof(node));
            Graph = graph ?? throw new ArgumentNullException(nameof(graph));
            MotionController = motionController;
            IoController = ioController;
            EventBus = eventBus;
            Services = services;
            FlowCallStack = flowCallStack ?? Array.Empty<string>();
        }

        /// <summary>
        /// 从节点参数字典中获取强类型参数值，支持 System.Text.Json 的 JsonElement 智能类型转换。
        /// </summary>
        public T? GetParameter<T>(string key, T? defaultValue = default)
        {
            if (Node.Parameters == null || !Node.Parameters.TryGetValue(key, out var rawVal) || rawVal == null)
            {
                return defaultValue;
            }

            try
            {
                if (rawVal is T directVal)
                {
                    return directVal;
                }

                if (rawVal is JsonElement jsonElem)
                {
                    var targetType = typeof(T);
                    var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

                    if (underlyingType == typeof(int) && jsonElem.TryGetInt32(out var iVal)) return (T)(object)iVal;
                    if (underlyingType == typeof(double) && jsonElem.TryGetDouble(out var dVal)) return (T)(object)dVal;
                    if (underlyingType == typeof(bool)) return (T)(object)jsonElem.GetBoolean();
                    if (underlyingType == typeof(string)) return (T)(object)jsonElem.GetString()!;
                    if (underlyingType == typeof(long) && jsonElem.TryGetInt64(out var lVal)) return (T)(object)lVal;

                    return JsonSerializer.Deserialize<T>(jsonElem.GetRawText());
                }

                return (T)Convert.ChangeType(rawVal, typeof(T), CultureInfo.InvariantCulture);
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 在流程全局上下文 Data 字典中写入变量。
        /// 若值为 System.Text.Json 的 JsonElement（JSON 反序列化后常见），自动收敛为 CLR 基础类型，
        /// 避免后续 GetData&lt;T&gt; 的 InvalidCastException。
        /// </summary>
        public void SetVariable<T>(string key, T value)
        {
            FlowContext.SetData(key, CoerceJsonElement(value));
        }

        private static object? CoerceJsonElement(object? value)
        {
            if (value is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.True: return true;
                    case JsonValueKind.False: return false;
                    case JsonValueKind.String: return je.GetString();
                    case JsonValueKind.Number:
                        if (je.TryGetInt64(out var l)) return l;
                        if (je.TryGetDouble(out var d)) return d;
                        return je.GetDouble();
                    default:
                        return je.Clone(); // 对象/数组保留 JsonElement
                }
            }
            return value;
        }

        /// <summary>
        /// 从流程全局上下文 Data 字典中读取变量。
        /// </summary>
        public T? GetVariable<T>(string key, T? defaultValue = default)
        {
            try
            {
                return FlowContext.GetData<T>(key);
            }
            catch
            {
                return defaultValue;
            }
        }

        public void LogInfo(string message) =>
            FlowContext.Logger?.Info($"[V2 Flow:{Graph.FlowName}][Node:{Node.Name}] {message}");

        public void LogWarn(string message) =>
            FlowContext.Logger?.Warn($"[V2 Flow:{Graph.FlowName}][Node:{Node.Name}] {message}");

        public void LogError(string message, Exception? ex = null) =>
            FlowContext.Logger?.Error(ex != null ? $"[V2 Flow:{Graph.FlowName}][Node:{Node.Name}] {message}: {ex.Message}" : $"[V2 Flow:{Graph.FlowName}][Node:{Node.Name}] {message}");
    }
}
