using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 当通信通道接收到数据时触法的事件参数
    /// </summary>
    public class ChannelDataReceivedEventArgs : EventArgs
    {
        /// <summary>
        /// 接收到的数据时刻
        /// </summary>
        public DateTime Timestamp { get; }
        /// <summary>
        /// 原始字节数据
        /// </summary>
        public byte[] Data { get; }
        /// <summary>
        /// 远端设备标示细节
        /// </summary>
        public string RemoteEndpoint { get; }

        public ChannelDataReceivedEventArgs(byte[] data,string remoteEndpoint = null)
        {
            Timestamp = DateTime.Now;
            Data = data ?? new byte[0];
            RemoteEndpoint = remoteEndpoint;
        }

    }
}
