#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sophon.Contracts;
using Sophon.Infrastructure.Config;
using Xunit;

namespace Sophon.Infrastructure.Tests
{
    public class ConfigStoresAndValidationTests : IDisposable
    {
        private readonly string _tempDirectory;

        public ConfigStoresAndValidationTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "SophonConfigTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void MotionCardProfileStore_RoundTrip_And_ActiveProfile_WorkCorrectly()
        {
            string filePath = Path.Combine(_tempDirectory, "motion_card_profiles.json");
            var store = new MotionCardProfileStore(filePath);

            // Initially empty
            Assert.Empty(store.Load());
            Assert.Null(store.GetActive());

            // Save seeds
            var seeds = MotionCardProfileStore.SeedDefaults();
            Assert.Single(seeds);
            Assert.Equal(4, seeds[0].Axes.Count);

            seeds.Add(new MotionCardProfile
            {
                ProfileName = "Profile2",
                Driver = DriverKind.LeadShineDmc,
                CardModel = "DMC5800",
                CardNo = 1,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.LeadShineDmc),
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "LeadAxis1", PulsePerUnit = 2000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            });

            store.Save(seeds);

            // Load back
            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("默认配置", loaded[0].ProfileName);
            Assert.Equal(DriverKind.GoogolGts, loaded[0].Driver);
            Assert.Equal(4, loaded[0].Axes.Count);
            Assert.Equal("Profile2", loaded[1].ProfileName);
            Assert.Equal(DriverKind.LeadShineDmc, loaded[1].Driver);
            Assert.Equal(AccelParamKind.AccelerationTime, loaded[1].Platform.Accel);

            // Active profile
            store.SetActive("Profile2");
            var active = store.GetActive();
            Assert.NotNull(active);
            Assert.Equal("Profile2", active!.ProfileName);

            // Re-instantiate store with same file path to verify persistence
            var store2 = new MotionCardProfileStore(filePath);
            var active2 = store2.GetActive();
            Assert.NotNull(active2);
            Assert.Equal("Profile2", active2!.ProfileName);
        }

        [Fact]
        public void CameraConfigStore_RoundTrip_WorksCorrectly()
        {
            string filePath = Path.Combine(_tempDirectory, "camera_configs.json");
            var store = new CameraConfigStore(filePath);

            Assert.Empty(store.Load());

            var seeds = CameraConfigStore.SeedDefaults();
            Assert.Single(seeds);
            Assert.Equal("相机1", seeds[0].Name);
            Assert.Equal(CameraVendor.HikvisionMvs, seeds[0].Vendor);

            seeds.Add(new CameraConfig
            {
                Name = "HikCam",
                Vendor = CameraVendor.HikvisionMvs,
                DeviceKey = "SN123456",
                ExposureUs = 15000,
                Gain = 6.0,
                GrabTimeoutMs = 2000,
                OptimizePacketSize = true
            });

            store.Save(seeds);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("相机1", loaded[0].Name);
            Assert.Equal(CameraVendor.HikvisionMvs, loaded[0].Vendor);
            Assert.Equal("HikCam", loaded[1].Name);
            Assert.Equal(CameraVendor.HikvisionMvs, loaded[1].Vendor);
            Assert.Equal("SN123456", loaded[1].DeviceKey);
            Assert.Equal(15000, loaded[1].ExposureUs);
            Assert.Equal(6.0, loaded[1].Gain);
        }

