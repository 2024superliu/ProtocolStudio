namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 通信通道枚举类型
    /// </summary>
    public enum ChannelType
    {
        /// <summary>
        /// TCP服务端
        /// </summary>
        TcpServer,
        /// <summary>
        /// TCP客户端
        /// </summary>
        TcpClient,
        /// <summary>
        /// UDP 节点
        /// </summary>
        Udp,
        /// <summary>
        /// 串口通信
        /// </summary>
        SerialPort,
        /// <summary>
        /// CAN总线接口
        /// </summary>
        Can,
    }
}
