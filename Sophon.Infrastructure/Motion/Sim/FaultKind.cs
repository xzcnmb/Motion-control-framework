#nullable enable
namespace Sophon.Infrastructure.Motion.Sim
{
    /// <summary>
    /// 仿真控制器故障注入类型。
    /// </summary>
    public enum FaultKind
    {
        /// <summary>正方向硬限位激活</summary>
        PositiveHardLimit,

        /// <summary>负方向硬限位激活</summary>
        NegativeHardLimit,

        /// <summary>控制器连接断开/掉线</summary>
        Disconnected,

        /// <summary>驱动器报警</summary>
        DriveAlarm,

        /// <summary>跟随误差超差</summary>
        FollowingError
    }
}
