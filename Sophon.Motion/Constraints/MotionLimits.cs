using Sophon.Contracts;
using Sophon.Motion.Profiles;

namespace Sophon.Motion.Constraints;

/// <summary>
/// 单轴或多轴运动约束与软限位配置。
/// 充当全局运动安全的第一道防线，供点动、示教、插补及工艺流程统一复用。
/// </summary>
public sealed class MotionLimits
{
    /// <summary>最大运行速度 (物理单位/s)</summary>
    public double MaxSpeed { get; set; } = 100.0;

    /// <summary>最大加速度 (物理单位/s²)</summary>
    public double MaxAccel { get; set; } = 500.0;

    /// <summary>最大减速度 (物理单位/s²)</summary>
    public double MaxDecel { get; set; } = 500.0;

    /// <summary>最大加加速度 (Jerk，物理单位/s³)</summary>
    public double MaxJerk { get; set; } = 2000.0;

    /// <summary>软限位负向边界（小值）</summary>
    public double SoftLimitMin { get; set; } = 0.0;

    /// <summary>软限位正向边界（大值）</summary>
    public double SoftLimitMax { get; set; } = 100.0;

    /// <summary>是否启用软限位校验</summary>
    public bool SoftLimitEnabled { get; set; } = true;

    /// <summary>
    /// 从 AxisDefinition 创建对应的 MotionLimits 实例。
    /// </summary>
    public static MotionLimits FromAxisDefinition(AxisDefinition axis)
    {
        if (axis == null) throw new ArgumentNullException(nameof(axis));
        return new MotionLimits
        {
            MaxSpeed = axis.MaxSpeed,
            MaxAccel = axis.MaxAccel,
            MaxDecel = axis.MaxDecel,
            MaxJerk = axis.MaxJerk,
            SoftLimitMin = axis.SoftLimitMin,
            SoftLimitMax = axis.SoftLimitMax,
            SoftLimitEnabled = axis.SoftLimitEnabled
        };
    }

    /// <summary>
    /// 验证目标位置是否越界。若越界则返回 false 并输出错误信息，或由调用方决定。
    /// </summary>
    /// <param name="currentPosition">当前物理位置</param>
    /// <param name="targetPosition">目标物理位置</param>
    /// <param name="errorMessage">若不合法，返回错误描述</param>
    /// <returns>目标是否合法</returns>
    public bool ValidateTarget(double currentPosition, double targetPosition, out string? errorMessage)
    {
        if (!SoftLimitEnabled)
        {
            errorMessage = null;
            return true;
        }

        if (targetPosition < SoftLimitMin)
        {
            errorMessage = $"Target position {targetPosition:F3} exceeds negative soft limit {SoftLimitMin:F3}.";
            return false;
        }

        if (targetPosition > SoftLimitMax)
        {
            errorMessage = $"Target position {targetPosition:F3} exceeds positive soft limit {SoftLimitMax:F3}.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 验证目标位置。若越界则直接抛出 ArgumentOutOfRangeException。
    /// </summary>
    public void ValidateTarget(double currentPosition, double targetPosition)
    {
        if (!ValidateTarget(currentPosition, targetPosition, out string? err))
        {
            throw new ArgumentOutOfRangeException(nameof(targetPosition), err);
        }
    }

    /// <summary>
    /// 当运动轨迹面临越界时，以软限位为终点，按减速能力重新裁剪生成规划 Profile。
    /// 确保在到达软限位边界时速度严格减至 0，绝不允许越界后才停止。
    /// </summary>
    /// <param name="currentPosition">当前物理位置</param>
    /// <param name="currentVelocity">当前瞬时速度 (物理单位/s)</param>
    /// <param name="desiredTarget">期望目标位置</param>
    /// <returns>裁剪后的梯形速度规划实例与最终安全目标位置</returns>
    public (TrapezoidalProfile Profile, double SafeTarget) ClampProfile(
        double currentPosition,
        double currentVelocity,
        double desiredTarget)
    {
        double safeTarget = desiredTarget;

        if (SoftLimitEnabled)
        {
            // 目标限幅至软限位内
            if (safeTarget > SoftLimitMax)
            {
                safeTarget = SoftLimitMax;
            }
            else if (safeTarget < SoftLimitMin)
            {
                safeTarget = SoftLimitMin;
            }
        }

        double delta = safeTarget - currentPosition;

        // 生成到 safeTarget 的梯形 Profile，终点速度设为 0
        var profile = new TrapezoidalProfile(
            v0: currentVelocity,
            vt: 0.0,
            vmax: MaxSpeed,
            amax: MaxAccel,
            decel: MaxDecel,
            s: delta
        );

        return (profile, safeTarget);
    }
}
