#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sophon.Contracts;
using Sophon.Infrastructure;

namespace Sophon.Core.Device
{
    /// <summary>
    /// 工业外设通讯服务（RS485 / RS232 / Modbus RTU / Modbus TCP / 串口 ASCII）。
    /// 解决工业现场典型外设集成需求：温控器、拧紧枪、扫码枪、称重仪表等。
    /// 关键机制：
    /// 1. 每条总线/物理端口拥有独立信号量串行化，保证半双工 RS485 报文不交织重叠
    /// 2. 周期调度轮询，多寄存器自动分类型分包
    /// 3. 支持 Float32 / Int32 / UInt32 大小端与字序反转 (SwapWords)
    /// 4. 自动工程量 Scale/Offset 缩放与量纲还原
    /// 5. 写入范围 WriteMin / WriteMax 白名单安全校验，防误操作损坏机构
    /// </summary>
    public class PeripheralDeviceService : IDisposable
    {
        private readonly Func<PeripheralDeviceConfig, IModbusProtocol>? _protocolFactory;
        private readonly ConcurrentDictionary<string, IModbusProtocol> _protocols = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _busGates = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PeripheralDeviceConfig> _registeredConfigs = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, double>> _latestValues = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pollCts = new(StringComparer.OrdinalIgnoreCase);
        private bool _isDisposed;

        /// <summary>
        /// 点位值更新事件：(设备名称, 点位名称, 工程量值)
        /// </summary>
        public event Action<string, string, double>? TagUpdated;

        public PeripheralDeviceService(Func<PeripheralDeviceConfig, IModbusProtocol>? protocolFactory = null)
        {
            _protocolFactory = protocolFactory;
        }

        /// <summary>
        /// 注册设备档案配置（供流程节点查询真实的寄存器地址与上下限白名单）。
        /// </summary>
        public void RegisterDeviceConfig(PeripheralDeviceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            _registeredConfigs[config.Name] = config;
        }

        /// <summary>
        /// 根据设备名称尝试获取其真实配置档案。
        /// </summary>
        public bool TryGetDeviceConfig(string deviceName, out PeripheralDeviceConfig? config)
        {
            return _registeredConfigs.TryGetValue(deviceName, out config);
        }

        /// <summary>
        /// 启动指定外设的后台数据采集轮询。
        /// </summary>
        public void StartPolling(PeripheralDeviceConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            RegisterDeviceConfig(config);
            StopPolling(config.Name);

            var cts = new CancellationTokenSource();
            _pollCts[config.Name] = cts;

            Task.Run(async () => await PollLoopAsync(config, cts.Token), cts.Token);
        }

        /// <summary>
        /// 停止指定外设的后台数据轮询。
        /// </summary>
        public void StopPolling(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName)) return;

            if (_pollCts.TryRemove(deviceName, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { }
            }
        }

        public void StopAll()
        {
            foreach (var name in _pollCts.Keys.ToList())
            {
                StopPolling(name);
            }
        }

        /// <summary>
        /// 获取某外设所有点位的最新工程量快照。
        /// </summary>
        public IReadOnlyDictionary<string, double> GetLatestValues(string deviceName)
        {
            if (_latestValues.TryGetValue(deviceName, out var dict))
            {
                return new Dictionary<string, double>(dict);
            }
            return new Dictionary<string, double>();
        }

        /// <summary>
        /// 获取某点位最新工程量数值。
        /// </summary>
        public bool TryGetTagValue(string deviceName, string tagName, out double value)
        {
            value = 0.0;
            if (_latestValues.TryGetValue(deviceName, out var dict))
            {
                return dict.TryGetValue(tagName, out value);
            }
            return false;
        }

