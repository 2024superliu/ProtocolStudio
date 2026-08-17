using System;
using System.Collections.Generic;
using System.Linq;
using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// 内置 Modbus RTU 解析器。支持常见读写功能码、异常响应及可选 CRC16/MODBUS 校验。
    /// </summary>
    public sealed class ModbusRtuProtocolParser : IProtocolParser
    {
        private static readonly HashSet<byte> SupportedFunctions = new HashSet<byte>
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x08, 0x0F, 0x10, 0x16, 0x17
        };

        public string Name => "Modbus RTU 解析器";

        public string Description =>
            "解析常用 Modbus RTU 请求、响应和异常报文，显示从站地址、功能码、寄存器/线圈参数及 CRC 状态。";

        public bool CanParse(byte[] data)
        {
            if (data == null || data.Length < 2 || data[0] > 247) return false;
            return SupportedFunctions.Contains((byte)(data[1] & 0x7F));
        }

        public ProtocolParseResult Parse(byte[] data)
        {
            if (!CanParse(data))
                return Failure("不是可识别的 Modbus RTU 报文。", data);

            CrcFrameInfo crc = AnalyzeCrc(data);
            byte[] frame = new byte[crc.PayloadLength];
            Buffer.BlockCopy(data, 0, frame, 0, frame.Length);

            var result = new ProtocolParseResult();
            result.Fields["协议"] = "Modbus RTU";
            result.Fields["原始长度"] = data.Length + " B";
            result.Fields["从站地址"] = FormatSlaveAddress(frame[0]);

            byte rawFunction = frame[1];
            byte function = (byte)(rawFunction & 0x7F);
            bool isException = (rawFunction & 0x80) != 0;
            string functionName = GetFunctionName(function);
            result.Fields["功能码"] = $"0x{rawFunction:X2}（{functionName}{(isException ? "异常响应" : string.Empty)}）";

            bool structureValid;
            string operationSummary;
            if (isException)
            {
                structureValid = ParseException(frame, result.Fields, out operationSummary);
            }
            else
            {
                structureValid = ParseFunction(frame, function, result.Fields, out operationSummary);
            }

            AddCrcFields(result.Fields, crc);
            result.Fields["HEX"] = ChecksumService.FormatBytes(data);

            result.Success = structureValid && (!crc.IsPresent || crc.IsValid);
            if (!structureValid)
                result.Summary = $"从站 {frame[0]}：报文结构或长度不符合 Modbus {functionName} 格式";
            else if (crc.IsPresent && !crc.IsValid)
                result.Summary = $"从站 {frame[0]}：{operationSummary}；CRC错误";
            else
                result.Summary = $"从站 {frame[0]}：{operationSummary}" +
                    (crc.IsPresent ? "；CRC正确" : "；未包含CRC");

            return result;
        }

        private static bool ParseFunction(
            byte[] frame,
            byte function,
            IDictionary<string, string> fields,
            out string summary)
        {
            switch (function)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                    return ParseRead(frame, function, fields, out summary);
                case 0x05:
                    return ParseWriteSingleCoil(frame, fields, out summary);
                case 0x06:
                    return ParseWriteSingleRegister(frame, fields, out summary);
                case 0x08:
                    return ParseDiagnostics(frame, fields, out summary);
                case 0x0F:
                    return ParseWriteMultipleCoils(frame, fields, out summary);
                case 0x10:
                    return ParseWriteMultipleRegisters(frame, fields, out summary);
                case 0x16:
                    return ParseMaskWriteRegister(frame, fields, out summary);
                case 0x17:
                    return ParseReadWriteMultipleRegisters(frame, fields, out summary);
                default:
                    summary = "暂不支持的功能码";
                    return false;
            }
        }

        private static bool ParseRead(
            byte[] frame,
            byte function,
            IDictionary<string, string> fields,
            out string summary)
        {
            string targetName = function == 0x01 ? "线圈" :
                function == 0x02 ? "离散输入" :
                function == 0x03 ? "保持寄存器" : "输入寄存器";

            if (frame.Length == 6)
            {
                ushort startAddress = ReadUInt16(frame, 2);
                ushort quantity = ReadUInt16(frame, 4);
                fields["报文类型"] = "请求";
                fields["起始地址"] = FormatWord(startAddress);
                fields["读取数量"] = quantity.ToString();
                summary = $"读取{targetName}请求，起始地址 {startAddress}，数量 {quantity}";
                return quantity > 0;
            }

            if (frame.Length >= 3 && frame[2] == frame.Length - 3)
            {
                int byteCount = frame[2];
                fields["报文类型"] = "响应";
                fields["数据字节数"] = byteCount.ToString();
                byte[] values = frame.Skip(3).Take(byteCount).ToArray();
                if (function == 0x03 || function == 0x04)
                {
                    if (byteCount % 2 != 0)
                    {
                        fields["寄存器数据"] = ChecksumService.FormatBytes(values);
                        summary = $"读取{targetName}响应，但寄存器数据长度不是偶数";
                        return false;
                    }

                    fields["寄存器数量"] = (byteCount / 2).ToString();
                    fields["寄存器值"] = FormatRegisters(values);
                }
                else
                {
                    fields["位数据"] = ChecksumService.FormatBytes(values);
                }

                summary = $"读取{targetName}响应，返回 {byteCount} 字节数据";
                return true;
            }

            summary = $"无法判断{targetName}请求或响应";
            return false;
        }

        private static bool ParseWriteSingleCoil(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length != 6)
            {
                summary = "写单个线圈报文长度错误";
                return false;
            }

            ushort address = ReadUInt16(frame, 2);
            ushort value = ReadUInt16(frame, 4);
            fields["报文类型"] = "请求/响应回显";
            fields["线圈地址"] = FormatWord(address);
            fields["写入值"] = value == 0xFF00 ? "ON（0xFF00）" :
                value == 0x0000 ? "OFF（0x0000）" : $"非法值 {FormatWord(value)}";
            summary = $"写单个线圈，地址 {address}，值 {(value == 0xFF00 ? "ON" : value == 0 ? "OFF" : "非法")}";
            return value == 0xFF00 || value == 0x0000;
        }

        private static bool ParseWriteSingleRegister(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length != 6)
            {
                summary = "写单个寄存器报文长度错误";
                return false;
            }

            ushort address = ReadUInt16(frame, 2);
            ushort value = ReadUInt16(frame, 4);
            fields["报文类型"] = "请求/响应回显";
            fields["寄存器地址"] = FormatWord(address);
            fields["写入值"] = FormatWord(value);
            summary = $"写单个保持寄存器，地址 {address}，值 {value}";
            return true;
        }

        private static bool ParseDiagnostics(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length != 6)
            {
                summary = "诊断报文长度错误";
                return false;
            }

            ushort subFunction = ReadUInt16(frame, 2);
            ushort diagnosticData = ReadUInt16(frame, 4);
            fields["报文类型"] = "请求/响应";
            fields["子功能码"] = FormatWord(subFunction);
            fields["诊断数据"] = FormatWord(diagnosticData);
            summary = $"诊断功能，子功能码 {subFunction}";
            return true;
        }

        private static bool ParseWriteMultipleCoils(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            return ParseWriteMultiple(frame, false, fields, out summary);
        }

        private static bool ParseWriteMultipleRegisters(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            return ParseWriteMultiple(frame, true, fields, out summary);
        }

        private static bool ParseWriteMultiple(
            byte[] frame,
            bool registers,
            IDictionary<string, string> fields,
            out string summary)
        {
            string targetName = registers ? "寄存器" : "线圈";
            if (frame.Length == 6)
            {
                ushort startAddress = ReadUInt16(frame, 2);
                ushort quantity = ReadUInt16(frame, 4);
                fields["报文类型"] = "响应";
                fields["起始地址"] = FormatWord(startAddress);
                fields["写入数量"] = quantity.ToString();
                summary = $"写多个{targetName}响应，起始地址 {startAddress}，数量 {quantity}";
                return quantity > 0;
            }

            if (frame.Length >= 7 && frame[6] == frame.Length - 7)
            {
                ushort startAddress = ReadUInt16(frame, 2);
                ushort quantity = ReadUInt16(frame, 4);
                int byteCount = frame[6];
                byte[] values = frame.Skip(7).Take(byteCount).ToArray();
                fields["报文类型"] = "请求";
                fields["起始地址"] = FormatWord(startAddress);
                fields["写入数量"] = quantity.ToString();
                fields["数据字节数"] = byteCount.ToString();
                fields[registers ? "寄存器值" : "线圈数据"] = registers
                    ? FormatRegisters(values)
                    : ChecksumService.FormatBytes(values);
                summary = $"写多个{targetName}请求，起始地址 {startAddress}，数量 {quantity}";
                return quantity > 0 && (!registers || byteCount == quantity * 2);
            }

            summary = $"写多个{targetName}报文长度错误";
            return false;
        }

        private static bool ParseMaskWriteRegister(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length != 8)
            {
                summary = "屏蔽写寄存器报文长度错误";
                return false;
            }

            ushort address = ReadUInt16(frame, 2);
            fields["报文类型"] = "请求/响应回显";
            fields["寄存器地址"] = FormatWord(address);
            fields["AND掩码"] = FormatWord(ReadUInt16(frame, 4));
            fields["OR掩码"] = FormatWord(ReadUInt16(frame, 6));
            summary = $"屏蔽写保持寄存器，地址 {address}";
            return true;
        }

        private static bool ParseReadWriteMultipleRegisters(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length >= 3 && frame[2] == frame.Length - 3)
            {
                int byteCount = frame[2];
                byte[] values = frame.Skip(3).Take(byteCount).ToArray();
                fields["报文类型"] = "响应";
                fields["数据字节数"] = byteCount.ToString();
                fields["寄存器值"] = FormatRegisters(values);
                summary = $"读/写多个寄存器响应，返回 {byteCount / 2} 个寄存器";
                return byteCount % 2 == 0;
            }

            if (frame.Length >= 11 && frame[10] == frame.Length - 11)
            {
                ushort readStart = ReadUInt16(frame, 2);
                ushort readQuantity = ReadUInt16(frame, 4);
                ushort writeStart = ReadUInt16(frame, 6);
                ushort writeQuantity = ReadUInt16(frame, 8);
                int byteCount = frame[10];
                fields["报文类型"] = "请求";
                fields["读取起始地址"] = FormatWord(readStart);
                fields["读取数量"] = readQuantity.ToString();
                fields["写入起始地址"] = FormatWord(writeStart);
                fields["写入数量"] = writeQuantity.ToString();
                fields["写入值"] = FormatRegisters(frame.Skip(11).Take(byteCount).ToArray());
                summary = $"读/写多个寄存器请求，读取 {readQuantity} 个，写入 {writeQuantity} 个";
                return byteCount == writeQuantity * 2;
            }

            summary = "读/写多个寄存器报文长度错误";
            return false;
        }

        private static bool ParseException(
            byte[] frame,
            IDictionary<string, string> fields,
            out string summary)
        {
            if (frame.Length != 3)
            {
                summary = "异常响应长度错误";
                return false;
            }

            byte exceptionCode = frame[2];
            fields["报文类型"] = "异常响应";
            fields["异常码"] = $"0x{exceptionCode:X2}（{GetExceptionName(exceptionCode)}）";
            summary = $"{GetFunctionName((byte)(frame[1] & 0x7F))}异常响应：{GetExceptionName(exceptionCode)}";
            return true;
        }

        private static CrcFrameInfo AnalyzeCrc(byte[] data)
        {
            var info = new CrcFrameInfo { PayloadLength = data.Length };
            if (data.Length <= 2) return info;

            bool valid = ChecksumService.VerifyAppendedChecksum(
                WpfProtocolStudio.Enums.ChecksumAlgorithm.Crc16Modbus,
                data,
                out byte[] expected,
                out byte[] actual);
            if (valid)
            {
                info.IsPresent = true;
                info.IsValid = true;
                info.PayloadLength = data.Length - 2;
                info.Expected = expected;
                info.Actual = actual;
                return info;
            }

            if (IsValidPayloadShape(data, data.Length)) return info;
            if (data.Length > 4 && IsValidPayloadShape(data, data.Length - 2))
            {
                info.IsPresent = true;
                info.IsValid = false;
                info.PayloadLength = data.Length - 2;
                info.Expected = expected;
                info.Actual = actual;
            }
            return info;
        }

        private static bool IsValidPayloadShape(byte[] data, int length)
        {
            if (data == null || length < 2 || length > data.Length) return false;
            byte rawFunction = data[1];
            byte function = (byte)(rawFunction & 0x7F);
            if ((rawFunction & 0x80) != 0) return length == 3;

            switch (function)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                    return length == 6 || (length >= 3 && data[2] == length - 3);
                case 0x05:
                case 0x06:
                case 0x08:
                    return length == 6;
                case 0x0F:
                case 0x10:
                    return length == 6 || (length >= 7 && data[6] == length - 7);
                case 0x16:
                    return length == 8;
                case 0x17:
                    return (length >= 3 && data[2] == length - 3) ||
                           (length >= 11 && data[10] == length - 11);
                default:
                    return false;
            }
        }

        private static void AddCrcFields(IDictionary<string, string> fields, CrcFrameInfo crc)
        {
            if (!crc.IsPresent)
            {
                fields["CRC状态"] = "未包含（或已由接收自动校验剥离）";
                return;
            }

            fields["CRC状态"] = crc.IsValid ? "正确" : "错误";
            fields["报文CRC"] = ChecksumService.FormatBytes(crc.Actual);
            fields["计算CRC"] = ChecksumService.FormatBytes(crc.Expected);
        }

        private static ushort ReadUInt16(byte[] data, int offset)
        {
            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static string FormatWord(ushort value)
        {
            return $"{value}（0x{value:X4}）";
        }

        private static string FormatSlaveAddress(byte address)
        {
            return address == 0 ? "0（广播地址）" : $"{address}（0x{address:X2}）";
        }

        private static string FormatRegisters(byte[] values)
        {
            if (values == null || values.Length == 0) return "无";
            var items = new List<string>();
            int registerCount = values.Length / 2;
            int displayedCount = Math.Min(registerCount, 32);
            for (int index = 0; index < displayedCount; index++)
            {
                ushort value = ReadUInt16(values, index * 2);
                items.Add($"[{index}]={value}（0x{value:X4}）");
            }
            if (registerCount > displayedCount) items.Add($"……另有 {registerCount - displayedCount} 个");
            if (values.Length % 2 != 0) items.Add($"尾部单字节 0x{values[values.Length - 1]:X2}");
            return string.Join("，", items);
        }

        private static string GetFunctionName(byte function)
        {
            switch (function)
            {
                case 0x01: return "读线圈";
                case 0x02: return "读离散输入";
                case 0x03: return "读保持寄存器";
                case 0x04: return "读输入寄存器";
                case 0x05: return "写单个线圈";
                case 0x06: return "写单个寄存器";
                case 0x08: return "诊断";
                case 0x0F: return "写多个线圈";
                case 0x10: return "写多个寄存器";
                case 0x16: return "屏蔽写寄存器";
                case 0x17: return "读/写多个寄存器";
                default: return "未知功能";
            }
        }

        private static string GetExceptionName(byte code)
        {
            switch (code)
            {
                case 0x01: return "非法功能";
                case 0x02: return "非法数据地址";
                case 0x03: return "非法数据值";
                case 0x04: return "从站设备故障";
                case 0x05: return "确认";
                case 0x06: return "从站设备忙";
                case 0x08: return "存储奇偶性差错";
                case 0x0A: return "网关路径不可用";
                case 0x0B: return "网关目标设备无响应";
                default: return "未知异常";
            }
        }

        private static ProtocolParseResult Failure(string message, byte[] data)
        {
            var result = new ProtocolParseResult
            {
                Success = false,
                Summary = message
            };
            result.Fields["HEX"] = ChecksumService.FormatBytes(data);
            return result;
        }

        private sealed class CrcFrameInfo
        {
            public bool IsPresent { get; set; }
            public bool IsValid { get; set; }
            public int PayloadLength { get; set; }
            public byte[] Expected { get; set; } = new byte[0];
            public byte[] Actual { get; set; } = new byte[0];
        }
    }
}
