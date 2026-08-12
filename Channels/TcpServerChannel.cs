using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Channels
{
    /// <summary>
    /// Tcp服务端
    /// </summary>
    public class TcpServerChannel : ICommunicationChannel
    {
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        //保存所有当前连入的客户端
        private readonly ConcurrentDictionary<string, System.Net.Sockets.TcpClient> _clients = new ConcurrentDictionary<string, System.Net.Sockets.TcpClient>();
        public string Name { get; set; } = "Tcp 服务端";
        public ChannelType ChannelType => ChannelType.TcpServer;
        public ChannelStatus Status { get; private set; } = ChannelStatus.Disconnected;

        public long BytesReceived { get; private set; }
        public long BytesSent { get; private set; }
        public int LocalPort { get; set; } = 8080;

        public event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        public event EventHandler<ChannelStatus> StatusChanged;
        /// <summary>
        /// 建立连接/初始化
        /// </summary>
        /// <returns></returns>
        public Task<bool> OpenAsync()
        {
            if (Status == ChannelStatus.Connected || Status == ChannelStatus.Connecting) return Task.FromResult(true);
            // 修改连接状态为连接
            UpdateStatus(ChannelStatus.Connecting);
            try
            {
                _listener = new TcpListener(IPAddress.Any, LocalPort);
                // 核心连接
                _listener.Start();
                _cts = new CancellationTokenSource();

                UpdateStatus(ChannelStatus.Connected);
                // 启动后台接收程序
                _ = Task.Run(() => AcceptClientsLoopAsync(_cts.Token));
                return Task.FromResult(true);


            }
            catch (Exception)
            {
                UpdateStatus(ChannelStatus.Error);
                return Task.FromResult(false);
            }
        }
        /// <summary>
        /// 监听并接受客户端连接的后台死循环
        /// </summary>
        /// <param name="token"></param>
        private async Task AcceptClientsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 阻塞等待下一个客户端连接
                    System.Net.Sockets.TcpClient client = await _listener.AcceptTcpClientAsync();
                    string clientKey = client.Client.RemoteEndPoint.ToString();
                    // 新客户端加入字典
                    _clients.TryAdd(clientKey, client);
                    _ = Task.Run(() => ClientReceiveLoopAsync(client, clientKey, token));
                }
                catch
                {
                    if (!token.IsCancellationRequested) UpdateStatus(ChannelStatus.Error);
                    break;
                }
            }
        }
        /// <summary>
        /// 为某一个具体客户端服务的接收循环
        /// </summary>
        private async Task ClientReceiveLoopAsync(System.Net.Sockets.TcpClient client, string clientKey, CancellationToken token)
        {
            //创建8kb接收缓冲区
            byte[] buffer = new byte[8192];
            NetworkStream stream = client.GetStream();
            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    // 核心接收
                    int readBytes = await stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (readBytes == 0) break;
                    BytesReceived += readBytes;
                    byte[] data = new byte[readBytes];
                    Array.Copy(buffer, 0, data, 0, readBytes);
                    DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs(data, clientKey));


                }
            }
            catch
            {

            }
            finally
            {
                _clients.TryRemove(clientKey, out _);
                client.Close();
            }
        }
        /// <summary>
        /// 广播发送：将数据发送给当前所有已连接的客户端
        /// </summary>
        public async Task<int> SendAsync(byte[] data)
        {
            if (Status != ChannelStatus.Connected || data == null || data.Length == 0) return 0;
            int successCount = 0;
            foreach(var kvp in _clients)
            {
                try
                {
                    NetworkStream stream = kvp.Value.GetStream();
                    await stream.WriteAsync(data, 0, data.Length);
                    successCount++;
                }
                catch { }
    
            }
            if (successCount > 0)
            {
                BytesSent += (long)data.Length * successCount;
                return data.Length;
            }
            return 0;
        }
        /// <summary>
        /// 关闭连接通道
        /// </summary>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public Task CloseAsync()
        {
            if (Status == ChannelStatus.Disconnected) return Task.CompletedTask;
            try
            {
                _cts?.Cancel();
                _listener?.Stop();

                foreach(var client in _clients.Values)
                {
                    client.Close();
                }
                _clients.Clear();
                _cts?.Dispose();
                _cts = null;
                _listener = null;
            }
            catch { }
            finally
            {
                UpdateStatus(ChannelStatus.Disconnected);
            }
            return Task.CompletedTask;
        }
        /// <summary>
        /// 重置收发计数器
        /// </summary>
        public void ResetStatistics()
        {
            BytesReceived = 0;
            BytesSent = 0;
        }
        /// <summary>
        /// 更新内部状态
        /// </summary>
        /// <param name="connecting"></param>
        private void UpdateStatus(ChannelStatus newStatus)
        {
            Status = newStatus;
            StatusChanged?.Invoke(this, Status);
        }
        /// <summary>
        /// 资源释放接口实现
        /// </summary>
        public void Dispose()
        {
            CloseAsync().Wait();
        }
    }
}
