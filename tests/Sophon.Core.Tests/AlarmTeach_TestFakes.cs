using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;

namespace Sophon.Core.Tests
{
    /// <summary>
    /// 测试用假运动控制器，供 Alarm/Teach 单元测试使用。
    /// </summary>
    public class FakeMotionControllerForAlarmTeach : IMotionController
    {
        public DriverKind Kind => DriverKind.Simulated;
        public ConnectionState State => ConnectionState.Ready;
        public event Action<ConnectionState>? StateChanged;
        public MotionCapability Capabilities => MotionCapability.HardLimitInput;

        private readonly List<AxisDefinition> _axes = new();
        public IReadOnlyList<AxisDefinition> Axes => _axes;

        public event Action<AxisDoneArgs>? AxisDone;
        public event Action<AxisFaultArgs>? AxisFault;
        public event Action<LimitTriggeredArgs>? LimitTriggered;

        public Dictionary<int, double> Positions { get; } = new();
        public Dictionary<int, double> Velocities { get; } = new();
        public HashSet<int> EnabledAxes { get; } = new();
        public HashSet<int> HomedAxes { get; } = new();

        public List<(int axisId, double target, double speed, Guid reqId)> MoveAbsCalls { get; } = new();
        public List<(int axisId, int dir, double speed, Guid reqId)> JogCalls { get; } = new();
        public List<int> StoppedAxes { get; } = new();
        public List<int> AbortedAxes { get; } = new();
        public bool AbortAllCalled { get; set; }

        public bool AutoFireAxisDoneOnMoveAbs { get; set; } = true;

        public FakeMotionControllerForAlarmTeach(params AxisDefinition[] axes)
        {
            if (axes != null && axes.Length > 0)
            {
                _axes.AddRange(axes);
                foreach (var ax in axes)
                {
                    Positions[ax.AxisId] = 0.0;
                    Velocities[ax.AxisId] = 0.0;
                }
            }
        }

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;

        public void EnableAxis(int axisId) => EnabledAxes.Add(axisId);
        public void DisableAxis(int axisId) => EnabledAxes.Remove(axisId);
        public bool IsAxisEnabled(int axisId) => EnabledAxes.Contains(axisId);
        public bool IsAxisHomed(int axisId) => HomedAxes.Contains(axisId);
        public double GetPosition(int axisId) => Positions.TryGetValue(axisId, out var p) ? p : 0.0;
        public double GetVelocity(int axisId) => Velocities.TryGetValue(axisId, out var v) ? v : 0.0;

        public Guid Jog(int axisId, int dir, double speed)
        {
            var req = Guid.NewGuid();
            JogCalls.Add((axisId, dir, speed, req));
            return req;
        }

        public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0)
        {
            var req = Guid.NewGuid();
            MoveAbsCalls.Add((axisId, target, speed, req));
            Positions[axisId] = target;

            if (AutoFireAxisDoneOnMoveAbs)
            {
                Task.Run(async () =>
                {
                    await Task.Delay(10);
                    FireAxisDone(req, true);
                });
            }

            return req;
        }

        public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0)
        {
            return MoveAbs(axisId, GetPosition(axisId) + delta, speed, accel, decel, jerk);
        }

        public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed)
        {
            var req = Guid.NewGuid();
            HomedAxes.Add(axisId);
            Positions[axisId] = 0.0;
            return req;
        }

        public Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<AxisDoneArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            MoveAbsCalls.Add((axisId, target, speed, Guid.NewGuid()));
            Positions[axisId] = target;
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            tcs.TrySetResult(new AxisDoneArgs(Guid.NewGuid(), true, string.Empty));
            return tcs.Task;
        }

        public Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, CancellationToken ct = default)
        {
            HomedAxes.Add(axisId);
            Positions[axisId] = 0.0;
            return Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), true, string.Empty));
        }

        public void Halt(int axisId) => StoppedAxes.Add(axisId);
        public void Stop(int axisId) => StoppedAxes.Add(axisId);
        public void StopMotion(int axisId) => StoppedAxes.Add(axisId);
        public void EmergencyStop(int axisId) => AbortedAxes.Add(axisId);
        public void Abort(int axisId, double decelRatio = 0) => AbortedAxes.Add(axisId);
        public void EmergencyStopAll() => AbortAllCalled = true;
        public void AbortAll() => AbortAllCalled = true;
        public void ResetAxis(int axisId) { }

        public void FireAxisDone(Guid reqId, bool success, string reason = "")
        {
            AxisDone?.Invoke(new AxisDoneArgs(reqId, success, reason));
        }

        public void FireLimitTriggered(int axisId, string limitPointName, bool isPositiveDirection, bool isHardLimit)
        {
            LimitTriggered?.Invoke(new LimitTriggeredArgs(axisId, limitPointName, isPositiveDirection, isHardLimit));
        }

        public void Dispose() { }
    }

    /// <summary>
    /// 测试用假 IO 控制器。
    /// </summary>
    public class FakeIoControllerForAlarmTeach : IIoController
    {
        public Dictionary<string, bool> DiMap { get; } = new();
        public Dictionary<string, bool> DoMap { get; } = new();

        public event Action<string, bool>? DiChanged;
        public IReadOnlyList<string> DiPointNames => new List<string>(DiMap.Keys);
        public IReadOnlyList<string> DoPointNames => new List<string>(DoMap.Keys);

        public bool ReadDi(string pointName) => DiMap.TryGetValue(pointName, out var v) && v;
        public void WriteDo(string pointName, bool value) => DoMap[pointName] = value;
        public IReadOnlyDictionary<string, bool> SnapshotDi() => new Dictionary<string, bool>(DiMap);
        public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>(DoMap);

        public void SetDi(string pointName, bool value)
        {
            DiMap[pointName] = value;
            DiChanged?.Invoke(pointName, value);
        }
    }
}
