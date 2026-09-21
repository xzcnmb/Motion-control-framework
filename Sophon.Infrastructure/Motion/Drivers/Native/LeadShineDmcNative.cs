using System;
using System.Runtime.InteropServices;

namespace Sophon.Infrastructure.Motion.Drivers.Native
{
    /// <summary>
    /// 雷赛 DMC 脉冲卡 LTDMC.dll 声明。只覆盖 DMC1000/3000/5000/5X10 这类本地脉冲卡。
    /// DMC-E / EMC / PAC 是 EtherCAT 主站，不是这套 API。所有签名未经真机验证。
    /// </summary>
    internal static class LeadShineDmcNative
    {
        private const string DllName = "LTDMC.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_init();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_board_close();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_sevon_enable(ushort CardNo, ushort axis, ushort on_off);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_get_sevon_enable(ushort CardNo, ushort axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_profile(ushort CardNo, ushort axis, double Min_Vel, double Max_Vel, double Tacc, double Tdec, double Stop_Vel);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_pmove(ushort CardNo, ushort axis, int Dist, ushort posi_mode);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_vmove(ushort CardNo, ushort axis, ushort dir);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_stop(ushort CardNo, ushort axis, ushort stop_mode);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_get_position(ushort CardNo, ushort axis, out int pos);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_read_current_speed(ushort CardNo, ushort axis, out int speed);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_set_position(ushort CardNo, ushort axis, int pos);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_check_done(ushort CardNo, ushort axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern uint dmc_axis_io_status(ushort CardNo, ushort axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_read_inbit(ushort CardNo, ushort bitno);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short dmc_write_outbit(ushort CardNo, ushort bitno, ushort on_off);
    }
}
