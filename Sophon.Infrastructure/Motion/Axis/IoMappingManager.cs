#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;

namespace Sophon.Infrastructure.Motion.Axis
{
    /// <summary>
    /// 工业 IO 映射管理控制器（实现 IIoController）。
    /// 核心职责：
    /// 1. 将应用层工艺逻辑点名（如 "DI_AIR_PRESSURE_OK", "DO_GRIP_OPEN"）
    ///    按配置表映射到物理硬件（卡号 + 物理通道位）；
    /// 2. 处理电气极性硬件反转 (Invert) 与 常开/常闭 (NO/NC) 逻辑反相，确保无论现场传感器是 NPN/PNP/NO/NC，上层应用逻辑值一致（1=有效，0=无效）；
    /// 3. 执行软件防抖滤波时间窗（候选值 + 候选首次翻转时间 + 稳定值 三元状态）；
    /// 4. 包装底层硬件 IO 驱动（Sim/固高/雷赛），支持动态热更新点位表（只读快照整体替换）；
    /// 5. 后台周期轮询已映射 DI，稳定值发生变化才发布 <see cref="DiChanged"/>。
    ///
    /// 线程约定：所有事件都在锁外发布，订阅者异常被隔离；后台轮询异常只记录不外逃。
    /// </summary>
    public class IoMappingManager : Sophon.Contracts.IIoController, IDisposable
    {
        /// <summary>默认后台轮询周期（毫秒）：机械接点 20ms 足够细腻，单次扫描只有几次字典查找。</summary>
        public const int DefaultPollIntervalMs = 20;

        /// <summary>Dispose 等待后台循环退出的上限（毫秒），避免 UI/流程线程被轮询线程卡住。</summary>
        private const int PollStopTimeoutMs = 500;

        private readonly IoPointConfigStore _store;
        private readonly Func<int, int, bool> _rawDiReader;
        private readonly Action<int, int, bool> _rawDoWriter;
        private readonly int _pollIntervalMs;

        private readonly CancellationTokenSource _cts = new();
        private readonly Task? _pollTask;

        /// <summary>
        /// DI 防抖状态与 DO 写入缓存的保护锁。
        /// 这两张表都用普通 Dictionary，读写一律进锁，因此不存在"字典扩容过程中被读一半"的问题。
        /// </summary>
        private readonly object _stateLock = new();
        private readonly Dictionary<string, DiDebounceState> _diStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _doStateCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>虚拟 raw 覆盖表的写入锁；读取走 <see cref="_virtualOverrides"/> 的 volatile 引用。</summary>
        private readonly object _overrideLock = new();

        /// <summary>序列化 Reload/SetPoint 的快照构造，避免两次热更新互相覆盖回旧表。</summary>
        private readonly object _reloadLock = new();

        private static readonly IReadOnlyDictionary<(int Card, int Bit), bool> EmptyVirtualOverrides =
            new Dictionary<(int Card, int Bit), bool>();

