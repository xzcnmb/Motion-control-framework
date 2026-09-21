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
            if (profile.Driver == DriverKind.Simulated || profile.Driver == DriverKind.ZmotionZmc)
            {
                profile.Driver = DriverKind.GoogolGts;
                if (string.IsNullOrWhiteSpace(profile.CardModel) ||
                    profile.CardModel.IndexOf("Sim", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    profile.CardModel.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    profile.CardModel = "GTS-400";
                }
                profileStore.Save(profiles);
                profileStore.SetActive(profile.ProfileName);
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
