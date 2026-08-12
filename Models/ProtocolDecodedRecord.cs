using System;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Models
{
    /// <summary>
    /// 分帧数据经协议插件解析后的只读显示记录。
    /// </summary>
    public sealed class ProtocolDecodedRecord
    {
        public DateTime Timestamp { get; set; }
        public DataDirection Direction { get; set; }
        public string ParserName { get; set; }
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string FieldsText { get; set; }
        public string RawHex { get; set; }

        public string TimeString => Timestamp.ToString("HH:mm:ss.fff");

        public string DirectionText
        {
            get
            {
                switch (Direction)
                {
                    case DataDirection.ChannelA_Rx: return "A · RX";
                    case DataDirection.ChannelA_Tx: return "A · TX";
                    case DataDirection.ChannelB_Rx: return "B · RX";
                    case DataDirection.ChannelB_Tx: return "B · TX";
                    default: return Direction.ToString();
                }
            }
        }

        public string DisplayText => string.IsNullOrWhiteSpace(FieldsText)
            ? Summary ?? string.Empty
            : string.IsNullOrWhiteSpace(Summary) ? FieldsText : Summary + " | " + FieldsText;
    }
}
