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

            // 旧档案（只有 Driver + CardModel 等字段）先按选型目录归一化，厂商/接口/系列与型号对齐。
            profile = MotionCardProfileStore.Normalize(profile);

            // 总线卡（固高 GEN/GE、雷赛 DMC-E/EMC/PAC、正运动 EtherCAT）与 ZMC 至今没有适配器：
            // 宁可拒绝启动，也绝不用脉冲 DLL 打开总线卡，更不静默回退 Sim。
            var catalogModel = MotionCardCatalog.Find(profile.CardModel);
            if (catalogModel != null)
            {
                // 目录命中的型号才是权威来源。手改 / 陈旧 JSON 会出现「Driver=Simulated、CardModel=GTS-400」这类自相矛盾的档案：
                // 只判型号是否已实现，就会静默按档案 Driver 造出 Sim，必须在启动前明确拒绝。
                if (catalogModel.Driver != profile.Driver)
                {
                    throw new InvalidOperationException(
                        $"控制卡档案「{profile.ProfileName}」的型号 {catalogModel.Model} 对应驱动是{MotionCardCatalog.Display(catalogModel.Driver)}，" +
                        $"与档案里填的驱动{MotionCardCatalog.Display(profile.Driver)}不一致，禁止启动。" +
                        "请回到板卡配置页（打开板卡配置）按选型目录重新选择该型号，不要手改或沿用陈旧 JSON。");
                }

                if (!catalogModel.IsImplemented)
                {
                    throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(catalogModel.Driver, catalogModel.Model));
                }
            }
            else if (!string.IsNullOrWhiteSpace(profile.CardModel))
            {
                // 型号非空却不在目录：不得按 Driver 猜一个型号继续启动
                // （拿脉冲 DLL 打开不认识的卡，比直接拒绝启动更危险）。
                throw new InvalidOperationException(
                    $"控制卡档案「{profile.ProfileName}」的型号 {profile.CardModel} 不在选型目录中，禁止启动。" +
                    "请回到板卡配置页（打开板卡配置）按「厂商 → 脉冲/总线 → 系列 → 型号」重新选择，不要按驱动推断型号。");
            }
            else if (!MotionCardCatalog.IsImplemented(profile.Driver))
            {
                throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(profile.Driver, profile.CardModel));
            }

            IReadOnlyList<AxisDefinition> axes = profile.Axes;
            if (axes == null || axes.Count == 0)
            {
                axes = AxisManager.LoadConfiguration();
            }
            if (axes == null || axes.Count == 0)
            {
                throw new InvalidOperationException($"控制卡档案「{profile.ProfileName}」没有轴配置，已拒绝启动，避免使用未确认的默认行程参数。");
            }

            // 先建 IO 适配器，再创建运动控制器：SimMotionController 需要注入 ioController，
            // 否则仿真链路里收到的一直是 null。
            var io = new IoMappingManager(
                null,
                MotionCardIoBridge.CreateDiReader(profile.Driver),
                MotionCardIoBridge.CreateDoWriter(profile.Driver));

            IMotionController controller = MotionControllerFactory.Create(
                profile.Driver,
                axes,
                ioController: io,
                allowSimFallback: false);

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
