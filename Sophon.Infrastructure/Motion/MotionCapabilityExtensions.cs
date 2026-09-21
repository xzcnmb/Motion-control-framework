#nullable enable
using System;
using System.Text;
using Sophon.Contracts;

namespace Sophon.Infrastructure.Motion
{
    /// <summary>
    /// Motion HAL 能力模型辅助方法。
    /// 业务层与协调器只针对 <see cref="MotionCapability"/> 功能标志位判定，
    /// 不针对厂商名称（DriverKind）做分支判断 —— 本类型是该约定的唯一入口。
    /// </summary>
    public static class MotionCapabilityExtensions
    {
        /// <summary>
        /// 判断控制器是否具备指定能力（可同时判定多个能力位：全部具备才返回 true）。
        /// 传入 <see cref="MotionCapability.None"/> 恒返回 true。
        /// </summary>
        /// <param name="controller">运动控制器实例（为 null 时返回 false）。</param>
        /// <param name="capability">待判定的能力位（可为组合值）。</param>
        /// <returns>控制器支持该能力（或能力组合）时返回 true。</returns>
        public static bool Supports(this IMotionController controller, MotionCapability capability)
        {
            if (controller == null) return false;
            return controller.Capabilities.Supports(capability);
        }

        /// <summary>
        /// 判断能力位集合中是否包含指定能力（可同时判定多个能力位：全部具备才返回 true）。
        /// </summary>
        /// <param name="capabilities">控制器声明的能力位集合。</param>
        /// <param name="capability">待判定的能力位（可为组合值）。</param>
        public static bool Supports(this MotionCapability capabilities, MotionCapability capability)
        {
            // None (0) 恒被满足；否则要求 capability 的所有位都在 capabilities 中。
            return (capabilities & capability) == capability;
        }

        /// <summary>
        /// 判断控制器是否具备指定能力中的任意一个（能力位组合按"任一命中"判定）。
        /// </summary>
        /// <param name="controller">运动控制器实例（为 null 时返回 false）。</param>
        /// <param name="capability">待判定的能力位组合。</param>
        public static bool SupportsAny(this IMotionController controller, MotionCapability capability)
        {
            if (controller == null) return false;
            return controller.Capabilities.SupportsAny(capability);
        }

        /// <summary>
        /// 判断能力位集合中是否包含指定能力组合中的任意一个。
        /// </summary>
        public static bool SupportsAny(this MotionCapability capabilities, MotionCapability capability)
        {
            if (capability == MotionCapability.None) return true;
            return (capabilities & capability) != MotionCapability.None;
        }

        /// <summary>
        /// 将能力位集合转换为可读的中文描述（用于 UI 展示与日志）。
        /// 例如："卡内整段缓冲插补; 卡内硬件限位急停; 加减速基于时间(秒)"。
        /// 空能力集返回 "无"。
        /// </summary>
        /// <param name="capabilities">能力位集合。</param>
        public static string DescribeCapabilities(this MotionCapability capabilities)
        {
            if (capabilities == MotionCapability.None) return "无";

            var sb = new StringBuilder();
            Append(sb, capabilities, MotionCapability.BufferedSegments, "卡内整段缓冲插补");
            Append(sb, capabilities, MotionCapability.HostInterpolation, "上位机周期插补");
            Append(sb, capabilities, MotionCapability.HardLimitInput, "卡内硬件限位急停");
            Append(sb, capabilities, MotionCapability.PvTStreaming, "PVT流式下发");
            Append(sb, capabilities, MotionCapability.HardwareCompareOutput, "硬件位置比较输出(飞拍)");
            Append(sb, capabilities, MotionCapability.BacklashCompensation, "机械反向间隙补偿");
            Append(sb, capabilities, MotionCapability.ContinuousVelocityBlending, "连续轨迹速度平滑");
            Append(sb, capabilities, MotionCapability.TimeBasedAcceleration, "加减速基于时间(秒)");
            Append(sb, capabilities, MotionCapability.HardwareEStopInput, "专用硬件急停输入");

            return sb.ToString(0, sb.Length - 2); // 去掉末尾的分隔符 "; "
        }

        /// <summary>
        /// 控制器能力的中文可读描述（便捷重载，等价于 controller.Capabilities.DescribeCapabilities()）。
        /// </summary>
        public static string DescribeCapabilities(this IMotionController controller)
        {
            return controller == null ? "无" : controller.Capabilities.DescribeCapabilities();
        }

        private static void Append(StringBuilder sb, MotionCapability capabilities, MotionCapability flag, string text)
        {
            if ((capabilities & flag) == flag)
            {
                sb.Append(text).Append("; ");
            }
        }
    }
}