        [Fact]
        public void CylinderConfigStore_RoundTrip_WorksCorrectly()
        {
            string filePath = Path.Combine(_tempDirectory, "cylinder_configs.json");
            var store = new CylinderConfigStore(filePath);

            Assert.Empty(store.Load());

            var seeds = CylinderConfigStore.SeedDefaults();
            Assert.Single(seeds);
            Assert.Equal("夹爪气缸", seeds[0].Name);
            Assert.Equal(ValveType.DoubleCoil, seeds[0].Valve);
            Assert.Equal("DO_Gripper_Work", seeds[0].WorkDoName);
            Assert.Equal("DO_Gripper_Home", seeds[0].HomeDoName);

            seeds.Add(new CylinderDefinition
            {
                Name = "推料气缸",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_Push_Work",
                HomeDoName = null,
                WorkSensorDiName = "DI_Push_Work",
                HomeSensorDiName = "DI_Push_Home",
                PulseWidthMs = 0,
                ConfirmTimeoutMs = 1000,
                InterlockGroup = "GroupA",
                ResetToHomeOnStart = true,
                EnableConditionDiNames = new List<string> { "DI_Safety_Door" }
            });

            store.Save(seeds);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("夹爪气缸", loaded[0].Name);
            Assert.Equal(ValveType.DoubleCoil, loaded[0].Valve);
            Assert.Equal("推料气缸", loaded[1].Name);
            Assert.Equal(ValveType.SingleCoil, loaded[1].Valve);
            Assert.Equal("GroupA", loaded[1].InterlockGroup);
            Assert.Single(loaded[1].EnableConditionDiNames);
            Assert.Equal("DI_Safety_Door", loaded[1].EnableConditionDiNames[0]);
        }

        [Fact]
        public void PeripheralDeviceConfigStore_RoundTrip_WorksCorrectly()
        {
            string filePath = Path.Combine(_tempDirectory, "peripheral_devices.json");
            var store = new PeripheralDeviceConfigStore(filePath);

            Assert.Empty(store.Load());

            var seeds = PeripheralDeviceConfigStore.SeedDefaults();
            Assert.Single(seeds);
            var dev = seeds[0];
            Assert.Equal("温控器1", dev.Name);
            Assert.Equal("温控", dev.Category);
            Assert.Equal(DeviceTransport.ModbusRtu, dev.Transport);
            Assert.Equal(2, dev.Tags.Count);

            var pv = dev.Tags.First(t => t.Name == "PV");
            Assert.Equal((ushort)0, pv.Address);
            Assert.Equal(0.1, pv.Scale);
            Assert.Equal("℃", pv.Unit);
            Assert.False(pv.Writable);

            var sv = dev.Tags.First(t => t.Name == "SV");
            Assert.Equal((ushort)1, sv.Address);
            Assert.Equal(0.1, sv.Scale);
            Assert.Equal("℃", sv.Unit);
            Assert.True(sv.Writable);

            store.Save(seeds);

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("温控器1", loaded[0].Name);
            Assert.Equal(2, loaded[0].Tags.Count);
            Assert.Equal("SV", loaded[0].Tags[1].Name);
            Assert.True(loaded[0].Tags[1].Writable);
        }

