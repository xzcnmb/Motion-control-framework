using Common;
using System.Collections.Generic;

namespace Sophon.Core
{
    public interface IFlowContext
    {
        /// <summary>
        /// 流程名称 来自WorkStation
        /// </summary>
        string FlowName { get; }

        /// <summary>
        /// 从单步中获取到的下一步索引
        /// </summary>
        int NextStepIndex { get; set; }

        /// <summary>
        /// 流程总步数
        /// </summary>
        int TotalSteps { get; set; }

        /// <summary>
        /// 存储流程中的数据
        /// </summary>
        Dictionary<string, object> Data { get; }

        /// <summary>
        /// Flow使用日志
        /// </summary>
        ILoggerManager Logger { get; }

        T GetData<T>(string key);

        void SetData<T>(string key, T value);

        /// <summary>换配方时清掉上一张图留下的变量，避免视觉坐标串到新配方。</summary>
        void ClearData();

        IFlowContext Clone();
    }
}