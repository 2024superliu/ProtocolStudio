using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Channels
{
    internal class UdpChannel : ICommunicationChannel
    {
        private UdpClient _udpClient;
        private CancellationTokenSource _cts;

        public string Name { get; set; } = "UDP 节点";

        public ChannelType ChannelType => ChannelType.Udp;

        public ChannelStatus Status { get; private set; } = ChannelStatus.Disconnected;

        public long BytesReceived { get; private set; }

        public long BytesSent { get; private set; }
        //配置参数
        public int LocalPort { get; set; } = 9000;
        public string TargetIp { get; set; } = "127.0.0.1";
        public int TargetPort { get; set; } = 9001;

        public event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        public event EventHandler<ChannelStatus> StatusChanged;
        /// <summary>
        /// 建立连接/初始化
        /// </summary>
        /// <returns></returns>
        public Task<bool> OpenAsync()
        {
            if (Status == ChannelStatus.Connected) return Task.FromResult(true);
            // 修改连接状态为连接
            UpdateStatus(ChannelStatus.Connecting);
            try
            {
                // 绑定本地端口
                _udpClient = new UdpClient(LocalPort);
                
                // 解决 Windows 特有的 UDP WSAECONNRESET (10054) 异常问题：
                // 当 UDP 发送给一个未开端口的目标时，Windows 会回应 ICMP 不可达，默认导致下一次 ReceiveAsync 抛出 10054 异常
                const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
                try { _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }

                _cts = new CancellationTokenSource();

                UpdateStatus(ChannelStatus.Connected);
                // 启动后台接收程序
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return Task.FromResult(true);
            }
            catch (Exception)
            {
                UpdateStatus(ChannelStatus.Error);
                return Task.FromResult(false);
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 异步等待 UDP 报文到达
                    UdpReceiveResult result = await _udpClient.ReceiveAsync();
                    byte[] data = result.Buffer;
                    if (data != null && data.Length > 0)
                    {
                        BytesReceived += data.Length;
                        DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs(data, result.RemoteEndPoint.ToString()));
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Socket 已被主动关闭，安全退出循环
                    break;
                }
                catch (OperationCanceledException)
                {
                    // 已取消，安全退出循环
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[UDP 接收监听]: {ex.Message}");
                }
            }
        }

        private void UpdateStatus(ChannelStatus newStatus)
        {
            Status = newStatus;
            StatusChanged?.Invoke(this, Status);
        }

        public void ResetStatistics()
        {
            BytesReceived = 0;
            BytesSent = 0;
        }

        public async Task<int> SendAsync(byte[] data)
        {
            if (Status != ChannelStatus.Connected || _udpClient == null || data == null || data.Length == 0)
                return 0;

            try
            {
                IPEndPoint targetEndPoint = new IPEndPoint(IPAddress.Parse(TargetIp), TargetPort);
                int sent = await _udpClient.SendAsync(data, data.Length, targetEndPoint);
                BytesSent += sent;
                return sent;
            }
            catch
            {
                return 0;
            }
        }
        /// <summary>
        /// 异步关闭通道
        /// </summary>
        public Task CloseAsync()
        {
            if (Status == ChannelStatus.Disconnected) return Task.FromResult(true);

            try
            {
                _cts?.Cancel();
                _udpClient?.Close();
                _udpClient?.Dispose();
                _cts?.Dispose();
                _cts = null;
                _udpClient = null;
            }
            catch { }
            finally
            {
                UpdateStatus(ChannelStatus.Disconnected);
            }
            return Task.FromResult(true);
        }
        /// <summary>
        /// 资源释放
        /// </summary>
        public void Dispose()
        {
            CloseAsync().Wait();
        }
    }
}
