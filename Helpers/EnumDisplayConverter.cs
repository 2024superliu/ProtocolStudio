using System;
using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Data;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// 仅负责将界面中的枚举值转换为中文，不改变枚举值及配置内容。
    /// </summary>
    public sealed class EnumDisplayConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string text = GetDisplayText(value);
            return string.Equals(parameter as string, "DataHeader", StringComparison.Ordinal)
                ? $"数据内容（{text}）"
                : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        private static string GetDisplayText(object value)
        {
            if (value is ChannelType channelType)
            {
                switch (channelType)
                {
                    case ChannelType.TcpServer: return "TCP 服务端";
                    case ChannelType.TcpClient: return "TCP 客户端";
                    case ChannelType.Udp: return "UDP 节点";
                    case ChannelType.SerialPort: return "串口";
                    case ChannelType.Can: return "CAN 总线";
                }
            }

            if (value is ChannelStatus channelStatus)
            {
                switch (channelStatus)
                {
                    case ChannelStatus.Disconnected: return "已断开";
                    case ChannelStatus.Connecting: return "连接中";
                    case ChannelStatus.Connected: return "已连接";
                    case ChannelStatus.Error: return "错误";
                }
            }

            if (value is DisplayFormat displayFormat)
            {
                switch (displayFormat)
                {
                    case DisplayFormat.Hex: return "十六进制（HEX）";
                    case DisplayFormat.Ascii: return "ASCII 文本";
                    case DisplayFormat.HexAndAscii: return "HEX + ASCII";
                    case DisplayFormat.Binary: return "二进制";
                }
            }

            if (value is FrameMode frameMode)
            {
                switch (frameMode)
                {
                    case FrameMode.None: return "按接收块（不重分帧）";
                    case FrameMode.FixedLength: return "固定长度";
                    case FrameMode.Delimiter: return "分隔符";
                    case FrameMode.TimeInterval: return "时间间隔";
                }
            }

            if (value is ChecksumAlgorithm checksumAlgorithm)
            {
                switch (checksumAlgorithm)
                {
                    case ChecksumAlgorithm.Crc16Modbus: return "CRC16 / MODBUS";
                    case ChecksumAlgorithm.Crc16CcittFalse: return "CRC16 / CCITT-FALSE";
                    case ChecksumAlgorithm.Crc32: return "CRC32 / ISO-HDLC";
                }
            }

            if (value is DisconnectStrategy strategy)
            {
                switch (strategy)
                {
                    case DisconnectStrategy.Discard: return "丢弃数据";
                    case DisconnectStrategy.Buffer: return "缓存补发";
                    case DisconnectStrategy.StopPeer: return "暂停转发";
                }
            }

            if (value is Parity parity)
            {
                switch (parity)
                {
                    case Parity.None: return "无校验";
                    case Parity.Odd: return "奇校验";
                    case Parity.Even: return "偶校验";
                    case Parity.Mark: return "标记校验";
                    case Parity.Space: return "空格校验";
                }
            }

            if (value is StopBits stopBits)
            {
                switch (stopBits)
                {
                    case StopBits.None: return "无";
                    case StopBits.One: return "1 位";
                    case StopBits.Two: return "2 位";
                    case StopBits.OnePointFive: return "1.5 位";
                }
            }

            return value?.ToString() ?? string.Empty;
        }
    }
}
