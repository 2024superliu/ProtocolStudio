using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Channels;
using WpfProtocolStudio.Engine;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Events;
using WpfProtocolStudio.Helpers;
using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;
using WpfProtocolStudio.Services;
using Microsoft.Win32;
using System.IO; // 引入 Windows 文件对话框

namespace WpfProtocolStudio.ViewModels
{
    public class MainViewModel : ViewModelBase, IDisposable
    {
        private sealed class ProtocolParseWorkItem
        {
            public DateTime Timestamp { get; set; }
            public DataDirection Direction { get; set; }
            public byte[] Data { get; set; }
            public IProtocolParser Parser { get; set; }
        }

        public ForwardingEngine Engine { get; } = new ForwardingEngine();
        public LogService LogService { get; } = new LogService();
        private readonly DispatcherTimer _statsTimer;
        private readonly DispatcherTimer _uiFlushTimer;
        private readonly ConcurrentQueue<ForwardingDataEventArgs> _pendingUiRecords = new ConcurrentQueue<ForwardingDataEventArgs>();
        private int _pendingUiRecordCount;
        private readonly ConcurrentQueue<ProtocolParseWorkItem> _protocolParseQueue = new ConcurrentQueue<ProtocolParseWorkItem>();
        private readonly SemaphoreSlim _protocolParseSignal = new SemaphoreSlim(0);
        private readonly CancellationTokenSource _protocolParseCts = new CancellationTokenSource();
        private Task _protocolParseWorker;
        private int _protocolParseQueueCount;
        private CancellationTokenSource _historySearchCts;
        private CancellationTokenSource _fileSendCts;
        private readonly FileTransferReceiver _fileReceiver = new FileTransferReceiver();
        private readonly RawBurstFileReceiver _rawBurstFileReceiver = new RawBurstFileReceiver();
        private readonly DataFramingService _dataFramingService = new DataFramingService();
        private string _rxFileDirectory;
        private string[] _historySelectedFiles = new string[0];
        private bool _disposed;
        // 4 个 UI 数据显示集合
        public RangeObservableCollection<DataRecord> ChannelARxRecords { get; } = new RangeObservableCollection<DataRecord>();
        public RangeObservableCollection<DataRecord> ChannelATxRecords { get; } = new RangeObservableCollection<DataRecord>();
        public RangeObservableCollection<DataRecord> ChannelBRxRecords { get; } = new RangeObservableCollection<DataRecord>();
        public RangeObservableCollection<DataRecord> ChannelBTxRecords { get; } = new RangeObservableCollection<DataRecord>();
        public RangeObservableCollection<HistoryDataRecord> HistorySearchResults { get; } = new RangeObservableCollection<HistoryDataRecord>();
        public RangeObservableCollection<ProtocolDecodedRecord> RealtimeProtocolResults { get; } = new RangeObservableCollection<ProtocolDecodedRecord>();
        // 可选通信类型与串口全参数下拉列表
        public Array ChannelTypes => Enum.GetValues(typeof(ChannelType));
        public string[] AvailablePorts
        {
            get
            {
                var ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();
                return ports.Length > 0 ? ports : new string[] { "COM1", "COM2", "COM3", "COM4" };
            }
        }
        public int[] AvailableBaudRates => new int[] { 9600, 19200, 38400, 57600, 115200, 921600 };
        public int[] AvailableDataBits => new int[] { 8, 7, 6, 5 };
        public Array AvailableStopBits => Enum.GetValues(typeof(StopBits));
        public Array AvailableParities => Enum.GetValues(typeof(Parity));

        // --- A 端配置属性 ---
        private ChannelType _selectedTypeA = ChannelType.TcpServer;
        public ChannelType SelectedTypeA { get => _selectedTypeA; set { if (SetProperty(ref _selectedTypeA, value)) AutoCloseChannelAIfOpen(); } }
        private string _portA = "8080";
        public string PortA { get => _portA; set { if (SetProperty(ref _portA, value)) AutoCloseChannelAIfOpen(); } }
        private string _localPortA = "9000";
        public string LocalPortA { get => _localPortA; set { if (SetProperty(ref _localPortA, value)) AutoCloseChannelAIfOpen(); } }
        private string _ipA = "127.0.0.1";
        public string IpA { get => _ipA; set { if (SetProperty(ref _ipA, value)) AutoCloseChannelAIfOpen(); } }
        private string _comPortA = "COM1";
        public string ComPortA { get => _comPortA; set { if (SetProperty(ref _comPortA, value)) AutoCloseChannelAIfOpen(); } }
        private int _baudRateA = 9600;
        public int BaudRateA { get => _baudRateA; set { if (SetProperty(ref _baudRateA, value)) AutoCloseChannelAIfOpen(); } }
        private int _dataBitsA = 8;
        public int DataBitsA { get => _dataBitsA; set { if (SetProperty(ref _dataBitsA, value)) AutoCloseChannelAIfOpen(); } }
        private StopBits _stopBitsA = StopBits.One;
        public StopBits StopBitsA { get => _stopBitsA; set { if (SetProperty(ref _stopBitsA, value)) AutoCloseChannelAIfOpen(); } }
        private Parity _parityA = Parity.None;
        public Parity ParityA { get => _parityA; set { if (SetProperty(ref _parityA, value)) AutoCloseChannelAIfOpen(); } }

        private string _statusTextA = "A端: 断开";
        public string StatusTextA { get => _statusTextA; set => SetProperty(ref _statusTextA, value); }
        private string _statsTextA = "RX: 0 B | TX: 0 B";
        public string StatsTextA { get => _statsTextA; set => SetProperty(ref _statsTextA, value); }
        private bool _isChannelAOpen;
        public bool IsChannelAOpen { get => _isChannelAOpen; set => SetProperty(ref _isChannelAOpen, value); }
        private string _btnTextA = "打开通道";
        public string BtnTextA { get => _btnTextA; set => SetProperty(ref _btnTextA, value); }

        // --- B 端配置属性 ---
        private ChannelType _selectedTypeB = ChannelType.TcpClient;
        public ChannelType SelectedTypeB { get => _selectedTypeB; set { if (SetProperty(ref _selectedTypeB, value)) AutoCloseChannelBIfOpen(); } }
        private string _ipB = "127.0.0.1";
        public string IpB { get => _ipB; set { if (SetProperty(ref _ipB, value)) AutoCloseChannelBIfOpen(); } }
        private string _portB = "8080";
        public string PortB { get => _portB; set { if (SetProperty(ref _portB, value)) AutoCloseChannelBIfOpen(); } }
        private string _localPortB = "9000";
        public string LocalPortB { get => _localPortB; set { if (SetProperty(ref _localPortB, value)) AutoCloseChannelBIfOpen(); } }
        private string _comPortB = "COM2";
        public string ComPortB { get => _comPortB; set { if (SetProperty(ref _comPortB, value)) AutoCloseChannelBIfOpen(); } }
        private int _baudRateB = 9600;
        public int BaudRateB { get => _baudRateB; set { if (SetProperty(ref _baudRateB, value)) AutoCloseChannelBIfOpen(); } }
        private int _dataBitsB = 8;
        public int DataBitsB { get => _dataBitsB; set { if (SetProperty(ref _dataBitsB, value)) AutoCloseChannelBIfOpen(); } }
        private StopBits _stopBitsB = StopBits.One;
        public StopBits StopBitsB { get => _stopBitsB; set { if (SetProperty(ref _stopBitsB, value)) AutoCloseChannelBIfOpen(); } }
        private Parity _parityB = Parity.None;
        public Parity ParityB { get => _parityB; set { if (SetProperty(ref _parityB, value)) AutoCloseChannelBIfOpen(); } }

        // CAN 参数 (FR-3)
        private string _canInterfaceA = "CAN1";
        public string CanInterfaceA { get => _canInterfaceA; set { if (SetProperty(ref _canInterfaceA, value)) AutoCloseChannelAIfOpen(); } }
        private int _canBaudRateA = 500000;
        public int CanBaudRateA { get => _canBaudRateA; set { if (SetProperty(ref _canBaudRateA, value)) AutoCloseChannelAIfOpen(); } }
        private string _canFilterA = "";
        public string CanFilterA { get => _canFilterA; set { if (SetProperty(ref _canFilterA, value)) AutoCloseChannelAIfOpen(); } }
        private string _canDriverPathA = "ControlCAN.dll";
        public string CanDriverPathA { get => _canDriverPathA; set { if (SetProperty(ref _canDriverPathA, value)) AutoCloseChannelAIfOpen(); } }
        private int _canDeviceTypeA = 4;
        public int CanDeviceTypeA { get => _canDeviceTypeA; set { if (SetProperty(ref _canDeviceTypeA, value)) AutoCloseChannelAIfOpen(); } }
        private int _canDeviceIndexA;
        public int CanDeviceIndexA { get => _canDeviceIndexA; set { if (SetProperty(ref _canDeviceIndexA, value)) AutoCloseChannelAIfOpen(); } }
        private string _canTransmitIdA = "0x123";
        public string CanTransmitIdA { get => _canTransmitIdA; set { if (SetProperty(ref _canTransmitIdA, value)) AutoCloseChannelAIfOpen(); } }

        private string _canInterfaceB = "CAN2";
        public string CanInterfaceB { get => _canInterfaceB; set { if (SetProperty(ref _canInterfaceB, value)) AutoCloseChannelBIfOpen(); } }
        private int _canBaudRateB = 500000;
        public int CanBaudRateB { get => _canBaudRateB; set { if (SetProperty(ref _canBaudRateB, value)) AutoCloseChannelBIfOpen(); } }
        private string _canFilterB = "";
        public string CanFilterB { get => _canFilterB; set { if (SetProperty(ref _canFilterB, value)) AutoCloseChannelBIfOpen(); } }
        private string _canDriverPathB = "ControlCAN.dll";
        public string CanDriverPathB { get => _canDriverPathB; set { if (SetProperty(ref _canDriverPathB, value)) AutoCloseChannelBIfOpen(); } }
        private int _canDeviceTypeB = 4;
        public int CanDeviceTypeB { get => _canDeviceTypeB; set { if (SetProperty(ref _canDeviceTypeB, value)) AutoCloseChannelBIfOpen(); } }
        private int _canDeviceIndexB;
        public int CanDeviceIndexB { get => _canDeviceIndexB; set { if (SetProperty(ref _canDeviceIndexB, value)) AutoCloseChannelBIfOpen(); } }
        private string _canTransmitIdB = "0x123";
        public string CanTransmitIdB { get => _canTransmitIdB; set { if (SetProperty(ref _canTransmitIdB, value)) AutoCloseChannelBIfOpen(); } }

        public int[] CanBaudRateOptions => new[] { 125000, 250000, 500000, 1000000 };
        public string[] CanInterfaceOptions => new[] { "CAN1", "CAN2", "CAN3", "CAN4" };

        private string _statusTextB = "B端: 断开";
        public string StatusTextB { get => _statusTextB; set => SetProperty(ref _statusTextB, value); }
        private string _statsTextB = "RX: 0 B | TX: 0 B";
        public string StatsTextB { get => _statsTextB; set => SetProperty(ref _statsTextB, value); }
        private bool _isChannelBOpen;
        public bool IsChannelBOpen { get => _isChannelBOpen; set => SetProperty(ref _isChannelBOpen, value); }
        private string _btnTextB = "打开通道";
        public string BtnTextB { get => _btnTextB; set => SetProperty(ref _btnTextB, value); }

        // 保存日志
        private bool _saveARxLog = false;
        public bool SaveARxLog { get => _saveARxLog; set => SetProperty(ref _saveARxLog, value); }
        private bool _saveATxLog = false;
        public bool SaveATxLog { get => _saveATxLog; set => SetProperty(ref _saveATxLog, value); }
        private bool _saveBRxLog = false;
        public bool SaveBRxLog { get => _saveBRxLog; set => SetProperty(ref _saveBRxLog, value); }
        private bool _saveBTxLog = false;
        public bool SaveBTxLog { get => _saveBTxLog; set => SetProperty(ref _saveBTxLog, value); }

        // 选择 A/B 接收文件保存文件夹；每个协议文件按原文件名分别落盘。
        private bool _saveARxFile;
        public bool SaveARxFile
        {
            get => _saveARxFile;
            set
            {
                // 保存
                if (_saveARxFile == value) return;
                // 选择文件夹
                if (value && string.IsNullOrWhiteSpace(_rxFileDirectory) && !SelectRawReceiveDirectory())
                {
                    OnPropertyChanged(nameof(SaveARxFile));
                    return;
                }
                // 如果已经设置就不需要在选择
                if (SetProperty(ref _saveARxFile, value))
                {
                    _fileReceiver.ConfigureDirection(DataDirection.ChannelA_Rx, value, _rxFileDirectory);
                    _rawBurstFileReceiver.ConfigureDirection(DataDirection.ChannelA_Rx, value, _rxFileDirectory);
                }
            }
        }