        [Fact]
        public void MotionProfileValidator_CatchesAllRequiredRules()
        {
            // 1. Duplicate axis ID
            var pDup = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X1", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 },
                    new() { AxisId = 0, Name = "X2", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            var errsDup = MotionProfileValidator.Validate(pDup);
            Assert.Contains(errsDup, e => e.Contains("轴编号重复"));

            // 2. Bad pulse per unit (<= 0)
            var pPulse = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 0, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 },
                    new() { AxisId = 1, Name = "Y", PulsePerUnit = -10, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            var errsPulse = MotionProfileValidator.Validate(pPulse);
            Assert.Contains(errsPulse, e => e.Contains("脉冲当量必须大于0") && e.Contains("轴[0:X]"));
            Assert.Contains(errsPulse, e => e.Contains("脉冲当量必须大于0") && e.Contains("轴[1:Y]"));

            // 3. Inverted soft limit (SoftLimitMin >= SoftLimitMax when SoftLimitEnabled)
            var pSoftLimit = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, SoftLimitEnabled = true, SoftLimitMin = 100, SoftLimitMax = 50, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 },
                    new() { AxisId = 1, Name = "Y", PulsePerUnit = 1000, SoftLimitEnabled = true, SoftLimitMin = 50, SoftLimitMax = 50, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            var errsSoftLimit = MotionProfileValidator.Validate(pSoftLimit);
            Assert.Contains(errsSoftLimit, e => e.Contains("软限位设置错误") && e.Contains("轴[0:X]"));
            Assert.Contains(errsSoftLimit, e => e.Contains("软限位设置错误") && e.Contains("轴[1:Y]"));

            // 4. Missing GTS config file (RequiresConfigFile=true, ConfigFilePath empty or not exist)
            var pGtsMissing = new MotionCardProfile
            {
                Driver = DriverKind.GoogolGts,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGts),
                ConfigFilePath = null,
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            var errsGts1 = MotionProfileValidator.Validate(pGtsMissing);
            Assert.Contains(errsGts1, e => e.Contains("要求配置文件，但配置文件路径(ConfigFilePath)为空"));

            pGtsMissing.ConfigFilePath = @"C:\NonExistentDirectory\gts.cfg";
            var errsGts2 = MotionProfileValidator.Validate(pGtsMissing);
            Assert.Contains(errsGts2, e => e.Contains("配置文件不存在"));

            // 5. Missing ZMC connection string (UsesConnectionString=true, ConnectionString empty)
            var pZmcMissing = new MotionCardProfile
            {
                Driver = DriverKind.ZmotionZmc,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.ZmotionZmc),
                ConnectionString = "",
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            var errsZmc = MotionProfileValidator.Validate(pZmcMissing);
            Assert.Contains(errsZmc, e => e.Contains("要求连接字符串，但连接字符串(ConnectionString)为空"));

            // 6. MaxSpeed / Accel / Decel <= 0
            var pKinematics = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 0, MaxAccel = -1, MaxDecel = 0 }
                }
            };
            var errsKin = MotionProfileValidator.Validate(pKinematics);
            Assert.Contains(errsKin, e => e.Contains("最大速度必须大于0"));
            Assert.Contains(errsKin, e => e.Contains("最大加速度必须大于0"));
            Assert.Contains(errsKin, e => e.Contains("最大减速度必须大于0"));

            // 7. Axis count out of 0..64 range
            var pTooMany = new MotionCardProfile
            {
                Axes = Enumerable.Range(0, 65).Select(i => new AxisDefinition { AxisId = i, Name = $"A{i}", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }).ToList()
            };
            var errsCount = MotionProfileValidator.Validate(pTooMany);
            Assert.Contains(errsCount, e => e.Contains("轴数量超出有效范围"));

            // 8. Derived accel-time > 10s warning when Accel == AccelerationTime
            var pAccelTime = new MotionCardProfile
            {
                Platform = new PlatformOptions
                {
                    Accel = AccelParamKind.AccelerationTime
                },
                Axes = new List<AxisDefinition>
                {
                    // MaxSpeed = 1000, MaxAccel = 50 -> derived time = 1000 / 50 = 20s > 10s
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 1000, MaxAccel = 50, MaxDecel = 50 }
                }
            };
            var errsAccel = MotionProfileValidator.Validate(pAccelTime);
            Assert.Contains(errsAccel, e => e.Contains("推导加速时间") && e.Contains("超过10秒"));
        }

        [Fact]
        public void PlatformParamMapper_AxisOffset_And_AccelTimeConversion_WorkCorrectly()
        {
            var pGts = new PlatformOptions { AxisIndexBase = 1, Accel = AccelParamKind.AccelerationValue };
            var pZmc = new PlatformOptions { AxisIndexBase = 0, Accel = AccelParamKind.AccelerationValue };
            var pLead = new PlatformOptions { AxisIndexBase = 1, Accel = AccelParamKind.AccelerationTime };

            // Axis index mapping
            Assert.Equal(1, PlatformParamMapper.ToHardwareAxisIndex(0, pGts));
            Assert.Equal(2, PlatformParamMapper.ToHardwareAxisIndex(1, pGts));
            Assert.Equal(0, PlatformParamMapper.ToHardwareAxisIndex(0, pZmc));
            Assert.Equal(1, PlatformParamMapper.ToHardwareAxisIndex(1, pZmc));
            Assert.Equal(1, PlatformParamMapper.ToHardwareAxisIndex(0, pLead));

            // Accel param conversion
            // Value mode: returns maxAccel
            Assert.Equal(1500.0, PlatformParamMapper.ToAccelParam(500, 1500, pGts));
            Assert.Equal(2000.0, PlatformParamMapper.ToAccelParam(500, 2000, pZmc));

            // Time mode: returns maxSpeed / maxAccel
            // 500 / 1000 = 0.5s
            Assert.Equal(0.5, PlatformParamMapper.ToAccelParam(500, 1000, pLead));
            // Guard against division by zero
            Assert.Equal(0.0, PlatformParamMapper.ToAccelParam(500, 0, pLead));
            Assert.Equal(0.0, PlatformParamMapper.ToAccelParam(500, -10, pLead));
        }

        [Fact]
        public void MotionCardCatalog_SeparatesPulseAndBus_And_DoesNotMixLeadShineApis()
        {
            var pulse = MotionCardCatalog.Find("DMC5810");
            var bus = MotionCardCatalog.Find("DMC-E5032");
            Assert.NotNull(pulse);
            Assert.NotNull(bus);
            Assert.Equal(MotionVendor.LeadShine, pulse!.Vendor);
            Assert.Equal(MotionVendor.LeadShine, bus!.Vendor);
            Assert.Equal(MotionCommandInterface.Pulse, pulse.CommandInterface);
            Assert.Equal(MotionCommandInterface.EtherCAT, bus.CommandInterface);
            Assert.Equal(DriverKind.LeadShineDmc, pulse.Driver);
            Assert.Equal(DriverKind.LeadShineEtherCAT, bus.Driver);
            Assert.Equal(AccelParamKind.AccelerationTime, pulse.Accel);
            Assert.Equal(AccelParamKind.AccelerationValue, bus.Accel);
            Assert.True(pulse.IsImplemented);
            Assert.False(bus.IsImplemented);
            Assert.Equal("LTDMC.dll", pulse.NativeLibrary);
            Assert.DoesNotContain("LTDMC.dll", bus.NativeLibrary, StringComparison.OrdinalIgnoreCase);

            var gts = MotionCardCatalog.Find("GTS-400");
            var gen = MotionCardCatalog.Find("GEN-1000-16");
            Assert.NotNull(gts);
            Assert.NotNull(gen);
            Assert.Equal(DriverKind.GoogolGts, gts!.Driver);
            Assert.Equal(DriverKind.GoogolGen, gen!.Driver);
            Assert.True(gts.RequiresConfigFile);
            Assert.False(gen.RequiresConfigFile);
        }

        [Fact]
        public void MotionProfileValidator_RejectsEtherCATCard_And_AxisCountOverModelLimit()
        {
            var bus = MotionCardCatalog.Find("DMC-E5032");
            Assert.NotNull(bus);
            var pBus = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            pBus.ApplyModel(bus!);
            var errsBus = MotionProfileValidator.Validate(pBus);
            Assert.Contains(errsBus, e => e.Contains("EtherCAT") && e.Contains("脉冲卡"));

            var pulse = MotionCardCatalog.Find("DMC1020");
            Assert.NotNull(pulse);
            var pTooMany = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 },
                    new() { AxisId = 1, Name = "Y", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 },
                    new() { AxisId = 2, Name = "Z", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            pTooMany.ApplyModel(pulse!);
            var errsCount = MotionProfileValidator.Validate(pTooMany);
            Assert.Contains(errsCount, e => e.Contains("最多 2 轴"));
        }

        [Fact]
        public void DefaultPlatformFor_UsesCatalogModel_NotBrandAlone()
        {
            var dmcPulse = MotionCardProfile.DefaultPlatformFor(DriverKind.LeadShineDmc, "DMC5810");
            var dmcBus = MotionCardProfile.DefaultPlatformFor(DriverKind.LeadShineEtherCAT, "DMC-E5032");
            Assert.Equal(AccelParamKind.AccelerationTime, dmcPulse.Accel);
            Assert.False(dmcPulse.UsesConnectionString);
            Assert.Equal(AccelParamKind.AccelerationValue, dmcBus.Accel);

            var gts = MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGts, "GTS-400");
            var gen = MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGen, "GEN-1000-16");
            Assert.True(gts.RequiresConfigFile);
            Assert.False(gen.RequiresConfigFile);
        }
    }
}
