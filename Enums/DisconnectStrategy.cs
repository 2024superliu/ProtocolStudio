using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 一端断开时的转发应对策略
    /// </summary>
    public enum DisconnectStrategy
    {
        /// <summary>
        /// 丢数据：继续接受数据并显示对端数据，但丢弃无法转发的数据
        /// </summary>
        Discard,
        /// <summary>
        /// 缓冲数据：将无法转发数据暂存入缓冲区，待恢复后补发
        /// </summary>
        Buffer,
        /// <summary>
        /// 停止对端转发：自动暂停双向转发
        /// </summary>
        StopPeer,
    }
}
