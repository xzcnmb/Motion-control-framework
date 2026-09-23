using System.Collections.Generic;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion;
using Sophon.Infrastructure.Motion.Drivers;
using Sophon.Infrastructure.Motion.Sim;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    /// <summary>
    /// Item ⑥ Motion HAL 能力模型测试：
    /// 能力查询扩展、各驱动能力位组合正确性、以及「只看功能标志、不看厂商品牌」的校验/映射行为。
    /// </summary>
    public class MotionCapabilityTests
    {
        // ---------- 1. 能力查询扩展 (Supports) ----------

        [Fact]
        public void Supports_ShouldQueryControllerCapabilityBits()
        {
            using var sim = new SimMotionController();

            Assert.True(sim.Supports(MotionCapability.HostInterpolation));
            Assert.True(sim.Supports(MotionCapability.BufferedSegments));
            Assert.True(sim.Supports(MotionCapability.HardLimitInput));
            Assert.True(sim.Supports(MotionCapability.ContinuousVelocityBlending));

            Assert.False(sim.Supports(MotionCapability.PvTStreaming));
            Assert.False(sim.Supports(MotionCapability.HardwareCompareOutput));
            Assert.False(sim.Supports(MotionCapability.BacklashCompensation));
            Assert.False(sim.Supports(MotionCapability.TimeBasedAcceleration));
            Assert.False(sim.Supports(MotionCapability.HardwareEStopInput));
        }

        [Fact]
        public void Supports_NoneCapability_ShouldAlwaysBeTrue()
        {
            using var sim = new SimMotionController();
            Assert.True(sim.Supports(MotionCapability.None));
            Assert.True(MotionCapability.None.Supports(MotionCapability.None));
        }

        [Fact]
        public void Supports_CombinedCapability_ShouldRequireAllBits()
        {
            using var sim = new SimMotionController();

            // 组合值全部具备 -> true
            Assert.True(sim.Supports(MotionCapability.HostInterpolation | MotionCapability.BufferedSegments));

            // 组合值缺一位 -> false（能力模型按位与全量判定，不允许"部分支持"假阳性）
            Assert.False(sim.Supports(MotionCapability.HostInterpolation | MotionCapability.PvTStreaming));
        }

        [Fact]
        public void SupportsAny_ShouldHitWhenAnyBitMatches()
        {
            using var sim = new SimMotionController();

            Assert.True(sim.SupportsAny(MotionCapability.PvTStreaming | MotionCapability.HostInterpolation));
            Assert.False(sim.SupportsAny(MotionCapability.PvTStreaming | MotionCapability.BacklashCompensation));
            Assert.True(sim.SupportsAny(MotionCapability.None));
        }

        [Fact]
        public void Supports_NullController_ShouldReturnFalse()
        {
            IMotionController? controller = null;
            Assert.False(controller!.Supports(MotionCapability.BufferedSegments));
        }

        // ---------- 2. 各驱动能力位组合正确性 (bitmask composition) ----------

        [Fact]
        public void SimController_Capabilities_ShouldMatchDeclaredBitmask()
        {
            using var sim = new SimMotionController();
            Assert.Equal(
                MotionCapability.BufferedSegments |
                MotionCapability.HostInterpolation |
                MotionCapability.HardLimitInput |
                MotionCapability.ContinuousVelocityBlending,
                sim.Capabilities);
        }

        [Fact]
        public void GoogolGtsController_Capabilities_ShouldMatchDeclaredBitmask()
        {
            using var gts = MotionControllerFactory.Create(DriverKind.GoogolGts);
            Assert.Equal(
                MotionCapability.BufferedSegments |
                MotionCapability.HardLimitInput |
                MotionCapability.ContinuousVelocityBlending |
                MotionCapability.HardwareCompareOutput |
                MotionCapability.BacklashCompensation,
                gts.Capabilities);

            Assert.True(gts.Supports(MotionCapability.HardwareCompareOutput));
            Assert.True(gts.Supports(MotionCapability.BacklashCompensation));
            Assert.False(gts.Supports(MotionCapability.HostInterpolation));
            Assert.False(gts.Supports(MotionCapability.PvTStreaming));
            Assert.False(gts.Supports(MotionCapability.TimeBasedAcceleration));
        }

        [Fact]
        public void LeadShineDmcController_Capabilities_ShouldMatchDeclaredBitmask()
        {
            using var dmc = MotionControllerFactory.Create(DriverKind.LeadShineDmc);
            Assert.Equal(
                MotionCapability.BufferedSegments |
                MotionCapability.HardLimitInput |
                MotionCapability.TimeBasedAcceleration |
                MotionCapability.HardwareCompareOutput,
                dmc.Capabilities);

            Assert.True(dmc.Supports(MotionCapability.TimeBasedAcceleration));
            Assert.True(dmc.Supports(MotionCapability.HardwareCompareOutput));
            Assert.False(dmc.Supports(MotionCapability.ContinuousVelocityBlending));
            Assert.False(dmc.Supports(MotionCapability.BacklashCompensation));
            Assert.False(dmc.Supports(MotionCapability.HostInterpolation));
        }

        // ---------- 3. 能力中文描述 ----------

        [Fact]
        public void DescribeCapabilities_ShouldReturnReadableChineseText()
        {
            var caps = MotionCapability.BufferedSegments |
                       MotionCapability.HardLimitInput |
                       MotionCapability.ContinuousVelocityBlending |
                       MotionCapability.TimeBasedAcceleration |
                       MotionCapability.HardwareCompareOutput |
                       MotionCapability.BacklashCompensation |
                       MotionCapability.HostInterpolation |
                       MotionCapability.PvTStreaming |
                       MotionCapability.HardwareEStopInput;

            string text = caps.DescribeCapabilities();

            Assert.Equal(
                "卡内整段缓冲插补; 上位机周期插补; 卡内硬件限位急停; PVT流式下发; 硬件位置比较输出(飞拍); " +
                "机械反向间隙补偿; 连续轨迹速度平滑; 加减速基于时间(秒); 专用硬件急停输入",
                text);
        }

        [Fact]
        public void DescribeCapabilities_None_ShouldReturnNoneText()
        {
            Assert.Equal("无", MotionCapability.None.DescribeCapabilities());
        }

        [Fact]
        public void DescribeCapabilities_SimController_ShouldListItsOwnFlags()
        {
            using var sim = new SimMotionController();
            string text = sim.DescribeCapabilities();

            Assert.Contains("卡内整段缓冲插补", text);
            Assert.Contains("上位机周期插补", text);
            Assert.Contains("卡内硬件限位急停", text);
            Assert.Contains("连续轨迹速度平滑", text);
            Assert.DoesNotContain("PVT流式下发", text);
        }

        // ---------- 4. 校验/映射只看功能标志，不看厂商品牌 ----------

        private static AxisDefinition MakeAxis(int id, bool hardLimit = false) => new()
        {
            AxisId = id,
            Name = $"A{id}",
            PulsePerUnit = 1000,
            MaxSpeed = 100,
            MaxAccel = 500,
            MaxDecel = 500,
            HardLimitEnabled = hardLimit
        };

        [Fact]
        public void ValidateAgainstController_ShouldRejectProfileDeclaringCapabilitiesControllerLacks()
        {
            using var sim = new SimMotionController(); // 无 TimeBasedAcceleration / 无 Buffered? 有 Buffered

            var profile = new MotionCardProfile
            {
                Driver = DriverKind.Simulated,
                Platform = new PlatformOptions
                {
                    SupportsBufferedSegments = true,
                    Accel = AccelParamKind.AccelerationTime
                },
                Axes = new List<AxisDefinition> { MakeAxis(0, hardLimit: true) }
            };

            var errors = MotionProfileValidator.ValidateAgainstController(profile, sim);

            // 档案声明时间语义加减速，Sim 不具备 TimeBasedAcceleration -> 必须报错
            Assert.Contains(errors, e => e.Contains("TimeBasedAcceleration"));
            // Sim 具备 BufferedSegments 与 HardLimitInput -> 不得误报
            Assert.DoesNotContain(errors, e => e.Contains("SupportsBufferedSegments"));
            Assert.DoesNotContain(errors, e => e.Contains("HardLimitEnabled"));
        }

        [Fact]
        public void ValidateAgainstController_ShouldPass_WhenControllerHasMatchingCapabilities()
        {
            using var dmc = MotionControllerFactory.Create(DriverKind.LeadShineDmc); // 有 TimeBasedAcceleration

            var profile = new MotionCardProfile
            {
                Driver = DriverKind.LeadShineDmc,
                // 非仿真驱动必须写目录内的型号（校验器不再按 Driver 猜型号）
                CardModel = "DMC5810",
                Platform = new PlatformOptions
                {
                    SupportsBufferedSegments = true,
                    Accel = AccelParamKind.AccelerationTime
                },
                Axes = new List<AxisDefinition> { MakeAxis(0, hardLimit: true) }
            };

            var errors = MotionProfileValidator.ValidateAgainstController(profile, dmc);
            Assert.Empty(errors);
        }

        [Fact]
        public void ValidateAgainstController_BufferedSegmentsMismatch_ShouldError()
        {
            // 构造一个不具备 BufferedSegments 能力的假控制器（纯能力位驱动，与厂商无关）
            var bare = new BareController(MotionCapability.HardLimitInput);

            var profile = new MotionCardProfile
            {
                Platform = new PlatformOptions { SupportsBufferedSegments = true },
                Axes = new List<AxisDefinition> { MakeAxis(0) }
            };

            var errors = MotionProfileValidator.ValidateAgainstController(profile, bare);
            Assert.Contains(errors, e => e.Contains("BufferedSegments"));
        }

        [Fact]
        public void ValidateAgainstController_HardLimitAxisWithoutCapability_ShouldError()
        {
            var bare = new BareController(MotionCapability.None);

            var profile = new MotionCardProfile
            {
                Platform = new PlatformOptions(),
                Axes = new List<AxisDefinition> { MakeAxis(0, hardLimit: true) }
            };

            var errors = MotionProfileValidator.ValidateAgainstController(profile, bare);
            Assert.Contains(errors, e => e.Contains("HardLimitInput"));
        }

        [Fact]
        public void ValidateAgainstController_NullController_ShouldReportError()
        {
            IMotionController? controller = null;
            var profile = new MotionCardProfile
            {
                Axes = new List<AxisDefinition> { MakeAxis(0) }
            };

            var errors = MotionProfileValidator.ValidateAgainstController(profile, controller!);
            Assert.Contains(errors, e => e.Contains("未提供运动控制器实例"));
        }

        [Fact]
        public void BaseValidate_ShouldKeyOffPlatformFlags_NotDriverBrand()
        {
            // 同一份 RequiresConfigFile=true 的 Platform 选项，Driver 填不同品牌，校验行为必须一致
            // （证明校验器只认功能标志，不认厂商名称）。
            var platform = new PlatformOptions { RequiresConfigFile = true };

            var pSim = new MotionCardProfile
            {
                Driver = DriverKind.Simulated,
                Platform = platform,
                ConfigFilePath = null,
                Axes = new List<AxisDefinition> { MakeAxis(0) }
            };
            var pGts = new MotionCardProfile
            {
                Driver = DriverKind.GoogolGts,
                CardModel = "GTS-400",
                Platform = platform,
                ConfigFilePath = null,
                Axes = new List<AxisDefinition> { MakeAxis(0) }
            };

            var errsSim = MotionProfileValidator.Validate(pSim);
            var errsGts = MotionProfileValidator.Validate(pGts);

            Assert.Contains(errsSim, e => e.Contains("要求配置文件，但配置文件路径(ConfigFilePath)为空"));
            Assert.Contains(errsGts, e => e.Contains("要求配置文件，但配置文件路径(ConfigFilePath)为空"));
        }

        [Fact]
        public void ToAccelParam_ByControllerCapability_ShouldFollowTimeBasedAccelerationFlag()
        {
            using var dmc = MotionControllerFactory.Create(DriverKind.LeadShineDmc); // 具备 TimeBasedAcceleration
            using var sim = new SimMotionController();                                // 不具备

            // 时间语义：500 / 1000 = 0.5s
            Assert.Equal(0.5, PlatformParamMapper.ToAccelParam(500, 1000, dmc));
            // 除零保护
            Assert.Equal(0.0, PlatformParamMapper.ToAccelParam(500, 0, dmc));
            // 非时间语义：直接返回加速度值
            Assert.Equal(1500.0, PlatformParamMapper.ToAccelParam(500, 1500, sim));
        }

        /// <summary>
        /// 最小能力位假控制器：仅用于验证「校验/映射只看能力位，与厂商无关」。
        /// </summary>
        private sealed class BareController : IMotionController
        {
            public BareController(MotionCapability capabilities) => Capabilities = capabilities;

            public DriverKind Kind => DriverKind.Simulated;
            public ConnectionState State => ConnectionState.Ready;
            public MotionCapability Capabilities { get; }
            public IReadOnlyList<AxisDefinition> Axes => new List<AxisDefinition>();
            public event Action<ConnectionState>? StateChanged;
            public event Action<AxisDoneArgs>? AxisDone;
            public event Action<AxisFaultArgs>? AxisFault;
            public event Action<LimitTriggeredArgs>? LimitTriggered;

            public Task ConnectAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
            public Task DisconnectAsync() => Task.CompletedTask;
            public void EnableAxis(int axisId) { }
            public void DisableAxis(int axisId) { }
            public bool IsAxisEnabled(int axisId) => false;
            public bool IsAxisHomed(int axisId) => false;
            public double GetPosition(int axisId) => 0;
            public double GetVelocity(int axisId) => 0;
            public Guid Jog(int axisId, int dir, double speed) => Guid.NewGuid();
            public Guid MoveAbs(int axisId, double target, double speed, double accel, double decel, double jerk = 0) => Guid.NewGuid();
            public Task<AxisDoneArgs> MoveAbsAsync(int axisId, double target, double speed, double accel, double decel, double jerk = 0, System.Threading.CancellationToken ct = default)
                => Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), true, "stub"));
            public Guid MoveRel(int axisId, double delta, double speed, double accel, double decel, double jerk = 0) => Guid.NewGuid();
            public Guid Home(int axisId, HomingMode mode, HomeDirection dir, double speed) => Guid.NewGuid();
            public Task<AxisDoneArgs> HomeAsync(int axisId, HomingMode mode, HomeDirection dir, double speed, System.Threading.CancellationToken ct = default)
                => Task.FromResult(new AxisDoneArgs(Guid.NewGuid(), true, "stub"));
            public void Halt(int axisId) { }
            public void Stop(int axisId) { }
            public void StopMotion(int axisId) { }
            public void EmergencyStop(int axisId) { }
            public void Abort(int axisId, double decelRatio = 0) { }
            public void EmergencyStopAll() { }
            public void AbortAll() { }
            public void ResetAxis(int axisId) { }
            public void Dispose() { }
        }
    }
}
