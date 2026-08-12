using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Interfaces;

namespace WpfProtocolStudio.Channels
{
    /// <summary>
    /// 基于周立功兼容 ControlCAN.dll API 的 CAN 通道。
    /// 驱动 DLL 可配置；同一设备的多个 CAN 通道通过引用计数共享设备句柄。
    /// </summary>
    public sealed class CanChannel : ICommunicationChannel
    {
        private const uint StatusOk = 1;
        private const uint ReceiveError = 0xFFFFFFFF;
        private const int ReceiveBatchSize = 100;

        private static readonly object DeviceSyncRoot = new object();
        private static readonly Dictionary<string, int> OpenDevices = new Dictionary<string, int>();

        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private IntPtr _nativeLibraryHandle = IntPtr.Zero;
        private bool _deviceAcquired;

        public string Name { get; set; } = "CAN 总线";
        public ChannelType ChannelType => ChannelType.Can;
        public ChannelStatus Status { get; private set; } = ChannelStatus.Disconnected;
        public long BytesReceived { get; private set; }
        public long BytesSent { get; private set; }

        public string DriverPath { get; set; } = "ControlCAN.dll";
        public uint DeviceType { get; set; } = 4;
        public uint DeviceIndex { get; set; }
        public uint ChannelIndex { get; set; }
        public int BaudRate { get; set; } = 500000;
        public string FilterId { get; set; } = string.Empty;
        public uint TransmitId { get; set; } = 0x123;
        public string LastError { get; private set; } = string.Empty;

        public event EventHandler<ChannelDataReceivedEventArgs> DataReceived;
        public event EventHandler<ChannelStatus> StatusChanged;

