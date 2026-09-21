#nullable enable
namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 报警严重级别（五级标准）。
    /// </summary>
    public enum AlarmSeverity
    {
        /// <summary>提示信息，不影响运行</summary>
        Info,

        /// <summary>警告信息，需关注但不强制停止</summary>
        Warning,

        /// <summary>错误，单轴停或局部报警</summary>
        Error,

        /// <summary>停机，停止所有轴或流程正常减速停止</summary>
        Stop,

        /// <summary>紧急停止，立即切断运动并置位工站急停报警</summary>
        EStop
    }

    /// <summary>
    /// 报警联动模式。
    /// </summary>
    public enum LinkageMode
    {
        /// <summary>无联动</summary>
        None,

        /// <summary>停止触发轴</summary>
        StopAxis,

        /// <summary>停止全部轴（减速停或AbortAll）</summary>
        StopAllAxes,

        /// <summary>停止流程</summary>
        StopFlow,

        /// <summary>全部急停（AbortAll + 工站置为 Alarm）</summary>
        EStopAll
    }

    /// <summary>
    /// 报警定义元数据。
    /// </summary>
    public record AlarmDefinition(
        string Code,
        string Message,
        AlarmSeverity Severity = AlarmSeverity.Error,
        LinkageMode LinkageMode = LinkageMode.None,
        bool AutoReset = false);

    /// <summary>
    /// 活动报警记录。
    /// </summary>
    public record ActiveAlarm(
        string Code,
        string Message,
        AlarmSeverity Severity,
        LinkageMode LinkageMode,
        string Detail,
        string Source,
        System.DateTime TriggerTime);

    /// <summary>
    /// 历史报警记录（用于持久化）。
    /// </summary>
    public class AlarmHistoryRecord
    {
        public int Id { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public AlarmSeverity Severity { get; set; }
        public LinkageMode LinkageMode { get; set; }
        public string Detail { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public System.DateTime TriggerTime { get; set; }
        public System.DateTime? ClearTime { get; set; }
    }
}
