using System;
using System.Collections.Generic;
using System.Linq;

namespace Sophon.Contracts
{
    /// <summary>
    /// 适配器落地状态。目录里可以有尚未写适配器的型号，禁止把它们当成已接入。
    /// </summary>
    public enum MotionAdapterStatus
    {
        /// <summary>仓库有 P/Invoke 骨架，未经真机验证。</summary>
        ImplementedUnverified,
        /// <summary>仅产品目录，工厂必须拒绝。</summary>
        CatalogOnly,
    }

    /// <summary>
    /// 一条控制卡型号记录。来源：固高 / 雷赛 / 正运动官网产品线，不是营销文案合并。
    /// </summary>
    public sealed class MotionCardModelDescriptor
    {
        public MotionVendor Vendor { get; init; }
        public MotionCommandInterface CommandInterface { get; init; }
        public MotionHostLink HostLink { get; init; }
        public string Series { get; init; } = string.Empty;
        public string SeriesDisplay { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public int MaxAxes { get; init; }
        public DriverKind Driver { get; init; }
        public string NativeLibrary { get; init; } = string.Empty;
        public MotionAdapterStatus Adapter { get; init; }
        public AccelParamKind Accel { get; init; } = AccelParamKind.AccelerationValue;
        public int AxisIndexBase { get; init; }
        public bool RequiresConfigFile { get; init; }
        public bool SupportsBufferedSegments { get; init; } = true;
        public string Notes { get; init; } = string.Empty;

        public bool UsesConnectionString => HostLink == MotionHostLink.Ethernet;

        public bool IsImplemented => Adapter == MotionAdapterStatus.ImplementedUnverified;

        public PlatformOptions CreatePlatform() => new()
        {
            AxisIndexBase = AxisIndexBase,
            Accel = Accel,
            RequiresConfigFile = RequiresConfigFile,
            UsesConnectionString = UsesConnectionString,
            SupportsBufferedSegments = SupportsBufferedSegments,
        };
    }

    /// <summary>
    /// 控制卡选型目录。按「厂商 → 脉冲/总线 → 系列 → 型号」展开。
    /// 雷赛脉冲卡走 LTDMC，EtherCAT 卡不是同一套 API；固高 GTS 与 GEN 同理。
    /// </summary>
    public static class MotionCardCatalog
    {
        private const string GtsDll = "gts.dll";
        private const string LtdmcDll = "LTDMC.dll";
        private const string GenSdk = "GEN SDK（未接入）";
        private const string GeSdk = "GE SDK（未接入）";
        private const string LeadshineEcSdk = "雷赛 EtherCAT SDK（未接入）";
        private const string ZmotionDll = "zmotion.dll（未接入）";

        private static readonly MotionCardModelDescriptor[] All = Build();

        public static IReadOnlyList<MotionCardModelDescriptor> Models => All;

        public static IReadOnlyList<MotionVendor> Vendors { get; } = new[]
        {
            // 第一项是「仿真控制器（无板卡）」：不绑定任何真实控制卡型号，选它即可离线跑通 Sim 链路。
            MotionVendor.Simulated,
            MotionVendor.Googol,
            MotionVendor.LeadShine,
            MotionVendor.Zmotion,
        };

        public static string Display(MotionVendor vendor) => vendor switch
        {
            MotionVendor.Googol => "固高科技",
            MotionVendor.LeadShine => "雷赛智能",
            MotionVendor.Zmotion => "正运动",
            MotionVendor.Simulated => "仿真控制器（无板卡）",
            _ => vendor.ToString(),
        };

        public static string Display(MotionCommandInterface iface) => iface switch
        {
            MotionCommandInterface.Pulse => "脉冲卡（本地脉冲/方向）",
            MotionCommandInterface.EtherCAT => "总线卡（EtherCAT 主站）",
            MotionCommandInterface.GLink => "总线卡（gLink-II 主站）",
            MotionCommandInterface.Analog => "模拟量伺服卡",
            _ => iface.ToString(),
        };

        public static string Display(MotionHostLink link) => link switch
        {
            MotionHostLink.Pci => "PCI",
            MotionHostLink.Pcie => "PCIe",
            MotionHostLink.Ethernet => "以太网",
            MotionHostLink.Usb => "USB",
            MotionHostLink.Simulated => "仿真",
            _ => link.ToString(),
        };