        public Task<bool> OpenAsync()
        {
            if (Status == ChannelStatus.Connected) return Task.FromResult(true);

            UpdateStatus(ChannelStatus.Connecting);
            LastError = string.Empty;

            try
            {
                if (!LoadDriver())
                {
                    UpdateStatus(ChannelStatus.Error);
                    return Task.FromResult(false);
                }

                if (!TryGetTiming(BaudRate, out byte timing0, out byte timing1))
                {
                    LastError = $"不支持的 CAN 波特率：{BaudRate}";
                    ReleaseDriver();
                    UpdateStatus(ChannelStatus.Error);
                    return Task.FromResult(false);
                }

                if (!AcquireDevice())
                {
                    LastError = $"无法打开 CAN 设备，设备类型={DeviceType}，设备索引={DeviceIndex}";
                    ReleaseDriver();
                    UpdateStatus(ChannelStatus.Error);
                    return Task.FromResult(false);
                }

                VciInitConfig config = BuildInitConfig(timing0, timing1);
                if (NativeMethods.VCI_InitCAN(DeviceType, DeviceIndex, ChannelIndex, ref config) != StatusOk)
                {
                    LastError = $"CAN 通道 {ChannelIndex} 初始化失败";
                    ReleaseDevice();
                    ReleaseDriver();
                    UpdateStatus(ChannelStatus.Error);
                    return Task.FromResult(false);
                }

                if (NativeMethods.VCI_StartCAN(DeviceType, DeviceIndex, ChannelIndex) != StatusOk)
                {
                    LastError = $"CAN 通道 {ChannelIndex} 启动失败";
                    ReleaseDevice();
                    ReleaseDriver();
                    UpdateStatus(ChannelStatus.Error);
                    return Task.FromResult(false);
                }

                _cts = new CancellationTokenSource();
                UpdateStatus(ChannelStatus.Connected);
                _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return Task.FromResult(true);
            }
            catch (BadImageFormatException)
            {
                LastError = "CAN 驱动位数与当前程序不匹配，请使用与程序架构一致的 ControlCAN.dll";
            }
            catch (DllNotFoundException)
            {
                LastError = $"未找到 CAN 驱动：{DriverPath}";
            }
            catch (EntryPointNotFoundException)
            {
                LastError = "CAN 驱动缺少 ControlCAN 标准入口函数";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            ReleaseDevice();
            ReleaseDriver();
            UpdateStatus(ChannelStatus.Error);
            return Task.FromResult(false);
        }

        public async Task CloseAsync()
        {
            if (Status == ChannelStatus.Disconnected && !_deviceAcquired) return;

            try
            {
                _cts?.Cancel();
                if (_receiveTask != null)
                {
                    await Task.WhenAny(_receiveTask, Task.Delay(500));
                }
                if (_deviceAcquired)
                {
                    try { NativeMethods.VCI_ResetCAN(DeviceType, DeviceIndex, ChannelIndex); } catch { }
                }
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _receiveTask = null;
                ReleaseDevice();
                ReleaseDriver();
                UpdateStatus(ChannelStatus.Disconnected);
            }
        }

        public Task<int> SendAsync(byte[] data)
        {
            if (Status != ChannelStatus.Connected || data == null || data.Length == 0)
                return Task.FromResult(0);

            int sentBytes = 0;
            try
            {
                while (sentBytes < data.Length)
                {
                    int payloadLength = Math.Min(8, data.Length - sentBytes);
                    VciCanObject frame = CreateCanObject();
                    frame.ID = TransmitId;
                    frame.ExternFlag = (byte)(TransmitId > 0x7FF ? 1 : 0);
                    frame.DataLen = (byte)payloadLength;
                    Buffer.BlockCopy(data, sentBytes, frame.Data, 0, payloadLength);

                    uint sentFrames = NativeMethods.VCI_Transmit(DeviceType, DeviceIndex, ChannelIndex, ref frame, 1);
                    if (sentFrames != 1) break;
                    sentBytes += payloadLength;
                }
                BytesSent += sentBytes;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            return Task.FromResult(sentBytes);
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            VciCanObject[] frames = new VciCanObject[ReceiveBatchSize];
            for (int i = 0; i < frames.Length; i++) frames[i] = CreateCanObject();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    uint count = NativeMethods.VCI_Receive(
                        DeviceType, DeviceIndex, ChannelIndex, frames, (uint)frames.Length, 50);

                    if (count == ReceiveError)
                    {
                        await Task.Delay(20, token);
                        continue;
                    }

                    int actualCount = (int)Math.Min(count, (uint)frames.Length);
                    for (int i = 0; i < actualCount; i++)
                    {
                        int length = Math.Min(frames[i].DataLen, (byte)8);
                        if (length <= 0) continue;

                        byte[] payload = new byte[length];
                        Buffer.BlockCopy(frames[i].Data, 0, payload, 0, length);
                        BytesReceived += length;
                        string endpoint = $"CAN{ChannelIndex + 1} ID=0x{frames[i].ID:X}";
                        DataReceived?.Invoke(this, new ChannelDataReceivedEventArgs(payload, endpoint));
                    }

                    if (actualCount == 0) await Task.Delay(2, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
            }
        }

        private bool LoadDriver()
        {
            string configuredPath = string.IsNullOrWhiteSpace(DriverPath) ? "ControlCAN.dll" : DriverPath.Trim();
            if (!Path.IsPathRooted(configuredPath))
            {
                string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configuredPath);
                if (File.Exists(localPath)) configuredPath = localPath;
            }

            if (!string.Equals(Path.GetFileName(configuredPath), "ControlCAN.dll", StringComparison.OrdinalIgnoreCase))
            {
                LastError = "当前 CAN 适配器要求驱动文件名为 ControlCAN.dll";
                return false;
            }

            _nativeLibraryHandle = NativeMethods.LoadLibrary(configuredPath);
            if (_nativeLibraryHandle == IntPtr.Zero)
            {
                LastError = $"无法加载 CAN 驱动：{configuredPath}";
                return false;
            }
            return true;
        }

        private void ReleaseDriver()
        {
            if (_nativeLibraryHandle == IntPtr.Zero) return;
            NativeMethods.FreeLibrary(_nativeLibraryHandle);
            _nativeLibraryHandle = IntPtr.Zero;
        }

        private string DeviceKey => $"{DeviceType}:{DeviceIndex}";

        private bool AcquireDevice()
        {
            lock (DeviceSyncRoot)
            {
                if (OpenDevices.TryGetValue(DeviceKey, out int references))
                {
                    OpenDevices[DeviceKey] = references + 1;
                    _deviceAcquired = true;
                    return true;
                }

                if (NativeMethods.VCI_OpenDevice(DeviceType, DeviceIndex, 0) != StatusOk) return false;
                OpenDevices[DeviceKey] = 1;
                _deviceAcquired = true;
                return true;
            }
        }

        private void ReleaseDevice()
        {
            if (!_deviceAcquired) return;
            lock (DeviceSyncRoot)
            {
                if (OpenDevices.TryGetValue(DeviceKey, out int references))
                {
                    references--;
                    if (references <= 0)
                    {
                        try { NativeMethods.VCI_CloseDevice(DeviceType, DeviceIndex); } catch { }
                        OpenDevices.Remove(DeviceKey);
                    }
                    else
                    {
                        OpenDevices[DeviceKey] = references;
                    }
                }
                _deviceAcquired = false;
            }
        }

        private VciInitConfig BuildInitConfig(byte timing0, byte timing1)
        {
            VciInitConfig config = new VciInitConfig
            {
                AccCode = 0,
                AccMask = 0xFFFFFFFF,
                Filter = 1,
                Timing0 = timing0,
                Timing1 = timing1,
                Mode = 0
            };

            if (TryParseCanId(FilterId, out uint filterId))
            {
                if (filterId <= 0x7FF)
                {
                    config.AccCode = filterId << 21;
                    config.AccMask = ~(0x7FFu << 21);
                }
                else
                {
                    config.AccCode = filterId << 3;
                    config.AccMask = ~(0x1FFFFFFFu << 3);
                }
            }
            return config;
        }

        private static bool TryParseCanId(string text, out uint id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            return uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out id) && id <= 0x1FFFFFFF;
        }

