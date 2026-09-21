using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core
{
    public interface IFlowEngine
    {
        /// <summary>
        /// 流程名称（来自 WorkStation）。
        /// </summary>
        string FlowName { get; }

        /// <summary>
        /// 执行流程直至结束/被取消。步骤失败向上抛出 StepExecuteException，
        /// 由调用方（WorkStation）统一决定状态去向，杜绝异常逃逸到后台线程。
        /// </summary>
        /// <param name="context">流程上下文</param>
        /// <param name="token">取消令牌：停止流程的单一事实来源</param>
        Task RunAsync(IFlowContext context, CancellationToken token);
    }
}