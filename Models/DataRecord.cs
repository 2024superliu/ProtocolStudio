using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Models
{
    /// <summary>
    /// 显示在 4 视图中的单条收发数据模型
    /// </summary>
    public class DataRecord
    {
        public DateTime Timestamp { get; set; }
        public DataDirection Direction { get; set; }
        public byte[] RawData { get; set; }
        public int Length => RawData?.Length ?? 0;
        public string Description { get; set; }
        public bool? IsChecksumValid { get; set; }
        public string ChecksumStatusText => !IsChecksumValid.HasValue
            ? "—"
            : IsChecksumValid.Value ? "CRC OK" : "CRC 错误";
        public string TimeString => Timestamp.ToString("HH:mm:ss.fff");
        // 保存这一次数据的快照
        public DisplayFormat RecordFormat { get; set; } = DisplayFormat.Hex;

        /// <summary>
        /// 转换为十六进制字符串显示 (如 "01 03 00 00 00 02 C4 0B")
        /// </summary>
        public string HexContent
        {
            get
            {
                if (RawData == null || RawData.Length == 0) return string.Empty;
                StringBuilder sb = new StringBuilder(RawData.Length * 3);
                foreach (byte b in RawData)
                {
                    sb.AppendFormat("{0:X2} ", b);
                }
                return sb.ToString().TrimEnd();
            }
        }
        /// <summary>
        /// 转换为 ASCII / UTF-8 / GBK 可读文本显示 (支持中文与不可见字符转义)
        /// </summary>
        public string AsciiContent
        {
            get
            {
                if (RawData == null || RawData.Length == 0) return string.Empty;

                // 优先采用系统默认编码 (GBK) 或 UTF-8 解码，解决中文字符显示为 ??????? 的问题
                string str;
                try
                {
                    str = Encoding.Default.GetString(RawData);
                }
                catch
                {
                    str = Encoding.ASCII.GetString(RawData);
                }

                // 处理不可见控制字符，防止表格在 WPF 中显示为空白或换行错乱
                StringBuilder sb = new StringBuilder(str.Length);
                foreach (char c in str)
                {
                    if (c == '\r') sb.Append("\\r");
                    else if (c == '\n') sb.Append("\\n");
                    else if (c == '\0') sb.Append("\\0");
                    else if (c < 32) sb.Append('.');
                    else sb.Append(c);
                }
                return sb.ToString();
            }
        }
        
        /// <summary>
        /// 转为二进制字符
        /// </summary>
        public string BinaryContent
        {
            get
            {
                if (RawData == null || RawData.Length == 0) return string.Empty;
                StringBuilder sb = new StringBuilder(RawData.Length * 9);
                foreach(byte b in RawData)
                {
                    sb.Append(Convert.ToString(b, 2).PadLeft(8, '0')).Append(' ');
                }
                return sb.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// 十六进制 + ASCII 混合格式
        /// </summary>
        public string HexAndAsciiContent => $"{HexContent} | ASCII: {AsciiContent}";

        /// <summary>
        /// 记录该条数据生成/接收时刻的显示格式快照 (FR-13)
        /// </summary>
        public DisplayFormat Format { get; set; } = DisplayFormat.Hex;

        /// <summary>
        /// 根据数据生成时绑定的 Format 渲染格式化文本内容
        /// </summary>
        public string DisplayText
        {
            get
            {
                switch (Format)
                {
                    case DisplayFormat.Ascii:
                        return AsciiContent;
                    case DisplayFormat.Binary:
                        return BinaryContent;
                    case DisplayFormat.HexAndAscii:
                        return HexAndAsciiContent;
                    case DisplayFormat.Hex:
                    default:
                        return HexContent;
                }
            }
        }
        
    }
}
