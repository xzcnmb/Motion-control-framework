using System.Collections.Generic;

namespace Sophon.Contracts
{
    /// <summary>
    /// 外设通讯设备配置（平台无关）：温控器、拧紧枪、扫码枪等经 485/232/TCP 集成的设备。
    /// 设备层依据 <see cref="Transport"/> 选择 Modbus RTU/TCP 或裸串口 ASCII，
    /// 并按 <see cref="Tags"/> 定义的寄存器点表读写、按 <see cref="PollPeriodMs"/> 轮询。
    /// </summary>
    public class PeripheralDeviceConfig
    {
        /// <summary>设备名称（如 "温控器1" / "拧紧枪" / "扫码枪"）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>设备类别标签（自由文本，界面分组用，如 "温控" / "拧紧" / "读码"）。</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>传输方式。</summary>
        public DeviceTransport Transport { get; set; } = DeviceTransport.ModbusRtu;

        // ---------- 串口参数（ModbusRtu / SerialAscii） ----------
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        /// <summary>校验位："None" / "Even" / "Odd"（避免直接依赖 System.IO.Ports，用字符串，设备层解析）。</summary>
        public string Parity { get; set; } = "None";
        public int DataBits { get; set; } = 8;
        /// <summary>停止位："One" / "Two" / "OnePointFive"。</summary>
        public string StopBits { get; set; } = "One";

        // ---------- TCP 参数（ModbusTcp） ----------
        public string Ip { get; set; } = "127.0.0.1";
        public int TcpPort { get; set; } = 502;

        /// <summary>Modbus 从站地址（1~247；RS485 总线上必须唯一）。</summary>
        public byte SlaveAddress { get; set; } = 1;

        /// <summary>轮询周期（ms）。温度类 500ms、扭矩类 1000ms 视节拍而定。</summary>
        public int PollPeriodMs { get; set; } = 500;

        /// <summary>读超时（ms）。</summary>
        public int TimeoutMs { get; set; } = 500;

        /// <summary>失败重试次数（超时/CRC 错/异常响应）。</summary>
        public int Retries { get; set; } = 2;

        /// <summary>SerialAscii 设备的帧结束符（扫码枪常见 "\r\n" 或 "\r"）。</summary>
        public string AsciiTerminator { get; set; } = "\r\n";

        /// <summary>寄存器点表（Modbus 设备用；ASCII 设备可留空）。</summary>
        public List<DeviceTag> Tags { get; set; } = new();
    }

    /// <summary>
    /// 设备寄存器点位定义。地址按 Modbus 库的 0 基址填写（手册 40001 → 填 0），量纲经 Scale/Offset 换算。
    /// </summary>
    public class DeviceTag
    {
        /// <summary>点名（业务侧引用，如 "PV" / "SV" / "Torque"）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>寄存器区。</summary>
        public ModbusArea Area { get; set; } = ModbusArea.HoldingRegister;

        /// <summary>0 基址起始地址。</summary>
        public ushort Address { get; set; }

        /// <summary>数据类型。</summary>
        public RegisterDataType DataType { get; set; } = RegisterDataType.UInt16;

        /// <summary>是否可写（写命令白名单，防误写）。</summary>
        public bool Writable { get; set; }

        /// <summary>工程量换算：EngineeringValue = raw * Scale + Offset（如温控 0.1℃ → Scale=0.1）。</summary>
        public double Scale { get; set; } = 1.0;

        /// <summary>工程量偏置。</summary>
        public double Offset { get; set; }

        /// <summary>Float32/Int32 的字序是否需要交换（大端设备常见的字/字节序问题）。</summary>
        public bool SwapWords { get; set; }

        /// <summary>单位（界面显示，如 "℃" / "N·m"）。</summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>写值下限（Writable 时的范围校验）。</summary>
        public double? WriteMin { get; set; }

        /// <summary>写值上限。</summary>
        public double? WriteMax { get; set; }
    }
}
