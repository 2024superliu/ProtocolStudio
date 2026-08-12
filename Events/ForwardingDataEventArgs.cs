using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Events
{
    /// <summary>
    /// 转发/收发数据参数
    /// </summary>
    public class ForwardingDataEventArgs : EventArgs
    {
        /// <summary>
        /// 时间戳
        /// </summary>
        public DateTime Timestamp { get; }
        /// <summary>
        /// 数据流向
        /// </summary>
        public DataDirection Direction { get; }
        /// <summary>
        /// 数据内容
        /// </summary>
        public byte[] Data { get; }
        /// <summary>
        /// 描述来源标记
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// 接收完整帧的CRC验证结果；null表示未执行自动验证。
        /// </summary>
        public bool? IsChecksumValid { get; set; }

        /// <summary>
        /// CRC通过后剥离校验字节的有效载荷，供协议解析器使用。
        /// </summary>
        public byte[] VerifiedPayload { get; set; }

        public ForwardingDataEventArgs(DataDirection direction, byte[] data,string description = "")
        {
            Timestamp = DateTime.Now;
            Direction = direction;
            Data = data ?? new byte[0];
            Description = description;
        }
    }
}
