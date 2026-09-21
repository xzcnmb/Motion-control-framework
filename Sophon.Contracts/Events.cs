using System;

namespace Sophon.Contracts
{
    /// <summary>
    /// 运动请求完成事件。【契约铁律】所有上层（轴状态机/流程 Move 节点/示教运行到点）
    /// 一律以 AxisDone 事件判定到位，禁止轮询 GetPosition 判断到位。
    /// 严格遵循 PLCopen 语义：互斥区分 Done(到位成功)、CommandAborted(被中途叫停/抢占)、Error(故障/越界)。
    /// </summary>
    public record AxisDoneArgs(
        Guid RequestId,
        bool Success,
        string Reason,
        CommandCompletionStatus Status = CommandCompletionStatus.Done,
        int AxisId = 0);

    /// <summary>
    /// 轴故障事件（驱动器报警/通讯错误等）。
    /// </summary>
    public record AxisFaultArgs(int AxisId, string FaultCode, string Message);

    /// <summary>
    /// 限位触发事件。IsPositiveDirection=true 表示正方向限位；IsHardLimit=false 表示软限位。
    /// </summary>
    public record LimitTriggeredArgs(int AxisId, string LimitPointName, bool IsPositiveDirection, bool IsHardLimit);

    /// <summary>
    /// 视觉触发请求。
    /// </summary>
    public record VisionTriggerRequest(Guid RequestId, string CameraId);

    /// <summary>
    /// 视觉测量结果。X/Y/AngleDeg=像素系原值（px/px/deg）；
    /// WorldX/WorldY/WorldAngleDeg=标定后的物理系（mm/mm/deg），未标定时与像素系一致。
    /// </summary>
    public record VisionResult(
        Guid RequestId,
        string CameraId,
        bool Ok,
        double X,
        double Y,
        double AngleDeg,
        double Score,
        double WorldX,
        double WorldY,
        double WorldAngleDeg);

    /// <summary>
    /// 灰度帧（实时取景用）。像素排列为行优先，每像素 1 字节。
    /// </summary>
    public record VisionFrame(byte[] GrayscaleData, int Width, int Height, long TimeStampMs);
}