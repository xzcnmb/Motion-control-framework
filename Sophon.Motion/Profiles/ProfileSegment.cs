namespace Sophon.Motion.Profiles;

/// <summary>
/// 速度规划段类型。
/// </summary>
public enum ProfileSegmentType
{
    /// <summary>加加速度增加段（加加速/减减速）</summary>
    ConstantJerkPositive,
    /// <summary>恒定加速度段</summary>
    ConstantAcceleration,
    /// <summary>加加速度反向减少段（减加速/加减速）</summary>
    ConstantJerkNegative,
    /// <summary>匀速段</summary>
    ConstantVelocity,
    /// <summary>恒定减速度段</summary>
    ConstantDeceleration
}

/// <summary>
/// 规划段信息。
/// </summary>
/// <param name="StartTime">本段起始时刻 (s)</param>
/// <param name="EndTime">本段结束时刻 (s)</param>
/// <param name="Duration">本段持续时间 (s)</param>
/// <param name="StartPosition">本段起始位置 (物理单位)</param>
/// <param name="EndPosition">本段结束位置 (物理单位)</param>
/// <param name="StartVelocity">本段起始速度 (物理单位/s)</param>
/// <param name="EndVelocity">本段结束速度 (物理单位/s)</param>
/// <param name="StartAcceleration">本段起始加速度 (物理单位/s²)</param>
/// <param name="EndAcceleration">本段结束加速度 (物理单位/s²)</param>
/// <param name="Jerk">本段恒定加加速度 (物理单位/s³)</param>
/// <param name="Type">段类型</param>
public record ProfileSegment(
    double StartTime,
    double EndTime,
    double Duration,
    double StartPosition,
    double EndPosition,
    double StartVelocity,
    double EndVelocity,
    double StartAcceleration,
    double EndAcceleration,
    double Jerk,
    ProfileSegmentType Type
);
