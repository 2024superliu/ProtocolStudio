using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Channels
{
    internal class SerialPortChannel : ICommunicationChannel
    {
        private SerialPort _serialPort;

        public event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        public event EventHandler<ChannelStatus> StatusChanged;

        public string Name { get; set; } = "串口";
        public ChannelType ChannelType => ChannelType.SerialPort;
        public ChannelStatus Status { get; private set; } = ChannelStatus.Disconnected;

        public long BytesReceived { get; private set; }
        public long BytesSent { get; private set; }

        public int LocalPort { get; set; } = 8080;
        ///<summary>
        ///串口参数
        /// </summary>
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;

        // 默认串口接收缓冲通常只有约 4KB，无法承受 921600 波特率下的 8192B 分包。
        private const int SerialReadBufferSize = 1024 * 1024;
        private const int SerialWriteBufferSize = 64 * 1024;


        /// <summary>
        /// 启动 TCP 服务端监听
        /// </summary>
        public Task<bool> OpenAsync()
        {
            if (Status == ChannelStatus.Connected) return Task.FromResult(true);

            UpdateStatus(ChannelStatus.Connecting);
            try
            {
                // 根据当前配置创建 SerialPort 对象
                _serialPort = new SerialPort(PortName, BaudRate, Parity, DataBits, StopBits);
                _serialPort.ReadBufferSize = SerialReadBufferSize;
                _serialPort.WriteBufferSize = SerialWriteBufferSize;
                _serialPort.ReceivedBytesThreshold = 1;

                // 绑定 Windows 驱动层的串口数据到达事件通知
                _serialPort.DataReceived += OnSerialPortDataReceived;

                // 打开串口
                _serialPort.Open();

                UpdateStatus(ChannelStatus.Connected);
                return Task.FromResult(true);
            }
            catch
            {
                UpdateStatus(ChannelStatus.Error);
                return Task.FromResult(false);
            }
        }
        private readonly object _lockBuffer = new object();
        private readonly MemoryStream _rxBuffer = new MemoryStream();
        private Timer _frameTimer;
        private const int FrameIdleTimeoutMs = 15; // 15ms 内无新字节即切为完整一帧

        /// <summary>
        /// 串口驱动回调函数 (带 15ms 断帧超时组包机制，解决拆包问题)
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnSerialPortDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                // 一次事件内持续读取，直到把驱动缓冲区取空，避免大包只读取一部分。
                while (_serialPort != null && _serialPort.IsOpen)
                {
                    int bytesToRead = _serialPort.BytesToRead;
                    if (bytesToRead <= 0) break;

                    byte[] buffer = new byte[Math.Min(bytesToRead, 64 * 1024)];
                    int readBytes = _serialPort.Read(buffer, 0, buffer.Length);
                    if (readBytes <= 0) break;

                    lock (_lockBuffer)
                    {
                        _rxBuffer.Write(buffer, 0, readBytes);
                        BytesReceived += readBytes;

                        // 重置/刷新 15ms 断帧组包定时器
                        if (_frameTimer == null)
                        {
                            _frameTimer = new Timer(FlushRxBufferCallback, null, FrameIdleTimeoutMs, Timeout.Infinite);
                        }
                        else
                        {
                            _frameTimer.Change(FrameIdleTimeoutMs, Timeout.Infinite);
                        }
                    }
                }
            }
            catch { }
        }

        private void FlushRxBufferCallback(object state)
        {
            byte[] data = null;
            lock (_lockBuffer)
            {
                if (_rxBuffer.Length > 0)
                {
                    data = _rxBuffer.ToArray();
                    _rxBuffer.SetLength(0);
                }
            }

            if (data != null && data.Length > 0)
            {
                // 将完整断帧报文推给上层视图与转发引擎
                DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs(data, PortName));
            }
        }


        /// <summary>
        /// 向串口写入字节数据
        /// </summary>
        public Task<int> SendAsync(byte[] data)
        {
            if (Status != ChannelStatus.Connected || _serialPort == null || !_serialPort.IsOpen || data == null || data.Length == 0)
                return Task.FromResult(0);

            try
            {
                // 调用原生 Write 方法同步写入驱动发送缓冲区
                _serialPort.Write(data, 0, data.Length);
                BytesSent += data.Length;
                return Task.FromResult(data.Length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[串口发送失败]: {ex.Message}");
                return Task.FromResult(0);
            }
        }
        /// <summary>
        /// 关闭串口
        /// </summary>
        /// <returns></returns>
        public Task CloseAsync()
        {
            if (Status == ChannelStatus.Disconnected) return Task.CompletedTask;

            try
            {
                if (_serialPort != null)
                {
                    // 解绑事件防止关闭过程中再次触发
                    _serialPort.DataReceived -= OnSerialPortDataReceived;
                    if (_serialPort.IsOpen) _serialPort.Close();
                    _serialPort.Dispose();
                    _serialPort = null;
                }
                _frameTimer?.Dispose();
                _frameTimer = null;
                lock (_lockBuffer) _rxBuffer.SetLength(0);
            }
            catch { }
            finally
            {
                UpdateStatus(ChannelStatus.Disconnected);
            }
            return Task.CompletedTask;
        }
        public void ResetStatistics()
        {
            BytesReceived = 0;
            BytesSent = 0;
        }

        private void UpdateStatus(ChannelStatus newStatus)
        {
            Status = newStatus;
            StatusChanged?.Invoke(this, Status);
        }

        public void Dispose()
        {
            CloseAsync().Wait();
        }

    }
}
