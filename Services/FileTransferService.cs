using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Services
{
    public static class FileTransferProtocol
    {
        internal static readonly byte[] Magic = Encoding.ASCII.GetBytes("WPSFILE1");
        internal const int HashLength = 32;
        internal const int FixedHeaderLength = 8 + 2 + HashLength;
        internal const int MaximumFileNameBytes = 1024;

        public static byte[] CreateHeader(string fileName, long fileLength, byte[] sha256)
        {
            string safeName = Path.GetFileName(fileName ?? string.Empty);
            byte[] nameBytes = Encoding.UTF8.GetBytes(safeName);
            if (nameBytes.Length == 0 || nameBytes.Length > MaximumFileNameBytes)
                throw new InvalidDataException("文件名为空或过长。");
            if (fileLength < 0)
                throw new InvalidDataException("文件长度无效。");
            if (sha256 == null || sha256.Length != HashLength)
                throw new InvalidDataException("文件 SHA-256 无效。");

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(fileLength);
                writer.Write((ushort)nameBytes.Length);
                writer.Write(sha256);
                writer.Write(nameBytes);
                writer.Flush();
                return stream.ToArray();
            }
        }

        public static Task<byte[]> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha256 = SHA256.Create())
                {
                    var buffer = new byte[64 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        sha256.TransformBlock(buffer, 0, read, buffer, 0);
                    }
                    sha256.TransformFinalBlock(new byte[0], 0, 0);
                    return sha256.Hash;
                }
            }, cancellationToken);
        }
    }

    public sealed class FileTransferEventArgs : EventArgs
    {
        public DataDirection Direction { get; set; }
        public string FileName { get; set; }
        public string SavedPath { get; set; }
        public long TotalBytes { get; set; }
        public long ReceivedBytes { get; set; }
        public string Message { get; set; }
    }

    public sealed class FileTransferReceiver : IDisposable
    {
        private enum ReceivePhase
        {
            SeekingMagic,
            ReadingFixedHeader,
            ReadingFileName,
            ReceivingFile
        }

        private sealed class ReceiveState
        {
            public readonly object SyncRoot = new object();
            public readonly byte[] FixedHeader = new byte[FileTransferProtocol.FixedHeaderLength];
            public ReceivePhase Phase = ReceivePhase.SeekingMagic;
            public int MagicMatched;
            public int FixedHeaderCount;
            public byte[] FileNameBytes;
            public int FileNameCount;
            public long TotalBytes;
            public long ReceivedBytes;
            public long LastReportedBytes;
            public byte[] ExpectedHash;
            public FileStream OutputStream;
            public SHA256 Hash;
            public string FileName;
            public string TargetPath;
            public string PartPath;
        }

        private const long MaximumAcceptedFileLength = 100L * 1024 * 1024 * 1024;
        private readonly Dictionary<DataDirection, ReceiveState> _states =
            new Dictionary<DataDirection, ReceiveState>
            {
                { DataDirection.ChannelA_Rx, new ReceiveState() },
                { DataDirection.ChannelB_Rx, new ReceiveState() }
            };
        private readonly Dictionary<DataDirection, bool> _enabledDirections =
            new Dictionary<DataDirection, bool>
            {
                { DataDirection.ChannelA_Rx, false },
                { DataDirection.ChannelB_Rx, false }
            };
        private readonly Dictionary<DataDirection, string> _outputDirectories =
            new Dictionary<DataDirection, string>
            {
                { DataDirection.ChannelA_Rx, null },
                { DataDirection.ChannelB_Rx, null }
            };
        private bool _disposed;

        public string OutputDirectory { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReceivedFiles");

        public event EventHandler<FileTransferEventArgs> FileStarted;
        public event EventHandler<FileTransferEventArgs> FileProgress;
        public event EventHandler<FileTransferEventArgs> FileCompleted;
        public event EventHandler<FileTransferEventArgs> FileFailed;

        public void ConfigureDirection(DataDirection direction, bool enabled, string outputDirectory)
        {
            if (!_states.TryGetValue(direction, out ReceiveState state)) return;

            lock (state.SyncRoot)
            {
                _outputDirectories[direction] = outputDirectory;
                _enabledDirections[direction] = enabled;
                if (!enabled) ResetState(state);
            }
        }

        public void Process(DataDirection direction, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0 || !_states.TryGetValue(direction, out ReceiveState state)) return;

            lock (state.SyncRoot)
            {
                if (!_enabledDirections[direction]) return;
                try
                {
                    int offset = 0;
                    while (offset < data.Length)
                    {
                        if (state.Phase == ReceivePhase.ReceivingFile)
                        {
                            int count = (int)Math.Min(state.TotalBytes - state.ReceivedBytes, data.Length - offset);
                            if (count > 0)
                            {
                                state.OutputStream.Write(data, offset, count);
                                state.Hash.TransformBlock(data, offset, count, data, offset);
                                state.ReceivedBytes += count;
                                offset += count;
                                ReportProgress(direction, state);
                            }

                            if (state.ReceivedBytes == state.TotalBytes)
                            {
                                CompleteFile(direction, state);
                            }
                            continue;
                        }

                        ProcessHeaderByte(direction, state, data[offset]);
                        offset++;
                    }
                }
                catch (Exception ex)
                {
                    FailAndReset(direction, state, $"文件接收失败：{ex.Message}");
                }
            }
        }

        private void ProcessHeaderByte(DataDirection direction, ReceiveState state, byte value)
        {
            switch (state.Phase)
            {
                case ReceivePhase.SeekingMagic:
                    if (value == FileTransferProtocol.Magic[state.MagicMatched])
                    {
                        state.MagicMatched++;
                        if (state.MagicMatched == FileTransferProtocol.Magic.Length)
                        {
                            state.MagicMatched = 0;
                            state.FixedHeaderCount = 0;
                            state.Phase = ReceivePhase.ReadingFixedHeader;
                        }
                    }
                    else
                    {
                        state.MagicMatched = value == FileTransferProtocol.Magic[0] ? 1 : 0;
                    }
                    break;

                case ReceivePhase.ReadingFixedHeader:
                    state.FixedHeader[state.FixedHeaderCount++] = value;
                    if (state.FixedHeaderCount == state.FixedHeader.Length)
                    {
                        long fileLength = BitConverter.ToInt64(state.FixedHeader, 0);
                        ushort fileNameLength = BitConverter.ToUInt16(state.FixedHeader, 8);
                        if (fileLength < 0 || fileLength > MaximumAcceptedFileLength ||
                            fileNameLength == 0 || fileNameLength > FileTransferProtocol.MaximumFileNameBytes)
                        {
                            ResetState(state);
                            return;
                        }

                        state.TotalBytes = fileLength;
                        state.ExpectedHash = new byte[FileTransferProtocol.HashLength];
                        Buffer.BlockCopy(state.FixedHeader, 10, state.ExpectedHash, 0, state.ExpectedHash.Length);
                        state.FileNameBytes = new byte[fileNameLength];
                        state.FileNameCount = 0;
                        state.Phase = ReceivePhase.ReadingFileName;
                    }
                    break;

                case ReceivePhase.ReadingFileName:
                    state.FileNameBytes[state.FileNameCount++] = value;
                    if (state.FileNameCount == state.FileNameBytes.Length)
                    {
                        string fileName = Encoding.UTF8.GetString(state.FileNameBytes);
                        BeginFile(direction, state, fileName);
                        if (state.TotalBytes == 0) CompleteFile(direction, state);
                    }
                    break;
            }
        }

        private void BeginFile(DataDirection direction, ReceiveState state, string fileName)
        {
            string outputDirectory = _outputDirectories[direction];
            if (string.IsNullOrWhiteSpace(outputDirectory)) outputDirectory = OutputDirectory;
            Directory.CreateDirectory(outputDirectory);
            string safeName = SanitizeFileName(fileName);
            string directionPrefix = direction == DataDirection.ChannelA_Rx ? "A_RX_" : "B_RX_";
            string targetFileName = directionPrefix + safeName;
            string targetPath = CreateUniqueTargetPath(outputDirectory, targetFileName);
            string partPath = targetPath + ".part";

            state.FileName = targetFileName;
            state.TargetPath = targetPath;
            state.PartPath = partPath;
            state.OutputStream = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            state.Hash = SHA256.Create();
            state.ReceivedBytes = 0;
            state.LastReportedBytes = 0;
            state.Phase = ReceivePhase.ReceivingFile;

            FileStarted?.Invoke(this, CreateEventArgs(direction, state, $"开始接收文件：{targetFileName}"));
        }

        private void ReportProgress(DataDirection direction, ReceiveState state)
        {
            long reportStep = Math.Max(256 * 1024, state.TotalBytes / 100);
            if (state.ReceivedBytes < state.TotalBytes && state.ReceivedBytes - state.LastReportedBytes < reportStep) return;
            state.LastReportedBytes = state.ReceivedBytes;
            FileProgress?.Invoke(this, CreateEventArgs(direction, state, "正在接收文件"));
        }

        private void CompleteFile(DataDirection direction, ReceiveState state)
        {
            state.Hash.TransformFinalBlock(new byte[0], 0, 0);
            byte[] actualHash = state.Hash.Hash;
            state.OutputStream.Flush();
            state.OutputStream.Dispose();
            state.OutputStream = null;
            state.Hash.Dispose();
            state.Hash = null;

            if (!actualHash.SequenceEqual(state.ExpectedHash))
            {
                string partPath = state.PartPath;
                var args = CreateEventArgs(direction, state, $"文件校验失败，未完成文件保留为：{partPath}");
                ResetState(state);
                FileFailed?.Invoke(this, args);
                return;
            }

            File.Move(state.PartPath, state.TargetPath);
            var completedArgs = CreateEventArgs(direction, state, $"文件接收完成：{state.TargetPath}");
            ResetState(state);
            FileCompleted?.Invoke(this, completedArgs);
        }

        private void FailAndReset(DataDirection direction, ReceiveState state, string message)
        {
            var args = CreateEventArgs(direction, state, message);
            ResetState(state);
            FileFailed?.Invoke(this, args);
        }

        private static FileTransferEventArgs CreateEventArgs(DataDirection direction, ReceiveState state, string message)
        {
            return new FileTransferEventArgs
            {
                Direction = direction,
                FileName = state.FileName,
                SavedPath = state.TargetPath,
                TotalBytes = state.TotalBytes,
                ReceivedBytes = state.ReceivedBytes,
                Message = message
            };
        }

        private static string SanitizeFileName(string fileName)
        {
            string safeName = Path.GetFileName(fileName ?? string.Empty);
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalidCharacter, '_');
            return string.IsNullOrWhiteSpace(safeName) ? "received-file.bin" : safeName;
        }

        private static string CreateUniqueTargetPath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 1;
            while (File.Exists(candidate) || File.Exists(candidate + ".part"))
            {
                candidate = Path.Combine(directory, $"{baseName}_{index++:000}{extension}");
            }
            return candidate;
        }

        private static void ResetState(ReceiveState state)
        {
            state.OutputStream?.Dispose();
            state.Hash?.Dispose();
            state.OutputStream = null;
            state.Hash = null;
            state.Phase = ReceivePhase.SeekingMagic;
            state.MagicMatched = 0;
            state.FixedHeaderCount = 0;
            state.FileNameBytes = null;
            state.FileNameCount = 0;
            state.TotalBytes = 0;
            state.ReceivedBytes = 0;
            state.LastReportedBytes = 0;
            state.ExpectedHash = null;
            state.FileName = null;
            state.TargetPath = null;
            state.PartPath = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (ReceiveState state in _states.Values)
            {
                lock (state.SyncRoot) ResetState(state);
            }
        }
    }

    /// <summary>
    /// 兼容普通串口/网络助手的“从文件发送”裸字节流。
    /// 发送端不提供文件头时，以一段连续接收数据作为一个文件，空闲超时后落盘。
    /// </summary>
    public sealed class RawBurstFileReceiver : IDisposable
    {
        private sealed class RawReceiveState
        {
            public readonly object SyncRoot = new object();
            public bool Enabled;
            public bool Suppressed;
            public string OutputDirectory;
            public string PartPath;
            public FileStream Stream;
            public Timer IdleTimer;
            public DateTime StartedAt;
            public long ReceivedBytes;
        }

        private const int FileEndIdleMilliseconds = 2000;
        private readonly Dictionary<DataDirection, RawReceiveState> _states =
            new Dictionary<DataDirection, RawReceiveState>
            {
                { DataDirection.ChannelA_Rx, new RawReceiveState() },
                { DataDirection.ChannelB_Rx, new RawReceiveState() }
            };
        private bool _disposed;

        public event EventHandler<FileTransferEventArgs> FileCompleted;
        public event EventHandler<FileTransferEventArgs> FileFailed;

        public void ConfigureDirection(DataDirection direction, bool enabled, string outputDirectory)
        {
            if (!_states.TryGetValue(direction, out RawReceiveState state)) return;

            lock (state.SyncRoot)
            {
                state.OutputDirectory = outputDirectory;
                state.Enabled = enabled;
                if (!enabled && state.Stream != null) CompleteLocked(direction, state);
            }
        }

        public void Process(DataDirection direction, byte[] data)
        {
            if (_disposed || data == null || data.Length == 0 ||
                !_states.TryGetValue(direction, out RawReceiveState state)) return;

            lock (state.SyncRoot)
            {
                if (!state.Enabled || state.Suppressed) return;

                try
                {
                    if (state.Stream == null) BeginLocked(direction, state);
                    state.Stream.Write(data, 0, data.Length);
                    state.ReceivedBytes += data.Length;
                    state.IdleTimer.Change(FileEndIdleMilliseconds, Timeout.Infinite);
                }
                catch (Exception ex)
                {
                    FailLocked(direction, state, $"原始文件保存失败：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 检测到 WPSFILE1 协议文件后，删除并暂停并行建立的裸流临时文件。
        /// </summary>
        public void Suppress(DataDirection direction)
        {
            if (!_states.TryGetValue(direction, out RawReceiveState state)) return;
            lock (state.SyncRoot)
            {
                state.Suppressed = true;
                DiscardLocked(state);
            }
        }

        public void Resume(DataDirection direction)
        {
            if (!_states.TryGetValue(direction, out RawReceiveState state)) return;
            lock (state.SyncRoot) state.Suppressed = false;
        }

        private void BeginLocked(DataDirection direction, RawReceiveState state)
        {
            string directory = state.OutputDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReceivedFiles");
            Directory.CreateDirectory(directory);

            string prefix = direction == DataDirection.ChannelA_Rx ? "A_RX" : "B_RX";
            state.StartedAt = DateTime.Now;
            state.PartPath = Path.Combine(
                directory,
                $".{prefix}_{state.StartedAt:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.part");
            state.Stream = new FileStream(
                state.PartPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            state.ReceivedBytes = 0;
            state.IdleTimer = new Timer(
                _ => CompleteFromTimer(direction),
                null,
                FileEndIdleMilliseconds,
                Timeout.Infinite);
        }

        private void CompleteFromTimer(DataDirection direction)
        {
            if (!_states.TryGetValue(direction, out RawReceiveState state)) return;
            lock (state.SyncRoot)
            {
                if (state.Stream != null) CompleteLocked(direction, state);
            }
        }

        private void CompleteLocked(DataDirection direction, RawReceiveState state)
        {
            string partPath = state.PartPath;
            long receivedBytes = state.ReceivedBytes;
            DateTime startedAt = state.StartedAt;

            try
            {
                state.IdleTimer?.Dispose();
                state.IdleTimer = null;
                state.Stream.Flush(true);
                state.Stream.Dispose();
                state.Stream = null;

                string extension = DetectFileExtension(partPath);
                string prefix = direction == DataDirection.ChannelA_Rx ? "A_RX" : "B_RX";
                string fileName = $"{prefix}_{startedAt:yyyyMMdd_HHmmss_fff}{extension}";
                string targetPath = CreateUniquePath(Path.GetDirectoryName(partPath), fileName);
                File.Move(partPath, targetPath);

                ResetValues(state);
                FileCompleted?.Invoke(this, new FileTransferEventArgs
                {
                    Direction = direction,
                    FileName = Path.GetFileName(targetPath),
                    SavedPath = targetPath,
                    TotalBytes = receivedBytes,
                    ReceivedBytes = receivedBytes,
                    Message = $"原始文件接收完成：{targetPath}"
                });
            }
            catch (Exception ex)
            {
                FailLocked(direction, state, $"原始文件保存失败：{ex.Message}");
            }
        }

        private void FailLocked(DataDirection direction, RawReceiveState state, string message)
        {
            string partPath = state.PartPath;
            long receivedBytes = state.ReceivedBytes;
            try { state.IdleTimer?.Dispose(); } catch { }
            try { state.Stream?.Dispose(); } catch { }
            state.IdleTimer = null;
            state.Stream = null;
            ResetValues(state);

            FileFailed?.Invoke(this, new FileTransferEventArgs
            {
                Direction = direction,
                SavedPath = partPath,
                TotalBytes = receivedBytes,
                ReceivedBytes = receivedBytes,
                Message = message
            });
        }

        private static void DiscardLocked(RawReceiveState state)
        {
            string partPath = state.PartPath;
            try { state.IdleTimer?.Dispose(); } catch { }
            try { state.Stream?.Dispose(); } catch { }
            state.IdleTimer = null;
            state.Stream = null;
            ResetValues(state);
            try
            {
                if (!string.IsNullOrWhiteSpace(partPath) && File.Exists(partPath))
                    File.Delete(partPath);
            }
            catch { }
        }

        private static void ResetValues(RawReceiveState state)
        {
            state.PartPath = null;
            state.ReceivedBytes = 0;
            state.StartedAt = default(DateTime);
        }

        private static string DetectFileExtension(string path)
        {
            var header = new byte[16];
            int count;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                count = stream.Read(header, 0, header.Length);

            if (count >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07) &&
                (header[3] == 0x04 || header[3] == 0x06 || header[3] == 0x08))
            {
                if (ContainsAscii(path, "word/")) return ".docx";
                if (ContainsAscii(path, "xl/")) return ".xlsx";
                if (ContainsAscii(path, "ppt/")) return ".pptx";
                return ".zip";
            }
            if (count >= 5 && header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D) return ".pdf";
            if (count >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47) return ".png";
            if (count >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return ".jpg";
            if (count >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46) return ".gif";
            if (count >= 8 && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0) return ".doc";
            return ".bin";
        }

        private static bool ContainsAscii(string path, string text)
        {
            byte[] pattern = Encoding.ASCII.GetBytes(text);
            int matched = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int value;
                while ((value = stream.ReadByte()) >= 0)
                {
                    if (value == pattern[matched])
                    {
                        matched++;
                        if (matched == pattern.Length) return true;
                    }
                    else
                    {
                        matched = value == pattern[0] ? 1 : 0;
                    }
                }
            }
            return false;
        }

        private static string CreateUniquePath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 1;
            while (File.Exists(candidate))
                candidate = Path.Combine(directory, $"{baseName}_{index++:000}{extension}");
            return candidate;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (KeyValuePair<DataDirection, RawReceiveState> pair in _states)
            {
                lock (pair.Value.SyncRoot)
                {
                    if (pair.Value.Stream != null) CompleteLocked(pair.Key, pair.Value);
                    pair.Value.IdleTimer?.Dispose();
                    pair.Value.IdleTimer = null;
                }
            }
        }
    }
}
