using System;
using System.IO;
using System.Threading.Tasks;
using OpenCvSharp;
using Sophon.Contracts;
using Sophon.Vision.Providers;
using Sophon.Vision.Utilities;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class OpenCvVisionProviderTests
    {
        [Fact]
        public void MatchTemplate_SyntheticImage_ShouldLocateWithinSubpixelAccuracy()
        {
            // 构造 60x60 的模板图像：白底，中央有一个黑圆和交叉十字
            int tplW = 60;
            int tplH = 60;
            using var tplMat = new Mat(tplH, tplW, MatType.CV_8UC1, new Scalar(255));
            Cv2.Circle(tplMat, new Point(tplW / 2, tplH / 2), 16, new Scalar(0), -1);
            Cv2.Circle(tplMat, new Point(tplW / 2, tplH / 2), 8, new Scalar(255), -1);
            Cv2.Line(tplMat, new Point(tplW / 2 - 20, tplH / 2), new Point(tplW / 2 + 20, tplH / 2), new Scalar(0), 2);
            Cv2.Line(tplMat, new Point(tplW / 2, tplH / 2 - 20), new Point(tplW / 2, tplH / 2 + 20), new Scalar(0), 2);

            // 构造大场景图 640x480，白底
            int sceneW = 640;
            int sceneH = 480;
            using var sceneMat = new Mat(sceneH, sceneW, MatType.CV_8UC1, new Scalar(255));

            // 将模板放置在场景的目标中心位置 (TargetCenterX = 312.0, TargetCenterY = 235.0)
            int targetTopLeftX = 312 - tplW / 2; // 282
            int targetTopLeftY = 235 - tplH / 2; // 205
            double expectedCenterX = 312.0;
            double expectedCenterY = 235.0;

            var targetRoi = new Rect(targetTopLeftX, targetTopLeftY, tplW, tplH);
            tplMat.CopyTo(new Mat(sceneMat, targetRoi));

            // 使用内存源构建 OpenCvVisionProvider
            var (provider, memSource) = VisionProviderFactory.CreateInMemoryProvider(
                new OpenCvVisionConfig
                {
                    MinScore = 0.8,
                    MatchMethod = TemplateMatchModes.CCoeffNormed
                },
                new[] { "CamOpenCv" }
            );

            using (provider)
            {
                var ocvProvider = (OpenCvVisionProvider)provider;
                ocvProvider.SetTemplate(tplMat);
                memSource.SetFrame(sceneMat);

                Guid reqId = Guid.NewGuid();
                var result = ocvProvider.ExecuteMatch(reqId, "CamOpenCv");

                Assert.True(result.Ok, $"匹配失败，Score: {result.Score}");
                Assert.True(result.Score >= 0.95, $"匹配得分应接近1.0，实际: {result.Score}");

                // 检验亚像素精度在 0.5px 内
                double errorX = Math.Abs(result.X - expectedCenterX);
                double errorY = Math.Abs(result.Y - expectedCenterY);

                Assert.True(errorX <= 0.5, $"X坐标定位误差超标: {errorX} px (预期={expectedCenterX}, 实际={result.X})");
                Assert.True(errorY <= 0.5, $"Y坐标定位误差超标: {errorY} px (预期={expectedCenterY}, 实际={result.Y})");
            }
        }

        [Fact]
        public async Task Trigger_WithInMemorySource_ShouldTriggerResultReady()
        {
            using var tplMat = new Mat(40, 40, MatType.CV_8UC1, new Scalar(255));
            Cv2.Circle(tplMat, new Point(20, 20), 10, new Scalar(50), -1);

            using var sceneMat = new Mat(200, 200, MatType.CV_8UC1, new Scalar(255));
            tplMat.CopyTo(new Mat(sceneMat, new Rect(80, 80, 40, 40)));

            var (provider, memSource) = VisionProviderFactory.CreateInMemoryProvider();
            using (provider)
            {
                var ocvProvider = (OpenCvVisionProvider)provider;
                ocvProvider.SetTemplate(tplMat);
                memSource.SetFrame(sceneMat);

                var tcs = new TaskCompletionSource<VisionResult>();
                provider.ResultReady += res => tcs.TrySetResult(res);

                Guid reqId = provider.Trigger("Camera1");
                var result = await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task
                    ? await tcs.Task
                    : null;

                Assert.NotNull(result);
                Assert.Equal(reqId, result.RequestId);
                Assert.True(result.Ok);
                Assert.InRange(result.X, 99.5, 100.5); // 中心应在 80 + 20 = 100
                Assert.InRange(result.Y, 99.5, 100.5);
            }
        }

        [Fact]
        public void DirectoryFrameSource_ShouldGrabLatestImageFile()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "SophonDirSource_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string imgFile1 = Path.Combine(tempDir, "img1.png");
                string imgFile2 = Path.Combine(tempDir, "img2.png");

                using (var mat1 = new Mat(100, 100, MatType.CV_8UC1, new Scalar(100)))
                {
                    mat1.SaveImage(imgFile1);
                }

                // 稍微延时确保 LastWriteTime 差异
                System.Threading.Thread.Sleep(50);

                using (var mat2 = new Mat(120, 150, MatType.CV_8UC1, new Scalar(200)))
                {
                    mat2.SaveImage(imgFile2);
                }

                using var dirSource = new DirectoryFrameSource(tempDir);
                bool success = dirSource.TryGrab(out var grabbedMat);

                Assert.True(success);
                Assert.NotNull(grabbedMat);
                using (grabbedMat)
                {
                    // 应当抓取到最新的 img2 (150x120)
                    Assert.Equal(150, grabbedMat.Width);
                    Assert.Equal(120, grabbedMat.Height);
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void ImageBufferUtils_ToGrayscaleBytes_ShouldConvertCorrectly()
        {
            using var bgrMat = new Mat(50, 60, MatType.CV_8UC3, new Scalar(100, 150, 200));
            byte[] bytes = ImageBufferUtils.ToGrayscaleBytes(bgrMat);

            Assert.Equal(50 * 60, bytes.Length);

            var vf = ImageBufferUtils.ToVisionFrame(bgrMat);
            Assert.Equal(60, vf.Width);
            Assert.Equal(50, vf.Height);
            Assert.Equal(50 * 60, vf.GrayscaleData.Length);
        }
    }
}
