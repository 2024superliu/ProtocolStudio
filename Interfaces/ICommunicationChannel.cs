using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Interfaces
{
    /// <summary>
    /// 通信通道需要必须实现的统一抽象接口
    /// </summary>
    public interface ICommunicationChannel : IDisposable
    {
        /// <summary>
        /// 通道标示名称
        /// </summary>
        string Name { get; set; }
        /// <summary>
        /// 通道类型
        /// </summary>
        ChannelType ChannelType { get; }
        /// <summary>
        /// 通道运行状态
        /// </summary>
        ChannelStatus Status { get; }
        /// <summary>
        /// 已经接收字符统计
        /// </summary>
        long BytesReceived { get; }
        /// <summary>
        /// 已经发送字符统计
        /// </summary>
        long BytesSent { get; }
        /// <summary>
        /// 当接收数据时触发事件
        /// </summary>
        event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        /// <summary>
        /// 当通道改变时触发事件
        /// </summary>
        event EventHandler<ChannelStatus> StatusChanged;
        /// <summary>
        /// 异步打开通道
        /// </summary>
        /// <returns></returns>
        Task<bool> OpenAsync();
        /// <summary>
        /// 异步关闭通道
        /// </summary>
        /// <returns></returns>
        Task CloseAsync();
        /// <summary>
        /// 向该通道发送数据
        /// </summary>
        Task<int> SendAsync(byte[] data);
        /// <summary>
        /// 重置收发字节计数器
        /// </summary>
        void ResetStatistics();
    }
}
