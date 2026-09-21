#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// V2 节点注册表。用于节点类型发现、元数据查询及实例创建，
    /// 为流程引擎执行和 Wave3-E Nodify 画布编辑器提供统一的节点类型元数据支持。
    /// </summary>
    public static class FlowNodeRegistry
    {
        private static readonly ConcurrentDictionary<string, Func<FlowNodeBase>> Factories =
            new(StringComparer.OrdinalIgnoreCase);

        static FlowNodeRegistry()
        {
            Register<StartNode>("Start");
            Register<DelayNode>("Delay");
            Register<AxisMoveNode>("AxisMove");
            Register<AxisJogNode>("AxisJog");
            Register<AxisHomeNode>("AxisHome");
            Register<AxisEnableNode>("AxisEnable");
            Register<MultiAxisInterpNode>("MultiAxisInterp");
            Register<DiWaitNode>("DiWait");
            Register<DoSetNode>("DoSet");
            Register<VariableNode>("Variable");
            Register<BranchNode>("Branch");
            Register<LoopNode>("Loop");
            Register<ParallelNode>("Parallel");
            Register<JumpNode>("Jump");
            Register<EventPublishNode>("EventPublish");
            Register<EventWaitNode>("EventWait");
            Register<SubFlowNode>("SubFlow");
            Register<VisionMeasureNode>("VisionMeasure");
            Register<CylinderMoveNode>("CylinderMove");
            Register<DeviceReadNode>("DeviceRead");
            Register<DeviceWriteNode>("DeviceWrite");
        }

        /// <summary>
        /// 注册自定义或扩展节点类型。
        /// </summary>
        public static void Register<T>(string? nodeType = null) where T : FlowNodeBase, new()
        {
            var temp = new T();
            var typeKey = !string.IsNullOrWhiteSpace(nodeType) ? nodeType : temp.NodeType;
            Factories[typeKey] = () => new T();
        }

        /// <summary>
        /// 注册工厂方法。
        /// </summary>
        public static void Register(string nodeType, Func<FlowNodeBase> factory)
        {
            Factories[nodeType] = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>
        /// 根据节点类型标识创建逻辑节点执行实例。
        /// </summary>
        public static FlowNodeBase? Create(string nodeType)
        {
            if (Factories.TryGetValue(nodeType, out var factory))
            {
                return factory();
            }
            return null;
        }

        /// <summary>
        /// 判断指定节点类型是否已注册。
        /// </summary>
        public static bool IsRegistered(string nodeType) =>
            Factories.ContainsKey(nodeType);

        /// <summary>
        /// 获取所有已注册的节点类型及其参数 Schema 字典。
        /// </summary>
        public static Dictionary<string, IReadOnlyList<ParameterSchema>> GetAllSchemas()
        {
            var result = new Dictionary<string, IReadOnlyList<ParameterSchema>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in Factories)
            {
                try
                {
                    var instance = kvp.Value();
                    result[kvp.Key] = instance.ParameterSchemas;
                }
                catch
                {
                    result[kvp.Key] = Array.Empty<ParameterSchema>();
                }
            }
            return result;
        }

        /// <summary>
        /// 获取已注册的全部节点类型标识列表。
        /// </summary>
        public static IReadOnlyCollection<string> RegisteredTypes => (IReadOnlyCollection<string>)Factories.Keys.ToList();
    }
}
