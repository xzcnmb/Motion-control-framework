using System;
using System.Collections.Generic;

namespace Sophon.Contracts
{
    /// <summary>
    /// 运动控制卡配置档案（平台无关的顶层配置模型）。
    /// 一个档案描述“加载哪种控制卡、卡级连接参数、以及该卡下的各轴定义”。
    /// 不同平台（固高 GTS / 雷赛 DMC / 正运动 ZMC）的差异通过 <see cref="PlatformOptions"/> 归一化承载，
    /// 使 UI 配置页与驱动适配层对同一模型编程。
    /// </summary>
    public class MotionCardProfile
    {
        /// <summary>档案名称（界面显示、多套配置切换用）。</summary>
        public string ProfileName { get; set; } = "默认配置";

        /// <summary>目标驱动/平台类型。由选型目录按型号写入，禁止手填成总线卡再走脉冲 DLL。</summary>
        public DriverKind Driver { get; set; } = DriverKind.Simulated;

        /// <summary>厂商。与 <see cref="CardModel"/> 共同决定走哪套 SDK。</summary>
        public MotionVendor Vendor { get; set; } = MotionVendor.Simulated;

        /// <summary>脉冲 / 模拟量 / EtherCAT / gLink：雷赛 DMC 脉冲卡与 DMC-E 总线卡必须分开。</summary>
        public MotionCommandInterface CommandInterface { get; set; } = MotionCommandInterface.Pulse;

        /// <summary>产品系列（如 DMC5000、DMC-E5000、GTS-VB、GEN）。</summary>
        public string Series { get; set; } = string.Empty;

        /// <summary>控制卡型号，目录主键（如 "GTS-400" / "DMC5810" / "DMC-E5032"）。</summary>
        public string CardModel { get; set; } = string.Empty;

        /// <summary>
        /// 卡号 / 设备标识。语义随平台而异：
        /// 固高=channel/cardNum；雷赛=dmc_board_init 返回的 snum；正运动=open 返回的 handle 序号。
        /// 统一以整数存储，适配层解释。
        /// </summary>
        public int CardNo { get; set; }

        /// <summary>
        /// 连接字符串（网口控制器用，如正运动 ZMC 的 IP，或雷赛 EtherCAT 总线设备节点）。
        /// 板卡型（PCI/PCIe）留空。
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// 配置文件路径（固高 GTS 需加载 *.cfg，如 GTS800.cfg / gts1.cfg；正运动可加载 *.bas）。
        /// 不需要的平台留空。
        /// </summary>
        public string? ConfigFilePath { get; set; }

        /// <summary>本卡的各轴定义。</summary>
        public List<AxisDefinition> Axes { get; set; } = new();

        /// <summary>平台差异选项（轴号基准、加减速语义等）。</summary>
        public PlatformOptions Platform { get; set; } = new();

        /// <summary>
        /// 把目录里的型号写进档案：驱动、接口、系列、平台选项一并带上，避免脉冲卡与总线卡混用。
        /// </summary>
        public void ApplyModel(MotionCardModelDescriptor model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            Vendor = model.Vendor;
            CommandInterface = model.CommandInterface;
            Series = model.Series;
            CardModel = model.Model;
            Driver = model.Driver;
            Platform = model.CreatePlatform();
        }

        /// <summary>
        /// 先按型号从目录取平台选项；旧档案没有型号时才退回按驱动种类，再退回默认值。
        /// </summary>
        public static PlatformOptions DefaultPlatformFor(DriverKind driver, string? cardModel = null)
        {
            var byModel = MotionCardCatalog.Find(cardModel);
            if (byModel != null)
            {
                return byModel.CreatePlatform();
            }

            var byDriver = MotionCardCatalog.FindByDriver(driver);
            if (byDriver != null)
            {
                return byDriver.CreatePlatform();
            }

            return driver switch
            {
                DriverKind.GoogolGts => new PlatformOptions
                {
                    AxisIndexBase = 1,
                    Accel = AccelParamKind.AccelerationValue,
                    RequiresConfigFile = true,
                    SupportsBufferedSegments = true,
                },
                DriverKind.LeadShineDmc or DriverKind.LeadShineEtherCAT => new PlatformOptions
                {
                    AxisIndexBase = 1,
                    Accel = DriverKind.LeadShineDmc == driver ? AccelParamKind.AccelerationTime : AccelParamKind.AccelerationValue,
                    RequiresConfigFile = false,
                    SupportsBufferedSegments = true,
                    UsesConnectionString = driver == DriverKind.LeadShineEtherCAT,
                },
                DriverKind.GoogolGen or DriverKind.GoogolGe => new PlatformOptions
                {
                    AxisIndexBase = 0,
                    Accel = AccelParamKind.AccelerationValue,
                    RequiresConfigFile = false,
                    SupportsBufferedSegments = false,
                },
                DriverKind.ZmotionZmc => new PlatformOptions
                {
                    AxisIndexBase = 0,
                    Accel = AccelParamKind.AccelerationValue,
                    RequiresConfigFile = false,
                    SupportsBufferedSegments = true,
                    UsesConnectionString = true,
                },
                DriverKind.ZmotionEtherCAT => new PlatformOptions
                {
                    AxisIndexBase = 0,
                    Accel = AccelParamKind.AccelerationValue,
                    RequiresConfigFile = false,
                    SupportsBufferedSegments = false,
                },
                _ => new PlatformOptions
                {
                    AxisIndexBase = 0,
                    Accel = AccelParamKind.AccelerationValue,
                    RequiresConfigFile = false,
                    SupportsBufferedSegments = false,
                },
            };
        }
    }

    /// <summary>
    /// 平台差异归一化选项。适配层依据这些标志把统一的 AxisDefinition 转换为各厂商 API 参数。
    /// </summary>
    public class PlatformOptions
    {
        /// <summary>硬件轴号基准：固高/雷赛=1，正运动=0。适配层 = 用户轴号 + (AxisIndexBase)。</summary>
        public int AxisIndexBase { get; set; }

        /// <summary>加减速参数语义（加速度值 vs 加速时间）。</summary>
        public AccelParamKind Accel { get; set; } = AccelParamKind.AccelerationValue;

        /// <summary>是否必须加载卡配置文件（固高 GTS 为 true）。</summary>
        public bool RequiresConfigFile { get; set; }

        /// <summary>是否通过连接字符串（IP 等）连接（正运动网口、雷赛总线设备）。</summary>
        public bool UsesConnectionString { get; set; }

        /// <summary>该平台是否支持卡内整段缓冲插补下发。</summary>
        public bool SupportsBufferedSegments { get; set; }
    }
}
