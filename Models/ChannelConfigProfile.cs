using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Models
{
    /// <summary>
    /// 单个通道的完整配置数据模型 (用于 JSON 保存与加载)
    /// </summary>
    public class SingleChannelConfig
    {
        public ChannelType ChannelType { get; set; } = ChannelType.TcpServer;
        public int LocalPort { get; set; } = 8080;
        public string TargetIp { get; set; } = "127.0.0.1";
        public int TargetPort { get; set; } = 8080;

        // 串口配置
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;

        // CAN 配置
        // 设备类型 (例如: 4 代表 USBCAN-2A/2C)
        public int CanDeviceType { get; set; } = 4;
        // 兼容早期配置文件中的拼写错误字段
        public int CanDeviceTpye { get => CanDeviceType; set => CanDeviceType = value; }
        // 设备索引 (索引 0)
        public int CanDeviceIndex { get; set; } = 0;
        // CAN 通道号 (通道 0 或 通道 1)
        public int CanChannelIndex { get; set; } = 0;
        // 波特率 (250K / 500K / 1M)
        public string CanBaudRate { get; set; } = "500k";
        // 过滤器验收码 (AccCode)
        public string CanAccCode { get; set; } = "0x00000000";
        // 过滤器屏蔽码 (AccMask)
        public string CanAccMask { get; set; } = "0xFFFFFFFF";
        // ControlCAN 兼容驱动位置与主动发送帧 ID
        public string CanDriverPath { get; set; } = "ControlCAN.dll";
        public uint CanTransmitId { get; set; } = 0x123;
    }
    /// <summary>
    /// 包含 A/B 通道及全局设置的存档模型 (FR-5)
    /// </summary>
    public class ChannelConfigProfile
    {
        public string ProfileName { get; set; } = "默认配置";
        public SingleChannelConfig ChannelA { get; set; } = new SingleChannelConfig();
        public SingleChannelConfig ChannelB { get; set; } = new SingleChannelConfig();
        public bool IsForwardingEnabled { get; set; } = true;
        public DisconnectStrategy DisconnectStrategy { get; set; } = DisconnectStrategy.Discard;
        public DisplayFormat DisplayFormat { get; set; } = DisplayFormat.Hex;
    }
}