        /// <summary>
        /// 只读映射快照。<see cref="Reload"/> / <see cref="SetPoint"/> 一律构造完整的新快照后一次性替换，
        /// 读线程要么看到旧快照、要么看到完整的新快照，绝不会出现"Clear 之后还没写完"的空窗口。
        /// </summary>
        private volatile IoPointMapSnapshot _snapshot =
            new IoPointMapSnapshot(new Dictionary<string, IoPointDefinition>(StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// 虚拟 raw 物理电平覆盖（卡号 + 通道位 -&gt; 物理电平），供 Sim/测试注入。
        /// 同样以"整体替换"保证原子性，清除时不留中间态。
        /// </summary>
        private volatile IReadOnlyDictionary<(int Card, int Bit), bool> _virtualOverrides = EmptyVirtualOverrides;

        private volatile bool _isPolling;
        private long _pollCycleCount;
        private int _disposed;
        private volatile Exception? _lastError;

        /// <summary>DI 状态变化事件（点名 -&gt; 新的稳定逻辑值）。只在防抖后的稳定值发生变化时发布，相同值不重复发布。</summary>
        public event Action<string, bool>? DiChanged;

        /// <summary>
        /// IO 诊断事件：轮询/读写异常、点名未配置等都会经此出口上报（订阅者异常被隔离）。
        /// 与 <see cref="LastError"/> 配合，保证问题不被静默吞掉。
        /// </summary>
        public event Action<Exception>? IoError;

        /// <summary>最后一次诊断异常（后台轮询异常、点名未配置、底层读写失败）。</summary>
        public Exception? LastError => _lastError;

        /// <summary>后台轮询是否正在运行（Dispose 后为 false）。</summary>
        public bool IsPolling => _isPolling;

        /// <summary>实际使用的后台轮询周期（毫秒）。</summary>
        public int PollIntervalMs => _pollIntervalMs;

        /// <summary>后台轮询已完成的扫描次数（诊断用）。</summary>
        public long PollCycleCount => Interlocked.Read(ref _pollCycleCount);

        public IReadOnlyList<string> DiPointNames => _snapshot.DiNames.ToList();

        public IReadOnlyList<string> DoPointNames => _snapshot.DoNames.ToList();

        /// <param name="store">点位配置存储；为空时用默认存储（空表会写入 SeedDefaults）。</param>
        /// <param name="rawDiReader">底层物理 DI 读取委托（卡号 + 通道位 -&gt; 物理电平）。</param>
        /// <param name="rawDoWriter">底层物理 DO 写入委托（卡号 + 通道位，已完成极性映射后的物理电平）。</param>
        /// <param name="pollIntervalMs">后台轮询周期（毫秒），非正值回落到 <see cref="DefaultPollIntervalMs"/>。</param>
        /// <param name="startPolling">是否立即启动后台轮询；测试/延迟启动场景可传 false，再用 <see cref="PollDiOnce"/> 手动扫描。</param>
        public IoMappingManager(
            IoPointConfigStore? store = null,
            Func<int, int, bool>? rawDiReader = null,
            Action<int, int, bool>? rawDoWriter = null,
            int pollIntervalMs = DefaultPollIntervalMs,
            bool startPolling = true)
        {
            _store = store ?? new IoPointConfigStore();
            // 缺省回退到内存缓存（无真实卡时）
            _rawDiReader = rawDiReader ?? ((card, bit) => false);
            _rawDoWriter = rawDoWriter ?? ((card, bit, val) => { });
            _pollIntervalMs = pollIntervalMs > 0 ? pollIntervalMs : DefaultPollIntervalMs;

            Reload();

            if (startPolling)
            {
                _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
            }
        }

        /// <summary>
        /// 重新加载持久化 IO 映射表。
        /// 新表构造成完整的只读快照后一次性替换；空 Store 仍保留默认 Seed/Save 行为。
        /// </summary>
        public void Reload()
        {
            lock (_reloadLock)
            {
                var list = _store.Load();
                if (list.Count == 0)
                {
                    list = IoPointConfigStore.SeedDefaults();
                    _store.Save(list);
                }

                var map = new Dictionary<string, IoPointDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in list)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.LogicalName))
                    {
                        RecordError(new InvalidDataException("IO 映射表存在空点位或空逻辑点名，已跳过该条配置。"));
                        continue;
                    }
                    map[p.LogicalName] = p;
                }

