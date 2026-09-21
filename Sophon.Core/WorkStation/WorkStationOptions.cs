#nullable enable

namespace Sophon.Core
{
    /// <summary>
    /// 工站运行选项。生产默认循环跑绑定的流程图（PackML Execute）；
    /// 单元测试用 <see cref="SingleShot"/> 保持「跑完 Idle」语义。
    /// </summary>
    public sealed class WorkStationOptions
    {
        public static WorkStationOptions SingleShot { get; } = new() { LoopRecipe = false };

        public static WorkStationOptions Cyclic(string? boundFlowName = null) => new()
        {
            LoopRecipe = true,
            BoundFlowName = boundFlowName
        };

        /// <summary>绑定的 v2 流程图名（SophonData/flows/{name}.json）。空则用工站名。</summary>
        public string? BoundFlowName { get; init; }

        /// <summary>true：Start 后循环执行配方直到 Stop。false：跑完一次回 Idle。</summary>
        public bool LoopRecipe { get; init; } = true;
    }
}
