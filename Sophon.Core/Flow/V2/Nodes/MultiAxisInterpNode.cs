#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 多轴插补/协同运动节点（v1.0 预留实现：逐轴 MoveAbs 同步并发启动 + Task.WhenAll(AxisDone) 协同到位；
    /// 接口与参数契约冻结，Wave4 接 Sophon.Motion 核心后无缝升级内部插补流）。
    /// 完成语义严格映射 PLCopen <see cref="CommandCompletionStatus"/>：
    /// 全部轴 Done -> Completed("Out")；任一轴 CommandAborted -> Failed("命令被中止 (CommandAborted)")；
    /// 任一轴 Error -> Failed("硬件故障/限位错误 (Error)")。
    /// </summary>
    public class MultiAxisInterpNode : FlowNodeBase
    {
        public override string NodeType => "MultiAxisInterp";
        public override string Name => "多轴插补定位";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("axisIds", "联动轴ID列表", "json", "[0,1]", "参与插补/联动的轴 ID 列表，支持数组或逗号分隔如 '0,1'", isRequired: true),
            new ParameterSchema("targets", "目标坐标列表", "json", "[100.0,200.0]", "各轴目标位置列表，长度须与轴列表一致", isRequired: true),
            new ParameterSchema("targetsFromContext", "目标取自上下文", "json", null, "上下文变量名数组（如 ['VisionWorldX','VisionWorldY']），存在时优先于 targets 使用（视觉引导闭环用）"),
            new ParameterSchema("speed", "合成速度(mm/s)", "double", 100.0, "定位最大速度", isRequired: true),
            new ParameterSchema("accel", "合成加速度(mm/s²)", "double", 500.0, "加速度", isRequired: true),
            new ParameterSchema("decel", "合成减速度(mm/s²)", "double", 500.0, "减速度", isRequired: true),
            new ParameterSchema("timeoutMs", "协同到位超时(ms)", "int", 30000, "全部轴到位超时上限")
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var motion = ctx.MotionController;
            if (motion == null)
            {
                return NodeExecutionResult.Failed("运动控制器 (IMotionController) 未注入或未就绪");
            }

            var axisIds = ParseIntArray(ctx, "axisIds");
            double[] targets = ResolveTargets(ctx, axisIds);

            if (axisIds.Length == 0 || targets.Length == 0 || axisIds.Length != targets.Length)
            {
                return NodeExecutionResult.Failed($"多轴插补参数无效: axisIds 数量({axisIds.Length}) 与 targets 数量({targets.Length}) 不一致或为空");
            }

            double speed = ctx.GetParameter<double>("speed", 100.0);
            double accel = ctx.GetParameter<double>("accel", 500.0);
            double decel = ctx.GetParameter<double>("decel", 500.0);
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 30000);

            ctx.LogInfo($"多轴插补启动: 轴=[{string.Join(",", axisIds)}], 目标=[{string.Join(",", targets)}], 速度={speed}, 超时={timeoutMs}ms");

            // 工业 Fail-Fast 连锁急停保护（参考西门子/雷赛标准：单轴故障时其余轴连锁急停）
            using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new Task<AxisDoneArgs>[axisIds.Length];

            for (int i = 0; i < axisIds.Length; i++)
            {
                int currentAxis = axisIds[i];
                var task = motion.MoveAbsAsync(currentAxis, targets[i], speed, accel, decel, 0, failFastCts.Token);
                tasks[i] = task;

                // 注册单轴失败快速响应：一旦任一轴快速失败或越界，立即中断其余所有协同轴，防止其余轴单奔造成机械撞刀
                _ = task.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && !t.Result.Success)
                    {
                        try { failFastCts.Cancel(); } catch { }
                        foreach (var otherAxis in axisIds)
                        {
                            if (otherAxis != currentAxis)
                            {
                                try { motion.Abort(otherAxis); } catch { }
                            }
                        }
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
            }

            try
            {
                var allResults = await Task.WhenAll(tasks)
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), failFastCts.Token);

                foreach (var done in allResults)
                {
                    // PLCopen 完成状态精确映射：Done=协同到位；CommandAborted=被叫停/抢占；Error=故障/限位
                    if (!(done.Status == CommandCompletionStatus.Done && done.Success))
                    {
                        var detail = $"多轴协同运动中有轴未成功完成: 轴 {done.AxisId} {AxisMoveNode.DescribeCompletionStatus(done)}";
                        ctx.LogError(detail);
                        return NodeExecutionResult.Failed(detail);
                    }
                }

                ctx.LogInfo("多轴协同运动全部到位成功 (Done)");
                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                // 区分外部取消与单轴故障连锁急停：已有轴带非 Done 状态完成时，优先呈现 PLCopen 精确状态
                string? faultDetail = null;
                foreach (var t in tasks)
                {
                    if (t.IsCompletedSuccessfully &&
                        !(t.Result.Status == CommandCompletionStatus.Done && t.Result.Success))
                    {
                        faultDetail = $"轴 {t.Result.AxisId} {AxisMoveNode.DescribeCompletionStatus(t.Result)}";
                        break;
                    }
                }

                foreach (var axisId in axisIds) { try { motion.Abort(axisId); } catch { } }

                var msg = faultDetail != null
                    ? $"多轴协同运动失败: {faultDetail}（单轴故障触发连锁急停，其余轴已 Abort）"
                    : "多轴协同运动被外部取消 (CommandAborted)";
                ctx.LogError(msg);
                return NodeExecutionResult.Failed(msg);
            }
            catch (TimeoutException)
            {
                foreach (var axisId in axisIds) { try { motion.StopMotion(axisId); } catch { } }
                var msg = $"多轴协同运动未在 {timeoutMs}ms 内全部到位 (到位超时)";
                ctx.LogError(msg);
                return NodeExecutionResult.Failed(msg);
            }
        }

        /// <summary>
        /// 目标解析：优先 targetsFromContext（视觉引导等动态目标），否则静态 targets。
        /// </summary>
        private static double[] ResolveTargets(NodeExecutionContext ctx, int[] axisIds)
        {
            var keys = ParseStringArray(ctx, "targetsFromContext");
            if (keys.Length > 0)
            {
                if (keys.Length != axisIds.Length)
                {
                    throw new InvalidOperationException($"targetsFromContext 数量({keys.Length}) 与 axisIds 数量({axisIds.Length}) 不一致");
                }
                var resolved = new double[keys.Length];
                for (int i = 0; i < keys.Length; i++)
                {
                    var v = ctx.GetVariable<object>(keys[i]);
                    if (v is null)
                    {
                        throw new InvalidOperationException($"上下文中不存在数值变量 '{keys[i]}'，无法解析动态目标");
                    }
                    resolved[i] = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                }
                return resolved;
            }
            return ParseDoubleArray(ctx, "targets");
        }

        private static string[] ParseStringArray(NodeExecutionContext ctx, string key)
        {
            if (ctx.Node.Parameters.TryGetValue(key, out var val) && val != null)
            {
                if (val is string[] arr) return arr;
                if (val is IEnumerable<string> enu) return enu.ToArray();
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    return je.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                }
                if (val is string str)
                {
                    return str.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim()).ToArray();
                }
            }
            return Array.Empty<string>();
        }

        private static int[] ParseIntArray(NodeExecutionContext ctx, string key)
        {
            if (ctx.Node.Parameters.TryGetValue(key, out var val) && val != null)
            {
                if (val is int[] arr) return arr;
                if (val is IEnumerable<int> enu) return enu.ToArray();
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    return je.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                }
                if (val is string str)
                {
                    // 容错：去掉 JSON 风格方括号与引号，再按逗号/分号/空格切分
                    var cleaned = str.Trim().Trim('[', ']').Replace("'", "").Replace("\"", "");
                    return cleaned.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => int.TryParse(s.Trim(), out var v) ? v : throw new InvalidOperationException($"axisIds 无法解析的项: '{s}'"))
                                  .ToArray();
                }
            }
            return Array.Empty<int>();
        }

        private static double[] ParseDoubleArray(NodeExecutionContext ctx, string key)
        {
            if (ctx.Node.Parameters.TryGetValue(key, out var val) && val != null)
            {
                if (val is double[] arr) return arr;
                if (val is IEnumerable<double> enu) return enu.ToArray();
                if (val is JsonElement je && je.ValueKind == JsonValueKind.Array)
                {
                    return je.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                }
                if (val is string str)
                {
                    // 容错：去掉 JSON 风格方括号与引号，再按逗号/分号/空格切分
                    var cleaned = str.Trim().Trim('[', ']').Replace("'", "").Replace("\"", "");
                    return cleaned.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => double.TryParse(s.Trim(), out var v) ? v : throw new InvalidOperationException($"targets 无法解析的项: '{s}'"))
                                  .ToArray();
                }
            }
            return Array.Empty<double>();
        }
    }
}
