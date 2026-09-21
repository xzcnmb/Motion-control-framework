using System;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Vision.Providers;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class SimVisionProviderTests
    {
        [Fact]
        public async Task Trigger_ShouldReturnRequestId_AndInvokeResultReadyWithinNoise()
        {
            using var provider = new SimVisionProvider(new[] { "CamTest" });
            await provider.ConnectAsync();

            double gtX = 200.0;
            double gtY = 150.0;
            double gtAngle = 12.5;
            double noise = 0.2; // 0.2 px 噪声

            provider.SetGroundTruth(gtX, gtY, gtAngle);
            provider.SetNoiseLevel(noise);

            var tcs = new TaskCompletionSource<VisionResult>();
            provider.ResultReady += res =>
            {
                if (res.CameraId == "CamTest")
                {
                    tcs.TrySetResult(res);
                }
            };

            Guid reqId = provider.Trigger("CamTest");

            // 等待触发结果返回（SimProvider 模拟 20~80ms）
            var result = await Task.WhenAny(tcs.Task, Task.Delay(1500)) == tcs.Task
                ? await tcs.Task
                : null;

            Assert.NotNull(result);
            Assert.Equal(reqId, result.RequestId);
            Assert.Equal("CamTest", result.CameraId);
            Assert.True(result.Ok);
            Assert.True(result.Score > 0.8);

            // 验证测量值与真值在噪声 3-sigma 范围内 (0.2 * 3 = 0.6)
            Assert.InRange(result.X, gtX - 1.0, gtX + 1.0);
            Assert.InRange(result.Y, gtY - 1.0, gtY + 1.0);
            Assert.InRange(result.AngleDeg, gtAngle - 1.0, gtAngle + 1.0);
        }

        [Fact]
        public async Task Trigger_WithFailureInjected_ShouldReturnOkFalse()
        {
            using var provider = new SimVisionProvider(new[] { "CamTest" });
            await provider.ConnectAsync();

            provider.InjectFailure(true);

            var tcs = new TaskCompletionSource<VisionResult>();
            provider.ResultReady += res => tcs.TrySetResult(res);

            provider.Trigger("CamTest");

            var result = await Task.WhenAny(tcs.Task, Task.Delay(1000)) == tcs.Task
                ? await tcs.Task
                : null;

            Assert.NotNull(result);
            Assert.False(result.Ok);
        }

        [Fact]
        public async Task StartLive_ShouldPublishFrames_WithConfiguredDimensions()
        {
            using var provider = new SimVisionProvider();
            provider.ConfigureImage(320, 240, fps: 30);

            var tcs = new TaskCompletionSource<VisionFrame>();
            provider.FrameReady += frame =>
            {
                tcs.TrySetResult(frame);
            };

            provider.StartLive("SimCam1");

            var frame = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task
                ? await tcs.Task
                : null;

            provider.StopLive();

            Assert.NotNull(frame);
            Assert.Equal(320, frame.Width);
            Assert.Equal(240, frame.Height);
            Assert.Equal(320 * 240, frame.GrayscaleData.Length);
        }
    }
}
