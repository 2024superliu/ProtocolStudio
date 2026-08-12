using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// FR-27：按方向独立缓存数据，只改变显示记录的边界，不改变转发和日志原始数据。
    /// </summary>
    public sealed class DataFramingService : IDisposable
    {
        private sealed class DirectionState
        {
            public readonly object SyncRoot = new object();
            public readonly List<byte> Buffer = new List<byte>();
            public Timer IdleTimer;
            public string Description = string.Empty;
        }

        private const int MaximumBufferedBytes = 4 * 1024 * 1024;
        private readonly Dictionary<DataDirection, DirectionState> _states =
            Enum.GetValues(typeof(DataDirection))
                .Cast<DataDirection>()
                .ToDictionary(direction => direction, direction => new DirectionState());

        private readonly object _configurationLock = new object();
        private FrameMode _mode;
        private int _fixedLength = 8;
        private byte[] _delimiter = { 0x0D, 0x0A };
        private int _idleMilliseconds = 50;
        private bool _disposed;

        public event EventHandler<ForwardingDataEventArgs> FrameReady;

        public void Configure(FrameMode mode, int fixedLength, byte[] delimiter, int idleMilliseconds)
        {
            lock (_configurationLock)
            {
                _mode = mode;
                _fixedLength = fixedLength;
                _delimiter = delimiter == null ? new byte[0] : (byte[])delimiter.Clone();
                _idleMilliseconds = idleMilliseconds;
            }
            ResetAll();
        }

        public void Process(ForwardingDataEventArgs record)
        {
            if (_disposed || record == null || record.Data == null || record.Data.Length == 0) return;

            FrameMode mode;
            int fixedLength;
            byte[] delimiter;
            int idleMilliseconds;
            lock (_configurationLock)
            {
                mode = _mode;
                fixedLength = _fixedLength;
                delimiter = _delimiter;
                idleMilliseconds = _idleMilliseconds;
            }

            if (mode == FrameMode.None)
            {
                RaiseFrame(record.Direction, record.Data, record.Description);
                return;
            }

            DirectionState state = _states[record.Direction];
            var frames = new List<byte[]>();
            string description;

            lock (state.SyncRoot)
            {
                if (state.Buffer.Count == 0) state.Description = record.Description ?? string.Empty;
                state.Buffer.AddRange(record.Data);
                description = state.Description;

                switch (mode)
                {
                    case FrameMode.FixedLength:
                        while (state.Buffer.Count >= fixedLength)
                            frames.Add(TakeFromStart(state.Buffer, fixedLength));
                        break;

                    case FrameMode.Delimiter:
                        int delimiterIndex;
                        while ((delimiterIndex = FindDelimiter(state.Buffer, delimiter)) >= 0)
                            frames.Add(TakeFromStart(state.Buffer, delimiterIndex + delimiter.Length));
                        break;

                    case FrameMode.TimeInterval:
                        if (state.IdleTimer == null)
                        {
                            state.IdleTimer = new Timer(
                                _ => FlushDirection(record.Direction),
                                null,
                                idleMilliseconds,
                                Timeout.Infinite);
                        }
                        else
                        {
                            state.IdleTimer.Change(idleMilliseconds, Timeout.Infinite);
                        }
                        break;
                }

                if (state.Buffer.Count >= MaximumBufferedBytes)
                    frames.Add(TakeFromStart(state.Buffer, state.Buffer.Count));

                if (state.Buffer.Count == 0) state.Description = string.Empty;
            }

            foreach (byte[] frame in frames)
                RaiseFrame(record.Direction, frame, description);
        }

        public void FlushAll()
        {
            foreach (DataDirection direction in _states.Keys.ToArray())
                FlushDirection(direction);
        }

        public void ResetAll()
        {
            foreach (DirectionState state in _states.Values)
            {
                lock (state.SyncRoot)
                {
                    state.IdleTimer?.Dispose();
                    state.IdleTimer = null;
                    state.Buffer.Clear();
                    state.Description = string.Empty;
                }
            }
        }

        private void FlushDirection(DataDirection direction)
        {
            if (_disposed) return;
            DirectionState state = _states[direction];
            byte[] frame = null;
            string description = string.Empty;

            lock (state.SyncRoot)
            {
                state.IdleTimer?.Dispose();
                state.IdleTimer = null;
                if (state.Buffer.Count > 0)
                {
                    frame = TakeFromStart(state.Buffer, state.Buffer.Count);
                    description = state.Description;
                    state.Description = string.Empty;
                }
            }

            if (frame != null) RaiseFrame(direction, frame, description);
        }

        private void RaiseFrame(DataDirection direction, byte[] data, string description)
        {
            FrameReady?.Invoke(this, new ForwardingDataEventArgs(direction, data, description));
        }

        private static byte[] TakeFromStart(List<byte> buffer, int count)
        {
            byte[] result = buffer.GetRange(0, count).ToArray();
            buffer.RemoveRange(0, count);
            return result;
        }

        private static int FindDelimiter(List<byte> buffer, byte[] delimiter)
        {
            if (delimiter == null || delimiter.Length == 0 || buffer.Count < delimiter.Length) return -1;
            for (int index = 0; index <= buffer.Count - delimiter.Length; index++)
            {
                bool matches = true;
                for (int offset = 0; offset < delimiter.Length; offset++)
                {
                    if (buffer[index + offset] == delimiter[offset]) continue;
                    matches = false;
                    break;
                }
                if (matches) return index;
            }
            return -1;
        }

        public void Dispose()
        {
            if (_disposed) return;
            FlushAll();
            _disposed = true;
            ResetAll();
        }
    }
}
