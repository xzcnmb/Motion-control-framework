namespace Sophon.Motion.Trajectory;

/// <summary>
/// 运动段队列，用于管理连续多段轨迹（线段、圆弧）的缓存与前瞻调度。
/// </summary>
public sealed class SegmentQueue
{
    private readonly List<MotionSegment> _segments = new();

    /// <summary>当前队列中的所有运动段（只读快照）</summary>
    public IReadOnlyList<MotionSegment> Segments => _segments;

    /// <summary>队列中段总数</summary>
    public int Count => _segments.Count;

    /// <summary>
    /// 添加直线段到队列末尾。
    /// </summary>
    public void EnqueueLine(double[] start, double[] end, double targetSpeed)
    {
        _segments.Add(new MotionSegment(start, end, targetSpeed));
    }

    /// <summary>
    /// 添加圆弧段到队列末尾。
    /// </summary>
    public void EnqueueArc(double[] start, double[] end, double[] center, double radius, double arcLength, double targetSpeed)
    {
        _segments.Add(new MotionSegment(start, end, center, radius, arcLength, targetSpeed));
    }

    /// <summary>
    /// 添加自定义运动段。
    /// </summary>
    public void Enqueue(MotionSegment segment)
    {
        _segments.Add(segment);
    }

    /// <summary>
    /// 清空队列。
    /// </summary>
    public void Clear()
    {
        _segments.Clear();
    }

    /// <summary>
    /// 弹出队列头部的段。
    /// </summary>
    public MotionSegment? Dequeue()
    {
        if (_segments.Count == 0) return null;
        var seg = _segments[0];
        _segments.RemoveAt(0);
        return seg;
    }
}