        private static bool TryGetTiming(int baudRate, out byte timing0, out byte timing1)
        {
            timing0 = 0;
            timing1 = 0;
            switch (baudRate)
            {
                case 1000000: timing0 = 0x00; timing1 = 0x14; return true;
                case 800000: timing0 = 0x00; timing1 = 0x16; return true;
                case 500000: timing0 = 0x00; timing1 = 0x1C; return true;
                case 250000: timing0 = 0x01; timing1 = 0x1C; return true;
                case 125000: timing0 = 0x03; timing1 = 0x1C; return true;
                case 100000: timing0 = 0x04; timing1 = 0x1C; return true;
                case 50000: timing0 = 0x09; timing1 = 0x1C; return true;
                case 20000: timing0 = 0x18; timing1 = 0x1C; return true;
                case 10000: timing0 = 0x31; timing1 = 0x1C; return true;
                default: return false;
            }
        }

        private static VciCanObject CreateCanObject()
        {
            return new VciCanObject
            {
                Data = new byte[8],
                Reserved = new byte[3]
            };
        }

        public void ResetStatistics()
        {
            BytesReceived = 0;
            BytesSent = 0;
        }

        private void UpdateStatus(ChannelStatus newStatus)
        {
            if (Status == newStatus) return;
            Status = newStatus;
            StatusChanged?.Invoke(this, newStatus);
        }

        public void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VciCanObject
        {
            public uint ID;
            public uint TimeStamp;
            public byte TimeFlag;
            public byte SendType;
            public byte RemoteFlag;
            public byte ExternFlag;
            public byte DataLen;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] Data;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VciInitConfig
        {
            public uint AccCode;
            public uint AccMask;
            public uint Reserved;
            public byte Filter;
            public byte Timing0;
            public byte Timing1;
            public byte Mode;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr LoadLibrary(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool FreeLibrary(IntPtr hModule);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_OpenDevice(uint deviceType, uint deviceIndex, uint reserved);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_CloseDevice(uint deviceType, uint deviceIndex);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_InitCAN(uint deviceType, uint deviceIndex, uint canIndex, ref VciInitConfig config);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_StartCAN(uint deviceType, uint deviceIndex, uint canIndex);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_ResetCAN(uint deviceType, uint deviceIndex, uint canIndex);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_Transmit(uint deviceType, uint deviceIndex, uint canIndex,
                ref VciCanObject send, uint length);

            [DllImport("ControlCAN.dll", CallingConvention = CallingConvention.StdCall)]
            public static extern uint VCI_Receive(uint deviceType, uint deviceIndex, uint canIndex,
                [In, Out] VciCanObject[] receive, uint length, int waitTime);
        }
    }
}
