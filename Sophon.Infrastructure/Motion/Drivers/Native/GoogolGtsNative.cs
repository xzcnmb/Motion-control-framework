using System;
using System.Runtime.InteropServices;

namespace Sophon.Infrastructure.Motion.Drivers.Native
{
    /// <summary>
    /// 固高 GTS / GTS-VB / GTHD 的 gts.dll 声明。GEN EtherCAT、GE gLink-II 不是这套 API。
    /// 所有签名未经真机验证。
    /// </summary>
    internal static class GoogolGtsNative
    {
        private const string DllName = "gts.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_Open(short channel = 0, short param = 1);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_Close();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_Reset();

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_AxisOn(short axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_AxisOff(short axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_PrfTrap(short axis);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_SetTrapPrm(short axis, ref TTrapPrm pPrm);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_SetPos(short axis, int pos);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_GetPos(short axis, out int pPos);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_SetVel(short axis, double vel);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_GetVel(short axis, out double pVel);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_Update(int mask);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_Stop(int mask, int option);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_GetSts(short axis, out int pSts, short count = 1, uint pClock = 0);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_LmtsOn(short axis, short limitType);

        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_ClrSts(short axis, short count = 1);

        /// <summary>diType: 1=通用输入 GPI。未经真机验证。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_GetDi(short diType, out int pValue);

        /// <summary>doType: 1=通用输出 GPO。未经真机验证。</summary>
        [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
        public static extern short GT_SetDoBit(short doType, short doIndex, short value);

        [StructLayout(LayoutKind.Sequential)]
        public struct TTrapPrm
        {
            public double acc;
            public double dec;
            public double velStart;
            public short smoothTime;
        }
    }
}
