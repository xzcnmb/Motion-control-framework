#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Flow.V2
{
    /// <summary>
    /// 视觉测量/定位节点（视觉引导闭环入口）。
    /// 触发一次视觉采集测量，等待结果，并把 OK/像素坐标/标定后物理坐标写入流程上下文。
    /// 后续配合 BranchNode（按 okKey 分流）与 MultiAxisInterpNode（targetsFromContext 取物理坐标）完成"视觉定位→补偿搬运"闭环。
    /// 视觉提供者经引擎构造时的 services（IServiceProvider）解析，未注入时节点如实失败。
    /// </summary>
    public class VisionMeasureNode : FlowNodeBase
    {
        public override string NodeType => "VisionMeasure";
        public override string Name => "视觉测量/定位";

        public override IReadOnlyList<ParameterSchema> ParameterSchemas => new[]
        {
            new ParameterSchema("cameraId", "相机ID", "string", "Cam1", "视觉提供者的相机标识", isRequired: true),
            new ParameterSchema("timeoutMs", "测量超时(ms)", "int", 5000, "等待测量结果超时上限，必须带超时", isRequired: true),
            new ParameterSchema("okKey", "OK变量名", "string", "VisionOk", "测量是否成功(布尔)写入上下文的键"),
            new ParameterSchema("xKey", "X变量名", "string", "VisionX", "像素X写入上下文的键"),
            new ParameterSchema("yKey", "Y变量名", "string", "VisionY", "像素Y写入上下文的键"),
            new ParameterSchema("angleKey", "角度变量名", "string", "VisionAngle", "像素角度写入上下文的键"),
            new ParameterSchema("scoreKey", "得分变量名", "string", "VisionScore", "匹配得分写入上下文的键"),
            new ParameterSchema("worldXKey", "物理X变量名", "string", "VisionWorldX", "标定后物理X写入上下文的键"),
            new ParameterSchema("worldYKey", "物理Y变量名", "string", "VisionWorldY", "标定后物理Y写入上下文的键"),
            new ParameterSchema("worldAngleKey", "物理角度变量名", "string", "VisionWorldAngle", "标定后物理角度写入上下文的键"),
        };

        public override async Task<NodeExecutionResult> ExecuteAsync(NodeExecutionContext ctx, CancellationToken ct)
        {
            var provider = ctx.Services?.GetService(typeof(IVisionProvider)) as IVisionProvider;
            if (provider == null)
            {
                return NodeExecutionResult.Failed("视觉提供者(IVisionProvider)未注入：请在创建 FlowEngineV2 时传入 services 容器");
            }

            string cameraId = ctx.GetParameter<string>("cameraId", "Cam1") ?? "Cam1";
            int timeoutMs = ctx.GetParameter<int>("timeoutMs", 5000);
            if (timeoutMs <= 0) timeoutMs = 5000;

            string okKey = ctx.GetParameter<string>("okKey", "VisionOk") ?? "VisionOk";
            string xKey = ctx.GetParameter<string>("xKey", "VisionX") ?? "VisionX";
            string yKey = ctx.GetParameter<string>("yKey", "VisionY") ?? "VisionY";
            string angleKey = ctx.GetParameter<string>("angleKey", "VisionAngle") ?? "VisionAngle";
            string scoreKey = ctx.GetParameter<string>("scoreKey", "VisionScore") ?? "VisionScore";
            string worldXKey = ctx.GetParameter<string>("worldXKey", "VisionWorldX") ?? "VisionWorldX";
            string worldYKey = ctx.GetParameter<string>("worldYKey", "VisionWorldY") ?? "VisionWorldY";
            string worldAngleKey = ctx.GetParameter<string>("worldAngleKey", "VisionWorldAngle") ?? "VisionWorldAngle";

            var tcs = new TaskCompletionSource<VisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            Guid requestId = Guid.Empty;

            void Handler(VisionResult r)
            {
                if (r.RequestId == requestId)
                {
                    tcs.TrySetResult(r);
                }
            }

            provider.ResultReady += Handler;
            requestId = provider.Trigger(cameraId);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() =>
            {
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                else if (timeoutCts.IsCancellationRequested) tcs.TrySetException(new TimeoutException($"视觉测量超时({timeoutMs}ms)"));
            });

            try
            {
                var result = await tcs.Task;
                ctx.LogInfo($"视觉测量完成: Ok={result.Ok}, 像素=({result.X:F2},{result.Y:F2},{result.AngleDeg:F2}°), 物理=({result.WorldX:F2},{result.WorldY:F2}), 得分={result.Score:F3}");

                ctx.SetVariable(okKey, result.Ok);
                ctx.SetVariable(xKey, result.X);
                ctx.SetVariable(yKey, result.Y);
                ctx.SetVariable(angleKey, result.AngleDeg);
                ctx.SetVariable(scoreKey, result.Score);
                ctx.SetVariable(worldXKey, result.WorldX);
                ctx.SetVariable(worldYKey, result.WorldY);
                ctx.SetVariable(worldAngleKey, result.WorldAngleDeg);

                return NodeExecutionResult.Completed("Out");
            }
            catch (OperationCanceledException)
            {
                return NodeExecutionResult.Failed("视觉测量被取消");
            }
            catch (TimeoutException tex)
            {
                return NodeExecutionResult.Failed(tex.Message);
            }
            catch (Exception ex)
            {
                return NodeExecutionResult.Failed($"视觉测量失败: {ex.Message}");
            }
            finally
            {
                provider.ResultReady -= Handler;
            }
        }
    }
}