        public static string Display(DriverKind kind) => kind switch
        {
            DriverKind.Simulated => "仿真控制器",
            DriverKind.GoogolGts => "固高 GTS 脉冲/模拟量卡（gts.dll）",
            DriverKind.LeadShineDmc => "雷赛 DMC 脉冲卡（LTDMC.dll）",
            DriverKind.ZmotionZmc => "正运动脉冲/网口控制器（未实现）",
            DriverKind.GoogolGen => "固高 GEN EtherCAT 主站（未实现）",
            DriverKind.GoogolGe => "固高 GE gLink-II 主站（未实现）",
            DriverKind.LeadShineEtherCAT => "雷赛 EtherCAT 总线卡（未实现，不是 LTDMC）",
            DriverKind.ZmotionEtherCAT => "正运动 EtherCAT 主站（未实现）",
            _ => kind.ToString(),
        };

        public static IReadOnlyList<MotionCommandInterface> InterfacesFor(MotionVendor vendor) =>
            All.Where(m => m.Vendor == vendor)
               .Select(m => m.CommandInterface)
               .Distinct()
               .ToList();

        public static IReadOnlyList<string> SeriesFor(MotionVendor vendor, MotionCommandInterface iface) =>
            All.Where(m => m.Vendor == vendor && m.CommandInterface == iface)
               .Select(m => m.Series)
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .ToList();

        public static string SeriesDisplay(string series)
        {
            var match = All.FirstOrDefault(m => string.Equals(m.Series, series, StringComparison.OrdinalIgnoreCase));
            return match?.SeriesDisplay ?? series;
        }

        public static IReadOnlyList<MotionCardModelDescriptor> ModelsFor(
            MotionVendor vendor,
            MotionCommandInterface iface,
            string series) =>
            All.Where(m => m.Vendor == vendor
                           && m.CommandInterface == iface
                           && string.Equals(m.Series, series, StringComparison.OrdinalIgnoreCase))
               .ToList();

