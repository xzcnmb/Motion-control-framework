using Common;
using DryIoc;
using Prism.Ioc;
using Sophon.Common;
using System;
using System.Configuration;
using System.Linq;
using System.Reflection;

namespace Sophon.Infrastructure
{
    public static class InfrastructureModuleRegister
    {
        public static void RegisterInfrastructure(this IContainerRegistry containerRegistry)
        {
            var container = ((IContainerExtension<IContainer>)containerRegistry).Instance;
            var assembly = typeof(InfrastructureModuleRegister).Assembly;
            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsDefined(typeof(InjectableAttribute), false));
            var SingletonTypes = types
                 .Where(t => t.GetCustomAttribute<InjectableAttribute>().Lifetime == DependencyLifetime.Singleton).ToList();

            container.RegisterMany(SingletonTypes, Reuse.Singleton);

            container.RegisterDelegate<DbContext>(c =>
            {
                var factory = c.Resolve<ILoggerFactory>();
                string connstr = DbPathProvider.BuildConnectionString();
                return new DbContext(connstr, factory);
            }, Reuse.Singleton);

            container.RegisterDelegate<ICardFactory>(c =>
            {
                string brand = ConfigurationManager.AppSettings["CardBrand"];

                switch (brand)
                {
                    case "LeadShine":
                        return new LeadShineFactory();

                    case "GoogolTech":
                        return new GoogolTechFactory();

                    case null:
                        // 未配置板卡品牌：回退到 LeadShine 空壳驱动保证程序可启动（仅供无卡调试，
                        // Wave2-B 将改为 DriverKind 配置 + 内置 Sim 控制器，且不静默回退）
                        c.Resolve<ILoggerFactory>().CreateLogger("Hardware")
                            .Warn("未配置板卡品牌(CardBrand)，暂时回退 LeadShine 空壳驱动（未接真卡）");
                        return new LeadShineFactory();
                }
                throw new Exception("未知板卡品牌");
            }, Reuse.Singleton);

            container.RegisterDelegate<IAxisController>(c =>
              c.Resolve<ICardFactory>().CreateAxisController(), Reuse.Singleton);
            container.RegisterDelegate<IIoController>(c =>
              c.Resolve<ICardFactory>().CreateIoController(), Reuse.Singleton);
        }
    }
}