        private bool _saveBRxFile;
        public bool SaveBRxFile
        {
            get => _saveBRxFile;
            set
            {
                if (_saveBRxFile == value) return;
                if (value && string.IsNullOrWhiteSpace(_rxFileDirectory) && !SelectRawReceiveDirectory())
                {
                    OnPropertyChanged(nameof(SaveBRxFile));
                    return;
                }

                if (SetProperty(ref _saveBRxFile, value))
                {
                    _fileReceiver.ConfigureDirection(DataDirection.ChannelB_Rx, value, _rxFileDirectory);
                    _rawBurstFileReceiver.ConfigureDirection(DataDirection.ChannelB_Rx, value, _rxFileDirectory);
                }
            }
        }

        // 自动保存总开关 (FR-18)
        private bool _isAutoSaveEnabled;
        public bool IsAutoSaveEnabled
        {
            get => _isAutoSaveEnabled;
            set
            {
                if (_isAutoSaveEnabled == value) return;
                if (value && !TrySelectAutoSaveLogDirectory(false))
                {
                    OnPropertyChanged(nameof(IsAutoSaveEnabled));
                    return;
                }
                SetProperty(ref _isAutoSaveEnabled, value);
            }
        }

        // 自动保存日志文件目录配置
        private string _autoSaveLogDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        public string AutoSaveLogDirectory
        {
            get => _autoSaveLogDirectory;
            set
            {
                if (SetProperty(ref _autoSaveLogDirectory, value))
                {
                    if (LogService != null)
                    {
                        LogService.LogDirectory = value;
                    }
                }
            }
        }

