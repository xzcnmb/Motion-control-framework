using System;
using Sophon.Contracts;
using Sophon.Vision.Providers;
using Xunit;

namespace Sophon.Vision.Tests
{
    public class HikvisionFrameSourceTests
    {
        [Fact]
        public void HikvisionFrameSource_WhenSdkDllMissing_ShouldHonestFailWithoutSimulation()
        {
            var config = new CameraConfig
            {
                Name = "HikCam1",
                Vendor = CameraVendor.HikvisionMvs,
                DeviceKey = "0",
                TriggerMode = CameraTriggerMode.Software,
                ExposureUs = 5000,
                Gain = 1.0,
                OptimizePacketSize = true,
                GrabTimeoutMs = 500,
                FlipY = false
            };

            // 构造真实的海康图像源，在无实际海康 SDK 的机器上应严格诚实失败，禁止静默仿真
            using var source = new HikvisionFrameSource(config);

            Assert.False(source.IsAvailable, "未安装 MVS SDK 时 IsAvailable 必须为 false");
            Assert.False(source.IsOpen, "未连接真实硬件时 IsOpen 必须为 false");
            Assert.False(source.IsGrabbing, "未采集时 IsGrabbing 必须为 false");
            Assert.NotNull(source.LastError);
            Assert.Contains("MvCamCtrl.NET.dll", source.LastError);

            // 尝试采图时必须返回 false 且不能虚构图像帧
            bool grabSuccess = source.TryGrab(out var frame);
            Assert.False(grabSuccess);
            Assert.Null(frame);

            // EnsureOpen 必须抛出明确的强类型异常
            var ex = Assert.Throws<HikvisionCameraException>(() => source.EnsureOpen());
            Assert.Contains("海康相机不可用", ex.Message);
        }

        [Fact]
        public void VisionProviderFactory_CreateFrameSource_ShouldSupportBothVendors()
        {
            var hikConfig = new CameraConfig
            {
                Name = "HikTest",
                Vendor = CameraVendor.HikvisionMvs
            };

            using var hikSource = VisionProviderFactory.CreateFrameSource(hikConfig);
            Assert.IsType<HikvisionFrameSource>(hikSource);

            var simConfig = new CameraConfig
            {
                Name = "SimTest",
                Vendor = CameraVendor.Simulated
            };

            using var simSource = VisionProviderFactory.CreateFrameSource(simConfig);
            Assert.IsType<InMemoryFrameSource>(simSource);
        }

        [Fact]
        public void VisionProviderFactory_CreateProvider_HikvisionMvs_ShouldBuildOpenCvProvider()
        {
            var hikConfig = new CameraConfig
            {
                Name = "HikCam01",
                Vendor = CameraVendor.HikvisionMvs
            };

            using var provider = VisionProviderFactory.CreateProvider(hikConfig);
            Assert.NotNull(provider);
            Assert.IsType<OpenCvVisionProvider>(provider);
            Assert.Contains("HikCam01", provider.Cameras);
        }

        [Fact]
        public void VisionProviderFactory_CreateProvider_Simulated_ShouldBuildSimProvider()
        {
            var simConfig = new CameraConfig
            {
                Name = "SimCam01",
                Vendor = CameraVendor.Simulated
            };

            using var provider = VisionProviderFactory.CreateProvider(simConfig);
            Assert.NotNull(provider);
            Assert.IsType<SimVisionProvider>(provider);
            Assert.Contains("SimCam01", provider.Cameras);
        }
    }
}