        public static MotionCardModelDescriptor? Find(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            return All.FirstOrDefault(m => string.Equals(m.Model, model.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static MotionCardModelDescriptor? FindByDriver(DriverKind driver) =>
            All.FirstOrDefault(m => m.Driver == driver);

        /// <summary>
        /// 用档案里的型号解析目录项。旧档案只有 Driver + CardModel 也能对上。
        /// 型号非空但不在目录时不按 Driver 套第一个型号：保留原档案，交给校验提示用户重新选择。
        /// 只有型号为空的旧档案才允许按 Driver 反推。
        /// </summary>
        public static MotionCardModelDescriptor? Resolve(MotionCardProfile? profile)
        {
            if (profile == null)
            {
                return null;
            }

            var byModel = Find(profile.CardModel);
            if (byModel != null)
            {
                return byModel;
            }

            // 型号非空却不在目录：不得按 Driver 静默套该驱动第一个型号，交校验提示重新选择。
            if (!string.IsNullOrWhiteSpace(profile.CardModel))
            {
                return null;
            }

            // 型号为空的旧档案才允许按驱动种类反推。
            return FindByDriver(profile.Driver);
        }

        public static MotionVendor VendorOf(DriverKind driver) => driver switch
        {
            DriverKind.GoogolGts or DriverKind.GoogolGen or DriverKind.GoogolGe => MotionVendor.Googol,
            DriverKind.LeadShineDmc or DriverKind.LeadShineEtherCAT => MotionVendor.LeadShine,
            DriverKind.ZmotionZmc or DriverKind.ZmotionEtherCAT => MotionVendor.Zmotion,
            _ => MotionVendor.Simulated,
        };

        public static bool IsImplemented(DriverKind driver) =>
            driver is DriverKind.Simulated or DriverKind.GoogolGts or DriverKind.LeadShineDmc;

        public static string NotImplementedMessage(DriverKind driver, string? model = null)
        {
            string label = string.IsNullOrWhiteSpace(model) ? Display(driver) : $"{model}（{Display(driver)}）";
            return driver switch
            {
                DriverKind.LeadShineEtherCAT =>
                    $"{label} 是 EtherCAT 总线主站，和雷赛 DMC 脉冲卡（LTDMC.dll）不是同一套 API。仓库尚未接入总线适配器，禁止当脉冲卡打开。",
                DriverKind.GoogolGen =>
                    $"{label} 是固高 GEN EtherCAT 主站，和 GTS 脉冲卡（gts.dll）不是同一套 API。仓库尚未接入。",
                DriverKind.GoogolGe =>
                    $"{label} 是固高 GE gLink-II 主站，不是 GTS。仓库尚未接入。",
                DriverKind.ZmotionZmc =>
                    $"{label} 仓库里没有正运动脉冲/网口适配器。",
                DriverKind.ZmotionEtherCAT =>
                    $"{label} 是正运动 EtherCAT 主站。仓库尚未接入。",
                _ => $"{label} 当前不支持。",
            };
        }

        private static MotionCardModelDescriptor[] Build()
        {
            var list = new List<MotionCardModelDescriptor>();

            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "GTS", "GTS 脉冲卡（PCI）", "GTS-400", "固高科技 GTS-400（4 轴 PCI 脉冲卡）", 4,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "固高经典 PCI 脉冲卡。轴号从 1 起，必须加载 cfg。未经真机验证。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "GTS", "GTS 脉冲卡（PCI）", "GTS-800", "固高科技 GTS-800（8 轴 PCI 脉冲卡）", 8,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "固高 8 轴 PCI 脉冲卡。未经真机验证。");

            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "GTS-PV", "GTS-PV 脉冲卡（PCI）", "GTS-400-PV", "固高科技 GTS-400-PV（4 轴 PCI 脉冲卡）", 4,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "GTS-PV 系列，仍走 gts.dll。未经真机验证。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "GTS-PV", "GTS-PV 脉冲卡（PCI）", "GTS-800-PV", "固高科技 GTS-800-PV（8 轴 PCI 脉冲卡）", 8,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "GTS-PV 8 轴。未经真机验证。");

            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "GTS-VB", "GTS-VB 脉冲卡（PCIe）", "GTS-400-VB", "固高科技 GTS-400-VB（4 轴 PCIe 脉冲卡）", 4,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "PCIe 脉冲卡，与 PCI GTS 同属 gts.dll 家族，型号资源不同。未经真机验证。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "GTS-VB", "GTS-VB 脉冲卡（PCIe）", "GTS-800-VB", "固高科技 GTS-800-VB（8 轴 PCIe 脉冲卡）", 8,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "PCIe 8 轴脉冲卡。未经真机验证。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "GTS-VB", "GTS-VB 脉冲卡（PCIe）", "GTS-1600-VB", "固高科技 GTS-1600-VB（16 轴 PCIe 脉冲卡）", 16,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "PCIe 16 轴脉冲卡。未经真机验证。");

            Add(list, MotionVendor.Googol, MotionCommandInterface.Analog, MotionHostLink.Pci,
                "GTHD", "GTHD 模拟量伺服卡", "GTHD-400", "固高科技 GTHD-400（4 轴 PCI 模拟量伺服卡）", 4,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "模拟量输出，不是脉冲方向。接线与伺服参数与脉冲卡不同。未经真机验证。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.Analog, MotionHostLink.Pci,
                "GTHD", "GTHD 模拟量伺服卡", "GTHD-800", "固高科技 GTHD-800（8 轴 PCI 模拟量伺服卡）", 8,
                DriverKind.GoogolGts, GtsDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationValue, 1, true,
                "8 轴模拟量。未经真机验证。");

            Add(list, MotionVendor.Googol, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "GEN", "GEN EtherCAT 主站", "GEN-1000-08", "固高科技 GEN-1000-08（8 轴 PCIe EtherCAT 总线卡）", 8,
                DriverKind.GoogolGen, GenSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "EtherCAT 主站，轴在从站驱动器上。不是 GTS 脉冲卡。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "GEN", "GEN EtherCAT 主站", "GEN-1000-16", "固高科技 GEN-1000-16（16 轴 PCIe EtherCAT 总线卡）", 16,
                DriverKind.GoogolGen, GenSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "16 轴 EtherCAT 主站。未接入。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "GEN", "GEN EtherCAT 主站", "GEN-1000-32", "固高科技 GEN-1000-32（32 轴 PCIe EtherCAT 总线卡）", 32,
                DriverKind.GoogolGen, GenSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "32 轴 EtherCAT 主站。未接入。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "GEN", "GEN EtherCAT 主站", "GEN-1000-64", "固高科技 GEN-1000-64（64 轴 PCIe EtherCAT 总线卡）", 64,
                DriverKind.GoogolGen, GenSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "64 轴 EtherCAT 主站。未接入。");

            Add(list, MotionVendor.Googol, MotionCommandInterface.GLink, MotionHostLink.Pcie,
                "GE", "GE gLink-II 主站", "GE-004", "固高科技 GE-004（4 轴 PCIe gLink-II 总线卡）", 4,
                DriverKind.GoogolGe, GeSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "固高私有总线，不是 EtherCAT，也不是 GTS。");
            Add(list, MotionVendor.Googol, MotionCommandInterface.GLink, MotionHostLink.Pcie,
                "GE", "GE gLink-II 主站", "GE-008", "固高科技 GE-008（8 轴 PCIe gLink-II 总线卡）", 8,
                DriverKind.GoogolGe, GeSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "8 轴 gLink-II。未接入。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC1000", "DMC1000 脉冲卡（PCI）", "DMC1020", "雷赛智能 DMC1020（2 轴 PCI 脉冲卡）", 2,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "雷赛入门 PCI 脉冲卡。加减速是时间（Tacc/Tdec）。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC1000", "DMC1000 脉冲卡（PCI）", "DMC1040", "雷赛智能 DMC1040（4 轴 PCI 脉冲卡）", 4,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "4 轴 PCI 脉冲卡。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC1000", "DMC1000 脉冲卡（PCI）", "DMC1080", "雷赛智能 DMC1080（8 轴 PCI 脉冲卡）", 8,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "8 轴 PCI 脉冲卡。未经真机验证。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC3000", "DMC3000 脉冲卡（PCI）", "DMC3020", "雷赛智能 DMC3020（2 轴 PCI 脉冲卡）", 2,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "DMC3000 系列 PCI 脉冲卡。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC3000", "DMC3000 脉冲卡（PCI）", "DMC3040", "雷赛智能 DMC3040（4 轴 PCI 脉冲卡）", 4,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "4 轴。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC3000", "DMC3000 脉冲卡（PCI）", "DMC3080", "雷赛智能 DMC3080（8 轴 PCI 脉冲卡）", 8,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "8 轴。未经真机验证。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC5000", "DMC5000 脉冲卡（PCI）", "DMC5410", "雷赛智能 DMC5410（4 轴 PCI 脉冲卡）", 4,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "DMC5000 点位卡。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC5000", "DMC5000 脉冲卡（PCI）", "DMC5810", "雷赛智能 DMC5810（8 轴 PCI 脉冲卡）", 8,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "8 轴点位卡。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pci,
                "DMC5000", "DMC5000 脉冲卡（PCI）", "DMC5800", "雷赛智能 DMC5800（8 轴 PCI 脉冲卡，旧型号名）", 8,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "兼容旧档案里的 DMC5800 写法，按 8 轴 PCI 脉冲卡处理。未经真机验证。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "DMC5X10", "DMC5X10 脉冲卡（PCIe）", "DMC5X10-08", "雷赛智能 DMC5X10-08（8 轴 PCIe 脉冲卡）", 8,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "PCIe 脉冲卡。不要和 DMC-E5000 EtherCAT 卡搞混。未经真机验证。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "DMC5X10", "DMC5X10 脉冲卡（PCIe）", "DMC5X10-16", "雷赛智能 DMC5X10-16（16 轴 PCIe 脉冲卡）", 16,
                DriverKind.LeadShineDmc, LtdmcDll, MotionAdapterStatus.ImplementedUnverified,
                AccelParamKind.AccelerationTime, 1, false,
                "16 轴 PCIe 脉冲。未经真机验证。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "DMC-E5000", "DMC-E5000 EtherCAT 主站（PCIe）", "DMC-E5016", "雷赛智能 DMC-E5016（16 轴 PCIe EtherCAT 总线卡）", 16,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "总线卡。轴在 EtherCAT 从站上，不能当 DMC5000 脉冲卡打开。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "DMC-E5000", "DMC-E5000 EtherCAT 主站（PCIe）", "DMC-E5032", "雷赛智能 DMC-E5032（32 轴 PCIe EtherCAT 总线卡）", 32,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "32 轴 EtherCAT 主站。未接入。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "DMC-E5000", "DMC-E5000 EtherCAT 主站（PCIe）", "DMC-E5064", "雷赛智能 DMC-E5064（64 轴 PCIe EtherCAT 总线卡）", 64,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "64 轴 EtherCAT 主站。未接入。");

            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Ethernet,
                "EMC", "EMC EtherCAT 控制器（独立式）", "EMC-E0808", "雷赛智能 EMC-E0808（8 轴以太网 EtherCAT 总线控制器）", 8,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "独立式总线控制器，以太网连接，不是 PCI 脉冲卡。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Ethernet,
                "EMC", "EMC EtherCAT 控制器（独立式）", "EMC-E1616", "雷赛智能 EMC-E1616（16 轴以太网 EtherCAT 总线控制器）", 16,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "16 轴独立式。未接入。");
            Add(list, MotionVendor.LeadShine, MotionCommandInterface.EtherCAT, MotionHostLink.Ethernet,
                "PAC", "PAC EtherCAT 控制器（独立式）", "PAC-E0808", "雷赛智能 PAC-E0808（8 轴以太网 EtherCAT 总线控制器）", 8,
                DriverKind.LeadShineEtherCAT, LeadshineEcSdk, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "PAC 系列独立式总线控制器。未接入。");

            Add(list, MotionVendor.Zmotion, MotionCommandInterface.Pulse, MotionHostLink.Ethernet,
                "ZMC", "ZMC 脉冲控制器（以太网）", "ZMC406", "正运动 ZMC406（6 轴以太网脉冲控制器）", 6,
                DriverKind.ZmotionZmc, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "正运动网口脉冲控制器。仓库无适配器。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.Pulse, MotionHostLink.Ethernet,
                "ZMC", "ZMC 脉冲控制器（以太网）", "ZMC432", "正运动 ZMC432（32 轴以太网脉冲控制器）", 32,
                DriverKind.ZmotionZmc, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "32 轴网口。未接入。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.Pulse, MotionHostLink.Ethernet,
                "ECI", "ECI 脉冲控制器（以太网）", "ECI2418", "正运动 ECI2418（4 轴以太网脉冲控制器）", 4,
                DriverKind.ZmotionZmc, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "ECI 以太网脉冲。未接入。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.Pulse, MotionHostLink.Ethernet,
                "ECI", "ECI 脉冲控制器（以太网）", "ECI2828", "正运动 ECI2828（8 轴以太网脉冲控制器）", 8,
                DriverKind.ZmotionZmc, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "ECI 8 轴。未接入。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.Pulse, MotionHostLink.Pcie,
                "PCIE", "PCIE 脉冲卡", "PCIE1648", "正运动 PCIE1648（16 轴 PCIe 脉冲卡）", 16,
                DriverKind.ZmotionZmc, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "正运动 PCIe 脉冲卡。未接入。");

            Add(list, MotionVendor.Zmotion, MotionCommandInterface.EtherCAT, MotionHostLink.Ethernet,
                "ECI-E", "ECI EtherCAT 主站", "ECI2828E", "正运动 ECI2828E（8 轴以太网 EtherCAT 总线控制器）", 8,
                DriverKind.ZmotionEtherCAT, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "正运动 EtherCAT 主站，不是脉冲 ECI。未接入。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.EtherCAT, MotionHostLink.Pcie,
                "PCIE-E", "PCIE EtherCAT 主站", "PCIE1648E", "正运动 PCIE1648E（16 轴 PCIe EtherCAT 总线卡）", 16,
                DriverKind.ZmotionEtherCAT, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "PCIe EtherCAT 主站。未接入。");
            Add(list, MotionVendor.Zmotion, MotionCommandInterface.EtherCAT, MotionHostLink.Ethernet,
                "ZMC-E", "ZMC EtherCAT 主站", "ZMC432E", "正运动 ZMC432E（32 轴以太网 EtherCAT 总线控制器）", 32,
                DriverKind.ZmotionEtherCAT, ZmotionDll, MotionAdapterStatus.CatalogOnly,
                AccelParamKind.AccelerationValue, 0, false,
                "ZMC EtherCAT。未接入。");

            return list.ToArray();
        }

        private static void Add(
            List<MotionCardModelDescriptor> list,
            MotionVendor vendor,
            MotionCommandInterface iface,
            MotionHostLink link,
            string series,
            string seriesDisplay,
            string model,
            string displayName,
            int maxAxes,
            DriverKind driver,
            string nativeLibrary,
            MotionAdapterStatus adapter,
            AccelParamKind accel,
            int axisIndexBase,
            bool requiresCfg,
            string notes)
        {
            list.Add(new MotionCardModelDescriptor
            {
                Vendor = vendor,
                CommandInterface = iface,
                HostLink = link,
                Series = series,
                SeriesDisplay = seriesDisplay,
                Model = model,
                DisplayName = displayName,
                MaxAxes = maxAxes,
                Driver = driver,
                NativeLibrary = nativeLibrary,
                Adapter = adapter,
                Accel = accel,
                AxisIndexBase = axisIndexBase,
                RequiresConfigFile = requiresCfg,
                // 卡内整段缓冲插补按命令接口判定：脉冲/模拟量适配器整段下发到本卡缓冲区；
                // EtherCAT / gLink-II 主站的轴在从站上、曲线由主站周期同步下发，不能误报为支持整段缓冲。
                SupportsBufferedSegments = iface is MotionCommandInterface.Pulse or MotionCommandInterface.Analog,
                Notes = notes,
            });
        }
    }
}
