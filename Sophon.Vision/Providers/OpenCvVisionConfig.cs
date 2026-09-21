using OpenCvSharp;

namespace Sophon.Vision.Providers
{
    /// <summary>
    /// OpenCv 模板匹配配置对象。
    /// </summary>
    public sealed class OpenCvVisionConfig
    {
        /// <summary>
        /// 模板图片路径（若使用文件模板）。
        /// </summary>
        public string? TemplateImagePath { get; set; }

        /// <summary>
        /// 匹配方法，默认为归一化相关系数匹配 (TM_CCOEFF_NORMED)。
        /// </summary>
        public TemplateMatchModes MatchMethod { get; set; } = TemplateMatchModes.CCoeffNormed;

        /// <summary>
        /// 判定匹配成功的最小匹配得分（0.0 ~ 1.0），默认 0.75。
        /// </summary>
        public double MinScore { get; set; } = 0.75;

        /// <summary>
        /// 搜索区域矩形（ROI），若为 null 则搜索全图。
        /// </summary>
        public Rect? SearchRegion { get; set; }

        /// <summary>
        /// 是否开启角点/轮廓特征的角度估计。
        /// 开启时会利用主方向几何矩或模板邻域梯度计算旋转角。
        /// </summary>
        public bool EnableAngleEstimation { get; set; } = false;

        /// <summary>
        /// 角度搜索范围（±度），默认 15 度。
        /// </summary>
        public double AngleSearchRangeDeg { get; set; } = 15.0;

        /// <summary>
        /// 角度搜索步长（度），默认 1.0 度。
        /// </summary>
        public double AngleSearchStepDeg { get; set; } = 1.0;

        /// <summary>
        /// 实时取景帧率（FPS），默认 15。
        /// </summary>
        public int LiveFps { get; set; } = 15;
    }
}
