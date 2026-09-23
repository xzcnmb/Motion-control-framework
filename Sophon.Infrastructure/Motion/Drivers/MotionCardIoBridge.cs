#nullable enable
using System;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Drivers.Native;
using Sophon.Infrastructure.Motion.Sim;

namespace Sophon.Infrastructure.Motion.Drivers
{
    /// <summary>
    /// 将控制卡通用 DI/DO 接到 IoMappingManager 的原始读写委托。未经真机验证。
    /// 只有 <see cref="DriverKind.Simulated"/> 允许落到内存仿真 IO；GTS/DMC 只走各自厂商 DLL 且失败可见，
    /// 未接入的总线卡 / ZMC / 未知驱动一律明确拒绝，绝不用仿真值伪装成已接入。
    /// </summary>
    public static class MotionCardIoBridge
    {
        /// <summary>
        /// 仿真驱动共用的 IO 实例。<see cref="CreateDiReader"/> / <see cref="CreateDoWriter"/>
        /// 在 <see cref="DriverKind.Simulated"/> 下返回的读写委托都落在这一份状态上。
        /// </summary>
        public static SimIoController SimulatedIo { get; } = new SimIoController();

        /// <summary>仿真输入点的虚拟点名：卡号 + 通道位，避免和工艺逻辑点名混淆。</summary>
        private static string SimDiName(int card, int bit) => $"DI{card}_{bit}";

        /// <summary>仿真输出点的虚拟点名：卡号 + 通道位。</summary>
        private static string SimDoName(int card, int bit) => $"DO{card}_{bit}";

        public static Func<int, int, bool> CreateDiReader(DriverKind driver)
        {
            // 总线卡 / ZMC 没有适配器：明确拒绝，绝不允许落到 gts.dll 那条分支上。
            if (!MotionCardCatalog.IsImplemented(driver))
            {
                throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
            }

            if (driver == DriverKind.LeadShineDmc)
            {
                return (card, bit) =>
                {
                    ValidateChannel(card, bit, 16);
                    try
                    {
                        short result = LeadShineDmcNative.dmc_read_inbit((ushort)card, (ushort)bit);
                        if (result < 0) throw new InvalidOperationException($"DMC DI 读取失败，错误码 {result}");
                        return result != 0;
                    }
                    catch (DllNotFoundException ex)
                    {
                        throw new InvalidOperationException("未找到 LTDMC.dll，DI 读取不可用", ex);
                    }
                };
            }

            // 仿真驱动：只读内存仿真 IO，不加载任何厂商 DLL，也不用仿真值伪装成真卡已读到信号。
            if (driver == DriverKind.Simulated)
            {
                return (card, bit) =>
                {
                    ValidateChannel(card, bit, 32);
                    return SimulatedIo.ReadDi(SimDiName(card, bit));
                };
            }

            // 走到这里只可能是 GTS：其它驱动不允许落到 gts.dll 分支上读错卡。
            if (driver != DriverKind.GoogolGts)
            {
                throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
            }

            return (card, bit) =>
            {
                // GT_ 系列在 GT_Open 时选择已打开的单卡，GT_GetDi 的首参是 DI 类型而不是卡号。
                // 当前适配器只支持已打开的单卡 GPI；多卡配置必须显式阻断，不能读错卡。
                if (card != 0) throw new NotSupportedException("GTS GT_ API 当前只支持 CardNo=0 的已打开单卡");
                ValidateChannel(card, bit, 32);
                try
                {
                    short result = GoogolGtsNative.GT_GetDi(4, out int value); // MC_GPI = 4
                    if (result != 0) throw new InvalidOperationException($"GTS GPI 读取失败，错误码 {result}");
                    return ((value >> bit) & 1) != 0;
                }
                catch (DllNotFoundException ex)
                {
                    throw new InvalidOperationException("未找到 gts.dll，DI 读取不可用", ex);
                }
            };
        }

        public static Action<int, int, bool> CreateDoWriter(DriverKind driver)
        {
            // 同 CreateDiReader：未接入的驱动直接拒绝。
            if (!MotionCardCatalog.IsImplemented(driver))
            {
                throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
            }

            if (driver == DriverKind.LeadShineDmc)
            {
                return (card, bit, val) =>
                {
                    ValidateChannel(card, bit, 16);
                    try
                    {
                        short result = LeadShineDmcNative.dmc_write_outbit((ushort)card, (ushort)bit, (ushort)(val ? 1 : 0));
                        if (result < 0) throw new InvalidOperationException($"DMC DO 写入失败，错误码 {result}");
                    }
                    catch (DllNotFoundException ex)
                    {
                        throw new InvalidOperationException("未找到 LTDMC.dll，DO 写入不可用", ex);
                    }
                };
            }

            // 仿真驱动：只写内存仿真 IO，不加载任何厂商 DLL，也不用空操作伪装成已输出。
            if (driver == DriverKind.Simulated)
            {
                return (card, bit, val) =>
                {
                    ValidateChannel(card, bit, 32);
                    SimulatedIo.WriteDo(SimDoName(card, bit), val);
                };
            }

            // 走到这里只可能是 GTS：其它驱动不允许落到 gts.dll 分支上写错卡。
            if (driver != DriverKind.GoogolGts)
            {
                throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
            }

            return (card, bit, val) =>
            {
                if (card != 0) throw new NotSupportedException("GTS GT_ API 当前只支持 CardNo=0 的已打开单卡");
                ValidateChannel(card, bit, 16);
                try
                {
                    short result = GoogolGtsNative.GT_SetDoBit(1, (short)bit, (short)(val ? 1 : 0)); // GPO = 1
                    if (result != 0) throw new InvalidOperationException($"GTS GPO 写入失败，错误码 {result}");
                }
                catch (DllNotFoundException ex)
                {
                    throw new InvalidOperationException("未找到 gts.dll，DO 写入不可用", ex);
                }
            };
        }

        private static void ValidateChannel(int card, int bit, int width)
        {
            if (card < 0) throw new ArgumentOutOfRangeException(nameof(card), card, "卡号不能为负数");
            if (bit < 0 || bit >= width) throw new ArgumentOutOfRangeException(nameof(bit), bit, $"通道位必须在 0..{width - 1} 范围内");
        }
    }
}
