#nullable enable
namespace Sophon.Core.Alarm
{
    /// <summary>
    /// 报警联动处理器接口。
    /// </summary>
    public interface IAlarmLinkageHandler
    {
        /// <summary>
        /// 执行报警联动动作。
        /// </summary>
        /// <param name="def">报警定义</param>
        /// <param name="detail">报警明细或附加信息（如轴编号、限位点）</param>
        void Handle(AlarmDefinition def, string detail);
    }
}
