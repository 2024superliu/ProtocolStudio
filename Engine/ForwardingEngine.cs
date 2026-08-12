using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Engine
{
    /// <summary>
    /// 双向数据转发逻辑
    /// </summary>
    public class ForwardingEngine
    {
        private ICommunicationChannel _channelA;
        private ICommunicationChannel _channelB;

        public ICommunicationChannel ChannelA => _channelA;
        public ICommunicationChannel ChannelB => _channelB;

        // 暂存缓冲队列
        private readonly ConcurrentQueue<byte[]> _bufferForA = new ConcurrentQueue<byte[]>();
        private readonly ConcurrentQueue<byte[]> _bufferForB = new ConcurrentQueue<byte[]>();
        private const int MaxBufferedFramesPerTarget = 10000;

        public int BufferedForACount => _bufferForA.Count;
        public int BufferedForBCount => _bufferForB.Count;

        /// <summary>
        /// 是否启用双向数据转发 (FR-8: 暂停/恢复)
        /// </summary>
        public bool IsForwardingEnabled { get; set; } = true;
        /// <summary>
        /// 当一端断开时的应对策略 (FR-10)
        /// </summary>
        public DisconnectStrategy StrategyOnDisconnect { get; set; } = DisconnectStrategy.Discard;
        /// <summary>
        /// 当产生任意收发/转发数据时触发此事件 (FR-9)
        /// </summary>
        public event EventHandler<ForwardingDataEventArgs> DataForwarded;
        /// <summary>
        /// 当通道异常断开时通知 UI 提示用户
        /// </summary>
        public event EventHandler<string> ChannelDisconnectedNotice;
        /// <summary>
        /// 绑定 A 通道
        /// </summary>
        public void AttachChannelA(ICommunicationChannel channel)
        {
            if (_channelA != null)
            {
                _channelA.StatusChanged -= OnChannelAStatusReceived;
                _channelA.DataReceived -= OnChannelADataReceived;
            }
            _channelA = channel;

            if (_channelA != null)
            {
                _channelA.StatusChanged += OnChannelAStatusReceived;
                _channelA.DataReceived += OnChannelADataReceived;
                if (_channelA.Status == ChannelStatus.Connected)
                    OnChannelAStatusReceived(_channelA, ChannelStatus.Connected);
            }
        }



        /// <summary>
        /// 绑定 B 通道
        /// </summary>
        public void AttachChannelB(ICommunicationChannel channel)
        {
            if (_channelB != null)
            {
                _channelB.StatusChanged -= OnChannelBStatusReceived;
                _channelB.DataReceived -= OnChannelBDataReceived;
            }
            _channelB = channel;

            if (_channelB != null)
            {
                _channelB.StatusChanged += OnChannelBStatusReceived;
                _channelB.DataReceived += OnChannelBDataReceived;
                if (_channelB.Status == ChannelStatus.Connected)
                    OnChannelBStatusReceived(_channelB, ChannelStatus.Connected);
            }
        }
        /// <summary>
        /// 当 A 端重新连接成功后，刷新并补发缓冲区数据
        /// </summary>
        private async void OnChannelAStatusReceived(object sender, ChannelStatus status)
        {
            if(status == ChannelStatus.Disconnected || status == ChannelStatus.Error)
            {
                ChannelDisconnectedNotice?.Invoke(this, "A端连接已断开");
            }else if (status == ChannelStatus.Connected && StrategyOnDisconnect == DisconnectStrategy.Buffer)
            {
                // 恢复连接后清空补发缓冲
                while(_bufferForA.TryDequeue(out byte[] bufferedData))
                {
                    int sent = await _channelA.SendAsync(bufferedData);
                    if (sent <= 0)
                    {
                        EnqueueBounded(_bufferForA, bufferedData);
                        break;
                    }
                    RaiseDataForwarded(DataDirection.ChannelA_Tx, bufferedData, "重连补发数据");
                }
            }
        }
        /// <summary>
        /// 当 B 端重新连接成功后，刷新并补发缓冲区数据
        /// </summary>
        private async void OnChannelBStatusReceived(object sender, ChannelStatus status)
        {
            if (status == ChannelStatus.Disconnected || status == ChannelStatus.Error)
            {
                ChannelDisconnectedNotice?.Invoke(this, "B端连接已断开");
            }
            else if (status == ChannelStatus.Connected && StrategyOnDisconnect == DisconnectStrategy.Buffer)
            {
                // 恢复连接后清空补发缓冲
                while (_bufferForB.TryDequeue(out byte[] bufferedData))
                {
                    int sent = await _channelB.SendAsync(bufferedData);
                    if (sent <= 0)
                    {
                        EnqueueBounded(_bufferForB, bufferedData);
                        break;
                    }
                    RaiseDataForwarded(DataDirection.ChannelB_Tx, bufferedData, "重连补发数据");
                }
            }
        }


        /// <summary>
        /// A 收到数据时转发到 B
        /// </summary>
        private async void OnChannelADataReceived(object sender, ChannelDataReceivedEventArgs e)
        {
            // 1. 无论是否暂停，A通道会接收数据并显示(FR-8)
            RaiseDataForwarded(DataDirection.ChannelA_Rx, e.Data, e.RemoteEndpoint);

            // 2. 暂停转发时仅记录接收数据，不应被误判为对端断开
            if (!IsForwardingEnabled) return;

            // 3. B 端就绪时透明转发
            if (_channelB != null && _channelB.Status == ChannelStatus.Connected)
            {
                try
                {
                    int sendBytes = await _channelB.SendAsync(e.Data);
                    if (sendBytes > 0)
                    {
                        // 3. 触发 B 端发送事件 (B TX)
                        RaiseDataForwarded(DataDirection.ChannelB_Tx, e.Data, "转发自 A 端");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[转发失败 A->B]: {ex.Message}");
                }
            }
            else
            {
                // 4. B端未连接时根据定义策略处理
                HandleDisconnectionStrategy(e.Data, targetIsB: true);
            }
        }
        /// <summary>
        /// B 收到数据时转发到 A
        /// </summary>
        private async void OnChannelBDataReceived(object sender, ChannelDataReceivedEventArgs e)
        {
            // 1. 触发 B 端接收事件 (B RX)
            RaiseDataForwarded(DataDirection.ChannelB_Rx, e.Data, e.RemoteEndpoint);

            // 2. 暂停转发时仅记录接收数据
            if (!IsForwardingEnabled) return;

            // 3. A 端就绪时透明转发
            if (_channelA != null && _channelA.Status == ChannelStatus.Connected)
            {
                try
                {
                    int sendBytes = await _channelA.SendAsync(e.Data);
                    if (sendBytes > 0)
                    {
                        // 3. 触发 A 端发送事件 (A TX)
                        RaiseDataForwarded(DataDirection.ChannelA_Tx, e.Data, "转发自 B 端");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[转发失败 B->A]: {ex.Message}");
                }
            }
            else
            {
                HandleDisconnectionStrategy(e.Data, targetIsB: false);
            }
        }
        /// <summary>
        /// 执行断开策略逻辑 (FR-10)
        /// </summary>
        private void HandleDisconnectionStrategy(byte[] data, bool targetIsB)
        {
            switch (StrategyOnDisconnect)
            {
                case DisconnectStrategy.Discard:
                    // 丢弃数据，仅做日志/界面记录，不作处理
                    break;
                case DisconnectStrategy.Buffer:
                    // 有界缓冲，防止对端长期离线造成内存无限增长
                    if (targetIsB) EnqueueBounded(_bufferForB, data);
                    else EnqueueBounded(_bufferForA, data);
                    break;
                case DisconnectStrategy.StopPeer:
                    // 停止对端转发
                    IsForwardingEnabled = false;
                    ChannelDisconnectedNotice?.Invoke(this, $"{(targetIsB ? "B" : "A")}端断开，已依据策略自动暂停转发。");
                    break;
            }
        }
        /// <summary>
        /// 数据入队
        /// </summary>
        private static void EnqueueBounded(ConcurrentQueue<byte[]> queue, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            while (queue.Count >= MaxBufferedFramesPerTarget)
            {
                queue.TryDequeue(out _);
            }
            queue.Enqueue((byte[])data.Clone());
        }

        /// <summary>
        /// 主动向 A 端发送数据
        /// </summary>
        public async Task<int> SendToChannelAAsync(byte[] data)
        {
            if (_channelA == null || _channelA.Status != ChannelStatus.Connected) 
                return 0;

            int sent = await _channelA.SendAsync(data);
            if (sent > 0)
            {
                RaiseDataForwarded(DataDirection.ChannelA_Tx, data, "主动发送");
            }
            return sent;
        }

        /// <summary>
        /// 主动向 B 端发送数据
        /// </summary>
        public async Task<int> SendToChannelBAsync(byte[] data)
        {
            if (_channelB == null || _channelB.Status != ChannelStatus.Connected) 
                return 0;

            int sent = await _channelB.SendAsync(data);
            if (sent > 0)
            {
                RaiseDataForwarded(DataDirection.ChannelB_Tx, data, "主动发送");
            }
            return sent;
        }

        private void RaiseDataForwarded(DataDirection direction, byte[] data, string desc)
        {
            DataForwarded?.Invoke(this, new ForwardingDataEventArgs(direction, data, desc));
        }
    }
}
