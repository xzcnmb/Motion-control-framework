using Common;
using DryIoc;
using Newtonsoft.Json;
using NLog;
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
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Sophon.UI
{
    public partial class App : PrismApplicationBase
    {
        private static readonly Logger BootstrapLogger = LogManager.GetLogger("App.Bootstrap");

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
            regionManager.RequestNavigate("ContentRegion", "HomeView");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Prism 在 base.OnStartup 内完成容器注册、Shell 创建和 OnInitialized 调用。
            // 先安装全局边界，保证启动阶段的配置/驱动异常也能留下完整证据。
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            ConfigureJsonSettings();
            VerifyLoggingTarget();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                BootstrapLogger.Info("应用正在退出");
                LogManager.Flush(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                DispatcherUnhandledException -= OnDispatcherUnhandledException;
                base.OnExit(e);
            }
        }

        private static void ConfigureJsonSettings()
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                Converters = new List<JsonConverter>
                {
                    new ProtocolConfigConverter()
                }
            };
        }

        private static void VerifyLoggingTarget()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NLog.config");
                if (!File.Exists(configPath))
                {
                    BootstrapLogger.Error($"未找到 NLog.config：{configPath}");
                    return;
                }

                BootstrapLogger.Info("日志系统已启动");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"日志自检失败: {ex}");
            }
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            BootstrapLogger.Fatal(e.Exception, "UI 未处理异常");
            try
            {
                HandyControl.Controls.Growl.Error($"界面发生未处理异常，详情已写入日志：{e.Exception.Message}");
            }
            catch
            {
                MessageBox.Show(e.Exception.Message, "界面异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // UI 线程异常后的状态不可假定仍然一致，不继续吞掉异常运行。
            e.Handled = false;
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                BootstrapLogger.Fatal(ex, $"后台未处理异常（IsTerminating={e.IsTerminating}）");
            }
            else
            {
                BootstrapLogger.Fatal($"后台未处理异常：{e.ExceptionObject}");
            }
        }

        private static void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            BootstrapLogger.Error(e.Exception, "未观察到的后台任务异常");
            e.SetObserved();
        }
    }
}
