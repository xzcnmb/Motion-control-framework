#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sophon.Contracts;
using Sophon.Infrastructure.Motion;

namespace Sophon.Infrastructure.Config
{
    /// <summary>
    /// 运动控制卡配置档案合法性校验器。
    /// 【解耦铁律】本校验器只依据功能标志（<see cref="PlatformOptions"/> 的
    /// RequiresConfigFile / UsesConnectionString / Accel 语义，或控制器 <see cref="MotionCapability"/>
    /// 能力位）判定，绝不依据厂商品牌名称（DriverKind）做分支。
    /// </summary>
    public static class MotionProfileValidator
    {
        /// <summary>
        /// 校验控制卡档案配置，返回错误/警告信息列表（中文）。若校验通过则返回空列表。
        /// </summary>
        public static List<string> Validate(MotionCardProfile p)
        {
            var errors = new List<string>();

            if (p == null)
            {
                errors.Add("运动控制卡配置档案不能为空。");
                return errors;
            }

            // 0. 选型目录一致性：型号必须在目录内，且未接入的总线/正运动驱动直接拒绝
            ValidateAgainstCatalog(p, errors);

            // 1. 轴数量有效性 (0..64)
            if (p.Axes == null)
            {
                errors.Add("轴定义列表未初始化。");
            }
            else
            {
                if (p.Axes.Count < 0 || p.Axes.Count > 64)
                {
                    errors.Add($"轴数量超出有效范围(0~64): 当前轴数为 {p.Axes.Count}。");
                }

                var model = MotionCardCatalog.Resolve(p);
                if (model != null && p.Axes.Count > model.MaxAxes)
                {
                    errors.Add($"轴数量({p.Axes.Count})超出型号 {model.Model}({model.DisplayName}) 最多 {model.MaxAxes} 轴的上限，请减少轴数或更换型号。");
                }
            }

            // 2. 轴 ID 唯一性与各轴参数检查
            if (p.Axes != null)
            {
                var duplicateIds = p.Axes
                    .GroupBy(a => a.AxisId)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();

                foreach (var dupId in duplicateIds)
                {
                    errors.Add($"轴编号重复: 轴号 {dupId} 重复定义。");
                }

                foreach (var axis in p.Axes)
                {
                    string axisTag = $"轴[{axis.AxisId}:{axis.Name}]";

                    // PulsePerUnit > 0
                    if (axis.PulsePerUnit <= 0)
                    {
                        errors.Add($"{axisTag} 脉冲当量必须大于0，当前值: {axis.PulsePerUnit}。");
                    }

                    // if SoftLimitEnabled then SoftLimitMin < SoftLimitMax
                    if (axis.SoftLimitEnabled && axis.SoftLimitMin >= axis.SoftLimitMax)
                    {
                        errors.Add($"{axisTag} 软限位设置错误: 软限位下限({axis.SoftLimitMin})必须小于上限({axis.SoftLimitMax})。");
                    }

                    // MaxSpeed / Accel / Decel > 0
                    if (axis.MaxSpeed <= 0)
                    {
                        errors.Add($"{axisTag} 最大速度必须大于0，当前值: {axis.MaxSpeed}。");
                    }
                    if (axis.MaxAccel <= 0)
                    {
                        errors.Add($"{axisTag} 最大加速度必须大于0，当前值: {axis.MaxAccel}。");
                    }
                    if (axis.MaxDecel <= 0)
                    {
                        errors.Add($"{axisTag} 最大减速度必须大于0，当前值: {axis.MaxDecel}。");
                    }
                }
            }

            // 3. 平台选项校验
            if (p.Platform == null)
            {
                errors.Add("平台差异选项(Platform)未设置。");
            }
            else
            {
                // if Platform.RequiresConfigFile then ConfigFilePath non-empty (+warn if !File.Exists)
                if (p.Platform.RequiresConfigFile)
                {
                    if (string.IsNullOrWhiteSpace(p.ConfigFilePath))
                    {
                        errors.Add("当前平台要求配置文件，但配置文件路径(ConfigFilePath)为空。");
                    }
                    else if (!File.Exists(p.ConfigFilePath))
                    {
                        errors.Add($"配置文件不存在: {p.ConfigFilePath}。");
                    }
                }

                // if Platform.UsesConnectionString then ConnectionString non-empty
                if (p.Platform.UsesConnectionString && string.IsNullOrWhiteSpace(p.ConnectionString))
                {
                    errors.Add("当前平台要求连接字符串，但连接字符串(ConnectionString)为空。");
                }

                // if Accel==AccelerationTime warn when a derived accel-time (MaxSpeed/MaxAccel) >10s
                if (p.Platform.Accel == AccelParamKind.AccelerationTime && p.Axes != null)
                {
                    foreach (var axis in p.Axes)
                    {
                        if (axis.MaxAccel > 0)
                        {
                            double derivedTime = axis.MaxSpeed / axis.MaxAccel;
                            if (derivedTime > 10.0)
                            {
                                errors.Add($"轴[{axis.AxisId}:{axis.Name}] 推导加速时间({derivedTime:F2}s)超过10秒，请检查最大速度与加速度设置。");
                            }
                        }
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// 在基础校验之上追加「档案声明 vs 控制器能力」一致性校验。
        /// 依据控制器 <see cref="MotionCapability"/> 能力位（而非厂商品牌）判定档案是否可用：
        /// 档案通过 Platform 标志声明的功能，控制器必须具备对应能力位，否则该档案下发后将失真。
        /// </summary>
        /// <param name="p">待校验的配置档案。</param>
        /// <param name="controller">目标运动控制器实例（取其 Capabilities 能力位）。</param>
        public static List<string> ValidateAgainstController(MotionCardProfile p, IMotionController controller)
        {
            var errors = Validate(p);

            if (controller == null)
            {
                errors.Add("未提供运动控制器实例，无法进行能力一致性校验。");
                return errors;
            }

            MotionCapability caps = controller.Capabilities;

            // 档案声明卡内整段缓冲插补 -> 控制器必须具备 BufferedSegments 能力
            if (p?.Platform != null && p.Platform.SupportsBufferedSegments &&
                !caps.Supports(MotionCapability.BufferedSegments))
            {
                errors.Add("档案声明了卡内整段缓冲插补(SupportsBufferedSegments)，但当前控制器不具备该能力(BufferedSegments)。");
            }

            // 档案声明加减速按时间(秒)语义 -> 控制器必须具备 TimeBasedAcceleration 能力
            if (p?.Platform != null && p.Platform.Accel == AccelParamKind.AccelerationTime &&
                !caps.Supports(MotionCapability.TimeBasedAcceleration))
            {
                errors.Add("档案加减速参数按时间(秒)语义配置，但当前控制器不具备 TimeBasedAcceleration 能力，参数将被误解释。");
            }

            // 轴启用硬限位 -> 控制器必须具备 HardLimitInput 能力（否则拿不到卡内微秒级急停语义）
            if (p?.Axes != null && p.Axes.Any(a => a.HardLimitEnabled) &&
                !caps.Supports(MotionCapability.HardLimitInput))
            {
                errors.Add("有轴启用了硬限位(HardLimitEnabled)，但当前控制器不具备卡内硬件限位输入能力(HardLimitInput)。");
            }

            return errors;
        }

        /// <summary>
        /// 选型目录一致性：型号必须在目录内、Driver 必须与型号匹配；
        /// 未接入的总线 / 正运动驱动一律拒绝，绝不静默回退到脉冲 DLL 或仿真。
        /// 旧档案只写了 Driver + CardModel 时按驱动种类反推；非仿真驱动没有型号、或型号非空却不在目录时，
        /// 一律要求按目录重新选择，禁止按 Driver 静默套第一个型号。
        /// </summary>
        private static void ValidateAgainstCatalog(MotionCardProfile p, List<string> errors)
        {
            if (p == null)
            {
                return;
            }

            // 纯仿真档案：不写型号，直接放行。
            if (p.Driver == DriverKind.Simulated && string.IsNullOrWhiteSpace(p.CardModel))
            {
                return;
            }

            var byModel = MotionCardCatalog.Find(p.CardModel);
            if (byModel == null)
            {
                if (!string.IsNullOrWhiteSpace(p.CardModel))
                {
                    errors.Add($"控制卡型号 {p.CardModel} 不在选型目录中，请按「厂商 → 脉冲/总线接口 → 系列 → 型号」重新选择。");
                    return;
                }

                // 非仿真驱动却没有型号：必须按目录重新选择，禁止按 Driver 猜一个型号，更不许静默回退 Sim。
                errors.Add($"未选择控制卡型号：请按「厂商 → 脉冲/总线接口 → 系列 → 型号」选择型号（当前驱动：{MotionCardCatalog.Display(p.Driver)}）。");

                if (!MotionCardCatalog.IsImplemented(p.Driver))
                {
                    errors.Add(MotionCardCatalog.NotImplementedMessage(p.Driver));
                }

                return;
            }

            if (p.Driver != byModel.Driver)
            {
                errors.Add($"驱动与型号不匹配：型号 {byModel.Model} 对应 {MotionCardCatalog.Display(byModel.Driver)}，当前档案选择的是 {MotionCardCatalog.Display(p.Driver)}。");
            }

            if (!MotionCardCatalog.IsImplemented(byModel.Driver))
            {
                errors.Add(MotionCardCatalog.NotImplementedMessage(byModel.Driver, byModel.Model));
            }
        }
    }
}
