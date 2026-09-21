#nullable enable
using System;
using System.Linq;
using Prism.Ioc;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Sophon.UI.ViewModels.Vision;
using Sophon.UI.Views.Vision;
using Sophon.UI.Vision;

namespace Sophon.UI
{
    /// <summary>
    /// 视觉 UI 模块依赖注入注册扩展。
    /// </summary>
    public static class VisionUiModuleRegister
    {
        public static IContainerRegistry RegisterVisionUi(this IContainerRegistry containerRegistry)
        {
            if (containerRegistry == null)
            {
                throw new ArgumentNullException(nameof(containerRegistry));
            }

            containerRegistry.RegisterForNavigation<VisionMonitorView, VisionMonitorViewModel>("VisionMonitorView");
            containerRegistry.RegisterForNavigation<VisionCalibrationView, VisionCalibrationViewModel>("VisionCalibrationView");
            containerRegistry.RegisterForNavigation<CameraConfigView, CameraConfigViewModel>("CameraConfigView");

            var store = new CameraConfigStore();
            var configs = store.Load();
            if (configs.Count == 0)
            {
                configs = CameraConfigStore.SeedDefaults();
                store.Save(configs);
            }

            var switchable = new SwitchableVisionProvider();
            var first = configs.FirstOrDefault(c =>
                c.Vendor != CameraVendor.Simulated &&
                !string.IsNullOrWhiteSpace(c.DeviceKey));
            if (first != null)
            {
                try
                {
                    switchable.Apply(first);
                }
                catch
                {
                }
            }

            containerRegistry.RegisterInstance(store);
            containerRegistry.RegisterInstance(switchable);
            containerRegistry.RegisterInstance<IVisionProvider>(switchable);

            return containerRegistry;
        }
    }
}
