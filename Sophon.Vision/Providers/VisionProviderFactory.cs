using System;
using System.Collections.Generic;
using Sophon.Contracts;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// 视觉提供者工厂。
    /// 根据配置快速构造 SimVisionProvider 或 OpenCvVisionProvider。
    /// </summary>
    public static class VisionProviderFactory
    {
        /// <summary>
        /// 创建仿真视觉提供者。
        /// </summary>
        /// <param name="cameras">相机列表</param>
        public static IVisionProvider CreateSimProvider(IEnumerable<string>? cameras = null)
        {
            return new SimVisionProvider(cameras);
        }

        /// <summary>
        /// 创建基于 OpenCv 的视觉提供者（结合指定图像源）。
        /// </summary>
        /// <param name="frameSource">图像源</param>
        /// <param name="config">匹配参数配置</param>
        /// <param name="cameras">相机列表</param>
        public static IVisionProvider CreateOpenCvProvider(
            IFrameSource frameSource,
            OpenCvVisionConfig? config = null,
            IEnumerable<string>? cameras = null)
        {
            return new OpenCvVisionProvider(frameSource, config, cameras);
        }

        /// <summary>
        /// 创建基于本地目录轮询的 OpenCv 视觉提供者。
        /// </summary>
        public static IVisionProvider CreateDirectoryWatchingProvider(
            string directoryPath,
            OpenCvVisionConfig? config = null,
            IEnumerable<string>? cameras = null)
        {
            var source = new DirectoryFrameSource(directoryPath);
            return new OpenCvVisionProvider(source, config, cameras);
        }

        /// <summary>
        /// 创建基于内存源的 OpenCv 视觉提供者（测试和动态注入用）。
        /// </summary>
        public static (IVisionProvider Provider, InMemoryFrameSource Source) CreateInMemoryProvider(
            OpenCvVisionConfig? config = null,
            IEnumerable<string>? cameras = null)
        {
            var source = new InMemoryFrameSource();
            var provider = new OpenCvVisionProvider(source, config, cameras);
            return (provider, source);
        }

        /// <summary>
        /// 根据 CameraConfig 创建对应的图像源 (IFrameSource)。
        /// 若 Vendor == HikvisionMvs 则创建 HikvisionFrameSource；
        /// 若 Vendor == Simulated 则根据 DeviceKey 判定为目录源或内存源。
        /// </summary>
        /// <param name="cameraConfig">相机配置</param>
        /// <param name="sdkDllPath">可选指定的 SDK DLL 路径（针对海康相机）</param>
        public static IFrameSource CreateFrameSource(CameraConfig cameraConfig, string? sdkDllPath = null)
        {
            if (cameraConfig == null) throw new ArgumentNullException(nameof(cameraConfig));

            switch (cameraConfig.Vendor)
            {
                case CameraVendor.HikvisionMvs:
                    return new HikvisionFrameSource(cameraConfig, sdkDllPath);

                case CameraVendor.Simulated:
                default:
                    if (!string.IsNullOrWhiteSpace(cameraConfig.DeviceKey) && System.IO.Directory.Exists(cameraConfig.DeviceKey))
                    {
                        return new DirectoryFrameSource(cameraConfig.DeviceKey);
                    }
                    return new InMemoryFrameSource();
            }
        }

        /// <summary>
        /// 根据 CameraConfig 创建对应的视觉提供者 (IVisionProvider)。
        /// 若 Vendor == Simulated 且未指定图片目录，则直接创建轻量级 SimVisionProvider；
        /// 否则结合 CreateFrameSource 构造搭载 OpenCvVisionProvider 的视觉服务。
        /// </summary>
        /// <param name="cameraConfig">相机配置</param>
        /// <param name="visionConfig">OpenCv 视觉匹配配置</param>
        /// <param name="sdkDllPath">可选指定的 SDK DLL 路径（针对海康相机）</param>
        public static IVisionProvider CreateProvider(
            CameraConfig cameraConfig,
            OpenCvVisionConfig? visionConfig = null,
            string? sdkDllPath = null)
        {
            if (cameraConfig == null) throw new ArgumentNullException(nameof(cameraConfig));

            if (cameraConfig.Vendor == CameraVendor.Simulated && string.IsNullOrWhiteSpace(cameraConfig.DeviceKey))
            {
                return CreateSimProvider(new[] { cameraConfig.Name });
            }

            var frameSource = CreateFrameSource(cameraConfig, sdkDllPath);
            return CreateOpenCvProvider(frameSource, visionConfig, new[] { cameraConfig.Name });
        }
    }
}
