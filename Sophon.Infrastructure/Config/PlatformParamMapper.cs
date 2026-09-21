#nullable enable
using Sophon.Contracts;
using Sophon.Infrastructure.Motion;

namespace Sophon.Infrastructure.Config
{
    /// <summary>
    /// 平台差异参数纯静态映射器。
    /// 负责用户层定义与底层硬件 API 参数之间的纯数学/规则转换。
    /// 【解耦铁律】映射只依据功能标志（PlatformOptions 语义或控制器 MotionCapability 能力位），
    /// 绝不依据厂商品牌名称（DriverKind）做分支；厂商原生差异封装在各驱动类内部。
    /// </summary>
    public static class PlatformParamMapper
    {
        /// <summary>
        /// 将用户轴编号映射为硬件轴号：userAxisId + p.AxisIndexBase。
        /// </summary>
        public static int ToHardwareAxisIndex(int userAxisId, PlatformOptions p)
        {
            if (p == null) return userAxisId;
            return userAxisId + p.AxisIndexBase;
        }

        /// <summary>
        /// 依据平台加减速参数语义计算适配层输出的加速度参数：
        /// 当平台为 AccelerationTime 时返回 maxSpeed / maxAccel (当 maxAccel > 0，否则 0)；
        /// 否则返回 maxAccel。
        /// </summary>
        public static double ToAccelParam(double maxSpeed, double maxAccel, PlatformOptions p)
        {
            if (p != null && p.Accel == AccelParamKind.AccelerationTime)
            {
                return maxAccel > 0 ? (maxSpeed / maxAccel) : 0;
            }

            return maxAccel;
        }

        /// <summary>
        /// 依据控制器能力位（而非档案 Platform 选项）计算加速度参数：
        /// 控制器具备 <see cref="MotionCapability.TimeBasedAcceleration"/> 时返回 maxSpeed / maxAccel
        /// (当 maxAccel &gt; 0，否则 0)；否则返回 maxAccel。
        /// 用于控制器实例已确定、需要以控制器真实能力为准的场景。
        /// </summary>
        public static double ToAccelParam(double maxSpeed, double maxAccel, IMotionController controller)
        {
            if (controller != null && controller.Supports(MotionCapability.TimeBasedAcceleration))
            {
                return maxAccel > 0 ? (maxSpeed / maxAccel) : 0;
            }

            return maxAccel;
        }
    }
}
