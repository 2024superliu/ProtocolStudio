using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 数据流方向，四个试图数据流向
    /// </summary>
    public enum DataDirection
    {
        /// <summary>
        /// A 端接收
        /// </summary>
        ChannelA_Rx,
        /// <summary>
        /// A 端发送
        /// </summary>
        ChannelA_Tx,
        /// <summary>
        /// B 端接收
        /// </summary>
        ChannelB_Rx,
        /// <summary>
        /// B 端发送
        /// </summary>
        ChannelB_Tx,
    }
}
