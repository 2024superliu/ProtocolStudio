using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 通道连接状态枚举
    /// </summary>
    public enum ChannelStatus
    {
        /// <summary>
        /// 已断开连接
        /// </summary>
        Disconnected,
        /// <summary>
        /// 正在连接
        /// </summary>
        Connecting,
        /// <summary>
        /// 已连接
        /// </summary>
        Connected,
        /// <summary>
        /// 错误
        /// </summary>
        Error,
    }
}
