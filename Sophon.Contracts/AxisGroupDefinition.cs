using System.Collections.Generic;

namespace Sophon.Contracts
{
    /// <summary>
    /// 轴组 (Axis Group / 坐标系) 定义实体（对标 PLCopen Part 4 协调运动规范）。
    /// 将多个单轴绑定为一个运动协调单元（如 XY 龙门、XYZ 笛卡尔机械手、点胶双轴），
    /// 提供群组状态机管理、插补同步、Fail-Fast 联动与单轴抢占仲裁。
    /// </summary>
    public class AxisGroupDefinition
    {
        /// <summary>轴组唯一名称（如 "GantryXY", "RobotArm_XYZ"）。</summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>成员轴 ID 列表（有序，如 0=X, 1=Y, 2=Z）。</summary>
        public List<int> AxisIds { get; set; } = new();

        /// <summary>群组默认合成定位速度 (mm/s)。</summary>
        public double DefaultSpeed { get; set; } = 100.0;

        /// <summary>群组默认合成加速度 (mm/s²)。</summary>
        public double DefaultAccel { get; set; } = 500.0;

        /// <summary>群组默认合成减速度 (mm/s²)。</summary>
        public double DefaultDecel { get; set; } = 500.0;

        /// <summary>描述说明。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>工站侧显示名。空则用工位 GroupName。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>本组相关 DI（限位、到位等），工站操作组时一起看，不是运动学 TCP。</summary>
        public List<string> RelatedDiNames { get; set; } = new();

        /// <summary>本组相关 DO（夹爪、气缸等）。</summary>
        public List<string> RelatedDoNames { get; set; } = new();
    }
}
