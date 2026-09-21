using System;

namespace Sophon.Contracts
{
    /// <summary>
    /// 时间源抽象：真实实现=Stopwatch；测试注入 ManualTimeSource（虚拟时钟），
    /// 让插补/停止/暂停恢复/急停中断全部确定性可测。
    /// </summary>
    public interface ITimeSource
    {
        /// <summary>自时钟启动以来经过的时间。</summary>
        TimeSpan Elapsed { get; }

        /// <summary>自时钟启动以来经过的毫秒数。</summary>
        long ElapsedMilliseconds { get; }
    }

    /// <summary>
    /// 真实时间源（Stopwatch 实现）。
    /// </summary>
    public sealed class StopwatchTimeSource : ITimeSource
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public TimeSpan Elapsed => _sw.Elapsed;
        public long ElapsedMilliseconds => _sw.ElapsedMilliseconds;
    }

    /// <summary>
    /// 虚拟时间源：测试手动推进。
    /// </summary>
    public sealed class ManualTimeSource : ITimeSource
    {
        private TimeSpan _now = TimeSpan.Zero;
        public TimeSpan Elapsed => _now;
        public long ElapsedMilliseconds => (long)_now.TotalMilliseconds;
        public void Advance(TimeSpan delta) => _now += delta;
        public void Set(TimeSpan time) => _now = time;
    }
}