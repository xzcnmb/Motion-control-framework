using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Core.Device;
using Sophon.Infrastructure;
using Xunit;

namespace Sophon.Core.Tests
{
    public class DeviceTests
    {
        #region Fake Controllers

        private class FakeIo : Sophon.Contracts.IIoController
        {
            public Dictionary<string, bool> Di { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, bool> Do { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<(string name, bool val)> DoWrites { get; } = new();

            public event Action<string, bool>? DiChanged;
            public IReadOnlyList<string> DiPointNames => new List<string>(Di.Keys);
            public IReadOnlyList<string> DoPointNames => new List<string>(Do.Keys);

            public bool ReadDi(string pointName) => Di.TryGetValue(pointName, out var v) && v;
            public void WriteDo(string pointName, bool value)
            {
                Do[pointName] = value;
                DoWrites.Add((pointName, value));
            }
            public IReadOnlyDictionary<string, bool> SnapshotDi() => new Dictionary<string, bool>(Di);
            public IReadOnlyDictionary<string, bool> SnapshotDo() => new Dictionary<string, bool>(Do);

            public void SetDi(string name, bool val)
            {
                Di[name] = val;
                DiChanged?.Invoke(name, val);
            }
        }

        private class FakeModbus : IModbusProtocol
        {
            public bool IsConnected { get; private set; } = true;
            public string IP { get; set; } = "127.0.0.1";
            public int Port { get; set; } = 502;
            public string PortName { get; set; } = "COM1";
            public int BaudRate { get; set; } = 9600;
            public Parity Parity { get; set; } = Parity.None;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public byte SlaveAddress { get; set; } = 1;
            public bool IsModbusTCP { get; set; } = false;

            public Dictionary<ushort, object> Registers { get; } = new();
            public List<(ushort addr, object val)> Writes { get; } = new();

            public void Connect() => IsConnected = true;
            public void Disconnect() => IsConnected = false;

            public Task<T> ReadAsync<T>(ModbusRegisterType type, ushort address)
            {
                if (Registers.TryGetValue(address, out var v))
                {
                    return Task.FromResult((T)Convert.ChangeType(v, typeof(T)));
                }
                return Task.FromResult(default(T)!);
            }

            public Task WriteAsync<T>(ModbusRegisterType type, ushort address, T value)
            {
                Registers[address] = value!;
                Writes.Add((address, value!));
                return Task.CompletedTask;
            }

            public Task<Dictionary<ushort, object>> ReadBatchAsync(ModbusRegisterType type, ushort startAddress, ushort length)
            {
                var dict = new Dictionary<ushort, object>();
                for (ushort i = 0; i < length; i++)
                {
                    ushort addr = (ushort)(startAddress + i);
                    if (Registers.TryGetValue(addr, out var v))
                    {
                        dict[addr] = v;
                    }
                    else
                    {
                        dict[addr] = (ushort)0;
                    }
                }
                return Task.FromResult(dict);
            }

            public Task WriteBatchAsync(ModbusRegisterType type, ushort startAddress, IEnumerable<object> values)
            {
                ushort addr = startAddress;
                foreach (var v in values)
                {
                    Registers[addr++] = v;
                }
                return Task.CompletedTask;
            }
        }

        #endregion

        #region CylinderService Tests

        [Fact]
        public async Task 双电控气缸_脉冲输出并到位确认成功()
        {
            var fakeIo = new FakeIo();
            var service = new CylinderService(fakeIo);

            var cyl = new CylinderDefinition
            {
                Name = "Grip1",
                Valve = ValveType.DoubleCoil,
                WorkDoName = "DO_OPEN",
                HomeDoName = "DO_CLOSE",
                WorkSensorDiName = "DI_OPENED",
                HomeSensorDiName = "DI_CLOSED",
                PulseWidthMs = 20,
                ConfirmTimeoutMs = 500
            };

            // 动作同时异步设置到位信号
            Task.Run(async () =>
            {
                await Task.Delay(50);
                fakeIo.SetDi("DI_OPENED", true);
            });

            var res = await service.MoveToAsync(cyl, CylinderPosition.Work);

            Assert.True(res.Success);
            Assert.Equal(CylinderPosition.Work, res.FinalPosition);
            // 确认 WorkDo 被置 true 后又被脉冲复位为 false
            Assert.Contains(fakeIo.DoWrites, w => w.name == "DO_OPEN" && w.val == true);
            Assert.Contains(fakeIo.DoWrites, w => w.name == "DO_OPEN" && w.val == false);
        }

        [Fact]
        public async Task 气缸到位超时_返回失败并报警说明()
        {
            var fakeIo = new FakeIo();
            var service = new CylinderService(fakeIo);

            var cyl = new CylinderDefinition
            {
                Name = "Cyl1",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_PUSH",
                WorkSensorDiName = "DI_PUSHED",
                ConfirmTimeoutMs = 60
            };

            // 不触发 DI_PUSHED
            var res = await service.MoveToAsync(cyl, CylinderPosition.Work);

            Assert.False(res.Success);
            Assert.Contains("超时", res.Message);
        }

        [Fact]
        public async Task 气缸互锁组_同组多气缸禁止同时Work()
        {
            var fakeIo = new FakeIo();
            var service = new CylinderService(fakeIo);

            var cyl1 = new CylinderDefinition
            {
                Name = "CylA",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_A",
                InterlockGroup = "Station1",
                PulseWidthMs = 10
            };

            var cyl2 = new CylinderDefinition
            {
                Name = "CylB",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_B",
                InterlockGroup = "Station1",
                PulseWidthMs = 10
            };

            var res1 = await service.MoveToAsync(cyl1, CylinderPosition.Work);
            Assert.True(res1.Success);

            // cyl2 尝试伸出，应被互锁拒绝
            var res2 = await service.MoveToAsync(cyl2, CylinderPosition.Work);
            Assert.False(res2.Success);
            Assert.Contains("互锁", res2.Message);
        }

        [Fact]
        public async Task 气缸前置条件_条件未满足拒绝动作()
        {
            var fakeIo = new FakeIo();
            fakeIo.SetDi("DI_AIR_PRESSURE_OK", false); // 气压不正常

            var service = new CylinderService(fakeIo);
            var cyl = new CylinderDefinition
            {
                Name = "CylAir",
                Valve = ValveType.SingleCoil,
                WorkDoName = "DO_WORK",
                EnableConditionDiNames = new List<string> { "DI_AIR_PRESSURE_OK" }
            };

            var res = await service.MoveToAsync(cyl, CylinderPosition.Work);
            Assert.False(res.Success);
            Assert.Contains("前置条件未满足", res.Message);
        }

        #endregion

        #region PeripheralDeviceService Tests

        [Fact]
        public async Task 外设点位读取_工程量缩放与字序反转正确()
        {
            var fakeModbus = new FakeModbus();
            // 写入两寄存器代表 25.5f 的浮点数
            float expectedVal = 25.5f;
            byte[] bytes = BitConverter.GetBytes(expectedVal);
            ushort w0 = BitConverter.ToUInt16(bytes, 0);
            ushort w1 = BitConverter.ToUInt16(bytes, 2);

            // 开启 SwapWords 时，存储相反字序
            fakeModbus.Registers[10] = w1;
            fakeModbus.Registers[11] = w0;

            var service = new PeripheralDeviceService(_ => fakeModbus);

            var dev = new PeripheralDeviceConfig
            {
                Name = "TempCtrl",
                PollPeriodMs = 20,
                Tags = new List<DeviceTag>
                {
                    new DeviceTag
                    {
                        Name = "PV",
                        Area = ModbusArea.HoldingRegister,
                        Address = 10,
                        DataType = RegisterDataType.Float32,
                        SwapWords = true,
                        Scale = 1.0,
                        Offset = 0.0
                    }
                }
            };

            service.StartPolling(dev);
            await Task.Delay(60);
            service.StopPolling(dev.Name);

            bool ok = service.TryGetTagValue("TempCtrl", "PV", out double val);
            Assert.True(ok);
            Assert.InRange(val, 25.49, 25.51);
        }

        [Fact]
        public async Task 外设点位写入_超出上下限范围被拒绝()
        {
            var fakeModbus = new FakeModbus();
            var service = new PeripheralDeviceService(_ => fakeModbus);

            var dev = new PeripheralDeviceConfig
            {
                Name = "TorqueGun",
                Tags = new List<DeviceTag>
                {
                    new DeviceTag
                    {
                        Name = "TargetTorque",
                        Area = ModbusArea.HoldingRegister,
                        Address = 0,
                        DataType = RegisterDataType.UInt16,
                        Writable = true,
                        Scale = 0.1, // 0.1 N*m
                        WriteMin = 5.0,
                        WriteMax = 50.0
                    }
                }
            };

            // 写入低于下限 3.0
            var resLow = await service.WriteTagAsync(dev, "TargetTorque", 3.0);
            Assert.False(resLow.Success);
            Assert.Contains("低于安全下限", resLow.Message);

            // 写入高于上限 60.0
            var resHigh = await service.WriteTagAsync(dev, "TargetTorque", 60.0);
            Assert.False(resHigh.Success);
            Assert.Contains("超过安全上限", resHigh.Message);

            // 合法写入 20.0 (raw = 20.0 / 0.1 = 200)
            var resOk = await service.WriteTagAsync(dev, "TargetTorque", 20.0);
            Assert.True(resOk.Success);
            Assert.Equal((ushort)200, (ushort)fakeModbus.Registers[0]);
        }

        #endregion
    }
}
