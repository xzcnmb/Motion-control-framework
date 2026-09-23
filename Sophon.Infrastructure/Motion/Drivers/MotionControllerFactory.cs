#nullable enable
using System;
using System.Collections.Generic;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion.Sim;

namespace Sophon.Infrastructure.Motion.Drivers
{
    /// <summary>
    /// 运动控制器工厂。
    /// 根据 <see cref="DriverKind"/> 创建对应的运动控制器实现。
    /// 【安全铁律】：请求真卡但初始化失败时，默认抛出异常（NeverSilentlyFallbackToSim 语义），除非显式传 allowSimFallback: true。
    /// </summary>
    public static class MotionControllerFactory
    {
        /// <summary>
        /// 创建运动控制器实例。
        /// </summary>
                /// <param name="kind">驱动类型（Simulated/GoogolGts/LeadShineDmc；总线卡与 ZMC 至今未实现）</param>
        /// <param name="axes">轴定义列表</param>
        /// <param name="timeSource">时间源（用于 Sim）</param>
        /// <param name="ioController">IO 控制器（用于 Sim）</param>
        /// <param name="allowSimFallback">当真卡创建/初始化失败时，是否允许自动降级到 Sim（默认 false，严禁静默回退）</param>
        /// <param name="logger">警告/异常日志记录委托</param>
        /// <param name="customInstantiator">测试注入使用的自定义实例化函数（可选）</param>
        /// <returns>运动控制器实例</returns>
        public static IMotionController Create(
            DriverKind kind,
            IEnumerable<AxisDefinition>? axes = null,
            ITimeSource? timeSource = null,
            Sophon.Contracts.IIoController? ioController = null,
            bool allowSimFallback = false,
            Action<string>? logger = null,
            Func<DriverKind, IMotionController>? customInstantiator = null)
        {
            switch (kind)
            {
                case DriverKind.Simulated:
                    return customInstantiator != null ? customInstantiator(kind) : new SimMotionController(axes, timeSource, ioController);

                case DriverKind.GoogolGts:
                    try
                    {
                        return customInstantiator != null ? customInstantiator(kind) : new GoogolGtsMotionController(axes);
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[MotionControllerFactory] 创建固高 GTS 控制器失败: {ex.Message}");
                        if (allowSimFallback)
                        {
                            logger?.Invoke("[MotionControllerFactory] 警告：已显式允许降级到仿真控制器 (allowSimFallback = true)！");
                            return new SimMotionController(axes, timeSource, ioController);
                        }
                        throw new InvalidOperationException($"创建固高 GTS 控制器失败，未允许降级到仿真 (NeverSilentlyFallbackToSim): {ex.Message}", ex);
                    }

                case DriverKind.LeadShineDmc:
                    try
                    {
                        return customInstantiator != null ? customInstantiator(kind) : new LeadShineDmcMotionController(axes);
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[MotionControllerFactory] 创建雷赛 DMC 控制器失败: {ex.Message}");
                        if (allowSimFallback)
                        {
                            logger?.Invoke("[MotionControllerFactory] 警告：已显式允许降级到仿真控制器 (allowSimFallback = true)！");
                            return new SimMotionController(axes, timeSource, ioController);
                        }
                        throw new InvalidOperationException($"创建雷赛 DMC 控制器失败，未允许降级到仿真 (NeverSilentlyFallbackToSim): {ex.Message}", ex);
                    }

                // 总线卡 / ZMC 至今没有适配器。按品牌名猜一个脉冲 DLL 去打开比直接失败更危险，
                // 总线主站也没有本机轴号可配，所以这里一律拒绝，绝不回退 Sim。
                case DriverKind.GoogolGen:
                case DriverKind.GoogolGe:
                case DriverKind.LeadShineEtherCAT:
                case DriverKind.ZmotionZmc:
                case DriverKind.ZmotionEtherCAT:
                    throw new NotSupportedException(MotionCardCatalog.NotImplementedMessage(kind));

                default:
                    throw new NotSupportedException($"不支持的运动控制器驱动类型: {kind}");
            }
        }
    }
}
