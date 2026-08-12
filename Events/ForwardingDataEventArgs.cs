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

        public ForwardingDataEventArgs(DataDirection direction, byte[] data,string description = "")
        {
            Timestamp = DateTime.Now;
            Direction = direction;
            Data = data ?? new byte[0];
            Description = description;
        }
    }
}
