namespace Sophon.Motion.Trajectory;

/// <summary>
/// 轨迹几何段类型。
/// </summary>
public enum SegmentGeometryKind
{
    /// <summary>直线段</summary>
    Line,
    /// <summary>圆弧段</summary>
    Arc
}

/// <summary>
/// 轨迹队列中的运动段单元。
/// </summary>
public class MotionSegment
{
    /// <summary>段几何类型</summary>
    public SegmentGeometryKind Kind { get; set; } = SegmentGeometryKind.Line;

    /// <summary>起点坐标</summary>
    public double[] StartPoint { get; set; }

    /// <summary>终点坐标</summary>
    public double[] EndPoint { get; set; }

    /// <summary>线段长度 / 弧长 (物理单位)</summary>
    public double Length { get; set; }

    /// <summary>期望运行速度 (Target Feedrate)</summary>
    public double TargetSpeed { get; set; }

    /// <summary>前瞻规划后的入口允许速度</summary>
    public double EntrySpeed { get; set; }

    /// <summary>前瞻规划后的出口允许速度</summary>
    public double ExitSpeed { get; set; }

    /// <summary>圆弧参数（当 Kind == Arc 时有效，圆心坐标）</summary>
    public double[]? ArcCenter { get; set; }

    /// <summary>圆弧半径</summary>
    public double ArcRadius { get; set; }

    /// <summary>
    /// 构造直线运动段。
    /// </summary>
    public MotionSegment(double[] start, double[] end, double targetSpeed)
    {
        StartPoint = (double[])start.Clone();
        EndPoint = (double[])end.Clone();
        TargetSpeed = targetSpeed;
        Kind = SegmentGeometryKind.Line;

        double sumSq = 0;
        for (int i = 0; i < start.Length; i++)
        {
            double d = end[i] - start[i];
            sumSq += d * d;
        }
        Length = Math.Sqrt(sumSq);
    }

    /// <summary>
    /// 构造圆弧运动段。
    /// </summary>
    public MotionSegment(double[] start, double[] end, double[] center, double radius, double arcLength, double targetSpeed)
    {
        StartPoint = (double[])start.Clone();
        EndPoint = (double[])end.Clone();
        ArcCenter = (double[])center.Clone();
        ArcRadius = radius;
        Length = arcLength;
        TargetSpeed = targetSpeed;
        Kind = SegmentGeometryKind.Arc;
    }

    /// <summary>
    /// 获取起点的切线单位方向向量。
    /// </summary>
    public double[] GetStartTangent()
    {
        if (Kind == SegmentGeometryKind.Line)
        {
            return GetLineDirection();
        }

        // 圆弧切线
        if (ArcCenter == null || ArcRadius < 1e-12) return GetLineDirection();
        // 向量 R = Start - Center
        double rx = StartPoint[0] - ArcCenter[0];
        double ry = StartPoint[1] - ArcCenter[1];
        // 判定绕向：根据 Start 到 End
        double cross = rx * (EndPoint[1] - StartPoint[1]) - ry * (EndPoint[0] - StartPoint[0]);
        double dir = cross >= 0 ? 1.0 : -1.0;
        // 切线 [-ry, rx] * dir
        double tx = -ry * dir;
        double ty = rx * dir;
        double len = Math.Sqrt(tx * tx + ty * ty);
        if (len < 1e-12) return new double[] { 1, 0, 0 };
        return new double[] { tx / len, ty / len, 0 };
    }

    /// <summary>
    /// 获取终点的切线单位方向向量。
    /// </summary>
    public double[] GetEndTangent()
    {
        if (Kind == SegmentGeometryKind.Line)
        {
            return GetLineDirection();
        }

        if (ArcCenter == null || ArcRadius < 1e-12) return GetLineDirection();
        double rx = EndPoint[0] - ArcCenter[0];
        double ry = EndPoint[1] - ArcCenter[1];
        double cross = (StartPoint[0] - ArcCenter[0]) * (EndPoint[1] - ArcCenter[1]) -
                       (StartPoint[1] - ArcCenter[1]) * (EndPoint[0] - ArcCenter[0]);
        double dir = cross >= 0 ? 1.0 : -1.0;
        double tx = -ry * dir;
        double ty = rx * dir;
        double len = Math.Sqrt(tx * tx + ty * ty);
        if (len < 1e-12) return new double[] { 1, 0, 0 };
        return new double[] { tx / len, ty / len, 0 };
    }

    private double[] GetLineDirection()
    {
        var dir = new double[StartPoint.Length];
        if (Length < 1e-12) return dir;
        for (int i = 0; i < StartPoint.Length; i++)
        {
            dir[i] = (EndPoint[i] - StartPoint[i]) / Length;
        }
        return dir;
    }
}
