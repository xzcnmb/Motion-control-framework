#nullable enable
using System;
using System.Collections.Generic;

namespace Sophon.Core
{
    /// <summary>
    /// 工站档案：物理单元（工站）绑定一张配方图（流程图）。
    /// 工站名和流程名可以不同；一个流程可被多个工站引用，同时只允许一个工站在跑。
    /// </summary>
    public sealed class WorkStationProfile
    {
        public string StationName { get; set; } = string.Empty;

        /// <summary>绑定的流程图名。空表示未绑定，启动应进报警。</summary>
        public string BoundFlowName { get; set; } = string.Empty;

        /// <summary>生产默认循环。单次模式仅测试使用。</summary>
        public bool LoopRecipe { get; set; } = true;
    }

    public sealed class WorkStationProfileFile
    {
        public List<WorkStationProfile> Stations { get; set; } = new();
    }
}
