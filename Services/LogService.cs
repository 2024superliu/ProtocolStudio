using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// 高性能异步日志持久化服务
    /// 使用后台 BlockingCollection 队列实现数据收发与磁盘 I/O 解耦
    /// </summary>
    public class LogService : IDisposable
    {
        private static readonly byte[] BinaryRecordMagic = { (byte)'W', (byte)'P', (byte)'S', (byte)'B' };
        internal const byte BinaryRecordVersion = 1;
        //生产者消费者后台异步队列
        private const int MaxQueuedRecords = 100000;
        private readonly BlockingCollection<ForwardingDataEventArgs> _logQueue =
            new BlockingCollection<ForwardingDataEventArgs>(new ConcurrentQueue<ForwardingDataEventArgs>(), MaxQueuedRecords);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Task _writeTask;
        private readonly Dictionary<string, string> _activeFiles = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _rollIndexes = new Dictionary<string, int>();
        private long _droppedRecordCount;

        public long DroppedRecordCount => Interlocked.Read(ref _droppedRecordCount);
        

        /// <summary>
        /// 日志根目录
        /// </summary>
        public string LogDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        /// <summary>
        /// 单个日志文件最大字节数（默认 10 MB，超过自动切分新文件）
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
        /// <summary>
        /// 四个独立保存开关
        /// </summary>
        public bool EnableLogA_Rx { get; set; } = true;
        public bool EnableLogA_Tx { get; set; } = true;
        public bool EnableLogB_Rx { get; set; } = true;
        public bool EnableLogB_Tx { get; set; } = true;

        public LogService()
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }
            // 启动后台写日志任务
            _writeTask = Task.Run(() => ProcessLogQueueAsync(_cts.Token));
        }
        /// <summary>
        /// 将收发记录推入写日志队列（非阻塞）
        /// </summary>
        public void Enqueue(ForwardingDataEventArgs record)
        {
            if (record == null || record.Data == null || record.Data.Length == 0) return;

            // 根据流向开关过滤
            switch (record.Direction)
            {
                case DataDirection.ChannelA_Rx:
                    if (!EnableLogA_Rx) return;
                    break;
                case DataDirection.ChannelA_Tx:
                    if (!EnableLogA_Tx) return;
                    break;
                case DataDirection.ChannelB_Rx:
                    if (!EnableLogB_Rx) return;
                    break;
                case DataDirection.ChannelB_Tx:
                    if (!EnableLogB_Tx) return;
                    break;
            }
            try
            {
                if (_logQueue.IsAddingCompleted || !_logQueue.TryAdd(record, 100))
                    Interlocked.Increment(ref _droppedRecordCount);
            }
            catch (InvalidOperationException)
            {
                Interlocked.Increment(ref _droppedRecordCount);
            }
        }
        /// <summary>
        /// 后台消费写磁盘循环
        /// </summary>
        private async Task ProcessLogQueueAsync(CancellationToken token)
        {
            try
            {
                foreach(var record in _logQueue.GetConsumingEnumerable(token))
                {
                    try
                    {
                        await WriteRecordToFileAsync(record);
                    }
                    catch(Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[日志写入失败]: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
        /// <summary>
        /// <summary>
        /// 自动保存日志的文件格式：TXT、CSV、BIN (FR-19)
        /// </summary>
        public LogFileFormat SaveFormat { get; set; } = LogFileFormat.TXT;

        /// <summary>
        /// 格式化并追加写入磁盘文件
        /// </summary>
        private async Task WriteRecordToFileAsync(ForwardingDataEventArgs record)
        {
            string ext = SaveFormat.ToString().ToLower();
            string dateStr = record.Timestamp.ToString("yyyy-MM-dd");
            if (SaveFormat == LogFileFormat.BIN) dateStr += "_structured";
            Directory.CreateDirectory(LogDirectory);
            string filePath = ResolveActiveFile(dateStr, ext);

            if (SaveFormat == LogFileFormat.BIN)
            {
                byte[] binaryRecord = CreateBinaryLogRecord(
                    record.Timestamp,
                    record.Direction,
                    record.Data,
                    record.Description);
                using (FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    await fs.WriteAsync(binaryRecord, 0, binaryRecord.Length);
                }
                return;
            }

            bool fileExists = File.Exists(filePath);
            StringBuilder sb = new StringBuilder();

            if (SaveFormat == LogFileFormat.CSV)
            {
                // 如果是 CSV 且文件刚创建，写入 UTF-8 BOM 和 CSV 表头
                if (!fileExists)
                {
                    sb.AppendLine("时间戳,方向,所属端,数据长度(B),数据内容(HEX),数据内容(ASCII),备注");
                }

                string hexStr = string.Join(" ", record.Data.Select(b => b.ToString("X2")));
                string asciiStr = Encoding.ASCII.GetString(record.Data).Replace("\"", "\"\"").Replace("\r", "\\r").Replace("\n", "\\n");
                string desc = (record.Description ?? "").Replace("\"", "\"\"");

                sb.AppendLine($"\"{record.Timestamp:yyyy-MM-dd HH:mm:ss.ffff}\",\"{GetDirectionText(record.Direction)}\",\"{GetEndpointText(record.Direction)}\",\"{record.Data.Length}\",\"{hexStr}\",\"{asciiStr}\",\"{desc}\"");
            }
            else
            {
                // ChannelA_Rx 等方向标识同时表达所属端和收发方向。
                string hexStr = string.Join(" ", record.Data.Select(b => b.ToString("X2")));
                string asciiStr = Encoding.ASCII.GetString(record.Data).Replace("\r", "\\r").Replace("\n", "\\n");
                sb.Append($"[{record.Timestamp:yyyy-MM-dd HH:mm:ss.ffff}] ");
                sb.Append($"[{GetDirectionCode(record.Direction)}] ");
                sb.Append($"[{GetEndpointCode(record.Direction)}]  ");
                sb.Append($"[{record.Data.Length}B] ");
                sb.Append($"HEX: {hexStr} | ASCII: {asciiStr}");

                if (!string.IsNullOrEmpty(record.Description))
                {
                    sb.Append($" | 备注: {record.Description}");
                }
                sb.AppendLine();
            }

            // 异步追加写入文本
            using (StreamWriter writer = new StreamWriter(filePath, true, Encoding.UTF8))
            {
                await writer.WriteAsync(sb.ToString());
            }
        }

        internal static string GetEndpointText(DataDirection direction)
        {
            return direction == DataDirection.ChannelA_Rx || direction == DataDirection.ChannelA_Tx ? "A端" : "B端";
        }

        internal static string GetDirectionText(DataDirection direction)
        {
            return direction == DataDirection.ChannelA_Rx || direction == DataDirection.ChannelB_Rx ? "接收" : "发送";
        }

        internal static string GetEndpointCode(DataDirection direction)
        {
            return direction == DataDirection.ChannelA_Rx || direction == DataDirection.ChannelA_Tx ? "ChannelA" : "ChannelB";
        }

        internal static string GetDirectionCode(DataDirection direction)
        {
            return direction == DataDirection.ChannelA_Rx || direction == DataDirection.ChannelB_Rx ? "Rx" : "Tx";
        }

        internal static byte[] CreateBinaryLogRecord(
            DateTime timestamp,
            DataDirection direction,
            byte[] data,
            string description)
        {
            byte[] payload = data ?? new byte[0];
            byte[] descriptionBytes = Encoding.UTF8.GetBytes(description ?? string.Empty);

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(BinaryRecordMagic);
                writer.Write(BinaryRecordVersion);
                writer.Write(timestamp.Ticks);
                writer.Write((byte)direction);
                writer.Write(payload.Length);
                writer.Write(descriptionBytes.Length);
                writer.Write(payload);
                writer.Write(descriptionBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private string ResolveActiveFile(string dateStr, string extension)
        {
            string directory = Path.GetFullPath(LogDirectory);
            string key = $"{directory}|{dateStr}|{extension}";

            if (!_activeFiles.TryGetValue(key, out string filePath))
            {
                int index = 0;
                filePath = Path.Combine(directory, $"{dateStr}.{extension}");
                while (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes)
                {
                    index++;
                    filePath = Path.Combine(directory, $"{dateStr}_{index:000}.{extension}");
                }
                _rollIndexes[key] = index;
                _activeFiles[key] = filePath;
            }

            if (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes)
            {
                int index = _rollIndexes[key] + 1;
                do
                {
                    filePath = Path.Combine(directory, $"{dateStr}_{index:000}.{extension}");
                    index++;
                }
                while (File.Exists(filePath) && new FileInfo(filePath).Length >= MaxFileSizeBytes);

                _rollIndexes[key] = index - 1;
                _activeFiles[key] = filePath;
            }
            return filePath;
        }

        public void Dispose()
        {

            if (!_logQueue.IsAddingCompleted) _logQueue.CompleteAdding();
            try
            {
                if (!_writeTask.Wait(5000))
                {
                    _cts.Cancel();
                    _writeTask.Wait(1000);
                }
            }
            catch { }
            _cts.Dispose();
            _logQueue.Dispose();
        }

    }
}
