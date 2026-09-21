#nullable enable
using System;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Drivers.Native;

namespace Sophon.Infrastructure.Motion.Drivers
{
    /// <summary>
    /// 将控制卡通用 DI/DO 接到 IoMappingManager 的原始读写委托。未经真机验证。
    /// 只覆盖已实现的脉冲卡适配器；总线卡没有这套位号 API。
    /// </summary>
    public static class MotionCardIoBridge
    {
        public static Func<int, int, bool> CreateDiReader(DriverKind driver)
        {
            if (driver == DriverKind.LeadShineDmc)
            {
                return (card, bit) =>
                {
                    try
                    {
                        return LeadShineDmcNative.dmc_read_inbit((ushort)card, (ushort)bit) != 0;
                    }
                    catch
                    {
                        return false;
                    }
                };
            }

            if (driver == DriverKind.GoogolGts)
            {
                return (card, bit) =>
                {
                    try
                    {
                        short ret = GoogolGtsNative.GT_GetDi(1, out int value);
                        if (ret != 0) return false;
                        return ((value >> bit) & 1) != 0;
                    }
                    catch
                    {
                        return false;
                    }
                };
            }

            throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
        }

        public static Action<int, int, bool> CreateDoWriter(DriverKind driver)
        {
            if (driver == DriverKind.LeadShineDmc)
            {
                return (card, bit, val) =>
                {
                    try
                    {
                        LeadShineDmcNative.dmc_write_outbit((ushort)card, (ushort)bit, (ushort)(val ? 1 : 0));
                    }
                    catch
                    {
                    }
                };
            }

            if (driver == DriverKind.GoogolGts)
            {
                return (card, bit, val) =>
                {
                    try
                    {
                        GoogolGtsNative.GT_SetDoBit(1, (short)bit, (short)(val ? 1 : 0));
                    }
                    catch
                    {
                    }
                };
            }

            throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(driver));
        }
    }
}
