#nullable enable
using System;

namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 安全等级分类（工业安全语义，从低到高）：
    /// <list type="bullet">
    /// <item><see cref="Safe"/>：一切正常，无任何激活的安全相关条件。</item>
    /// <item><see cref="Warning"/>：存在需关注但不强制停止的条件（软限位预警、Info/Warning 级报警）。</item>
    /// <item><see cref="Interlocked"/>：被互锁 / 禁止条件拦截（气缸互锁组被占用、瞬态方向禁止、Error 级报警），
    /// 运动被拒绝但设备保持受控，需人工确认或复位后方可继续。</item>
    /// <item><see cref="FaultEStop"/>：故障急停级（看门狗超时通信丢失、硬限位触发、Stop/EStop 级报警），
    /// 必须停所有轴并等待人工干预，禁止自动恢复运行。</item>
    /// </list>
    /// </summary>
    public enum SafetyLevel
    {
        /// <summary>安全：无任何激活的安全相关条件</summary>
        Safe,

        /// <summary>警告：需关注但不强制停止</summary>
        Warning,

        /// <summary>互锁：被互锁条件拦截，需人工确认 / 复位</summary>
        Interlocked,

        /// <summary>故障急停：看门狗超时 / 硬限位 / 急停级报警，必须人工干预</summary>
        FaultEStop
    }

    /// <summary>
    /// 安全评估输入（聚合四类安全信号）：
    /// 看门狗状态、限位状态（硬 / 软 / 瞬态禁止）、气缸互锁、全局报警严重级别。
    /// </summary>
    public sealed class SafetyInputs
    {
        /// <summary>
        /// 看门狗是否处于激活（心跳正常）状态。
        /// false 表示快照通信已丢失超过阈值（看门狗超时）。
        /// </summary>
        public bool WatchdogActive { get; set; } = true;

        /// <summary>是否存在硬限位触发（急停级）。</summary>
        public bool HardLimitTriggered { get; set; }

        /// <summary>是否存在软限位越界（预警 / 拦截级）。</summary>
        public bool SoftLimitTriggered { get; set; }

        /// <summary>是否存在瞬态协议下的方向运动禁止（限位触发后禁止同方向运动）。</summary>
        public bool MotionProhibited { get; set; }

        /// <summary>气缸互锁组是否处于占用状态（同组已有气缸占用工作位）。</summary>
        public bool CylinderInterlockEngaged { get; set; }

        /// <summary>全局最高报警严重级别（无活动报警时为 null）。</summary>
        public AlarmSeverity? MaxAlarmSeverity { get; set; }
    }

    /// <summary>
    /// 安全评估结果：聚合等级 + 判定原因（用于 HMI 展示与日志追溯）。
    /// </summary>
    public sealed class SafetyEvaluation
    {
        public SafetyEvaluation(SafetyLevel level, string reason)
        {
            Level = level;
            Reason = reason;
        }

        /// <summary>聚合后的安全等级。</summary>
        public SafetyLevel Level { get; }

        /// <summary>判定原因（取最严重的触发条件描述）。</summary>
        public string Reason { get; }

        /// <summary>是否处于故障急停级（需人工干预）。</summary>
        public bool IsFaultEStop => Level == SafetyLevel.FaultEStop;

        /// <summary>是否安全（可正常自动运行）。</summary>
        public bool IsSafe => Level == SafetyLevel.Safe;

        /// <summary>
        /// 聚合评估：按工业安全语义取最严重等级。
        /// 优先级：FaultEStop（看门狗超时 / 硬限位 / Stop、EStop 级报警）
        /// &gt; Interlocked（气缸互锁 / 方向禁止 / Error 级报警 / 软限位）
        /// &gt; Warning（Warning 级报警 / 软限位预警）
        /// &gt; Safe。
        /// </summary>
        public static SafetyEvaluation Evaluate(SafetyInputs? inputs)
        {
            if (inputs == null)
            {
                return new SafetyEvaluation(SafetyLevel.Safe, "无安全输入，默认安全");
            }

            // 1) 故障急停级：通信看门狗超时 / 硬限位 / Stop、EStop 级报警
            if (!inputs.WatchdogActive)
            {
                return new SafetyEvaluation(SafetyLevel.FaultEStop, "看门狗超时：快照通信丢失，设备状态不可信，停所有轴并等待人工干预");
            }

            if (inputs.HardLimitTriggered)
            {
                return new SafetyEvaluation(SafetyLevel.FaultEStop, "硬限位触发：轴已到达硬件限位，禁止自动恢复运动");
            }

            if (inputs.MaxAlarmSeverity is AlarmSeverity.EStop or AlarmSeverity.Stop)
            {
                return new SafetyEvaluation(SafetyLevel.FaultEStop,
                    $"存在 {inputs.MaxAlarmSeverity} 级报警：{AlarmSeverityText(inputs.MaxAlarmSeverity.Value)}");
            }

            // 2) 互锁级：气缸互锁占用 / 瞬态方向禁止 / Error 级报警
            if (inputs.CylinderInterlockEngaged)
            {
                return new SafetyEvaluation(SafetyLevel.Interlocked, "气缸互锁组被占用：同组气缸处于工作位置，禁止同时伸出");
            }

            if (inputs.MotionProhibited)
            {
                return new SafetyEvaluation(SafetyLevel.Interlocked, "瞬态协议方向禁止：限位触发后禁止向该方向运动，需手动复位");
            }

            if (inputs.MaxAlarmSeverity == AlarmSeverity.Error)
            {
                return new SafetyEvaluation(SafetyLevel.Interlocked, "存在 Error 级报警：相关动作被拦截，需人工确认");
            }

            // 3) 警告级：软限位越界 / Warning 级报警
            if (inputs.SoftLimitTriggered)
            {
                return new SafetyEvaluation(SafetyLevel.Warning, "软限位越界：轴已超出软限位范围，请注意");
            }

            if (inputs.MaxAlarmSeverity is AlarmSeverity.Warning or AlarmSeverity.Info)
            {
                return new SafetyEvaluation(SafetyLevel.Warning,
                    $"存在 {inputs.MaxAlarmSeverity} 级报警：{AlarmSeverityText(inputs.MaxAlarmSeverity.Value)}");
            }

            // 4) 安全
            return new SafetyEvaluation(SafetyLevel.Safe, "所有安全条件正常");
        }

        private static string AlarmSeverityText(AlarmSeverity severity) => severity switch
        {
            AlarmSeverity.Info => "提示信息",
            AlarmSeverity.Warning => "警告",
            AlarmSeverity.Error => "错误",
            AlarmSeverity.Stop => "停机",
            AlarmSeverity.EStop => "紧急停止",
            _ => "未知"
        };
    }
}
