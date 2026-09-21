namespace Sophon.Motion.Interpolation;

/// <summary>
/// 插补轨迹上的采样点。
/// </summary>
/// <param name="Time">采样时刻 t (s)</param>
/// <param name="Positions">各轴位置数组（物理单位）</param>
/// <param name="Velocities">各轴瞬时速度数组（物理单位/s）</param>
/// <param name="PathVelocity">沿轨迹切向速度标量（物理单位/s）</param>
public record PathPoint(
    double Time,
    double[] Positions,
    double[] Velocities,
    double PathVelocity
);

/// <summary>
/// 运动路径统一抽象接口。供上位机周期插补器或执行器统一按时间采样。
/// </summary>
public interface IMotionPath
{
    /// <summary>路径总运动时长 (s)</summary>
    double Duration { get; }

    /// <summary>涉及的轴数量</summary>
    int AxisCount { get; }

    /// <summary>
    /// 在指定时刻 t 进行采样。
    /// </summary>
    /// <param name="t">相对轨迹起始时刻的时间 (s)</param>
    /// <returns>采样点数据</returns>
    PathPoint SampleAt(double t);
}
