#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Common;
using Newtonsoft.Json;
using Prism.Events;
using Sophon.Application;
using Sophon.Contracts;
using Sophon.Core;
using Sophon.Infrastructure.Config;
using Sophon.Infrastructure.Motion.Drivers;
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
            Assert.Equal(DriverKind.Simulated, seeds[0].Driver);
            Assert.True(string.IsNullOrWhiteSpace(seeds[0].CardModel));

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
            Assert.Equal(DriverKind.Simulated, loaded[0].Driver);
            Assert.True(string.IsNullOrWhiteSpace(loaded[0].CardModel));
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

        [Fact]
        public void MotionCardProfileStore_NormalizesLegacyProfiles_WithoutCardModel()
        {
            // 旧档案只写了 Driver + CardModel（没有 Vendor / CommandInterface / Series / Platform）
            string filePath = Path.Combine(_tempDirectory, "legacy_motion_card_profiles.json");
            string legacyJson = @"[{
                ""profileName"": ""旧雷赛档案"",
                ""driver"": ""LeadShineDmc"",
                ""cardModel"": ""DMC5800"",
                ""cardNo"": 0,
                ""axes"": []
            }]";
            File.WriteAllText(filePath, legacyJson);

            var store = new MotionCardProfileStore(filePath);
            var loaded = store.Load();
            var legacy = Assert.Single(loaded);
            Assert.Equal(DriverKind.LeadShineDmc, legacy.Driver);
            Assert.Equal(MotionVendor.LeadShine, legacy.Vendor);
            Assert.Equal(MotionCommandInterface.Pulse, legacy.CommandInterface);
            Assert.Equal("DMC5000", legacy.Series);
            Assert.Equal(AccelParamKind.AccelerationTime, legacy.Platform.Accel);
            Assert.Equal(1, legacy.Platform.AxisIndexBase);

            // 拒绝未接入的总线型号，且不会把它当成雷赛脉冲卡打开
            var bus = MotionCardCatalog.Find("DMC-E5032");
            Assert.NotNull(bus);
            var pBus = new MotionCardProfile();
            pBus.ApplyModel(bus!);
            Assert.Contains(
                MotionProfileValidator.Validate(pBus),
                e => e.Contains("尚未接入"));
        }

        [Fact]
        public void MotionCardProfileStore_SeedDefaults_IsSimulated_And_RunsOffline()
        {
            var seeds = MotionCardProfileStore.SeedDefaults();
            var sim = Assert.Single(seeds);

            // 默认种子按仓库约定可离线运行：不绑真实型号、不需要 cfg / 连接字符串
            Assert.Equal("默认配置", sim.ProfileName);
            Assert.Equal(DriverKind.Simulated, sim.Driver);
            Assert.Equal(MotionVendor.Simulated, sim.Vendor);
            Assert.True(string.IsNullOrWhiteSpace(sim.CardModel));
            Assert.False(sim.Platform.RequiresConfigFile);
            Assert.False(sim.Platform.UsesConnectionString);
            Assert.Empty(MotionProfileValidator.Validate(sim));

            // 四轴默认参数的测试价值保持：X/Y/Z/R 一根都不能少
            Assert.Equal(new[] { "X", "Y", "Z", "R" }, sim.Axes.Select(a => a.Name).ToArray());
            Assert.All(sim.Axes, a => Assert.True(a.PulsePerUnit > 0 && a.MaxSpeed > 0));

            // 默认种子能真的造出控制器：离线 Sim 链路不加载任何厂商 DLL
            using var controller = MotionControllerFactory.Create(sim.Driver, sim.Axes, allowSimFallback: false);
            Assert.Equal(DriverKind.Simulated, controller.Kind);
            Assert.Equal(4, controller.Axes.Count);
        }

        [Fact]
        public void MotionCardCatalog_SimulatedVendor_IsSelectable_WithoutRealCardModel()
        {
            // 厂商第一级可以直接选到「仿真控制器（无板卡）」
            Assert.Contains(MotionVendor.Simulated, MotionCardCatalog.Vendors);
            Assert.Contains("仿真控制器", MotionCardCatalog.Display(MotionVendor.Simulated));

            // 仿真没有真实型号 / SDK：后三级级联为空，也不会冒充成任何真实脉冲卡
            Assert.Empty(MotionCardCatalog.InterfacesFor(MotionVendor.Simulated));
            Assert.Empty(MotionCardCatalog.SeriesFor(MotionVendor.Simulated, MotionCommandInterface.Pulse));
            Assert.Empty(MotionCardCatalog.ModelsFor(MotionVendor.Simulated, MotionCommandInterface.Pulse, string.Empty));
            Assert.DoesNotContain(MotionCardCatalog.Models, m => m.Driver == DriverKind.Simulated);

            // 纯仿真档案不写型号也能通过校验
            var sim = new MotionCardProfile { Driver = DriverKind.Simulated };
            Assert.Null(MotionCardCatalog.Resolve(sim));
            Assert.Empty(MotionProfileValidator.Validate(sim));
        }

        [Fact]
        public void MotionCardProfileStore_DoesNotRewriteUnknownModelToFirstModelOfDriver()
        {
            // CardModel 非空但不在目录：禁止按 Driver 静默套用该驱动第一个型号
            var legacy = new MotionCardProfile
            {
                ProfileName = "未知型号档案",
                Driver = DriverKind.GoogolGts,
                Vendor = MotionVendor.Googol,
                CommandInterface = MotionCommandInterface.Pulse,
                Series = "GTS",
                CardModel = "GTS-9999",
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };

            MotionCardProfileStore.Normalize(legacy);

            Assert.Equal("GTS-9999", legacy.CardModel);
            Assert.Equal(DriverKind.GoogolGts, legacy.Driver);
            Assert.Equal("GTS", legacy.Series);
            Assert.Null(MotionCardCatalog.Resolve(legacy));

            // 由校验提示用户重新选择，而不是悄悄换成目录第一个型号
            var errs = MotionProfileValidator.Validate(legacy);
            Assert.Contains(errs, e => e.Contains("GTS-9999") && e.Contains("不在选型目录中"));
        }

        [Fact]
        public void MotionCardProfileStore_NormalizesLegacyProfile_WithEmptyModel_ByDriver()
        {
            // CardModel 为空的旧档案仍允许按 Driver 反推目录型号
            var legacy = new MotionCardProfile
            {
                ProfileName = "旧空型号档案",
                Driver = DriverKind.LeadShineDmc
            };

            MotionCardProfileStore.Normalize(legacy);

            Assert.False(string.IsNullOrWhiteSpace(legacy.CardModel));
            Assert.NotNull(MotionCardCatalog.Find(legacy.CardModel));
            Assert.Equal(MotionVendor.LeadShine, legacy.Vendor);
            Assert.Equal(MotionCommandInterface.Pulse, legacy.CommandInterface);
            Assert.Equal(AccelParamKind.AccelerationTime, legacy.Platform.Accel);
            Assert.Equal(1, legacy.Platform.AxisIndexBase);
        }

        [Fact]
        public void MotionCardCatalog_BusModels_DoNotClaimBufferedSegments()
        {
            // 总线（EtherCAT / gLink-II）目录项不得误报"卡内整段缓冲插补"
            var busModels = MotionCardCatalog.Models
                .Where(m => m.CommandInterface is MotionCommandInterface.EtherCAT or MotionCommandInterface.GLink)
                .ToList();
            Assert.NotEmpty(busModels);
            Assert.All(busModels, m => Assert.False(m.SupportsBufferedSegments));

            Assert.False(MotionCardCatalog.Find("DMC-E5032")!.SupportsBufferedSegments);
            Assert.False(MotionCardCatalog.Find("GEN-1000-16")!.SupportsBufferedSegments);
            Assert.False(MotionCardCatalog.Find("GE-004")!.SupportsBufferedSegments);

            // 当前脉冲适配器保持 true
            Assert.True(MotionCardCatalog.Find("GTS-400")!.SupportsBufferedSegments);
            Assert.True(MotionCardCatalog.Find("DMC5810")!.SupportsBufferedSegments);

            // 平台选项跟着型号走，总线型号不再把 SupportsBufferedSegments 带成 true
            Assert.False(MotionCardProfile.DefaultPlatformFor(DriverKind.LeadShineEtherCAT, "DMC-E5032").SupportsBufferedSegments);
            Assert.True(MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGts, "GTS-400").SupportsBufferedSegments);
        }

        [Fact]
        public void MotionProfileValidator_RequiresCatalogModel_ForNonSimulatedDrivers()
        {
            var axis = new AxisDefinition { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 };

            // 非仿真驱动 + 空型号：必须按目录选型号，不允许按 Driver 猜一个
            var pGts = new MotionCardProfile
            {
                Driver = DriverKind.GoogolGts,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.GoogolGts),
                ConfigFilePath = @"C:\NonExistentDirectory\gts.cfg",
                Axes = new List<AxisDefinition> { axis }
            };
            Assert.Contains(MotionProfileValidator.Validate(pGts), e => e.Contains("未选择控制卡型号"));

            // 总线驱动 + 空型号同样拒绝，且不会静默回退到脉冲卡 / Sim
            var pBus = new MotionCardProfile
            {
                Driver = DriverKind.LeadShineEtherCAT,
                Platform = MotionCardProfile.DefaultPlatformFor(DriverKind.LeadShineEtherCAT),
                ConnectionString = "192.168.0.10",
                Axes = new List<AxisDefinition> { axis }
            };
            var errsBus = MotionProfileValidator.Validate(pBus);
            Assert.Contains(errsBus, e => e.Contains("未选择控制卡型号"));
            Assert.Contains(errsBus, e => e.Contains("尚未接入"));

            // 纯仿真 + 空型号放行（离线 Sim 链路）
            var pSim = new MotionCardProfile { Driver = DriverKind.Simulated, Axes = new List<AxisDefinition> { axis } };
            Assert.Empty(MotionProfileValidator.Validate(pSim));
        }

        [Fact]
        public void AlarmRepository_Constructs_With_Empty_RegisteredAlarms_When_LoadConfig_Returns_Null()
        {
            // 复现报警注册页打不开的链路：文件不存在 / 空白 / JSON 内容为 null 时 LoadConfig 返回 null
            var configManager = new FakeConfigManager();
            configManager.Enqueue(null);
            var repository = CreateAlarmRepository(configManager);

            Assert.NotNull(repository.RegisteredAlarms);
            Assert.Empty(repository.RegisteredAlarms);
            Assert.NotNull(repository.ActualAlarmList);
            Assert.Empty(repository.ActualAlarmList);
            Assert.Equal(1, configManager.LoadCallCount);
            Assert.Equal(0, configManager.SaveCallCount); // 只给空列表，不自动保存种子数据
        }

        [Fact]
        public void AlarmRepository_Repeated_Restore_With_Null_Config_Does_Not_Throw()
        {
            var configManager = new FakeConfigManager();
            var repository = CreateAlarmRepository(configManager);

            Assert.Null(Record.Exception(() => repository.Restore()));
            Assert.NotNull(repository.RegisteredAlarms);
            Assert.Empty(repository.RegisteredAlarms);

            Assert.Null(Record.Exception(() => repository.Restore()));
            Assert.NotNull(repository.RegisteredAlarms);
            Assert.Empty(repository.RegisteredAlarms);
            Assert.Equal(3, configManager.LoadCallCount); // 构造 1 次 + 手动 2 次
        }

        [Fact]
        public void AlarmRepository_Restore_Loads_Existing_Registered_Alarms_Completely()
        {
            var configManager = new FakeConfigManager();
            configManager.Enqueue(new List<AlarmItem>
            {
                new AlarmItem { AlarmCode = "A001", Content = "X轴正向硬限位", Time = new DateTime(2026, 9, 23, 8, 30, 0) },
                new AlarmItem { AlarmCode = "B002", Content = "气压低报警" }
            });
            var repository = CreateAlarmRepository(configManager);

            Assert.Equal(2, repository.RegisteredAlarms.Count);
            Assert.Equal("A001", repository.RegisteredAlarms[0].AlarmCode);
            Assert.Equal("X轴正向硬限位", repository.RegisteredAlarms[0].Content);
            Assert.Equal(new DateTime(2026, 9, 23, 8, 30, 0), repository.RegisteredAlarms[0].Time);
            Assert.Equal("B002", repository.RegisteredAlarms[1].AlarmCode);
            Assert.Equal("气压低报警", repository.RegisteredAlarms[1].Content);
        }

        [Fact]
        public void AlarmRepository_Restore_Does_Not_Swallow_Deserialize_Exception_And_Keeps_Previous_Collection()
        {
            var configManager = new FakeConfigManager();
            configManager.Enqueue(new List<AlarmItem> { new AlarmItem { AlarmCode = "A001", Content = "X轴正向硬限位" } });
            configManager.Enqueue(new JsonException("配置文件损坏"));
            var repository = CreateAlarmRepository(configManager);

            var loaded = repository.RegisteredAlarms;
            Assert.Single(loaded);

            var exception = Assert.Throws<JsonException>(() => repository.Restore());
            Assert.Contains("配置文件损坏", exception.Message);
            Assert.Same(loaded, repository.RegisteredAlarms); // 已有有效集合不被替换
            Assert.Single(repository.RegisteredAlarms);
            Assert.Equal("A001", repository.RegisteredAlarms[0].AlarmCode);
        }

        [Fact]
        public void ConfigManager_LoadConfig_Returns_Null_For_Missing_Blank_And_JsonNull()
        {
            var serializer = new JsonConfigSerializer();

            // 文件不存在
            string missingPath = Path.Combine(_tempDirectory, "missing", "alarm_config.json");
            Assert.False(File.Exists(missingPath));
            Assert.Null(new ConfigManager(serializer, missingPath).LoadConfig<List<AlarmItem>>());

            // 空白文件
            string blankPath = Path.Combine(_tempDirectory, "blank", "alarm_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(blankPath)!);
            File.WriteAllText(blankPath, "   \r\n\t");
            Assert.Null(new ConfigManager(serializer, blankPath).LoadConfig<List<AlarmItem>>());

            // JSON 内容为 null
            string jsonNullPath = Path.Combine(_tempDirectory, "jsonnull", "alarm_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonNullPath)!);
            File.WriteAllText(jsonNullPath, "null");
            Assert.Null(new ConfigManager(serializer, jsonNullPath).LoadConfig<List<AlarmItem>>());
        }

        [Fact]
        public void AlarmRepository_Restore_Through_RealConfigManager_Missing_Blank_NullJson_And_WithData()
        {
            var serializer = new JsonConfigSerializer();

            // 文件不存在
            string missingPath = Path.Combine(_tempDirectory, "missing", "alarm_config.json");
            Assert.Empty(CreateAlarmRepository(new ConfigManager(serializer, missingPath)).RegisteredAlarms);

            // 空白文件
            string blankPath = Path.Combine(_tempDirectory, "blank", "alarm_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(blankPath)!);
            File.WriteAllText(blankPath, "   \r\n\t");
            Assert.Empty(CreateAlarmRepository(new ConfigManager(serializer, blankPath)).RegisteredAlarms);

            // JSON 内容为 null
            string jsonNullPath = Path.Combine(_tempDirectory, "jsonnull", "alarm_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(jsonNullPath)!);
            File.WriteAllText(jsonNullPath, "null");
            Assert.Empty(CreateAlarmRepository(new ConfigManager(serializer, jsonNullPath)).RegisteredAlarms);

            // 有数据时按原样完整加载
            string dataPath = Path.Combine(_tempDirectory, "withdata", "alarm_config.json");
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            File.WriteAllText(dataPath, serializer.Serialize(new List<AlarmItem>
            {
                new AlarmItem { AlarmCode = "A001", Content = "X轴正向硬限位" },
                new AlarmItem { AlarmCode = "B002", Content = "气压低报警" }
            }));

            var repository = CreateAlarmRepository(new ConfigManager(serializer, dataPath));
            Assert.Equal(2, repository.RegisteredAlarms.Count);
            Assert.Equal("A001", repository.RegisteredAlarms[0].AlarmCode);
            Assert.Equal("X轴正向硬限位", repository.RegisteredAlarms[0].Content);
            Assert.Equal("B002", repository.RegisteredAlarms[1].AlarmCode);
            Assert.Equal("气压低报警", repository.RegisteredAlarms[1].Content);
        }

        [Fact]
        public void MotionCardCatalog_DisplayNames_Use_Vendor_Model_Axes_And_Category()
        {
            // 目录每项显示名都要能单独读出来历：中文厂商 + 型号 + 轴数 + 中文卡型/控制器类别 + 主机接口
            Assert.NotEmpty(MotionCardCatalog.Models);
            foreach (var model in MotionCardCatalog.Models)
            {
                string category = model.CommandInterface switch
                {
                    MotionCommandInterface.Pulse => "脉冲",
                    MotionCommandInterface.Analog => "模拟量",
                    MotionCommandInterface.EtherCAT => "总线",
                    MotionCommandInterface.GLink => "总线",
                    _ => model.CommandInterface.ToString(),
                };

                Assert.Contains(MotionCardCatalog.Display(model.Vendor), model.DisplayName, StringComparison.Ordinal);
                Assert.Contains(model.Model, model.DisplayName, StringComparison.Ordinal);
                Assert.Contains($"{model.MaxAxes} 轴", model.DisplayName, StringComparison.Ordinal);
                Assert.Contains(category, model.DisplayName, StringComparison.Ordinal);
                Assert.Contains(MotionCardCatalog.Display(model.HostLink), model.DisplayName, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void MotionCardCatalog_DisplayNameRename_Keeps_ModelKeys_And_Drivers()
        {
            // 只改显示名：型号匹配键、驱动、厂商、系列、平台配置都不动
            var gts = MotionCardCatalog.Find("GTS-400");
            var dmc = MotionCardCatalog.Find("DMC5810");
            var dmc5800 = MotionCardCatalog.Find("DMC5800");
            var bus = MotionCardCatalog.Find("DMC-E5032");
            var emc = MotionCardCatalog.Find("EMC-E0808");
            var eci = MotionCardCatalog.Find("ECI2418");
            Assert.NotNull(gts);
            Assert.NotNull(dmc);
            Assert.NotNull(dmc5800);
            Assert.NotNull(bus);
            Assert.NotNull(emc);
            Assert.NotNull(eci);

            Assert.Equal(DriverKind.GoogolGts, gts!.Driver);
            Assert.Equal(DriverKind.LeadShineDmc, dmc!.Driver);
            Assert.Equal(DriverKind.LeadShineDmc, dmc5800!.Driver);
            Assert.Equal(DriverKind.LeadShineEtherCAT, bus!.Driver);
            Assert.Equal(DriverKind.LeadShineEtherCAT, emc!.Driver);
            Assert.Equal(DriverKind.ZmotionZmc, eci!.Driver);

            // EMC / PAC 属于雷赛，不因改名变成正运动；旧型号名保留
            Assert.Equal(MotionVendor.LeadShine, emc!.Vendor);
            Assert.Contains("旧型号名", dmc5800!.DisplayName, StringComparison.Ordinal);

            // 代表脉冲卡：Find + ApplyModel 仍按原键 / 原驱动 / 原平台写入档案
            var pulse = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            pulse.ApplyModel(gts!);
            Assert.Equal("GTS-400", pulse.CardModel);
            Assert.Equal(DriverKind.GoogolGts, pulse.Driver);
            Assert.Equal(MotionVendor.Googol, pulse.Vendor);
            Assert.Equal(MotionCommandInterface.Pulse, pulse.CommandInterface);
            Assert.Equal("GTS", pulse.Series);
            Assert.Equal("gts.dll", gts!.NativeLibrary);
            Assert.True(pulse.Platform.RequiresConfigFile);
            Assert.Equal(1, pulse.Platform.AxisIndexBase);

            // 代表总线卡：仍按原键解析，且不与脉冲卡共用 LTDMC.dll
            var busProfile = new MotionCardProfile
            {
                Axes = new List<AxisDefinition>
                {
                    new() { AxisId = 0, Name = "X", PulsePerUnit = 1000, MaxSpeed = 100, MaxAccel = 500, MaxDecel = 500 }
                }
            };
            busProfile.ApplyModel(bus!);
            Assert.Equal("DMC-E5032", busProfile.CardModel);
            Assert.Equal(DriverKind.LeadShineEtherCAT, busProfile.Driver);
            Assert.Equal(MotionVendor.LeadShine, busProfile.Vendor);
            Assert.Equal(MotionCommandInterface.EtherCAT, busProfile.CommandInterface);
            Assert.Equal("DMC-E5000", busProfile.Series);
            Assert.Equal(AccelParamKind.AccelerationValue, busProfile.Platform.Accel);
            Assert.DoesNotContain("LTDMC.dll", bus!.NativeLibrary, StringComparison.OrdinalIgnoreCase);
        }

        private static AlarmRepository CreateAlarmRepository(IConfigManager configManager)
        {
            return new AlarmRepository(
                new FixedConfigManagerFactory(configManager),
                new EventAggregator(),
                new TestLoggerFactory());
        }

        private sealed class FakeConfigManager : IConfigManager
        {
            private readonly Queue<object?> _loadResults = new Queue<object?>();

            public int LoadCallCount { get; private set; }

            public int SaveCallCount { get; private set; }

            public void Enqueue(object? result)
            {
                _loadResults.Enqueue(result);
            }

            public T LoadConfig<T>()
            {
                LoadCallCount++;
                if (_loadResults.Count == 0)
                {
                    return default;
                }

                object? next = _loadResults.Dequeue();
                if (next is Exception exception)
                {
                    throw exception;
                }

                return next is null ? default : (T)next;
            }

            public void SaveConfig<T>(T config)
            {
                SaveCallCount++;
            }
        }

        private sealed class FixedConfigManagerFactory : IConfigManagerFactory
        {
            private readonly IConfigManager _configManager;

            public FixedConfigManagerFactory(IConfigManager configManager)
            {
                _configManager = configManager;
            }

            public IConfigManager CreateConfigManager(ConfigType type, string filename, string secondPath = "")
            {
                return _configManager;
            }
        }

        private sealed class TestLoggerFactory : ILoggerFactory
        {
            public ILoggerManager CreateLogger(string loggername)
            {
                return new TestLoggerManager();
            }
        }

        private sealed class TestLoggerManager : ILoggerManager
        {
            public void Trace(string msg)
            {
            }

            public void Debug(string msg)
            {
            }

            public void Info(string msg)
            {
            }

            public void Warn(string msg)
            {
            }

            public void Error(string msg)
            {
            }

            public void Fatal(string msg)
            {
            }
        }
    }
}