        // 转发控制与发送区属性
        public bool IsForwarding
        {
            get => Engine.IsForwardingEnabled;
            set
            {
                Engine.IsForwardingEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ForwardingStatusText));// 同步更新文字
            }
        }

        public string ForwardingStatusText => IsForwarding ? "双向转发运行中" : "双向转发已暂停";

        public Array DisconnectStrategies => Enum.GetValues(typeof(DisconnectStrategy));
        private DisconnectStrategy _selectedDisconnectStrategy = DisconnectStrategy.Discard;
        public DisconnectStrategy SelectedDisconnectStrategy
        {
            get => _selectedDisconnectStrategy;
            set
            {
                if (SetProperty(ref _selectedDisconnectStrategy, value))
                    Engine.StrategyOnDisconnect = value;
            }
        }

        // 显示格式列表
        public Array DisplayFormats => Enum.GetValues(typeof(DisplayFormat));

        private DisplayFormat _selectedDisplayFormat = DisplayFormat.Hex;
        // 当前选中的数据格式 （绑定下拉框）
        public DisplayFormat SelectDisplayFormat
        {
            get => _selectedDisplayFormat;
            set
            {
                if (SetProperty(ref _selectedDisplayFormat, value))
                {
                    OnPropertyChanged(nameof(DataContentColumnHeader));
                }
            }
        }

        /// <summary>
        /// 动态列标题文本 (FR-13，如 "数据内容 (Hex)")
        /// </summary>
        public string DataContentColumnHeader => $"数据内容 ({SelectDisplayFormat})";

        private bool _isDisplayPaused = false;
        /// <summary>
        /// 暂停界面刷屏显示 (FR-14)
        /// </summary>
        public bool IsDisplayPaused
        {
            get => _isDisplayPaused;
            set => SetProperty(ref _isDisplayPaused, value);
        }

        // FR-27 显示分帧配置。四个数据方向共用配置，但各自独立缓存。
        public Array FrameModes => Enum.GetValues(typeof(FrameMode));
        private FrameMode _selectedFrameMode = FrameMode.None;
        public FrameMode SelectedFrameMode
        {
            get => _selectedFrameMode;
            set
            {
                if (SetProperty(ref _selectedFrameMode, value))
                    ApplyFramingConfiguration();
            }
        }
        private int _frameFixedLength = 8;
        public int FrameFixedLength
        {
            get => _frameFixedLength;
            set
            {
                if (SetProperty(ref _frameFixedLength, value) && SelectedFrameMode == FrameMode.FixedLength)
                    ApplyFramingConfiguration();
            }
        }
        private string _frameDelimiterText = "0D 0A";
        public string FrameDelimiterText
        {
            get => _frameDelimiterText;
            set
            {
                if (SetProperty(ref _frameDelimiterText, value) && SelectedFrameMode == FrameMode.Delimiter)
                    ApplyFramingConfiguration();
            }
        }
        private bool _isFrameDelimiterHex = true;
        public bool IsFrameDelimiterHex
        {
            get => _isFrameDelimiterHex;
            set
            {
                if (SetProperty(ref _isFrameDelimiterHex, value))
                {
                    OnPropertyChanged(nameof(IsFrameDelimiterText));
                    if (SelectedFrameMode == FrameMode.Delimiter)
                        ApplyFramingConfiguration();
                }
            }
        }
        public bool IsFrameDelimiterText { get => !IsFrameDelimiterHex; set => IsFrameDelimiterHex = !value; }
        private int _frameIdleMilliseconds = 50;
        public int FrameIdleMilliseconds
        {
            get => _frameIdleMilliseconds;
            set
            {
                if (SetProperty(ref _frameIdleMilliseconds, value) && SelectedFrameMode != FrameMode.None)
                    ApplyFramingConfiguration();
            }
        }
        private string _frameConfigurationStatus = "当前按底层接收块显示，尚未启用重新分帧";
        public string FrameConfigurationStatus { get => _frameConfigurationStatus; set => SetProperty(ref _frameConfigurationStatus, value); }

        // FR-28 CRC辅助计算。
        public Array ChecksumAlgorithms => Enum.GetValues(typeof(ChecksumAlgorithm));
        private ChecksumAlgorithm _selectedChecksumAlgorithm = ChecksumAlgorithm.Crc16Modbus;
        public ChecksumAlgorithm SelectedChecksumAlgorithm { get => _selectedChecksumAlgorithm; set => SetProperty(ref _selectedChecksumAlgorithm, value); }
        private string _checksumInput = "01 03 00 00 00 02";
        public string ChecksumInput { get => _checksumInput; set => SetProperty(ref _checksumInput, value); }
        private bool _isChecksumInputHex = true;
        public bool IsChecksumInputHex
        {
            get => _isChecksumInputHex;
            set
            {
                if (SetProperty(ref _isChecksumInputHex, value))
                    OnPropertyChanged(nameof(IsChecksumInputText));
            }
        }
        public bool IsChecksumInputText { get => !IsChecksumInputHex; set => IsChecksumInputHex = !value; }
        private string _checksumResult = "等待计算";
        public string ChecksumResult { get => _checksumResult; set => SetProperty(ref _checksumResult, value); }

        private bool _isAutoReceiveChecksumEnabled;
        public bool IsAutoReceiveChecksumEnabled
        {
            get => _isAutoReceiveChecksumEnabled;
            set
            {
                if (SetProperty(ref _isAutoReceiveChecksumEnabled, value))
                    OnPropertyChanged(nameof(CrcValidationSummary));
            }
        }

        private bool _hideInvalidChecksumFrames;
        public bool HideInvalidChecksumFrames
        {
            get => _hideInvalidChecksumFrames;
            set => SetProperty(ref _hideInvalidChecksumFrames, value);
        }

        private ChecksumAlgorithm _selectedReceiveChecksumAlgorithm = ChecksumAlgorithm.Crc16Modbus;
        public ChecksumAlgorithm SelectedReceiveChecksumAlgorithm
        {
            get => _selectedReceiveChecksumAlgorithm;
            set
            {
                if (SetProperty(ref _selectedReceiveChecksumAlgorithm, value))
                    ExecuteResetCrcValidationStatistics();
            }
        }

        private bool _isReceiveChecksumHighByteFirst;
        public bool IsReceiveChecksumHighByteFirst
        {
            get => _isReceiveChecksumHighByteFirst;
            set
            {
                if (SetProperty(ref _isReceiveChecksumHighByteFirst, value))
                    ExecuteResetCrcValidationStatistics();
            }
        }

        private long _crcValidFrameCount;
        private long _crcInvalidFrameCount;
        public long CrcValidFrameCount => Interlocked.Read(ref _crcValidFrameCount);
        public long CrcInvalidFrameCount => Interlocked.Read(ref _crcInvalidFrameCount);
        public string CrcValidationSummary => IsAutoReceiveChecksumEnabled
            ? $"CRC正确: {CrcValidFrameCount} 帧 | CRC错误: {CrcInvalidFrameCount} 帧"
            : "接收CRC自动验证：未启用";

        // FR-29 协议解析插件。
        public ObservableCollection<IProtocolParser> ProtocolParsers { get; } = new ObservableCollection<IProtocolParser>();
        private IProtocolParser _selectedProtocolParser;
        public IProtocolParser SelectedProtocolParser
        {
            get => _selectedProtocolParser;
            set
            {
                if (SetProperty(ref _selectedProtocolParser, value))
                {
                    OnPropertyChanged(nameof(SelectedProtocolParserDescription));
                    if (IsRealtimeProtocolParsingEnabled)
                        RealtimeProtocolStatus = $"实时解析已启用：{value?.Name ?? "尚未选择解析器"}";
                }
            }
        }
        public string SelectedProtocolParserDescription => SelectedProtocolParser?.Description ?? "请选择协议解析器";
        private string _protocolInput = "01 03 00 00 00 02";
        public string ProtocolInput { get => _protocolInput; set => SetProperty(ref _protocolInput, value); }
        private bool _isProtocolInputHex = true;
        public bool IsProtocolInputHex
        {
            get => _isProtocolInputHex;
            set
            {
                if (SetProperty(ref _isProtocolInputHex, value))
                    OnPropertyChanged(nameof(IsProtocolInputText));
            }
        }
        public bool IsProtocolInputText { get => !IsProtocolInputHex; set => IsProtocolInputHex = !value; }
        private string _protocolParseOutput = "等待解析";
        public string ProtocolParseOutput { get => _protocolParseOutput; set => SetProperty(ref _protocolParseOutput, value); }
        private string _protocolPluginStatus = "尚未加载协议插件";
        public string ProtocolPluginStatus { get => _protocolPluginStatus; set => SetProperty(ref _protocolPluginStatus, value); }

        private bool _isRealtimeProtocolParsingEnabled;
        public bool IsRealtimeProtocolParsingEnabled
        {
            get => _isRealtimeProtocolParsingEnabled;
            set
            {
                if (SetProperty(ref _isRealtimeProtocolParsingEnabled, value))
                {
                    RealtimeProtocolStatus = value
                        ? $"实时解析已启用：{SelectedProtocolParser?.Name ?? "尚未选择解析器"}"
                        : "实时解析未启用";
                }
            }
        }

        private string _realtimeProtocolStatus = "实时解析未启用";
        public string RealtimeProtocolStatus
        {
            get => _realtimeProtocolStatus;
            set => SetProperty(ref _realtimeProtocolStatus, value);
        }

        private string _filterKeyword = string.Empty;

        // 自动保存日志格式枚举 (FR-19)
        public Array LogFileFormats => Enum.GetValues(typeof(LogFileFormat));
        // 发送格式定义(FR-22)
        private bool _isSendHex = false;
        /// <summary>
        /// 发送模式：false 为 ASCII 字符串模式，true 为 HEX 十六进制模式 (FR-22)
        /// </summary>
        public bool IsSendHex
        {
            get => _isSendHex;
            set
            {
                if(SetProperty(ref _isSendHex, value))
                {
                    OnPropertyChanged(nameof(IsSendAscii));
                }
            }
        }
        public bool IsSendAscii
        {
            get => !_isSendHex;
            set => IsSendHex = !value;
        }
        private bool TryParseHexString(string input, out byte[] bytes, out string errorMessage)
        {
            bytes = null;
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                errorMessage = "发送内容不能为空！";
                return false;
            }
            // 1. 去除所有空格与换行符
            string cleanHex = input.Replace(" ", "").Replace("\r", "").Replace("\n", "").Replace("\t", "");
            // 2. 正则校验：必须全部为 0-9, a-f, A-F
            if (!System.Text.RegularExpressions.Regex.IsMatch(cleanHex, @"^[0-9a-fA-F]+$"))
            {
                errorMessage = "HEX 输入格式不合法！只能包含 0-9, A-F, a-f 及空格。";
                return false;
            }
            // 3. 处理奇数位情况
            if (cleanHex.Length % 2 != 0)
            {
                errorMessage = "HEX 输入必须由完整字节组成，每个字节需要两位十六进制字符。";
                return false;
            }
            // 4. 解析为 byte 数组
            try
            {
                bytes = new byte[cleanHex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(cleanHex.Substring(i * 2, 2), 16);
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"HEX 字节解析失败: {ex.Message}";
                return false;
            }
        }
        public LogFileFormat SelectedAutoSaveFormat
        {
            get => LogService.SaveFormat;
            set
            {
                if (LogService.SaveFormat != value)
                {
                    LogService.SaveFormat = value;
                    OnAutoSaveFormatChanged();
                }
            }
        }

        public bool IsAutoSaveTxt
        {
            get => LogService.SaveFormat == LogFileFormat.TXT;
            set { if (value) { LogService.SaveFormat = LogFileFormat.TXT; OnAutoSaveFormatChanged(); } }
        }

        public bool IsAutoSaveCsv
        {
            get => LogService.SaveFormat == LogFileFormat.CSV;
            set { if (value) { LogService.SaveFormat = LogFileFormat.CSV; OnAutoSaveFormatChanged(); } }
        }

        public bool IsAutoSaveBin
        {
            get => LogService.SaveFormat == LogFileFormat.BIN;
            set { if (value) { LogService.SaveFormat = LogFileFormat.BIN; OnAutoSaveFormatChanged(); } }
        }

        private void OnAutoSaveFormatChanged()
        {
            OnPropertyChanged(nameof(IsAutoSaveTxt));
            OnPropertyChanged(nameof(IsAutoSaveCsv));
            OnPropertyChanged(nameof(IsAutoSaveBin));
            OnPropertyChanged(nameof(SelectedAutoSaveFormat));
        }
        /// <summary>
        /// 关键字检索与过滤 (FR-14，支持检索 HEX、ASCII、时间戳、备注)
        /// </summary>
        public string FilterKeyword
        {
            get => _filterKeyword;
            set
            {
                if (SetProperty(ref _filterKeyword, value))
                {
                    ApplyFilter();
                }
            }
        }

        /// <summary>
        /// 实时过滤与搜索历史数据 (FR-14)
        /// </summary>
        private void ApplyFilter()
        {
            var viewARx = System.Windows.Data.CollectionViewSource.GetDefaultView(ChannelARxRecords);
            var viewATx = System.Windows.Data.CollectionViewSource.GetDefaultView(ChannelATxRecords);
            var viewBRx = System.Windows.Data.CollectionViewSource.GetDefaultView(ChannelBRxRecords);
            var viewBTx = System.Windows.Data.CollectionViewSource.GetDefaultView(ChannelBTxRecords);

            Predicate<object> filter = item =>
            {
                if (string.IsNullOrWhiteSpace(FilterKeyword)) return true;
                if (item is DataRecord r)
                {
                    string kw = FilterKeyword.Trim();
                    return (r.HexContent != null && r.HexContent.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.AsciiContent != null && r.AsciiContent.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.BinaryContent != null && r.BinaryContent.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.Description != null && r.Description.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (r.TimeString != null && r.TimeString.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                return true;
            };

            if (viewARx != null) viewARx.Filter = filter;
            if (viewATx != null) viewATx.Filter = filter;
            if (viewBRx != null) viewBRx.Filter = filter;
            if (viewBTx != null) viewBTx.Filter = filter;
        }

        private string _historyFileSummary = "尚未选择历史日志文件";
        public string HistoryFileSummary
        {
            get => _historyFileSummary;
            set => SetProperty(ref _historyFileSummary, value);
        }

        private string _historyKeyword = string.Empty;
        public string HistoryKeyword
        {
            get => _historyKeyword;
            set => SetProperty(ref _historyKeyword, value);
        }

        private string _historySearchStatus = "请选择 TXT、CSV 或 BIN 日志文件";
        public string HistorySearchStatus
        {
            get => _historySearchStatus;
            set => SetProperty(ref _historySearchStatus, value);
        }

        private bool _isHistorySearching;
        public bool IsHistorySearching
        {
            get => _isHistorySearching;
            set
            {
                if (SetProperty(ref _isHistorySearching, value))
                    OnPropertyChanged(nameof(HistorySearchButtonText));
            }
        }

        public string HistorySearchButtonText => IsHistorySearching ? "停止" : "搜索";

        public string SendText { get; set; } = "Hello World";
        public bool SendToA { get; set; } = true;

        private string _sendBtnText = "发送数据";
        /// <summary>
        /// 发送按钮文本 (支持循环发送时在“发送数据”与“暂停发送”动态切换)
        /// </summary>
        public string SendBtnText
        {
            get => _sendBtnText;
            set => SetProperty(ref _sendBtnText, value);
        }

        private bool _isAutoSend = false;
        /// <summary>
        /// 循环发送勾选使能 (勾选不立即触发，仅作为参数使能)
        /// </summary>
        public bool IsAutoSend
        {
            get => _isAutoSend;
            set
            {
                if (SetProperty(ref _isAutoSend, value))
                {
                    if (!value)
                    {
                        // 取消勾选时停止定时器并关掉发送状态
                        StopAutoSendTimer();
                    }
                }
            }
        }

        private int _autoSendIntervalMs = 1000;
        /// <summary>
        /// 循环发送周期(ms)
        /// </summary>
        public int AutoSendIntervalMs
        {
            get => _autoSendIntervalMs;
            set => SetProperty(ref _autoSendIntervalMs, Math.Max(1, value));
        }

        private int _autoSendCount;
        public int AutoSendCount
        {
            get => _autoSendCount;
            set => SetProperty(ref _autoSendCount, Math.Max(0, value));
        }

        private int _autoSendCompletedCount;
        public int AutoSendCompletedCount
        {
            get => _autoSendCompletedCount;
            set => SetProperty(ref _autoSendCompletedCount, value);
        }

        private string _sendStatusText = "等待发送";
        public string SendStatusText
        {
            get => _sendStatusText;
            set => SetProperty(ref _sendStatusText, value);
        }

        private string _receivedFilesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReceivedFiles");
        public string ReceivedFilesDirectory
        {
            get => _receivedFilesDirectory;
            set
            {
                if (SetProperty(ref _receivedFilesDirectory, value))
                    _fileReceiver.OutputDirectory = value;
            }
        }

        private bool _isFileSending;
        public bool IsFileSending
        {
            get => _isFileSending;
            set
            {
                if (SetProperty(ref _isFileSending, value))
                    OnPropertyChanged(nameof(FileSendButtonText));
            }
        }

        public string FileSendButtonText => IsFileSending ? "停止文件" : "发送文件";

        private string _fileTransferStatus = "文件传输就绪";
        public string FileTransferStatus
        {
            get => _fileTransferStatus;
            set => SetProperty(ref _fileTransferStatus, value);
        }

        /// <summary>
        /// 停止循环发送定时器并重置按钮文本
        /// </summary>
        private void StopAutoSendTimer()
        {
            if (_autoSendCts != null)
            {
                _autoSendCts.Cancel();
            }
            SendBtnText = "发送数据";
        }

        // 常用报文模板集合 (FR-23)
        public ObservableCollection<MessageTemplate> QuickTemplates { get; } = new ObservableCollection<MessageTemplate>();

        private MessageTemplate _selectedTemplate;
        public MessageTemplate SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (SetProperty(ref _selectedTemplate, value) && value != null)
                {
                    SendText = value.Content;
                    IsSendHex = value.IsHex;
                    OnPropertyChanged(nameof(SendText));
                }
            }
        }

        // 命令
        public ICommand ToggleChannelACommand { get; }
        // 切换通道B
        public ICommand ToggleChannelBCommand { get; }
        // 发送数据
        public ICommand SendDataCommand { get; }
        // 清除记录
        public ICommand ClearRecordsCommand { get; }
        // 保存配置命令
        public ICommand SaveConfigCommand { get; }
        public ICommand LoadConfigCommand { get; }
        public ICommand ExportLogCommand { get; }
        public ICommand SelectReceiveDirectoryCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand SelectAutoSaveLogDirectoryCommand { get; }
        public ICommand ShowAboutCommand { get; }
        public ICommand AddTemplateCommand { get; }
        public ICommand DeleteTemplateCommand { get; }
        public ICommand SelectCanDriverACommand { get; }
        public ICommand SelectCanDriverBCommand { get; }
        public ICommand SelectHistoryLogFilesCommand { get; }
        public ICommand SearchHistoryCommand { get; }
        public ICommand ClearHistorySearchCommand { get; }
        public ICommand SendFileCommand { get; }
        public ICommand SelectReceivedFilesDirectoryCommand { get; }
        public ICommand ResetChannelAStatisticsCommand { get; }
        public ICommand ResetChannelBStatisticsCommand { get; }
        public ICommand FlushFramesCommand { get; }
        public ICommand CalculateChecksumCommand { get; }
        public ICommand VerifyChecksumCommand { get; }
        public ICommand ResetCrcValidationStatisticsCommand { get; }
        public ICommand ParseProtocolCommand { get; }
        public ICommand ReloadProtocolPluginsCommand { get; }
        public ICommand OpenProtocolPluginFolderCommand { get; }
        public ICommand ClearRealtimeProtocolResultsCommand { get; }

        private ICommunicationChannel _channelAObj;
        private ICommunicationChannel _channelBObj;
        // 通道重开会创建新的底层对象；保存已结束会话的统计，避免界面计数从 0 重新开始。
        private long _channelAReceivedHistory;
        private long _channelASentHistory;
        private long _channelBReceivedHistory;
        private long _channelBSentHistory;


        public MainViewModel()
        {

            Engine.DataForwarded += OnDataForwarded;
            Engine.ChannelDisconnectedNotice += OnChannelDisconnectedNotice;
            _dataFramingService.FrameReady += OnDisplayFrameReady;
            Engine.StrategyOnDisconnect = SelectedDisconnectStrategy;
            _fileReceiver.OutputDirectory = ReceivedFilesDirectory;
            _fileReceiver.FileStarted += OnFileReceiveStarted;
            _fileReceiver.FileProgress += OnFileReceiveProgress;
            _fileReceiver.FileCompleted += OnFileReceiveCompleted;
            _fileReceiver.FileFailed += OnFileReceiveFailed;
            _rawBurstFileReceiver.FileCompleted += OnFileReceiveCompleted;
            _rawBurstFileReceiver.FileFailed += OnFileReceiveFailed;

            // 加载持久化模板；首次运行时创建预置模板 (FR-23)
            foreach (MessageTemplate template in MessageTemplateManager.Load()) QuickTemplates.Add(template);
            if (QuickTemplates.Count == 0)
            {
                QuickTemplates.Add(new MessageTemplate { Name = "Modbus 03读寄存器", Content = "01 03 00 00 00 02 C4 0B", IsHex = true });
                QuickTemplates.Add(new MessageTemplate { Name = "Modbus 06写寄存器", Content = "01 06 00 01 00 01 19 CA", IsHex = true });
                QuickTemplates.Add(new MessageTemplate { Name = "PING 心跳包", Content = "PING", IsHex = false });
                MessageTemplateManager.Save(QuickTemplates);
            }

            ToggleChannelACommand = new RelayCommand(ExecuteToggleChannelA);
            ToggleChannelBCommand = new RelayCommand(ExecuteToggleChannelB);
            SendDataCommand = new RelayCommand(ExecuteSendData);
            ClearRecordsCommand = new RelayCommand(ExecuteClearRecords);
            SaveConfigCommand = new RelayCommand(ExecuteSaveConfig);
            LoadConfigCommand = new RelayCommand(ExecuteLoadConfig);
            ExportLogCommand = new RelayCommand(ExecuteExportLog);
            SelectReceiveDirectoryCommand = new RelayCommand(ExecuteSelectRawReceiveDirectory);
            SelectAutoSaveLogDirectoryCommand = new RelayCommand(ExecuteSelectAutoSaveLogDirectory);
            AddTemplateCommand = new RelayCommand(ExecuteAddTemplate);
            DeleteTemplateCommand = new RelayCommand(ExecuteDeleteTemplate);
            SelectCanDriverACommand = new RelayCommand(() => ExecuteSelectCanDriver(true));
            SelectCanDriverBCommand = new RelayCommand(() => ExecuteSelectCanDriver(false));
            SelectHistoryLogFilesCommand = new RelayCommand(ExecuteSelectHistoryLogFiles);
            SearchHistoryCommand = new RelayCommand(ExecuteSearchHistory);
            ClearHistorySearchCommand = new RelayCommand(ExecuteClearHistorySearch);
            SendFileCommand = new RelayCommand(ExecuteSendFile);
            SelectReceivedFilesDirectoryCommand = new RelayCommand(ExecuteSelectReceivedFilesDirectory);
            ResetChannelAStatisticsCommand = new RelayCommand(ExecuteResetChannelAStatistics);
            ResetChannelBStatisticsCommand = new RelayCommand(ExecuteResetChannelBStatistics);
            FlushFramesCommand = new RelayCommand(() => _dataFramingService.FlushAll());
            CalculateChecksumCommand = new RelayCommand(ExecuteCalculateChecksum);
            VerifyChecksumCommand = new RelayCommand(ExecuteVerifyChecksum);
            ResetCrcValidationStatisticsCommand = new RelayCommand(ExecuteResetCrcValidationStatistics);
            ParseProtocolCommand = new RelayCommand(ExecuteParseProtocol);
            ReloadProtocolPluginsCommand = new RelayCommand(ReloadProtocolPlugins);
            OpenProtocolPluginFolderCommand = new RelayCommand(ExecuteOpenProtocolPluginFolder);
            ClearRealtimeProtocolResultsCommand = new RelayCommand(ExecuteClearRealtimeProtocolResults);
            OpenLogFolderCommand = new RelayCommand(() =>
            {
                string logDir = string.IsNullOrEmpty(AutoSaveLogDirectory) ? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs") : AutoSaveLogDirectory;
                if (!System.IO.Directory.Exists(logDir)) System.IO.Directory.CreateDirectory(logDir);
                System.Diagnostics.Process.Start("explorer.exe", logDir);
            });
            ShowAboutCommand = new RelayCommand(() =>
            {
                MessageBox.Show("通信与协议双通道调试 Studio v1.0\n支持 TCP / UDP / 串口 / ControlCAN 中继转发与多格式显示。", "关于本软件", MessageBoxButton.OK, MessageBoxImage.Information);
            });

            // 启动定时刷新收发字节数定时器 (FR-4)
            _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _statsTimer.Tick += (s, e) => UpdateStats();
            _statsTimer.Start();

            _uiFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _uiFlushTimer.Tick += (s, e) => FlushPendingUiRecords();
            _uiFlushTimer.Start();

            ReloadProtocolPlugins();
            _protocolParseWorker = Task.Run(() => ProcessProtocolParseQueueAsync(_protocolParseCts.Token));
        }

        /// <summary>
        /// 将当前发送框文本存为新模板 (支持自定义名称 FR-23)
        /// </summary>
        private void ExecuteAddTemplate()
        {
            if (string.IsNullOrWhiteSpace(SendText))
            {
                MessageBox.Show("保存失败：发送框内容不能为空！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string defaultName = $"模板_{QuickTemplates.Count + 1}";
            string name = Microsoft.VisualBasic.Interaction.InputBox("请输入常用报文模板的自定义名称：", "保存常用报文模板", defaultName);
            if (string.IsNullOrWhiteSpace(name))
            {
                // 用户点击了取消或输入为空
                return;
            }

            var newTpl = new MessageTemplate { Name = name.Trim(), Content = SendText, IsHex = IsSendHex };
            QuickTemplates.Add(newTpl);
            SelectedTemplate = newTpl;
            MessageTemplateManager.Save(QuickTemplates);
            MessageBox.Show($"已成功将当前发送内容保存为模板：\n【{name}】", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 删除当前选中的常用报文模板 (FR-23)
        /// </summary>
        private void ExecuteDeleteTemplate()
        {
            if (SelectedTemplate == null)
            {
                MessageBox.Show("请先在【常用模板】下拉列表中选中需要删除的模板！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"确定要删除常用模板【{SelectedTemplate.Name}】吗？", "确认删除模板", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                var target = SelectedTemplate;
                SelectedTemplate = null;
                QuickTemplates.Remove(target);
                MessageTemplateManager.Save(QuickTemplates);
            }
        }

        private void ExecuteSelectCanDriver(bool isChannelA)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Title = $"选择 {(isChannelA ? "A" : "B")} 端 CAN 驱动",
                Filter = "ControlCAN 驱动 (ControlCAN.dll)|ControlCAN.dll|动态链接库 (*.dll)|*.dll",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() == true)
            {
                if (isChannelA) CanDriverPathA = dialog.FileName;
                else CanDriverPathB = dialog.FileName;
            }
        }

        private static int ParseCanChannelIndex(string interfaceName)
        {
            if (string.IsNullOrWhiteSpace(interfaceName)) return 0;
            string digits = new string(interfaceName.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int number) ? Math.Max(0, number - 1) : 0;
        }

        private static uint ParseCanId(string text, uint fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            string value = text.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            return uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint id) && id <= 0x1FFFFFFF
                ? id : fallback;
        }

        private static string GetChannelStatusText(ChannelStatus status)
        {
            switch (status)
            {
                case ChannelStatus.Connecting: return "连接中";
                case ChannelStatus.Connected: return "已连接";
                case ChannelStatus.Error: return "错误";
                default: return "已断开";
            }
        }

        private void OnChannelAStatusChanged(object sender, ChannelStatus status)
        {
            RunOnUiThread(() =>
            {
                StatusTextA = $"A端: {GetChannelStatusText(status)}";
                if (status == ChannelStatus.Disconnected || status == ChannelStatus.Error)
                {
                    IsChannelAOpen = false;
                    BtnTextA = "打开通道";
                }
            });
        }

        private void OnChannelBStatusChanged(object sender, ChannelStatus status)
        {
            RunOnUiThread(() =>
            {
                StatusTextB = $"B端: {GetChannelStatusText(status)}";
                if (status == ChannelStatus.Disconnected || status == ChannelStatus.Error)
                {
                    IsChannelBOpen = false;
                    BtnTextB = "打开通道";
                }
            });
        }

        private void OnChannelDisconnectedNotice(object sender, string message)
        {
            RunOnUiThread(() =>
            {
                OnPropertyChanged(nameof(IsForwarding));
                OnPropertyChanged(nameof(ForwardingStatusText));
                MessageBox.Show(message, "通道断开提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private static void RunOnUiThread(Action action)
        {
            Dispatcher dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        /// <summary>
        /// 当通道 A 处于打开状态时，任意参数修改将自动安全断开通道并复位按钮
        /// </summary>
        private async void AutoCloseChannelAIfOpen()
        {
            if (!IsChannelAOpen) return;
            await CloseChannelAAsync();
        }

        private async Task CloseChannelAAsync()
        {
            if (_channelAObj != null)
            {
                Engine.AttachChannelA(null);
                _channelAObj.StatusChanged -= OnChannelAStatusChanged;
                await _channelAObj.CloseAsync();
                _channelAReceivedHistory += _channelAObj.BytesReceived;
                _channelASentHistory += _channelAObj.BytesSent;
                _channelAObj.Dispose();
                _channelAObj = null;
            }
            IsChannelAOpen = false;
            StatusTextA = "A端: 已断开";
            BtnTextA = "打开通道";
        }

        private async Task OpenChannelAAsync()
        {
            if (_channelAObj != null) await CloseChannelAAsync();
            if (!int.TryParse(PortA, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("A 端端口必须是 1–65535 之间的整数。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                switch (SelectedTypeA)
                {
                    case ChannelType.TcpServer:
                        _channelAObj = new TcpServerChannel { LocalPort = port };
                        break;
                    case ChannelType.TcpClient:
                        _channelAObj = new TcpClientChannel { TargetIp = IpA, TargetPort = port };
                        break;
                    case ChannelType.Udp:
                        if (!int.TryParse(LocalPortA, out int lPortA) || lPortA < 1 || lPortA > 65535)
                            throw new ArgumentException("UDP 本地端口必须是 1–65535 之间的整数");
                        _channelAObj = new UdpChannel { LocalPort = lPortA, TargetIp = IpA, TargetPort = port };
                        break;
                    case ChannelType.SerialPort:
                        _channelAObj = new SerialPortChannel { PortName = ComPortA, BaudRate = BaudRateA, DataBits = DataBitsA, StopBits = StopBitsA, Parity = ParityA };
                        break;
                    case ChannelType.Can:
                        _channelAObj = new CanChannel
                        {
                            DriverPath = CanDriverPathA,
                            DeviceType = (uint)Math.Max(0, CanDeviceTypeA),
                            DeviceIndex = (uint)Math.Max(0, CanDeviceIndexA),
                            ChannelIndex = (uint)ParseCanChannelIndex(CanInterfaceA),
                            BaudRate = CanBaudRateA,
                            FilterId = CanFilterA,
                            TransmitId = ParseCanId(CanTransmitIdA, 0x123)
                        };
                        break;
                }

                if (_channelAObj != null)
                {
                    _channelAObj.StatusChanged += OnChannelAStatusChanged;
                    bool ok = await _channelAObj.OpenAsync();
                    if (ok)
                    {
                        Engine.AttachChannelA(_channelAObj);
                        IsChannelAOpen = true;
                        BtnTextA = "关闭通道";
                    }
                    else
                    {
                        IsChannelAOpen = false;
                        StatusTextA = "A端: 失败";
                        BtnTextA = "打开通道";
                        string detail = _channelAObj is CanChannel can ? can.LastError : $"请检查端口号或硬件参数（如 {ComPortA}）是否存在或已被其他程序占用。";
                        MessageBox.Show($"A 端【{_channelAObj.Name}】打开失败！\n{detail}", "打开通道提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _channelAObj.StatusChanged -= OnChannelAStatusChanged;
                        await _channelAObj.CloseAsync();
                        _channelAObj.Dispose();
                        _channelAObj = null;
                    }
                }
            }
            catch (Exception ex)
            {
                IsChannelAOpen = false;
                StatusTextA = "A端: 异常";
                BtnTextA = "打开通道";
                MessageBox.Show($"打开 A 端通道产生异常：\n{ex.Message}", "打开通道错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 连接通道A
        /// </summary>
        private async void ExecuteToggleChannelA()
        {
            if (IsChannelAOpen)
            {
                await CloseChannelAAsync();
            }
            else
            {
                await OpenChannelAAsync();
            }
        }

        /// <summary>
        /// 当通道 B 处于打开状态时，任意参数修改将自动安全断开通道并复位按钮
        /// </summary>
        private async void AutoCloseChannelBIfOpen()
        {
            if (!IsChannelBOpen) return;
            await CloseChannelBAsync();
        }

        private async Task CloseChannelBAsync()
        {
            if (_channelBObj != null)
            {
                Engine.AttachChannelB(null);
                _channelBObj.StatusChanged -= OnChannelBStatusChanged;
                await _channelBObj.CloseAsync();
                _channelBReceivedHistory += _channelBObj.BytesReceived;
                _channelBSentHistory += _channelBObj.BytesSent;
                _channelBObj.Dispose();
                _channelBObj = null;
            }
            IsChannelBOpen = false;
            StatusTextB = "B端: 已断开";
            BtnTextB = "打开通道";
        }

        private async Task OpenChannelBAsync()
        {
            if (_channelBObj != null) await CloseChannelBAsync();
            if (!int.TryParse(PortB, out int port) || port < 1 || port > 65535)
            {
                MessageBox.Show("B 端端口必须是 1–65535 之间的整数。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                switch (SelectedTypeB)
                {
                    case ChannelType.TcpServer:
                        _channelBObj = new TcpServerChannel { LocalPort = port };
                        break;
                    case ChannelType.TcpClient:
                        _channelBObj = new TcpClientChannel { TargetIp = IpB, TargetPort = port };
                        break;
                    case ChannelType.Udp:
                        if (!int.TryParse(LocalPortB, out int lPortB) || lPortB < 1 || lPortB > 65535)
                            throw new ArgumentException("UDP 本地端口必须是 1–65535 之间的整数");
                        _channelBObj = new UdpChannel { LocalPort = lPortB, TargetIp = IpB, TargetPort = port };
                        break;
                    case ChannelType.SerialPort:
                        _channelBObj = new SerialPortChannel { PortName = ComPortB, BaudRate = BaudRateB, DataBits = DataBitsB, StopBits = StopBitsB, Parity = ParityB };
                        break;
                    case ChannelType.Can:
                        _channelBObj = new CanChannel
                        {
                            DriverPath = CanDriverPathB,
                            DeviceType = (uint)Math.Max(0, CanDeviceTypeB),
                            DeviceIndex = (uint)Math.Max(0, CanDeviceIndexB),
                            ChannelIndex = (uint)ParseCanChannelIndex(CanInterfaceB),
                            BaudRate = CanBaudRateB,
                            FilterId = CanFilterB,
                            TransmitId = ParseCanId(CanTransmitIdB, 0x123)
                        };
                        break;
                }

                if (_channelBObj != null)
                {
                    _channelBObj.StatusChanged += OnChannelBStatusChanged;
                    bool ok = await _channelBObj.OpenAsync();
                    if (ok)
                    {
                        Engine.AttachChannelB(_channelBObj);
                        IsChannelBOpen = true;
                        BtnTextB = "关闭通道";
                    }
                    else
                    {
                        IsChannelBOpen = false;
                        StatusTextB = "B端: 失败";
                        BtnTextB = "打开通道";
                        string detail = _channelBObj is CanChannel can ? can.LastError : $"请检查端口号或硬件参数（如 {ComPortB}）是否存在或已被其他程序占用。";
                        MessageBox.Show($"B 端【{_channelBObj.Name}】打开失败！\n{detail}", "打开通道提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        _channelBObj.StatusChanged -= OnChannelBStatusChanged;
                        await _channelBObj.CloseAsync();
                        _channelBObj.Dispose();
                        _channelBObj = null;
                    }
                }
            }
            catch (Exception ex)
            {
                IsChannelBOpen = false;
                StatusTextB = "B端: 异常";
                BtnTextB = "打开通道";
                MessageBox.Show($"打开 B 端通道产生异常：\n{ex.Message}", "打开通道错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 连接通道B
        /// </summary>
        private async void ExecuteToggleChannelB()
        {
            if (IsChannelBOpen)
            {
                await CloseChannelBAsync();
            }
            else
            {
                await OpenChannelBAsync();
            }
        }
        private CancellationTokenSource _autoSendCts;

        /// <summary>
        /// 执行发送按钮逻辑（支持指定次数或无限循环，并可随时停止）
        /// </summary>
        private async void ExecuteSendData()
        {
            if (IsFileSending)
            {
                MessageBox.Show("文件发送期间不能插入普通数据，请先停止或等待文件发送完成。", "文件发送中", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (IsAutoSend)
            {
                // 如果循环发送正在运行中，用户点击按钮表示“暂停发送 ⏸”
                if (_autoSendCts != null)
                {
                    StopAutoSendTimer();
                    return;
                }

                SendBtnText = "暂停发送";
                _autoSendCts = new CancellationTokenSource();
                CancellationTokenSource currentCts = _autoSendCts;
                AutoSendCompletedCount = 0;
                await RunAutoSendLoopAsync(currentCts);
            }
            else
            {
                // 单次常规发送
                await PerformSendSingleFrameAsync(true);
            }
        }

        private async Task RunAutoSendLoopAsync(CancellationTokenSource currentCts)
        {
            CancellationToken token = currentCts.Token;
            int intervalMs = Math.Max(1, AutoSendIntervalMs);
            int requestedCount = Math.Max(0, AutoSendCount);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested && (requestedCount == 0 || AutoSendCompletedCount < requestedCount))
                {
                    long cycleStartedAt = stopwatch.ElapsedMilliseconds;
                    int sent = await PerformSendSingleFrameAsync(false);
                    if (sent <= 0) break;

                    AutoSendCompletedCount++;
                    SendStatusText = requestedCount == 0
                        ? $"循环发送中：已发送 {AutoSendCompletedCount} 帧，本帧 {sent} 字节"
                        : $"循环发送：{AutoSendCompletedCount}/{requestedCount} 帧，本帧 {sent} 字节";

                    if (requestedCount > 0 && AutoSendCompletedCount >= requestedCount) break;
                    int remainingDelay = intervalMs - (int)(stopwatch.ElapsedMilliseconds - cycleStartedAt);
                    if (remainingDelay > 0) await Task.Delay(remainingDelay, token);
                }

                if (!token.IsCancellationRequested && requestedCount > 0 && AutoSendCompletedCount >= requestedCount)
                    SendStatusText = $"循环发送完成：共 {AutoSendCompletedCount} 帧";
            }
            catch (OperationCanceledException)
            {
                SendStatusText = $"循环发送已停止：共发送 {AutoSendCompletedCount} 帧";
            }
            finally
            {
                if (ReferenceEquals(_autoSendCts, currentCts))
                {
                    _autoSendCts = null;
                    currentCts.Dispose();
                    SendBtnText = "发送数据";
                }
            }
        }

        /// <summary>
        /// 实际执行一帧底层数据发送
        /// </summary>
        private async Task<int> PerformSendSingleFrameAsync(bool showDialog)
        {
            if (string.IsNullOrEmpty(SendText))
            {
                SendStatusText = "发送失败：发送内容不能为空";
                if (showDialog) MessageBox.Show(SendStatusText, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 0;
            }

            byte[] data ;
            //根据模式转换数据(FR-22)
            if(IsSendHex){
                if(!TryParseHexString(SendText,out data,out string errorMsg))
                {
                    if (showDialog) MessageBox.Show(errorMsg, "HEX 格式校验错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    SendStatusText = errorMsg;
                    if (!showDialog) StopAutoSendTimer();
                    return 0;
                }
            }else{
                data = System.Text.Encoding.ASCII.GetBytes(SendText);
            }

            ICommunicationChannel target = SendToA ? _channelAObj : _channelBObj;
            string targetName = SendToA ? "A" : "B";
            if (target == null || target.Status != ChannelStatus.Connected)
            {
                StopAutoSendTimer();
                SendStatusText = $"发送失败：{targetName} 端通道未打开";
                if (showDialog) MessageBox.Show(SendStatusText, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return 0;
            }

            int sentBytes = SendToA
                ? await Engine.SendToChannelAAsync(data)
                : await Engine.SendToChannelBAsync(data);

            if (sentBytes <= 0)
            {
                StopAutoSendTimer();
                SendStatusText = target is TcpServerChannel
                    ? $"发送失败：{targetName} 端 TCP 服务端暂无客户端"
                    : $"发送失败：{targetName} 端底层设备未写入数据";
                if (showDialog) MessageBox.Show(SendStatusText, "发送提示", MessageBoxButton.OK, MessageBoxImage.Error);
                return 0;
            }

            SendStatusText = sentBytes == data.Length
                ? $"发送成功：{targetName} 端，{sentBytes} 字节"
                : $"部分发送：{targetName} 端，{sentBytes}/{data.Length} 字节";
            return sentBytes;
        }

        private async void ExecuteSendFile()
        {
            if (_fileSendCts != null)
            {
                _fileSendCts.Cancel();
                return;
            }

            if (_autoSendCts != null)
            {
                MessageBox.Show("请先停止循环发送，再发送文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ICommunicationChannel target = SendToA ? _channelAObj : _channelBObj;
            string targetName = SendToA ? "A" : "B";
            if (target == null || target.Status != ChannelStatus.Connected)
            {
                MessageBox.Show($"{targetName} 端通道未打开，无法发送文件。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = $"选择要通过 {targetName} 端发送的文件",
                Filter = "所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return;

            var currentCts = new CancellationTokenSource();
            _fileSendCts = currentCts;
            IsFileSending = true;
            bool forwardingWasEnabled = IsForwarding;

            try
            {
                // 文件帧必须连续写入，发送期间暂停透明转发，避免其它数据插入文件正文。
                if (forwardingWasEnabled) IsForwarding = false;

                var fileInfo = new FileInfo(dialog.FileName);
                FileTransferStatus = $"正在校验：{fileInfo.Name}";
                byte[] sha256 = await FileTransferProtocol.ComputeSha256Async(dialog.FileName, currentCts.Token);
                byte[] header = FileTransferProtocol.CreateHeader(fileInfo.Name, fileInfo.Length, sha256);
                await SendFileBlockAsync(header, targetName, currentCts.Token);

                long sentTotal = 0;
                byte[] buffer = new byte[16 * 1024];
                using (var stream = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, currentCts.Token)) > 0)
                    {
                        currentCts.Token.ThrowIfCancellationRequested();
                        byte[] block = read == buffer.Length ? buffer : buffer.Take(read).ToArray();
                        await SendFileBlockAsync(block, targetName, currentCts.Token);
                        sentTotal += read;
                        double percentage = fileInfo.Length == 0 ? 100 : sentTotal * 100.0 / fileInfo.Length;
                        FileTransferStatus = $"发送文件：{fileInfo.Name}  {percentage:0.0}% ({FormatFileSize(sentTotal)}/{FormatFileSize(fileInfo.Length)})";
                        await Task.Yield();
                    }
                }

                FileTransferStatus = $"文件发送完成：{fileInfo.Name}（{FormatFileSize(fileInfo.Length)}）";
            }
            catch (OperationCanceledException)
            {
                FileTransferStatus = "文件发送已停止；接收端未完成文件保留为 .part";
            }
            catch (Exception ex)
            {
                FileTransferStatus = $"文件发送失败：{ex.Message}";
                MessageBox.Show(FileTransferStatus, "发送文件失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (forwardingWasEnabled) IsForwarding = true;
                if (ReferenceEquals(_fileSendCts, currentCts))
                {
                    _fileSendCts = null;
                    currentCts.Dispose();
                    IsFileSending = false;
                }
            }
        }

        private async Task SendFileBlockAsync(byte[] block, string targetName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int sent = SendToA
                ? await Engine.SendToChannelAAsync(block)
                : await Engine.SendToChannelBAsync(block);
            if (sent != block.Length)
                throw new IOException($"{targetName} 端仅发送 {sent}/{block.Length} 字节，文件传输已中止");
        }

        private void ExecuteSelectReceivedFilesDirectory()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "请选择接收文件的保存文件夹";
                if (!string.IsNullOrEmpty(ReceivedFilesDirectory) && Directory.Exists(ReceivedFilesDirectory))
                    dialog.SelectedPath = ReceivedFilesDirectory;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    ReceivedFilesDirectory = dialog.SelectedPath;
                    FileTransferStatus = $"接收文件保存位置：{ReceivedFilesDirectory}";
                }
            }
        }

        private void OnFileReceiveStarted(object sender, FileTransferEventArgs e)
        {
            // 已识别为 WPSFILE1 协议文件，删除并暂停并行建立的裸流临时文件。
            _rawBurstFileReceiver.Suppress(e.Direction);
            RunOnUiThread(() => FileTransferStatus = $"开始接收：{e.FileName}（{FormatFileSize(e.TotalBytes)}）");
        }

        private void OnFileReceiveProgress(object sender, FileTransferEventArgs e)
        { 
            RunOnUiThread(() =>
            {
                double percentage = e.TotalBytes == 0 ? 100 : e.ReceivedBytes * 100.0 / e.TotalBytes;
                FileTransferStatus = $"接收文件：{e.FileName}  {percentage:0.0}% ({FormatFileSize(e.ReceivedBytes)}/{FormatFileSize(e.TotalBytes)})";
            });
        }

        private void OnFileReceiveCompleted(object sender, FileTransferEventArgs e)
        {
            _rawBurstFileReceiver.Resume(e.Direction);
            RunOnUiThread(() => FileTransferStatus = $"文件接收并校验完成：{e.SavedPath}");
        }

        private void OnFileReceiveFailed(object sender, FileTransferEventArgs e)
        {
            _rawBurstFileReceiver.Resume(e.Direction);
            RunOnUiThread(() => FileTransferStatus = e.Message);
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):0.##} MB";
            if (bytes >= 1024L) return $"{bytes / 1024d:0.##} KB";
            return $"{bytes} B";
        }
        /// <summary>
        /// 执行清除记录操作
        /// </summary>
        private void ExecuteClearRecords()
        {
            while (_pendingUiRecords.TryDequeue(out _))
                Interlocked.Decrement(ref _pendingUiRecordCount);
            ChannelARxRecords.Clear();
            ChannelATxRecords.Clear();
            ChannelBRxRecords.Clear();
            ChannelBTxRecords.Clear();
        }

        private void ExecuteSelectHistoryLogFiles()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择历史通信日志",
                Filter = "通信日志 (*.txt;*.csv;*.bin)|*.txt;*.csv;*.bin|文本日志 (*.txt)|*.txt|CSV 日志 (*.csv)|*.csv|二进制日志 (*.bin)|*.bin|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            };

            if (Directory.Exists(AutoSaveLogDirectory)) dialog.InitialDirectory = AutoSaveLogDirectory;
            if (dialog.ShowDialog() != true) return;

            _historySelectedFiles = dialog.FileNames;
            HistoryFileSummary = _historySelectedFiles.Length == 1
                ? _historySelectedFiles[0]
                : $"已选择 {_historySelectedFiles.Length} 个文件：{string.Join("；", _historySelectedFiles.Select(Path.GetFileName))}";
            HistorySearchStatus = $"已选择 {_historySelectedFiles.Length} 个日志文件，输入关键字后开始搜索";
        }

        private async void ExecuteSearchHistory()
        {
            if (IsHistorySearching)
            {
                _historySearchCts?.Cancel();
                return;
            }

            if (_historySelectedFiles == null || _historySelectedFiles.Length == 0)
            {
                MessageBox.Show("请先选择需要搜索的历史日志文件。", "历史数据搜索", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var currentCts = new CancellationTokenSource();
            _historySearchCts = currentCts;
            IsHistorySearching = true;
            HistorySearchStatus = "正在读取并搜索历史日志……";
            HistorySearchResults.Clear();

            try
            {
                HistorySearchResult result = await HistoryLogSearchService.SearchAsync(
                    _historySelectedFiles,
                    HistoryKeyword,
                    MaxHistorySearchResults,
                    currentCts.Token);

                if (currentCts.IsCancellationRequested) return;
                HistorySearchResults.AddRange(result.Records);
                string limitText = result.LimitReached ? $"，结果已限制为前 {MaxHistorySearchResults:N0} 条" : string.Empty;
                HistorySearchStatus = $"搜索完成：扫描 {result.ScannedRecordCount:N0} 条，找到 {result.Records.Count:N0} 条{limitText}";
            }
            catch (OperationCanceledException)
            {
                HistorySearchStatus = "历史搜索已停止";
            }
            catch (Exception ex)
            {
                HistorySearchStatus = $"历史搜索失败：{ex.Message}";
                MessageBox.Show(HistorySearchStatus, "历史数据搜索", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (ReferenceEquals(_historySearchCts, currentCts))
                {
                    _historySearchCts = null;
                    IsHistorySearching = false;
                }
                currentCts.Dispose();
            }
        }

        private void ExecuteClearHistorySearch()
        {
            _historySearchCts?.Cancel();
            HistorySearchResults.Clear();
            HistoryKeyword = string.Empty;
            HistorySearchStatus = _historySelectedFiles.Length > 0
                ? $"已选择 {_historySelectedFiles.Length} 个日志文件"
                : "请选择 TXT、CSV 或 BIN 日志文件";
        }
        /// <summary>
        /// 更新状态
        /// </summary>
        private void UpdateStats()
        {
            long channelAReceived = _channelAReceivedHistory + (_channelAObj?.BytesReceived ?? 0L);
            long channelASent = _channelASentHistory + (_channelAObj?.BytesSent ?? 0L);
            long channelBReceived = _channelBReceivedHistory + (_channelBObj?.BytesReceived ?? 0L);
            long channelBSent = _channelBSentHistory + (_channelBObj?.BytesSent ?? 0L);

            StatsTextA = $"RX: {channelAReceived} B | TX: {channelASent} B";
            StatsTextB = $"RX: {channelBReceived} B | TX: {channelBSent} B";
        }

        private void ExecuteResetChannelAStatistics()
        {
            _channelAReceivedHistory = 0;
            _channelASentHistory = 0;
            _channelAObj?.ResetStatistics();
            UpdateStats();
        }

        private void ExecuteResetChannelBStatistics()
        {
            _channelBReceivedHistory = 0;
            _channelBSentHistory = 0;
            _channelBObj?.ResetStatistics();
            UpdateStats();
        }
        /// <summary>
        /// 弹窗选择路径并保存当前配置文件
        /// </summary>
        private void ExecuteSaveConfig()
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Title = "保存通道配置文件",
                Filter = "JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                FileName = $"Config_{DateTime.Now:yyyyMMdd_HHmmss}.json",
                DefaultExt = ".json"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                var profile = new ChannelConfigProfile
                {
                    ChannelA = CreateChannelConfig(true),
                    ChannelB = CreateChannelConfig(false),
                    IsForwardingEnabled = IsForwarding,
                    DisconnectStrategy = SelectedDisconnectStrategy,
                    DisplayFormat = SelectDisplayFormat
                };

                // 执行保存
                bool ok = ConfigManager.SaveProfile(profile, saveFileDialog.FileName);
                if (ok)
                {
                    MessageBox.Show($"配置已成功保存至：\n{saveFileDialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("配置文件保存失败，请检查目录读写权限！", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        /// <summary>
        /// 弹窗选择并加载已有的配置文件
        /// </summary>
        private async void ExecuteLoadConfig()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Title = "选择要加载的通道配置文件",
                Filter = "JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                Multiselect = false
            };

            if (openFileDialog.ShowDialog() == true)
            {
                var profile = ConfigManager.LoadProfile(openFileDialog.FileName);
                if (profile == null)
                {
                    MessageBox.Show("无法解析该配置文件，格式可能已损坏！", "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                await CloseChannelAAsync();
                await CloseChannelBAsync();
                ApplyChannelConfig(profile.ChannelA, true);
                ApplyChannelConfig(profile.ChannelB, false);
                IsForwarding = profile.IsForwardingEnabled;
                SelectedDisconnectStrategy = profile.DisconnectStrategy;
                SelectDisplayFormat = profile.DisplayFormat;

                MessageBox.Show($"已成功加载配置文件：\n{openFileDialog.FileName}", "加载成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private string GetFormattedRecordText(DataRecord r, DisplayFormat format)
        {
            switch (format)
            {
                case DisplayFormat.Ascii: return r.AsciiContent;
                case DisplayFormat.Binary: return r.BinaryContent;
                case DisplayFormat.HexAndAscii: return r.HexAndAsciiContent;
                case DisplayFormat.Hex:
                default: return r.HexContent;
            }
        }

        /// <summary>
        /// 导出并保存当前界面显示的通信数据日志，手动保存触发FR-18
        /// </summary>
        private void ExecuteExportLog()
        {
            string defaultExt = SelectedAutoSaveFormat.ToString().ToLower();
            int filterIndex = 1; // 1: txt, 2: csv, 3: bin
            if (SelectedAutoSaveFormat == LogFileFormat.CSV) filterIndex = 2;
            else if (SelectedAutoSaveFormat == LogFileFormat.BIN) filterIndex = 3;

            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Title = $"导出保存通信调试日志文件 (默认使用设置格式: {SelectedAutoSaveFormat})",
                Filter = "文本日志文件 (*.txt)|*.txt|CSV表格文件 (*.csv)|*.csv|二进制数据文件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
                FilterIndex = filterIndex,
                FileName = $"Log_Export_{DateTime.Now:yyyyMMdd_HHmmss}.{defaultExt}",
                DefaultExt = $".{defaultExt}"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    string ext = Path.GetExtension(saveFileDialog.FileName).ToLower();

                    // 收集所有允许实例化的列表项
                    var exportRecords = new List<(DataDirection Direction, DataRecord Record)>();
                    if (SaveARxLog) exportRecords.AddRange(ChannelARxRecords.Select(r => (DataDirection.ChannelA_Rx, r)));
                    if (SaveATxLog) exportRecords.AddRange(ChannelATxRecords.Select(r => (DataDirection.ChannelA_Tx, r)));
                    if (SaveBRxLog) exportRecords.AddRange(ChannelBRxRecords.Select(r => (DataDirection.ChannelB_Rx, r)));
                    if (SaveBTxLog) exportRecords.AddRange(ChannelBTxRecords.Select(r => (DataDirection.ChannelB_Tx, r)));

                    // 重新按时间排序
                    exportRecords = exportRecords.OrderBy(item => item.Record.Timestamp).ToList();

                    if (ext == ".bin")
                    {
                        // 结构化 BIN：保留每条记录的时间、方向、所属端、长度、数据和备注。
                        using (FileStream fs = new FileStream(saveFileDialog.FileName, FileMode.Create, FileAccess.Write))
                        {
                            foreach (var item in exportRecords)
                            {
                                if (item.Record.RawData != null)
                                {
                                    byte[] binaryRecord = LogService.CreateBinaryLogRecord(
                                        item.Record.Timestamp,
                                        item.Direction,
                                        item.Record.RawData,
                                        item.Record.Description);
                                    fs.Write(binaryRecord, 0, binaryRecord.Length);
                                }
                            }
                        }
                    }
                    else if (ext == ".csv")
                    {
                        // FR-17：时间戳、方向、所属端、数据长度、数据内容。
                        StringBuilder sbCsv = new StringBuilder();
                        sbCsv.AppendLine("时间戳,方向,所属端,数据长度(B),数据内容(HEX),数据内容(ASCII),备注");
                        foreach (var item in exportRecords)
                        {
                            string hexStr = item.Record.HexContent;
                            string asciiStr = item.Record.AsciiContent.Replace("\"", "\"\"").Replace("\r", "\\r").Replace("\n", "\\n");
                            string desc = (item.Record.Description ?? "").Replace("\"", "\"\"");
                            sbCsv.AppendLine($"\"{item.Record.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\",\"{LogService.GetDirectionText(item.Direction)}\",\"{LogService.GetEndpointText(item.Direction)}\",\"{item.Record.Length}\",\"{hexStr}\",\"{asciiStr}\",\"{desc}\"");
                        }
                        File.WriteAllText(saveFileDialog.FileName, sbCsv.ToString(), Encoding.UTF8);
                    }
                    else
                    {
                        // 仅保留需求指定的按通道分类记录，不再重复生成全量时间轴。
                        StringBuilder sb = new StringBuilder();
                        sb.AppendLine($"=== 通信与协议调试日志 ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
                        sb.AppendLine($"总记录条数: {exportRecords.Count} 条");
                        sb.AppendLine();
                        sb.AppendLine("--- 【按通道分类详细记录】 ---");

                        if (SaveARxLog)
                        {
                            AppendFr17LogCategory(sb, "A端接收", DataDirection.ChannelA_Rx, ChannelARxRecords);
                        }
                        if (SaveATxLog)
                        {
                            AppendFr17LogCategory(sb, "A端发送", DataDirection.ChannelA_Tx, ChannelATxRecords);
                        }
                        if (SaveBRxLog)
                        {
                            AppendFr17LogCategory(sb, "B端接收", DataDirection.ChannelB_Rx, ChannelBRxRecords);
                        }
                        if (SaveBTxLog)
                        {
                            AppendFr17LogCategory(sb, "B端发送", DataDirection.ChannelB_Tx, ChannelBTxRecords);
                        }
                        File.WriteAllText(saveFileDialog.FileName, sb.ToString(), Encoding.UTF8);
                    }

                    MessageBox.Show($"日志数据已成功按选定数据流与目标格式 ({ext.ToUpper()}) 导出至：\n{saveFileDialog.FileName}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"导出日志失败：{ex.Message}", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private static void AppendFr17LogCategory(
            StringBuilder builder,
            string categoryName,
            DataDirection direction,
            IEnumerable<DataRecord> records)
        {
            IList<DataRecord> categoryRecords = records.OrderBy(record => record.Timestamp).ToList();
            builder.AppendLine($"\n>>> 【{categoryName}】（共 {categoryRecords.Count} 条）");

            foreach (DataRecord record in categoryRecords)
            {
                string description = string.IsNullOrEmpty(record.Description)
                    ? string.Empty
                    : $" | 备注: {record.Description.Replace("\r", "\\r").Replace("\n", "\\n")}";
                builder.AppendLine(
                    $"[{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] " +
                    $"[{LogService.GetDirectionCode(direction)}] " +
                    $"[{LogService.GetEndpointCode(direction)}]  " +
                    $"[{record.Length}B] " +
                    $"HEX: {record.HexContent} | ASCII: {record.AsciiContent}{description}");
            }
        }

        /// <summary>
        /// 弹窗由用户选择后台自动保存日志的文件夹路径
        /// </summary>
        private void ExecuteSelectAutoSaveLogDirectory()
        {
            TrySelectAutoSaveLogDirectory(true);
        }

        private bool TrySelectAutoSaveLogDirectory(bool showSuccessMessage)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "请选择后台自动保存日志的目标文件夹路径";
                if (!string.IsNullOrEmpty(AutoSaveLogDirectory) && System.IO.Directory.Exists(AutoSaveLogDirectory))
                {
                    dialog.SelectedPath = AutoSaveLogDirectory;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    AutoSaveLogDirectory = dialog.SelectedPath;
                    if (showSuccessMessage)
                    {
                        MessageBox.Show($"后台自动保存日志路径已成功更改为：\n{AutoSaveLogDirectory}", "设置成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return true;
                }
            }
            return false;
        }

        private void ExecuteSelectRawReceiveDirectory()
        {
            if (!SelectRawReceiveDirectory()) return;

            _fileReceiver.ConfigureDirection(DataDirection.ChannelA_Rx, SaveARxFile, _rxFileDirectory);
            _fileReceiver.ConfigureDirection(DataDirection.ChannelB_Rx, SaveBRxFile, _rxFileDirectory);
            _rawBurstFileReceiver.ConfigureDirection(DataDirection.ChannelA_Rx, SaveARxFile, _rxFileDirectory);
            _rawBurstFileReceiver.ConfigureDirection(DataDirection.ChannelB_Rx, SaveBRxFile, _rxFileDirectory);
        }

        private bool SelectRawReceiveDirectory()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "请选择 A-RX 和 B-RX 共用的接收文件保存文件夹";
                if (!string.IsNullOrWhiteSpace(_rxFileDirectory) && Directory.Exists(_rxFileDirectory))
                    dialog.SelectedPath = _rxFileDirectory;
                else if (Directory.Exists(ReceivedFilesDirectory))
                    dialog.SelectedPath = ReceivedFilesDirectory;

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return false;

                _rxFileDirectory = dialog.SelectedPath;
                ReceivedFilesDirectory = dialog.SelectedPath;
                FileTransferStatus = $"A-RX / B-RX 接收文件夹：{_rxFileDirectory}";
                return true;
            }
        }

        private void ApplyFramingConfiguration()
        {
            if (SelectedFrameMode == FrameMode.FixedLength &&
                (FrameFixedLength < 1 || FrameFixedLength > 1024 * 1024))
            {
                MessageBox.Show("固定帧长必须在 1～1,048,576 字节之间。", "分帧参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (SelectedFrameMode != FrameMode.None &&
                (FrameIdleMilliseconds < 1 || FrameIdleMilliseconds > 60000))
            {
                MessageBox.Show("时间间隔必须在 1～60,000 毫秒之间。", "分帧参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] delimiter = new byte[0];
            if (SelectedFrameMode == FrameMode.Delimiter)
            {
                if (!TryConvertToolInput(FrameDelimiterText, IsFrameDelimiterHex, out delimiter, out string errorMessage))
                {
                    MessageBox.Show($"分隔符无效：{errorMessage}", "分帧参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (delimiter.Length > 256)
                {
                    MessageBox.Show("分隔符不能超过 256 字节。", "分帧参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            _dataFramingService.Configure(SelectedFrameMode, FrameFixedLength, delimiter, FrameIdleMilliseconds);
            switch (SelectedFrameMode)
            {
                case FrameMode.FixedLength:
                    FrameConfigurationStatus = $"已启用固定长度分帧：每帧 {FrameFixedLength} 字节；不足一帧时空闲 {FrameIdleMilliseconds} ms 自动输出";
                    break;
                case FrameMode.Delimiter:
                    string delimiterBytes = BitConverter.ToString(delimiter).Replace("-", " ");
                    FrameConfigurationStatus = IsFrameDelimiterHex
                        ? $"已启用HEX分隔符分帧：{delimiterBytes}；未匹配时空闲 {FrameIdleMilliseconds} ms 自动输出"
                        : $"已启用文本分隔符分帧：“{FrameDelimiterText}” = {delimiterBytes}；未匹配时空闲 {FrameIdleMilliseconds} ms 自动输出";
                    break;
                case FrameMode.TimeInterval:
                    FrameConfigurationStatus = $"已启用时间分帧：连续空闲 {FrameIdleMilliseconds} ms 后输出一帧";
                    break;
                default:
                    FrameConfigurationStatus = "已关闭重新分帧，按底层接收块显示";
                    break;
            }
        }

        private void ExecuteCalculateChecksum()
        {
            if (!TryConvertToolInput(ChecksumInput, IsChecksumInputHex, out byte[] data, out string errorMessage))
            {
                ChecksumResult = "输入错误：" + errorMessage;
                return;
            }

            try
            {
                string value = ChecksumService.Calculate(SelectedChecksumAlgorithm, data);
                byte[] appendBytes = ChecksumService.GetChecksumBytes(SelectedChecksumAlgorithm, data);
                ChecksumResult = $"{value}  （{data.Length} 字节）｜追加字节：{ChecksumService.FormatBytes(appendBytes)}";
            }
            catch (Exception ex)
            {
                ChecksumResult = "计算失败：" + ex.Message;
            }
        }

        private void ExecuteVerifyChecksum()
        {
            if (!IsChecksumInputHex)
            {
                ChecksumResult = "末尾校验只支持 HEX 输入；请输入包含末尾校验字节的完整报文。";
                return;
            }
            if (!TryConvertToolInput(ChecksumInput, true, out byte[] completeFrame, out string errorMessage))
            {
                ChecksumResult = "输入错误：" + errorMessage;
                return;
            }

            bool passed = ChecksumService.VerifyAppendedChecksum(
                SelectedChecksumAlgorithm,
                completeFrame,
                out byte[] expected,
                out byte[] actual);
            ChecksumResult = passed
                ? $"校验通过｜报文末尾：{ChecksumService.FormatBytes(actual)}"
                : $"校验失败｜期望：{ChecksumService.FormatBytes(expected)}｜实际：{ChecksumService.FormatBytes(actual)}";
        }

        private void ExecuteResetCrcValidationStatistics()
        {
            Interlocked.Exchange(ref _crcValidFrameCount, 0);
            Interlocked.Exchange(ref _crcInvalidFrameCount, 0);
            OnPropertyChanged(nameof(CrcValidFrameCount));
            OnPropertyChanged(nameof(CrcInvalidFrameCount));
            OnPropertyChanged(nameof(CrcValidationSummary));
        }

        private void ReloadProtocolPlugins()
        {
            string selectedName = SelectedProtocolParser?.Name;
            string pluginDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            ProtocolPluginLoadResult loadResult = ProtocolPluginService.Load(pluginDirectory);

            ProtocolParsers.Clear();
            foreach (IProtocolParser parser in loadResult.Parsers) ProtocolParsers.Add(parser);
            SelectedProtocolParser = ProtocolParsers.FirstOrDefault(parser =>
                string.Equals(parser.Name, selectedName, StringComparison.OrdinalIgnoreCase))
                ?? ProtocolParsers.FirstOrDefault();

            string scanTime = DateTime.Now.ToString("HH:mm:ss");
            if (loadResult.Errors.Count > 0)
            {
                ProtocolPluginStatus = $"{scanTime} 扫描完成：{loadResult.ScannedDllCount} 个DLL，成功加载 " +
                    $"{loadResult.LoadedExternalParserCount} 个外部解析器，失败 {loadResult.Errors.Count} 个：" +
                    string.Join("；", loadResult.Errors);
            }
            else if (loadResult.ScannedDllCount == 0)
            {
                ProtocolPluginStatus = $"{scanTime} 扫描完成：未发现外部插件DLL；当前仅有内置“通用字节解析”。";
            }
            else if (loadResult.LoadedExternalParserCount == 0)
            {
                ProtocolPluginStatus = $"{scanTime} 扫描了 {loadResult.ScannedDllCount} 个DLL，但没有找到有效的IProtocolParser实现。";
            }
            else
            {
                ProtocolPluginStatus = $"{scanTime} 扫描完成：{loadResult.ScannedDllCount} 个DLL，成功加载 " +
                    $"{loadResult.LoadedExternalParserCount} 个外部解析器；解析器总数 {ProtocolParsers.Count}。";
            }
        }

        private void ExecuteOpenProtocolPluginFolder()
        {
            string pluginDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            Directory.CreateDirectory(pluginDirectory);
            System.Diagnostics.Process.Start("explorer.exe", pluginDirectory);
            ProtocolPluginStatus = $"插件目录：{pluginDirectory}；把插件DLL放入后点击“重新加载插件”。";
        }

        private void ExecuteParseProtocol()
        {
            if (SelectedProtocolParser == null)
            {
                ProtocolParseOutput = "请先选择协议解析器。";
                return;
            }
            if (!TryConvertToolInput(ProtocolInput, IsProtocolInputHex, out byte[] data, out string errorMessage))
            {
                ProtocolParseOutput = "输入错误：" + errorMessage;
                return;
            }

            try
            {
                if (!SelectedProtocolParser.CanParse(data))
                {
                    ProtocolParseOutput = $"“{SelectedProtocolParser.Name}”不能解析当前数据。";
                    return;
                }

                ProtocolParseResult result = SelectedProtocolParser.Parse(data);
                if (result == null)
                {
                    ProtocolParseOutput = "解析器未返回结果。";
                    return;
                }

                var builder = new StringBuilder();
                builder.AppendLine(result.Success ? "解析成功" : "解析失败");
                if (!string.IsNullOrWhiteSpace(result.Summary)) builder.AppendLine(result.Summary);
                if (result.Fields != null)
                {
                    foreach (KeyValuePair<string, string> field in result.Fields)
                        builder.AppendLine($"{field.Key}: {field.Value}");
                }
                ProtocolParseOutput = builder.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                ProtocolParseOutput = $"插件“{SelectedProtocolParser.Name}”解析异常：{ex.Message}";
            }
        }

        private static bool TryConvertToolInput(string input, bool isHex, out byte[] data, out string errorMessage)
        {
            data = null;
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                errorMessage = "输入不能为空";
                return false;
            }

            if (!isHex)
            {
                data = Encoding.UTF8.GetBytes(input);
                return true;
            }

            string clean = new string(input.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray());
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
            if (clean.Length == 0 || clean.Length % 2 != 0)
            {
                errorMessage = "HEX必须由完整的两位字节组成";
                return false;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(clean, "^[0-9a-fA-F]+$"))
            {
                errorMessage = "HEX只能包含0-9、A-F、空格或短横线";
                return false;
            }

            try
            {
                data = new byte[clean.Length / 2];
                for (int index = 0; index < data.Length; index++)
                    data[index] = Convert.ToByte(clean.Substring(index * 2, 2), 16);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void OnDisplayFrameReady(object sender, ForwardingDataEventArgs e)
        {
            if (IsDisplayPaused || _disposed) return;

            bool isReceiveFrame = e.Direction == DataDirection.ChannelA_Rx ||
                                  e.Direction == DataDirection.ChannelB_Rx;
            if (IsAutoReceiveChecksumEnabled && isReceiveFrame)
            {
                bool valid = ChecksumService.VerifyAppendedChecksum(
                    SelectedReceiveChecksumAlgorithm,
                    e.Data,
                    IsReceiveChecksumHighByteFirst,
                    out _,
                    out _);
                e.IsChecksumValid = valid;

                int checksumLength = SelectedReceiveChecksumAlgorithm == ChecksumAlgorithm.Crc32 ? 4 : 2;
                if (valid)
                {
                    int payloadLength = e.Data.Length - checksumLength;
                    e.VerifiedPayload = new byte[payloadLength];
                    Buffer.BlockCopy(e.Data, 0, e.VerifiedPayload, 0, payloadLength);
                    Interlocked.Increment(ref _crcValidFrameCount);
                }
                else
                {
                    Interlocked.Increment(ref _crcInvalidFrameCount);
                }

                RunOnUiThread(() =>
                {
                    OnPropertyChanged(nameof(CrcValidFrameCount));
                    OnPropertyChanged(nameof(CrcInvalidFrameCount));
                    OnPropertyChanged(nameof(CrcValidationSummary));
                });

                if (!valid && HideInvalidChecksumFrames) return;
            }

            _pendingUiRecords.Enqueue(e);
            int pendingCount = Interlocked.Increment(ref _pendingUiRecordCount);
            while (pendingCount > MaxPendingUiRecords && _pendingUiRecords.TryDequeue(out _))
                pendingCount = Interlocked.Decrement(ref _pendingUiRecordCount);

            if (!e.IsChecksumValid.HasValue || e.IsChecksumValid.Value)
                EnqueueRealtimeProtocolParse(e);
        }

        private void EnqueueRealtimeProtocolParse(ForwardingDataEventArgs frame)
        {
            IProtocolParser parser = SelectedProtocolParser;
            if (!IsRealtimeProtocolParsingEnabled || parser == null || frame?.Data == null) return;

            while (Volatile.Read(ref _protocolParseQueueCount) >= MaxPendingProtocolParses &&
                   _protocolParseQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _protocolParseQueueCount);
            }

            _protocolParseQueue.Enqueue(new ProtocolParseWorkItem
            {
                Timestamp = frame.Timestamp,
                Direction = frame.Direction,
                Data = (byte[])(frame.VerifiedPayload ?? frame.Data).Clone(),
                Parser = parser
            });
            Interlocked.Increment(ref _protocolParseQueueCount);
            _protocolParseSignal.Release();
        }

        private async Task ProcessProtocolParseQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await _protocolParseSignal.WaitAsync(cancellationToken);
                    while (_protocolParseQueue.TryDequeue(out ProtocolParseWorkItem item))
                    {
                        Interlocked.Decrement(ref _protocolParseQueueCount);
                        if (cancellationToken.IsCancellationRequested) return;
                        if (!IsRealtimeProtocolParsingEnabled) continue;

                        ProtocolDecodedRecord decoded = null;
                        try
                        {
                            if (!item.Parser.CanParse(item.Data)) continue;
                            ProtocolParseResult result = item.Parser.Parse(item.Data);
                            if (result == null) throw new InvalidOperationException("解析器未返回结果");
                            string fields = result.Fields == null
                                ? string.Empty
                                : string.Join(" | ", result.Fields.Select(field => $"{field.Key}: {field.Value}"));
                            decoded = new ProtocolDecodedRecord
                            {
                                Timestamp = item.Timestamp,
                                Direction = item.Direction,
                                ParserName = item.Parser.Name,
                                Success = result.Success,
                                Summary = result.Summary,
                                FieldsText = fields,
                                RawHex = ChecksumService.FormatBytes(item.Data)
                            };
                        }
                        catch (Exception ex)
                        {
                            decoded = new ProtocolDecodedRecord
                            {
                                Timestamp = item.Timestamp,
                                Direction = item.Direction,
                                ParserName = item.Parser.Name,
                                Success = false,
                                Summary = "解析异常：" + ex.Message,
                                FieldsText = string.Empty,
                                RawHex = ChecksumService.FormatBytes(item.Data)
                            };
                        }

                        ProtocolDecodedRecord record = decoded;
                        RunOnUiThread(() =>
                        {
                            if (_disposed) return;
                            if (RealtimeProtocolResults.Count >= MaxRealtimeProtocolResults)
                                RealtimeProtocolResults.RemoveRangeFromStart(
                                    RealtimeProtocolResults.Count - MaxRealtimeProtocolResults + 1);
                            RealtimeProtocolResults.Add(record);
                            RealtimeProtocolStatus = $"实时解析运行中：{record.ParserName}，已显示 {RealtimeProtocolResults.Count} 条";
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ExecuteClearRealtimeProtocolResults()
        {
            while (_protocolParseQueue.TryDequeue(out _))
                Interlocked.Decrement(ref _protocolParseQueueCount);
            RealtimeProtocolResults.Clear();
            RealtimeProtocolStatus = IsRealtimeProtocolParsingEnabled
                ? $"实时解析已启用：{SelectedProtocolParser?.Name ?? "尚未选择解析器"}"
                : "实时解析未启用";
        }
        /// <summary>
        /// 处理数据转发事件
        /// </summary>
        /// <param name="sender">事件发送者</param>
        /// <param name="e">数据转发事件参数</param>
        private void OnDataForwarded(object sender, ForwardingDataEventArgs e)
        {
            // 接收文件不受“暂停显示”影响，且按通道分别拼接跨帧数据。
            if (e.Direction == DataDirection.ChannelA_Rx || e.Direction == DataDirection.ChannelB_Rx)
            {
                _rawBurstFileReceiver.Process(e.Direction, e.Data);
                _fileReceiver.Process(e.Direction, e.Data);
            }

            // 校验用户是否允许保存当前方向数据
            bool shouldSaveLog = false;
            switch (e.Direction)
            {
                case DataDirection.ChannelA_Rx:shouldSaveLog = SaveARxLog;
                    break;
                case DataDirection.ChannelA_Tx:shouldSaveLog = SaveATxLog;
                    break;
                case DataDirection.ChannelB_Rx:shouldSaveLog = SaveBRxLog;
                    break;
                case DataDirection.ChannelB_Tx:shouldSaveLog = SaveBTxLog;
                    break;
            }
            if (IsAutoSaveEnabled && shouldSaveLog)
            {
                //1、将记录送至后台队列
                LogService.Enqueue(e);
            }
            // 2、分帧后的显示记录进入原有 A-RX/A-TX/B-RX/B-TX 区域。
            if (IsDisplayPaused) return;
            _dataFramingService.Process(e);
        }

        private SingleChannelConfig CreateChannelConfig(bool isChannelA)
        {
            ChannelType type = isChannelA ? SelectedTypeA : SelectedTypeB;
            string portText = isChannelA ? PortA : PortB;
            string localPortText = isChannelA ? LocalPortA : LocalPortB;
            int targetPort = ParsePortOrDefault(portText, 8080);
            int localPort = type == ChannelType.Udp
                ? ParsePortOrDefault(localPortText, 9000)
                : targetPort;

            return new SingleChannelConfig
            {
                ChannelType = type,
                LocalPort = localPort,
                TargetIp = isChannelA ? IpA : IpB,
                TargetPort = targetPort,
                PortName = isChannelA ? ComPortA : ComPortB,
                BaudRate = isChannelA ? BaudRateA : BaudRateB,
                DataBits = isChannelA ? DataBitsA : DataBitsB,
                StopBits = isChannelA ? StopBitsA : StopBitsB,
                Parity = isChannelA ? ParityA : ParityB,
                CanDeviceType = isChannelA ? CanDeviceTypeA : CanDeviceTypeB,
                CanDeviceIndex = isChannelA ? CanDeviceIndexA : CanDeviceIndexB,
                CanChannelIndex = ParseCanChannelIndex(isChannelA ? CanInterfaceA : CanInterfaceB),
                CanBaudRate = (isChannelA ? CanBaudRateA : CanBaudRateB).ToString(),
                CanAccCode = isChannelA ? CanFilterA : CanFilterB,
                CanDriverPath = isChannelA ? CanDriverPathA : CanDriverPathB,
                CanTransmitId = ParseCanId(isChannelA ? CanTransmitIdA : CanTransmitIdB, 0x123)
            };
        }

        private void ApplyChannelConfig(SingleChannelConfig config, bool isChannelA)
        {
            if (config == null) return;
            int canBaudRate = int.TryParse(config.CanBaudRate, out int parsedBaud) ? parsedBaud : 500000;
            string interfaceName = $"CAN{Math.Max(0, config.CanChannelIndex) + 1}";
            string transmitId = $"0x{config.CanTransmitId:X}";

            if (isChannelA)
            {
                SelectedTypeA = config.ChannelType;
                PortA = (config.ChannelType == ChannelType.TcpServer ? config.LocalPort : config.TargetPort).ToString();
                LocalPortA = config.LocalPort.ToString();
                IpA = config.TargetIp;
                ComPortA = config.PortName;
                BaudRateA = config.BaudRate;
                DataBitsA = config.DataBits;
                StopBitsA = config.StopBits;
                ParityA = config.Parity;
                CanDeviceTypeA = config.CanDeviceType;
                CanDeviceIndexA = config.CanDeviceIndex;
                CanInterfaceA = interfaceName;
                CanBaudRateA = canBaudRate;
                CanFilterA = config.CanAccCode;
                CanDriverPathA = config.CanDriverPath;
                CanTransmitIdA = transmitId;
            }
            else
            {
                SelectedTypeB = config.ChannelType;
                PortB = (config.ChannelType == ChannelType.TcpServer ? config.LocalPort : config.TargetPort).ToString();
                LocalPortB = config.LocalPort.ToString();
                IpB = config.TargetIp;
                ComPortB = config.PortName;
                BaudRateB = config.BaudRate;
                DataBitsB = config.DataBits;
                StopBitsB = config.StopBits;
                ParityB = config.Parity;
                CanDeviceTypeB = config.CanDeviceType;
                CanDeviceIndexB = config.CanDeviceIndex;
                CanInterfaceB = interfaceName;
                CanBaudRateB = canBaudRate;
                CanFilterB = config.CanAccCode;
                CanDriverPathB = config.CanDriverPath;
                CanTransmitIdB = transmitId;
            }
        }

        private static int ParsePortOrDefault(string text, int fallback)
        {
            return int.TryParse(text, out int value) && value >= 1 && value <= 65535 ? value : fallback;
        }

        private void FlushPendingUiRecords()
        {
            if (IsDisplayPaused || _disposed) return;

            List<DataRecord> aRx = new List<DataRecord>();
            List<DataRecord> aTx = new List<DataRecord>();
            List<DataRecord> bRx = new List<DataRecord>();
            List<DataRecord> bTx = new List<DataRecord>();

            int drained = 0;
            while (drained < MaxUiBatchSize && _pendingUiRecords.TryDequeue(out ForwardingDataEventArgs e))
            {
                Interlocked.Decrement(ref _pendingUiRecordCount);
                var record = new DataRecord
                {
                    Timestamp = e.Timestamp,
                    Direction = e.Direction,
                    RawData = e.Data,
                    Description = e.Description,
                    IsChecksumValid = e.IsChecksumValid,
                    Format = SelectDisplayFormat
                };

                switch (e.Direction)
                {
                    case DataDirection.ChannelA_Rx:
                        aRx.Add(record);
                        break;
                    case DataDirection.ChannelA_Tx:
                        aTx.Add(record);
                        break;
                    case DataDirection.ChannelB_Rx:
                        bRx.Add(record);
                        break;
                    case DataDirection.ChannelB_Tx:
                        bTx.Add(record);
                        break;
                }
                drained++;
            }

            AddBatchToRingBuffer(ChannelARxRecords, aRx);
            AddBatchToRingBuffer(ChannelATxRecords, aTx);
            AddBatchToRingBuffer(ChannelBRxRecords, bRx);
            AddBatchToRingBuffer(ChannelBTxRecords, bTx);

        }

        private const int MaxBufferCapacity = 10000;
        private const int MaxPendingUiRecords = 50000;
        private const int MaxUiBatchSize = 1000;
        private const int MaxHistorySearchResults = 20000;
        private const int MaxPendingProtocolParses = 2000;
        private const int MaxRealtimeProtocolResults = 2000;
        /// <summary>
        /// 环形缓冲区推送方法：超过上限时自动移除最老数据 (FR-15)
        /// </summary>
        private static void AddBatchToRingBuffer<T>(RangeObservableCollection<T> collection, IList<T> items)
        {
            if (items == null || items.Count == 0) return;
            int removeCount = collection.Count + items.Count - MaxBufferCapacity;
            if (removeCount > 0) collection.RemoveRangeFromStart(removeCount);
            collection.AddRange(items);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _statsTimer?.Stop();
            _uiFlushTimer?.Stop();
            _historySearchCts?.Cancel();
            _fileSendCts?.Cancel();
            _protocolParseCts.Cancel();
            _protocolParseSignal.Release();
            try { _protocolParseWorker?.Wait(1000); } catch (AggregateException) { }
            StopAutoSendTimer();
            _fileReceiver.FileStarted -= OnFileReceiveStarted;
            _fileReceiver.FileProgress -= OnFileReceiveProgress;
            _fileReceiver.FileCompleted -= OnFileReceiveCompleted;
            _fileReceiver.FileFailed -= OnFileReceiveFailed;
            _rawBurstFileReceiver.FileCompleted -= OnFileReceiveCompleted;
            _rawBurstFileReceiver.FileFailed -= OnFileReceiveFailed;
            _dataFramingService.FrameReady -= OnDisplayFrameReady;
            _dataFramingService.Dispose();
            _rawBurstFileReceiver.Dispose();
            _fileReceiver.Dispose();
            Engine.DataForwarded -= OnDataForwarded;
            Engine.ChannelDisconnectedNotice -= OnChannelDisconnectedNotice;
            Engine.AttachChannelA(null);
            Engine.AttachChannelB(null);

            if (_channelAObj != null)
            {
                _channelAObj.StatusChanged -= OnChannelAStatusChanged;
                try { _channelAObj.CloseAsync().GetAwaiter().GetResult(); } catch { }
                _channelAObj.Dispose();
                _channelAObj = null;
            }
            if (_channelBObj != null)
            {
                _channelBObj.StatusChanged -= OnChannelBStatusChanged;
                try { _channelBObj.CloseAsync().GetAwaiter().GetResult(); } catch { }
                _channelBObj.Dispose();
                _channelBObj = null;
            }

            LogService.Dispose();
            _protocolParseSignal.Dispose();
            _protocolParseCts.Dispose();
        }
    }
}
