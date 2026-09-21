using Common;
using System.Collections.Generic;

namespace Sophon.Core
{
    public class FlowContext : IFlowContext
    {
        public FlowContext(string flowName, ILoggerFactory loggerFactory)
        {
            FlowName = flowName;
            _loggerFactory = loggerFactory;
            Logger = _loggerFactory.CreateLogger(flowName);
            _data = new Dictionary<string, object>();
            _dataLock = new object();
        }

        /// <summary>
        /// 供 Clone 使用：复用父上下文的数据字典与其锁，确保并行分支对共享数据的读写序列化。
        /// </summary>
        private FlowContext(string flowName, ILoggerFactory loggerFactory, Dictionary<string, object> sharedData, object sharedLock)
        {
            FlowName = flowName;
            _loggerFactory = loggerFactory;
            Logger = _loggerFactory.CreateLogger(flowName);
            _data = sharedData;
            _dataLock = sharedLock;
        }

        public string FlowName { get; }
        public int NextStepIndex { get; set; }
        public int TotalSteps { get; set; }

        /// <summary>
        /// 原始数据字典。注意：并行分支下直接遍历此属性不是线程安全的，
        /// 请优先使用 GetData/SetData（已加锁）。此属性仅为兼容 v1 既有调用保留。
        /// </summary>
        public Dictionary<string, object> Data => _data;
        public ILoggerManager Logger { get; }

        private readonly Dictionary<string, object> _data;
        private readonly object _dataLock;
        private readonly ILoggerFactory _loggerFactory;

        public T GetData<T>(string key)
        {
            lock (_dataLock)
            {
                return _data.TryGetValue(key, out var value) ? (T)value : default;
            }
        }

        public void SetData<T>(string key, T value)
        {
            lock (_dataLock)
            {
                _data[key] = value;
            }
        }

        public void ClearData()
        {
            lock (_dataLock)
            {
                _data.Clear();
            }
        }

        /// <summary>
        /// data为浅拷贝，数据公用（与父上下文共享同一字典及其锁），其余字段不共用。
        /// </summary>
        public IFlowContext Clone()
        {
            return new FlowContext(this.FlowName, _loggerFactory, this._data, this._dataLock);
        }
    }
}