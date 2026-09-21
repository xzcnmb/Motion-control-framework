using Common;
using DryIoc;
using Newtonsoft.Json;
using Prism;
using Prism.Container.DryIoc;
using Prism.Ioc;
using Prism.Navigation.Regions;
using Sophon.Application;
using Sophon.Contracts;
using Sophon.Core;
using Sophon.Infrastructure;
using Sophon.UI.Views;
using Sophon.UI.Views.SubViews;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace Sophon.UI
{
    public partial class App : PrismApplicationBase
    {
        protected override IContainerExtension CreateContainerExtension()
        {
            return new DryIocContainerExtension();
        }

        protected override Window CreateShell()
        {
            Container.Resolve<IDatabaseInitializer>().Initialize();
            return Container.Resolve<MainWindow>();
        }

        protected override void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterCommon();
            containerRegistry.RegisterInfrastructure();
            containerRegistry.RegisterCore();
            containerRegistry.RegisterApplication();
            containerRegistry.RegisterShellUi();
            containerRegistry.RegisterAlarmTeachUi();
            containerRegistry.RegisterVisionUi();

            containerRegistry.RegisterSingleton<Sophon.Core.Device.CylinderService>();
            containerRegistry.RegisterSingleton<Sophon.Core.Device.PeripheralDeviceService>();
            containerRegistry.RegisterSingleton<INavigationGuardService, NavigationGuardService>();
            containerRegistry.RegisterInstance<System.IServiceProvider>(Container.GetContainer());

            containerRegistry.RegisterForNavigation<UserView, ViewModels.UserViewModel>("UserView");
            containerRegistry.RegisterForNavigation<AxisInfrastructureView, ViewModels.AxisInfrastructureViewModel>("AxisInfrastructureView");
            containerRegistry.RegisterForNavigation<IOInfrastructureView, ViewModels.IOInfrastructureViewModel>("IOInfrastructureView");
            containerRegistry.RegisterForNavigation<ProtocolInfrastructureView, ViewModels.ProtocolInfrastructureViewModel>("ProtocolInfrastructureView");
            containerRegistry.RegisterForNavigation<ParamView, ViewModels.ParamViewModel>("ParamView");
            containerRegistry.RegisterForNavigation<HomeView, ViewModels.HomeViewModel>("HomeView");
            containerRegistry.RegisterForNavigation<StationView, ViewModels.StationViewModel>("StationView");
            containerRegistry.RegisterForNavigation<AlarmRegisterView, ViewModels.AlarmRegisterViewModel>("AlarmRegisterView");
            containerRegistry.RegisterForNavigation<AlarmHistoryView, ViewModels.AlarmHistoryViewModel>("AlarmHistoryView");
            containerRegistry.RegisterForNavigation<MotionCardConfigView, ViewModels.MotionCardConfigViewModel>("MotionCardConfigView");
            containerRegistry.RegisterForNavigation<DeviceControlView, ViewModels.DeviceControlViewModel>("DeviceControlView");

            containerRegistry.RegisterDialog<AddParamView, AddParamViewModel>();
        }

        protected override async void OnInitialized()
        {
            base.OnInitialized();

            var axisService = Container.Resolve<ICardRepository>();
            var protocolService = Container.Resolve<IProtocolRepository>();
            var paramService = Container.Resolve<IParamRepository>();
            axisService.LoadAllConfigs();
            protocolService.LoadAllConfigs();
            paramService.LoadAllConfigs();

            try
            {
                var motion = Container.Resolve<IMotionController>();
                if (motion.State == ConnectionState.Disconnected)
                {
                    await motion.ConnectAsync();
                }
            }
            catch (Exception ex)
            {
                var logger = Container.Resolve<ILoggerFactory>().CreateLogger("App");
                logger.Error($"运动控制器连接失败：{ex.Message}");
            }

            var regionManager = Container.Resolve<IRegionManager>();
            try
            {
                if (regionManager.Regions.ContainsRegionWithName("ContentRegion"))
                {
                    var region = regionManager.Regions["ContentRegion"];
                    foreach (var view in System.Linq.Enumerable.ToArray(region.Views))
                    {
                        region.Remove(view);
                    }
                }
            }
            catch
            {
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter>
                {
                    new ProtocolConfigConverter()
                }
            };
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                HandyControl.Controls.Growl.Error($"界面异常已拦截，未退出：{e.Exception.Message}");
            }
            catch
            {
                MessageBox.Show(e.Exception.Message, "界面异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            e.Handled = true;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                try
                {
                    HandyControl.Controls.Growl.Error($"后台异常：{ex.Message}");
                }
                catch { }
            }
        }
    }
}
