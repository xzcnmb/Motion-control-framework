#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 循环节点。支持指定循环次数计数控制，或基于上下文条件的直到循环；
    /// 每次迭代从 "LoopBody" 端口输出，循环结束从 "Completed" 端口输出。
    ///
    /// 循环执行防护（工业执行线程安全）：
    /// 1. 迭代状态全部驻留上下文变量（counterKey），可被外部监视/复位，流程重入不残留；
    /// 2. until / break 条件满足即走 "Completed" 端口退出，continue 条件满足则自跳转回本节点
    ///    （跳过本次循环体，迭代计数照常递增）；
    /// 3. 硬性安全丝：单节点累计迭代数超过 maxIterations（默认 100,000）立即失败熔断，
    ///    杜绝 until 条件永假导致的无限循环挂死工业执行线程；
    /// 4. 引擎侧另有 MaxExecutionCount 动态环防御与静态环检测双重兜底。
    /// </summary>
    public class LoopNode : FlowNodeBase
    {
        /// <summary>循环安全丝默认阈值：单节点最大迭代次数。</summary>
        public const int DefaultMaxIterations = 100000;

        public override string NodeType => "Loop";
        public override string Name => "循环控制";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("loopCount", "循环次数", "int", 3, "循环执行总次数，设为 0 时由 untilConditionKey 决定", isRequired: true),
            new ParameterSchema("counterKey", "计数器变量名", "string", "", "上下文 Data 中存储当前已循环次数的键名，留空自动生成"),
            new ParameterSchema("untilConditionKey", "直到条件变量名", "string", "", "当该上下文布尔变量为 true 时强制结束循环"),
            new ParameterSchema("breakConditionKey", "跳出条件变量名", "string", "", "当该上下文布尔变量为 true 时立即跳出循环（语义同 until，面向报警/急停场景单独配置）"),
            new ParameterSchema("continueConditionKey", "继续条件变量名", "string", "", "当该上下文布尔变量为 true 时跳过本次循环体直接进入下一迭代"),
            new ParameterSchema("maxIterations", "安全丝阈值", "int", DefaultMaxIterations, "单节点累计迭代次数硬上限，超过即失败熔断（防无限循环挂死执行线程）"),
            new ParameterSchema("subFlowName", "循环体子流程名", "string", "", "可选：循环体若作为独立子图执行，可指定子流程名")
        };

        public override Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            int loopCount = ctx.GetParameter<int>("loopCount", 3);
            int maxIterations = ctx.GetParameter<int>("maxIterations", DefaultMaxIterations);
            string? counterKey = ctx.GetParameter<string>("counterKey");
            if (string.IsNullOrWhiteSpace(counterKey))
            {
                counterKey = $"_LoopCounter_{ctx.Node.Id}";
            }
            string? untilConditionKey = ctx.GetParameter<string>("untilConditionKey");
            string? breakConditionKey = ctx.GetParameter<string>("breakConditionKey");
            string? continueConditionKey = ctx.GetParameter<string>("continueConditionKey");

            // 参数防御：负次数视为配置错误，直接失败（绝不静默按无限循环处理）
            if (loopCount < 0)
            {
                return Task.FromResult(NodeExecutionResult.Failed(
                    $"循环节点参数无效: loopCount={loopCount} 为负数（应为 >=0，0 表示由 until/break 条件控制）"));
            }
            if (maxIterations <= 0)
            {
                return Task.FromResult(NodeExecutionResult.Failed(
                    $"循环节点参数无效: maxIterations={maxIterations} 必须为正数（安全丝阈值）"));
            }

            // 1. 退出条件（until / break）：满足即结束循环并复位计数器
            if (IsConditionTrue(ctx, untilConditionKey, out string? untilHitKey))
            {
                ctx.LogInfo($"循环终止: untilConditionKey '{untilHitKey}' 满足");
                ctx.SetVariable(counterKey, 0); // 重置
                return Task.FromResult(NodeExecutionResult.Completed("Completed"));
            }
            if (IsConditionTrue(ctx, breakConditionKey, out string? breakHitKey))
            {
                ctx.LogInfo($"循环跳出: breakConditionKey '{breakHitKey}' 满足");
                ctx.SetVariable(counterKey, 0); // 重置
                return Task.FromResult(NodeExecutionResult.Completed("Completed"));
            }

            int currentCount = ctx.GetVariable<int>(counterKey, 0);

            // 2. 计数循环完成判定（先于安全丝：合法完成不应被安全丝抢占）
            if (loopCount > 0 && currentCount >= loopCount)
            {
                ctx.LogInfo($"循环已完成全部 {loopCount} 次迭代 -> 进入 'Completed'");
                ctx.SetVariable(counterKey, 0); // 重置计数器供下一次流程调用
                return Task.FromResult(NodeExecutionResult.Completed("Completed"));
            }

            // 3. 安全丝熔断：硬性执行周期上限，超过立即失败（引擎随即中止整条流程）
            if (currentCount >= maxIterations)
            {
                var fuseMsg = $"循环安全丝熔断: 节点 '{ctx.Node.Name}'({ctx.Node.Id}) 累计迭代次数达到硬上限 " +
                              $"({maxIterations})，判定为失控循环，强制失败中止（请检查 until/break 条件是否可能永假）";
                ctx.LogError(fuseMsg);
                ctx.SetVariable(counterKey, 0); // 复位，便于人工干预后重跑
                return Task.FromResult(NodeExecutionResult.Failed(fuseMsg));
            }

            currentCount++;
            ctx.SetVariable(counterKey, currentCount);

            // 4. continue 条件：跳过本次循环体，自跳转回本节点进入下一迭代
            if (IsConditionTrue(ctx, continueConditionKey, out string? continueHitKey))
            {
                ctx.LogInfo($"循环第 {currentCount} 次迭代: continueConditionKey '{continueHitKey}' 满足 -> 跳过循环体");
                return Task.FromResult(NodeExecutionResult.Jump(ctx.Node.Id));
            }

            ctx.LogInfo($"循环迭代第 {currentCount}{(loopCount > 0 ? $"/{loopCount}" : "")} 次 -> 进入 'LoopBody'");
            return Task.FromResult(NodeExecutionResult.Completed("LoopBody"));
        }

        public override List<FlowPort> GetDefaultPorts()
        {
            return new List<FlowPort>
            {
                new("In", FlowPortDirection.In, "In"),
                new("LoopBody", FlowPortDirection.Out, "LoopBody"),
                new("Completed", FlowPortDirection.Out, "Completed")
            };
        }

        /// <summary>
        /// 判定上下文布尔条件是否满足。未配置键或值非布尔时视为不满足（绝不误跳）。
        /// </summary>
        private static bool IsConditionTrue(NodeExecutionContext ctx, string? conditionKey, out string? hitKey)
        {
            hitKey = conditionKey;
            if (string.IsNullOrWhiteSpace(conditionKey)) return false;

            var val = ctx.GetVariable<object>(conditionKey);
            if (val is bool b) return b;

            // 兼容 JSON 反序列化残留的 JsonElement 布尔
            if (val is System.Text.Json.JsonElement je)
            {
                return je.ValueKind == System.Text.Json.JsonValueKind.True;
            }
            return false;
        }
    }
}
