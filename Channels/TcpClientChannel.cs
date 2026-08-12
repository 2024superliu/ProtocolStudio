using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Channels 
{
    /// <summary>
    /// Tcp客户端通信通道
    /// </summary>
    public class TcpClientChannel : ICommunicationChannel
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private CancellationTokenSource _cts;

        public string Name { get; set; } = "Tcp 客户端";
        public ChannelType ChannelType => ChannelType.TcpClient;
        public ChannelStatus Status { get; private set; } = ChannelStatus.Disconnected;

        public long BytesReceived { get; private set; }
        public long BytesSent { get; private set; }

        public string TargetIp { get; set; } = "127.0.0.1";
        public int TargetPort { get; set; } = 8080;

        public event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        public event EventHandler<ChannelStatus> StatusChanged;
        /// <summary>
        /// 建立连接/初始化
        /// </summary>
        public async Task<bool> OpenAsync()
        {
            if (Status == ChannelStatus.Connected || Status == ChannelStatus.Connecting) return true;
            // 修改连接状态为连接
            UpdateStatus(ChannelStatus.Connecting);
            try
            {
                _tcpClient = new TcpClient();
                // 连接服务器
                await _tcpClient.ConnectAsync(TargetIp, TargetPort);
                // 获取数据流
                _stream = _tcpClient.GetStream();
                // 创建后台推出Cancellation token
                _cts = new CancellationTokenSource();

                UpdateStatus(ChannelStatus.Connected);
                // 启动后台接收程序
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return true;


            }catch(Exception)
            {
                UpdateStatus(ChannelStatus.Error);
                return false;
            }
        }
        /// <summary>
        /// 后台数据接收死循环
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            //创建8kb接收缓冲区
            byte[] buffer = new byte[8192];
            try
            {
                while (!token.IsCancellationRequested && _tcpClient.Connected)
                {
                    int readBytes = await _stream.ReadAsync(buffer, 0, buffer.Length, token);
                    if (readBytes == 0) break;
                    BytesReceived += readBytes;
                    byte[] data = new byte[readBytes];
                    Array.Copy(buffer, 0, data, 0, readBytes);
                    // 触发接收事件，将字节传递上层
                    DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs(data, $"{TargetIp}:{TargetPort}"));

                }
            }
            catch(Exception)
            {

            }
            finally
            {
                await CloseAsync();
            }
        }
        public async Task<int> SendAsync(byte[] data)
        {
            if (Status != ChannelStatus.Connected || _stream == null || data == null || data.Length == 0) return 0;
            try
            {
                // 核心写入Socket输出流
                await _stream.WriteAsync(data, 0, data.Length);
                BytesSent += data.Length;
                return data.Length;
            }
            catch
            {
                await CloseAsync();
                return 0;
            }
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
                //1、推出循环
                _cts?.Cancel();
                //2、关闭数据流和Socket连接
                _stream?.Dispose();
                _tcpClient?.Close();
                _cts?.Dispose();
                _cts = null;
                _stream = null;
                _tcpClient = null;
            }
            catch { }
            finally
            {
                //2、更新状态断开
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
    