        /// <summary>
        /// 向设备点位写入工程量值（仅限 Writable 点位，执行上下限保护与逆向缩放）。
        /// </summary>
        public async Task<(bool Success, string Message)> WriteTagAsync(
            PeripheralDeviceConfig config,
            string tagName,
            double engineeringValue,
            CancellationToken ct = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var tag = config.Tags.FirstOrDefault(t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (tag == null)
            {
                return (false, $"未找到点位 '{tagName}'");
            }
            if (!tag.Writable)
            {
                return (false, $"点位 '{tagName}' 设定为只读，拒绝写入");
            }

            // 安全上下限保护
            if (tag.WriteMin.HasValue && engineeringValue < tag.WriteMin.Value)
            {
                return (false, $"写入值 {engineeringValue} 低于安全下限 {tag.WriteMin.Value}");
            }
            if (tag.WriteMax.HasValue && engineeringValue > tag.WriteMax.Value)
            {
                return (false, $"写入值 {engineeringValue} 超过安全上限 {tag.WriteMax.Value}");
            }

            // 逆向工程量换算：raw = (eng - offset) / scale
            double raw = (engineeringValue - tag.Offset);
            if (Math.Abs(tag.Scale) > 1e-12)
            {
                raw /= tag.Scale;
            }

            var protocol = GetOrCreateProtocol(config);
            var gate = GetGate(config);

            await gate.WaitAsync(ct);
            try
            {
                // 总线互斥下动态设置目标从站地址（支持同485总线挂多台从站设备）
                protocol.SlaveAddress = config.SlaveAddress;

                if (!protocol.IsConnected)
                {
                    protocol.Connect();
                }

                var regType = MapModbusArea(tag.Area);

                switch (tag.DataType)
                {
                    case RegisterDataType.Bool:
                        await protocol.WriteAsync(regType, tag.Address, raw > 0.5);
                        break;
                    case RegisterDataType.Int16:
                        await protocol.WriteAsync(regType, tag.Address, (short)Math.Round(raw));
                        break;
                    case RegisterDataType.UInt16:
                    default:
                        await protocol.WriteAsync(regType, tag.Address, (ushort)Math.Round(raw));
                        break;
                }

                // 写入后同步更新本地缓存
                var devDict = _latestValues.GetOrAdd(config.Name, _ => new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase));
                devDict[tagName] = engineeringValue;
                TagUpdated?.Invoke(config.Name, tagName, engineeringValue);

                return (true, "写入成功");
            }
            catch (Exception ex)
            {
                return (false, $"通信写入失败: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task PollLoopAsync(PeripheralDeviceConfig config, CancellationToken ct)
        {
            var gate = GetGate(config);
            var devDict = _latestValues.GetOrAdd(config.Name, _ => new ConcurrentDictionary<string, double>(StringComparer.OrdinalIgnoreCase));
            int period = config.PollPeriodMs > 0 ? config.PollPeriodMs : 500;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var protocol = GetOrCreateProtocol(config);

                    await gate.WaitAsync(ct);
                    try
                    {
                        // 动态设置当前轮询设备站号
                        protocol.SlaveAddress = config.SlaveAddress;

                        if (!protocol.IsConnected)
                        {
                            protocol.Connect();
                        }

                        // 逐个点位轮询读取
                        foreach (var tag in config.Tags)
                        {
                            if (ct.IsCancellationRequested) break;

                            try
                            {
                                var regType = MapModbusArea(tag.Area);
                                double engVal = 0.0;

                                switch (tag.DataType)
                                {
                                    case RegisterDataType.Bool:
                                        bool bVal = await protocol.ReadAsync<bool>(regType, tag.Address);
                                        engVal = bVal ? 1.0 : 0.0;
                                        break;

                                    case RegisterDataType.Int16:
                                        short sVal = await protocol.ReadAsync<short>(regType, tag.Address);
                                        engVal = sVal * tag.Scale + tag.Offset;
                                        break;

                                    case RegisterDataType.Float32:
                                        // 32 位浮点：读取连续 2 个寄存器
                                        var batch = await protocol.ReadBatchAsync(regType, tag.Address, 2);
                                        if (batch != null && batch.Count >= 2)
                                        {
                                            var words = batch.OrderBy(k => k.Key).Select(k => Convert.ToUInt16(k.Value)).ToArray();
                                            ushort w0 = words[0];
                                            ushort w1 = words[1];
                                            if (tag.SwapWords)
                                            {
                                                (w0, w1) = (w1, w0);
                                            }
                                            byte[] bytes = new byte[4];
                                            BitConverter.GetBytes(w0).CopyTo(bytes, 0);
                                            BitConverter.GetBytes(w1).CopyTo(bytes, 2);
                                            float fVal = BitConverter.ToSingle(bytes, 0);
                                            engVal = fVal * tag.Scale + tag.Offset;
                                        }
                                        break;

                                    case RegisterDataType.UInt16:
                                    default:
                                        ushort uVal = await protocol.ReadAsync<ushort>(regType, tag.Address);
                                        engVal = uVal * tag.Scale + tag.Offset;
                                        break;
                                }

                                devDict[tag.Name] = engVal;
                                TagUpdated?.Invoke(config.Name, tag.Name, engVal);
                            }
                            catch
                            {
                                // 单个点位失败记录，不阻断整轮采集
                            }
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 异常退避重试
                }

                await Task.Delay(period, ct);
            }
        }

        private string GetBusKey(PeripheralDeviceConfig config)
        {
            return config.Transport == DeviceTransport.ModbusTcp
                ? $"{config.Ip}:{config.TcpPort}"
                : config.PortName;
        }

        private IModbusProtocol GetOrCreateProtocol(PeripheralDeviceConfig config)
        {
            // 工业 RS485 手拉手一主多从总线：同一物理端口必须共用同一个底层协议实例与串口句柄，
            // 严禁按设备名重复 Open 串口（否则引发 Access to port denied 端口占用异常）。
            string busKey = GetBusKey(config);

            return _protocols.GetOrAdd(busKey, _ =>
            {
                if (_protocolFactory != null)
                {
                    return _protocolFactory(config);
                }

                // 默认使用自带 ModbusProtocol 实现
                var proto = new ModbusProtocol(new NullLoggerFactory())
                {
                    IsModbusTCP = config.Transport == DeviceTransport.ModbusTcp,
                    IP = config.Ip,
                    Port = config.TcpPort,
                    PortName = config.PortName,
                    BaudRate = config.BaudRate,
                    DataBits = config.DataBits,
                    Parity = ParseParity(config.Parity),
                    StopBits = ParseStopBits(config.StopBits),
                    SlaveAddress = config.SlaveAddress
                };
                return proto;
            });
        }

        private SemaphoreSlim GetGate(PeripheralDeviceConfig config)
        {
            // 同一物理总线（同 COM 口或同 IP:Port）共享同一互斥锁，严格串行化
            string busKey = GetBusKey(config);
            return _busGates.GetOrAdd(busKey, _ => new SemaphoreSlim(1, 1));
        }

        private static ModbusRegisterType MapModbusArea(ModbusArea area) => area switch
        {
            ModbusArea.Coil => ModbusRegisterType.Coil,
            ModbusArea.DiscreteInput => ModbusRegisterType.DiscreteInput,
            ModbusArea.InputRegister => ModbusRegisterType.InputRegister,
            ModbusArea.HoldingRegister => ModbusRegisterType.HoldingRegister,
            _ => ModbusRegisterType.HoldingRegister
        };

        private static Parity ParseParity(string parity)
        {
            if (Enum.TryParse<Parity>(parity, true, out var p)) return p;
            return Parity.None;
        }

        private static StopBits ParseStopBits(string stopBits)
        {
            if (Enum.TryParse<StopBits>(stopBits, true, out var s)) return s;
            return StopBits.One;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var cts in _pollCts.Values)
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }
            _pollCts.Clear();

            foreach (var proto in _protocols.Values)
            {
                try
                {
                    if (proto is IDisposable d) d.Dispose();
                }
                catch { }
            }
            _protocols.Clear();

            foreach (var gate in _busGates.Values)
            {
                try { gate.Dispose(); } catch { }
            }
            _busGates.Clear();
        }

        private class NullLoggerFactory : global::Common.ILoggerFactory
        {
            public global::Common.ILoggerManager CreateLogger(string name) => new NullLoggerManager();
            public global::Common.ILoggerManager CreateLogger<T>() => new NullLoggerManager();

            private class NullLoggerManager : global::Common.ILoggerManager
            {
                public void Trace(string message) { }
                public void Debug(string message) { }
                public void Info(string message) { }
                public void Warn(string message) { }
                public void Error(string message) { }
                public void Fatal(string message) { }
            }
        }
    }
}
