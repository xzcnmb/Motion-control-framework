namespace Sophon.Motion.Trajectory;

/// <summary>
/// 拐角平滑圆弧过渡结果。
/// </summary>
public class BlendResult
{
    /// <summary>过渡圆弧的起点</summary>
    public double[] ArcStart { get; set; }

    /// <summary>过渡圆弧的终点</summary>
    public double[] ArcEnd { get; set; }

    /// <summary>过渡圆弧的圆心</summary>
    public double[] Center { get; set; }

    /// <summary>过渡圆弧半径</summary>
    public double Radius { get; set; }

    /// <summary>实际最大几何轮廓偏差（确保 ≤ 设定容差 ε）</summary>
    public double MaxDeviation { get; set; }

    /// <summary>
    /// 构造过渡结果。
    /// </summary>
    public BlendResult(double[] start, double[] end, double[] center, double radius, double dev)
    {
        ArcStart = start;
        ArcEnd = end;
        Center = center;
        Radius = radius;
        MaxDeviation = dev;
    }
}

/// <summary>
/// 转角平滑过渡（Corner Blending）。
/// 在相邻两直线段之间内切圆弧过渡，
/// 给定最大允许轮廓容差 ε，保证过渡圆弧与原尖锐折角的距离偏差 ≤ ε。
/// </summary>
public static class BlendCorner
{
    /// <summary>
    /// 计算两相邻直线段在拐角点 corner 处的过渡圆弧。
    /// </summary>
    /// <param name="pPrev">前段起点</param>
    /// <param name="corner">原两段交汇拐角点</param>
    /// <param name="pNext">后段终点</param>
    /// <param name="tolerance">最大允许轮廓轮廓容差 ε (正数，物理单位)</param>
    /// <returns>平滑圆弧几何过渡参数；若无需或无法平滑（如共线、反折）则返回 null</returns>
    public static BlendResult? ComputeBlend(double[] pPrev, double[] corner, double[] pNext, double tolerance)
    {
        if (tolerance <= 1e-12) return null;
        if (pPrev.Length < 2 || corner.Length < 2 || pNext.Length < 2) return null;

        int dim = Math.Min(Math.Min(pPrev.Length, corner.Length), pNext.Length);

        // 向量 v1: corner -> pPrev
        // 向量 v2: corner -> pNext
        double[] v1 = new double[dim];
        double[] v2 = new double[dim];
        double len1Sq = 0, len2Sq = 0;

        for (int i = 0; i < dim; i++)
        {
            v1[i] = pPrev[i] - corner[i];
            v2[i] = pNext[i] - corner[i];
            len1Sq += v1[i] * v1[i];
            len2Sq += v2[i] * v2[i];
        }

        double len1 = Math.Sqrt(len1Sq);
        double len2 = Math.Sqrt(len2Sq);

        if (len1 < 1e-9 || len2 < 1e-9) return null;

        // 单位向量 u1, u2 从 corner 指向两侧
        double[] u1 = new double[dim];
        double[] u2 = new double[dim];
        double dot = 0;
        for (int i = 0; i < dim; i++)
        {
            u1[i] = v1[i] / len1;
            u2[i] = v2[i] / len2;
            dot += u1[i] * u2[i];
        }

        dot = Math.Clamp(dot, -1.0, 1.0);

        // 夹角 alpha 为 u1 与 u2 之间的夹角 (折角内角)
        // 若 dot 接近 -1，说明两段共线（方向相反：折返180度），无法平滑
        // 若 dot 接近 1，说明两段重合（方向相同：直行0度），无需平滑
        if (dot >= 0.999999 || dot <= -0.999999)
        {
            return null;
        }

        double alpha = Math.Acos(dot); // 拐角内角 (0, pi)

        // 误差推导：
        // 设过渡圆弧半径为 R，切点到 corner 的切线长为 d = R / tan(alpha / 2)。
        // 拐角尖点 corner 到圆心 C 的距离为 L_c = R / sin(alpha / 2)。
        // 过渡圆弧与 corner 点的几何最大偏差为：
        // delta = L_c - R = R * (1 / sin(alpha / 2) - 1)
        // 欲使最大偏差 delta <= tolerance ε，则：
        // R_max = tolerance / (1 / sin(alpha / 2) - 1)
        // 同时，切点距离 d 必须受限于相邻两段的长度（不能超过相邻段长度的一半）：
        // d_max = min(len1, len2) * 0.5
        // R_limit = d_max * tan(alpha / 2)
        // 最终取 R = min(R_max, R_limit)

        double sinHalf = Math.Sin(alpha * 0.5);
        double tanHalf = Math.Tan(alpha * 0.5);

        if (sinHalf <= 1e-6 || tanHalf <= 1e-6) return null;

        double rFromTolerance = tolerance / ( (1.0 / sinHalf) - 1.0 );
        double dMax = Math.Min(len1, len2) * 0.45; // 保留微小安全裕度
        double rFromLength = dMax * tanHalf;

        double R = Math.Min(rFromTolerance, rFromLength);
        double d = R / tanHalf;
        double actualDev = R * ((1.0 / sinHalf) - 1.0);

        // 计算切点 ArcStart（在前一段上）与 ArcEnd（在后一段上）
        // ArcStart = corner + u1 * d
        // ArcEnd = corner + u2 * d
        double[] arcStart = new double[dim];
        double[] arcEnd = new double[dim];
        for (int i = 0; i < dim; i++)
        {
            arcStart[i] = corner[i] + u1[i] * d;
            arcEnd[i] = corner[i] + u2[i] * d;
        }

        // 计算圆心 C：
        // 角平分线方向单位向量 u_bisector = (u1 + u2) / ||u1 + u2||
        double[] bisector = new double[dim];
        double bisectorLenSq = 0;
        for (int i = 0; i < dim; i++)
        {
            bisector[i] = u1[i] + u2[i];
            bisectorLenSq += bisector[i] * bisector[i];
        }
        double bisectorLen = Math.Sqrt(bisectorLenSq);
        if (bisectorLen < 1e-9) return null;

        double[] center = new double[dim];
        double distToCenter = R / sinHalf;
        for (int i = 0; i < dim; i++)
        {
            center[i] = corner[i] + (bisector[i] / bisectorLen) * distToCenter;
        }

        return new BlendResult(arcStart, arcEnd, center, R, actualDev);
    }
}