                var snapshot = new IoPointMapSnapshot(map);
                _snapshot = snapshot; // 原子替换：读线程看不到中间态
                PruneVirtualOverrides(snapshot);
                PruneDebounceStates(snapshot);
            }
        }

        /// <summary>
        /// 注册或更新单个点位映射（同样走只读快照整体替换）。
        /// </summary>
        public void SetPoint(IoPointDefinition def)
        {
            if (def == null) throw new ArgumentNullException(nameof(def));

            lock (_reloadLock)
            {
                var copy = new Dictionary<string, IoPointDefinition>(_snapshot.Points, StringComparer.OrdinalIgnoreCase);
                copy[def.LogicalName] = def;
                _snapshot = new IoPointMapSnapshot(copy);
            }
        }

        /// <summary>
        /// 读数字输入点（逻辑语义：true=信号有效/触发，false=未触发）。
        /// 自动完成 物理电平 -&gt; 极性反转 -&gt; 常闭取反 -&gt; 滤波防抖，返回当前稳定逻辑值。
        /// </summary>
        public bool ReadDi(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return false;

            var snapshot = _snapshot;
            if (snapshot.Points.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DI)
            {
                bool raw;
                try
                {
                    raw = ReadRawDi(def);
                }
                catch (Exception ex)
                {
                    // 记录诊断痕迹后按原语义继续外抛：失败必须可见，不允许伪装成稳定值
                    RecordError(ex);
                    throw;
                }

                return UpdateDebounce(pointName, def, ToLogical(def, raw), Environment.TickCount64, publish: true);
            }

            // 点名未配置时保持历史兼容返回 false，但必须留下诊断痕迹，不能默默伪成功
            RecordUnknownPoint(pointName, IoDirection.DI);
            return false;
        }

        /// <summary>
        /// 写数字输出点（逻辑语义：true=输出有效，false=输出无效）。
        /// 自动完成 逻辑电平 -&gt; 极性反转 -&gt; 硬件物理位输出。
        /// </summary>
        public void WriteDo(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;

            var snapshot = _snapshot;
            if (snapshot.Points.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DO)
            {
                // 极性反转
                bool physicalOut = value ^ def.Invert;

                try
                {
                    _rawDoWriter(def.CardNo, def.ChannelBit, physicalOut);
                }
                catch (Exception ex)
                {
                    // 物理写失败：不更新缓存（不能把没写出去的值当成已写），但留下诊断痕迹
                    RecordError(ex);
                    throw;
                }

                lock (_stateLock)
                {
                    _doStateCache[pointName] = value;
                }
                return;
            }

            // 兼容旧行为：未配置点名仍写入缓存（SnapshotDo 只返回已映射点，不会读到它），
            // 但必须留下诊断痕迹，不能默默伪成功
            RecordUnknownPoint(pointName, IoDirection.DO);
            lock (_stateLock)
            {
                _doStateCache[pointName] = value;
            }
        }

        /// <summary>
        /// 模拟手动或外部触发输入点电平变化（测试或仿真用）。
        /// 注入的是【物理 raw 电平】，后续 <see cref="ReadDi"/> / <see cref="SnapshotDi"/> 与 <see cref="DiChanged"/>
        /// 都会经过同一套 极性反转 / 常闭取反 / 防抖滤波 链路，因此三者永远一致。
        /// </summary>
        public void SetVirtualDi(string pointName, bool value)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;

            var snapshot = _snapshot;
            if (snapshot.Points.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DI)
            {
                SetVirtualRawDi(def.CardNo, def.ChannelBit, value);
                // 立刻走一次与后台轮询完全相同的采样，让稳定值与事件同步跟上
                SampleDiPoint(pointName, def, publish: true);
                return;
            }

            RecordUnknownPoint(pointName, IoDirection.DI);
            // 兼容旧行为：未配置点名仍然广播一次
            RaiseDiChanged(pointName, value);
        }

        /// <summary>
        /// 清除指定 DI 点的虚拟注入，恢复由底层 <see cref="_rawDiReader"/> 提供物理电平。
        /// 清除后会重新采样一次，让稳定值与事件回到真实链路。
        /// </summary>
        public void ClearVirtualDi(string pointName)
        {
            if (string.IsNullOrWhiteSpace(pointName)) return;

            var snapshot = _snapshot;
            if (snapshot.Points.TryGetValue(pointName, out var def) && def.Direction == IoDirection.DI)
            {
                ClearVirtualRawDi(def.CardNo, def.ChannelBit);
                SampleDiPoint(pointName, def, publish: true);
            }
        }

        /// <summary>清除全部虚拟注入，并逐点重新采样，使稳定值回到真实链路。</summary>
        public void ClearAllVirtualDi()
        {
            lock (_overrideLock)
            {
                _virtualOverrides = EmptyVirtualOverrides;
            }

            var snapshot = _snapshot;
            foreach (var name in snapshot.DiNames)
            {
                if (snapshot.Points.TryGetValue(name, out var def) && def.Direction == IoDirection.DI)
                {
                    SampleDiPoint(name, def, publish: true);
                }
            }
        }

        public IReadOnlyDictionary<string, bool> SnapshotDi()
        {
            var snapshot = _snapshot;
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            // 只取快照内已映射的 DI 点，返回的都是当前稳定逻辑值
            foreach (var name in snapshot.DiNames)
            {
                if (snapshot.Points.TryGetValue(name, out var def) && def.Direction == IoDirection.DI)
                {
                    dict[name] = ReadDi(name);
                }
            }
            return dict;
        }

        public IReadOnlyDictionary<string, bool> SnapshotDo()
        {
            var snapshot = _snapshot;
            var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            lock (_stateLock)
            {
                foreach (var name in snapshot.DoNames)
                {
                    dict[name] = _doStateCache.TryGetValue(name, out var val) && val;
                }
            }
            return dict;
        }

        /// <summary>
        /// 立即执行一次 DI 扫描（只扫描已映射 DI 点）。后台循环与手动诊断/测试共用同一条采样链路，
        /// 调用线程不会被阻塞（无 Thread.Sleep），底层异常不会逃逸。
        /// </summary>
        public void PollDiOnce()
        {
            var snapshot = _snapshot;
            var names = snapshot.DiNames;
            if (names.Count == 0) return;

            long now = Environment.TickCount64;
            for (int i = 0; i < names.Count; i++)
            {
                if (!snapshot.Points.TryGetValue(names[i], out var def) || def.Direction != IoDirection.DI)
                {
                    continue;
                }

                bool logical;
                try
                {
                    logical = ToLogical(def, ReadRawDi(def));
                }
                catch (Exception ex)
                {
                    // 单点读失败只记录并跳过，不影响其它点，也不会打崩轮询线程
                    RecordError(ex);
                    continue;
                }

                UpdateDebounce(names[i], def, logical, now, publish: true);
            }
        }

        /// <summary>
        /// 停止后台轮询并释放内部 CancellationTokenSource。
        /// 反复调用安全；轮询循环退出时自行释放 CTS，这里只做取消与有界等待。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try { _cts.Cancel(); } catch (ObjectDisposedException) { }

            var task = _pollTask;
            if (task != null)
            {
                try { task.Wait(TimeSpan.FromMilliseconds(PollStopTimeoutMs)); }
                catch { /* 取消/超时都不允许从 Dispose 逃逸 */ }
            }

            // 轮询循环退出时会自行 Dispose CTS；只有在没有启动轮询或已确认停止时才由这里兜底释放，
            // 避免与轮询线程争用同一个 CTS。
            if (task == null || task.IsCompleted)
            {
                try { _cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// 虚拟 raw 覆盖查询点：默认读 <see cref="SetVirtualDi"/> 注入的覆盖表，
        /// 子类（Sim/测试 Fake）可重写以提供自己的 raw 注入来源。
        /// </summary>
        protected virtual bool? TryGetVirtualRawDi(int cardNo, int channelBit)
        {
            var overrides = _virtualOverrides;
            return overrides.TryGetValue((cardNo, channelBit), out bool raw) ? raw : (bool?)null;
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            _isPolling = true;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollIntervalMs));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _pollCycleCount);
                    try
                    {
                        PollDiOnce();
                    }
                    catch (Exception ex)
                    {
                        // 轮询异常绝不放任逃逸（否则后台任务静默死亡），记录并可诊断
                        RecordError(ex);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose 取消：正常退出路径
            }
            catch (Exception ex)
            {
                RecordError(ex);
            }
            finally
            {
                _isPolling = false;
                try { _cts.Dispose(); } catch (ObjectDisposedException) { }
            }
        }

        /// <summary>读物理电平：虚拟覆盖优先，交 <see cref="_rawDiReader"/> 兜底。</summary>
        private bool ReadRawDi(IoPointDefinition def)
        {
            bool? overridden = TryGetVirtualRawDi(def.CardNo, def.ChannelBit);
            if (overridden.HasValue) return overridden.Value;
            return _rawDiReader(def.CardNo, def.ChannelBit);
        }

        /// <summary>物理电平 -&gt; 逻辑值（极性反转 + 常闭取反）。全仓库唯一映射入口。</summary>
        private static bool ToLogical(IoPointDefinition def, bool raw)
        {
            // 常闭NC在未触发时物理导通(raw=1)，逻辑应为 false；动作断开(raw=0)时，逻辑为 true。
            return raw ^ def.Invert ^ (def.Switch == SwitchType.NormallyClose);
        }

        /// <summary>读一次物理电平并喂给防抖状态机（虚拟注入 / 后台轮询共用）。</summary>
        private bool SampleDiPoint(string pointName, IoPointDefinition def, bool publish)
        {
            bool logical;
            try
            {
                logical = ToLogical(def, ReadRawDi(def));
            }
            catch (Exception ex)
            {
                RecordError(ex);
                return GetStableState(pointName);
            }

            return UpdateDebounce(pointName, def, logical, Environment.TickCount64, publish);
        }

        /// <summary>防抖状态机：候选值 + 候选首次翻转时间 + 稳定值。</summary>
        private bool UpdateDebounce(string pointName, IoPointDefinition def, bool logical, long nowTick, bool publish)
        {
            bool result;
            bool changed = false;

            lock (_stateLock)
            {
                if (!_diStates.TryGetValue(pointName, out var state))
                {
                    // 初次采样只建立稳定值，不发布伪边沿
                    state = new DiDebounceState(logical, logical, nowTick);
                    _diStates[pointName] = state;
                    return logical;
                }

                result = state.Stable;

                if (logical == state.Stable)
                {
                    // 与当前稳定值一致：收敛候选，禁止重复发布
                    state.Candidate = logical;
                    state.CandidateSinceTick = nowTick;
                }
                else
                {
                    if (logical != state.Candidate)
                    {
                        // 出现新候选：记录候选值首次出现的时刻，滤波窗口从这里起算
                        state.Candidate = logical;
                        state.CandidateSinceTick = nowTick;
                    }

                    // 物理值保持超过 FilterMs 才确认新稳定值；无滤波(FilterMs=0)时立即确认
                    if (def.FilterMs <= 0 || nowTick - state.CandidateSinceTick >= def.FilterMs)
                    {
                        state.Stable = logical;
                        state.CandidateSinceTick = nowTick;
                        result = logical;
                        changed = true;
                    }
                }
            }

            // 事件一律在锁外发布，订阅者异常被隔离
            if (changed && publish)
            {
                RaiseDiChanged(pointName, result);
            }

            return result;
        }

        private bool GetStableState(string pointName)
        {
            lock (_stateLock)
            {
                return _diStates.TryGetValue(pointName, out var state) && state.Stable;
            }
        }

        private void SetVirtualRawDi(int cardNo, int channelBit, bool value)
        {
            lock (_overrideLock)
            {
                var copy = new Dictionary<(int Card, int Bit), bool>(_virtualOverrides);
                copy[(cardNo, channelBit)] = value;
                _virtualOverrides = copy; // 原子替换
            }
        }

        private void ClearVirtualRawDi(int cardNo, int channelBit)
        {
            lock (_overrideLock)
            {
                var current = _virtualOverrides;
                if (!current.ContainsKey((cardNo, channelBit))) return;

                var copy = new Dictionary<(int Card, int Bit), bool>(current);
                copy.Remove((cardNo, channelBit));
                _virtualOverrides = copy; // 原子替换
            }
        }

        /// <summary>Reload 后丢弃已不存在点名的防抖状态，避免脏状态影响新映射。</summary>
        private void PruneDebounceStates(IoPointMapSnapshot snapshot)
        {
            lock (_stateLock)
            {
                if (_diStates.Count == 0) return;

                var stale = _diStates.Keys.Where(k => !snapshot.Points.ContainsKey(k)).ToList();
                foreach (var name in stale)
                {
                    _diStates.Remove(name);
                }
            }
        }

        /// <summary>Reload 后丢弃不再被任何 DI 点引用的虚拟注入（整体替换，非原地删）。</summary>
        private void PruneVirtualOverrides(IoPointMapSnapshot snapshot)
        {
            lock (_overrideLock)
            {
                var current = _virtualOverrides;
                if (current.Count == 0) return;

                var live = new HashSet<(int Card, int Bit)>();
                foreach (var name in snapshot.DiNames)
                {
                    if (snapshot.Points.TryGetValue(name, out var def) && def.Direction == IoDirection.DI)
                    {
                        live.Add((def.CardNo, def.ChannelBit));
                    }
                }

                var kept = new Dictionary<(int Card, int Bit), bool>();
                foreach (var kv in current)
                {
                    if (live.Contains(kv.Key)) kept[kv.Key] = kv.Value;
                }

                if (kept.Count != current.Count)
                {
                    _virtualOverrides = kept;
                }
            }
        }

        private void RecordUnknownPoint(string pointName, IoDirection direction)
        {
            RecordError(new InvalidOperationException(
                $"{(direction == IoDirection.DI ? "DI" : "DO")} 点名「{pointName}」未在 IO 映射表中配置，" +
                "该点不映射到任何物理通道：已按兼容行为返回/写入，但没有真实硬件动作。"));
        }

        private void RecordError(Exception ex)
        {
            _lastError = ex;
            RaiseIoError(ex);
        }

        private void RaiseDiChanged(string pointName, bool value)
        {
            var handler = DiChanged;
            if (handler == null) return;

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<string, bool>)subscriber)(pointName, value);
                }
                catch
                {
                    // 订阅者异常隔离：不得影响轮询线程或调用方
                }
            }
        }

        private void RaiseIoError(Exception ex)
        {
            var handler = IoError;
            if (handler == null) return;

            foreach (var subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<Exception>)subscriber)(ex);
                }
                catch
                {
                    // 订阅者异常隔离
                }
            }
        }

        /// <summary>DI 防抖三元状态：稳定值 / 候选值 / 候选首次翻转时刻（只在 <see cref="_stateLock"/> 内访问）。</summary>
        private sealed class DiDebounceState
        {
            public DiDebounceState(bool stable, bool candidate, long candidateSinceTick)
            {
                Stable = stable;
                Candidate = candidate;
                CandidateSinceTick = candidateSinceTick;
            }

            public bool Stable { get; set; }

            public bool Candidate { get; set; }

            public long CandidateSinceTick { get; set; }
        }

        /// <summary>不可变映射快照：构造完成后不再修改，发布后即可被任意线程安全读取。</summary>
        private sealed class IoPointMapSnapshot
        {
            public IoPointMapSnapshot(IReadOnlyDictionary<string, IoPointDefinition> points)
            {
                Points = points;

                var di = new List<string>();
                var doPoints = new List<string>();
                foreach (var p in points.Values)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.LogicalName)) continue;
                    if (p.Direction == IoDirection.DI) di.Add(p.LogicalName);
                    else doPoints.Add(p.LogicalName);
                }

                DiNames = di;
                DoNames = doPoints;
            }

            public IReadOnlyDictionary<string, IoPointDefinition> Points { get; }

            public IReadOnlyList<string> DiNames { get; }

            public IReadOnlyList<string> DoNames { get; }
        }
    }
}
