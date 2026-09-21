#nullable enable
using System;
using System.Collections.Generic;
using DryIoc;
using Prism.Ioc;
using Sophon.Contracts;
using Sophon.Core;
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion.Axis;
using Sophon.Infrastructure.Motion.Drivers;
using Sophon.UI.ViewModels.Axis;
using Sophon.UI.ViewModels.FlowEditor;
using Sophon.UI.Views.Axis;
using Sophon.UI.Views.FlowEditor;

namespace Sophon.UI
{
    public static class ShellUiModuleRegister
    {
        public static void RegisterShellUi(this IContainerRegistry containerRegistry)
        {
            var profileStore = new MotionCardProfileStore();
            var profiles = profileStore.Load();
            if (profiles.Count == 0)
            {
                profiles = MotionCardProfileStore.SeedDefaults();
                profileStore.Save(profiles);
                profileStore.SetActive(profiles[0].ProfileName);
            }

            var profile = profileStore.GetActive() ?? profiles[0];
            if (profile.Driver == DriverKind.Simulated)
            {
                var gts400 = MotionCardCatalog.Find("GTS-400")
                             ?? throw new InvalidOperationException("选型目录缺少 GTS-400。");
                profile.ApplyModel(gts400);
                profileStore.Save(profiles);
                profileStore.SetActive(profile.ProfileName);
            }
            else
            {
                var catalog = MotionCardCatalog.Resolve(profile);
                if (catalog != null)
                {
                    profile.ApplyModel(catalog);
                }
            }

            if (!MotionCardCatalog.IsImplemented(profile.Driver))
            {
                throw new NotSupportedException(
                    MotionCardCatalog.NotImplementedMessage(profile.Driver, profile.CardModel)
                    + " 请在控制卡配置页改选已实现的脉冲卡（固高 GTS / 雷赛 DMC）。");
            }

            IReadOnlyList<AxisDefinition> axes = profile.Axes;
            if (axes == null || axes.Count == 0)
            {
                axes = AxisManager.LoadConfiguration();
            }
            if (axes == null || axes.Count == 0)
            {
                axes = MotionCardProfileStore.SeedDefaults()[0].Axes;
            }

            IMotionController controller = MotionControllerFactory.Create(
                profile.Driver,
                axes,
                allowSimFallback: false);

            var io = new IoMappingManager(
                null,
                MotionCardIoBridge.CreateDiReader(profile.Driver),
                MotionCardIoBridge.CreateDoWriter(profile.Driver));

            containerRegistry.RegisterInstance(profileStore);
            containerRegistry.RegisterInstance<IMotionController>(controller);
            containerRegistry.RegisterInstance<IIoController>(io);
            containerRegistry.RegisterSingleton<AxisManager>();
            containerRegistry.RegisterSingleton<IEventBus>(c => EventBus.GetInstance());

            containerRegistry.RegisterForNavigation<AxisDebugView, AxisDebugViewModel>("AxisDebugView");
            containerRegistry.RegisterForNavigation<FlowEditorView, FlowEditorViewModel>("FlowEditorView");
        }
    }
}
