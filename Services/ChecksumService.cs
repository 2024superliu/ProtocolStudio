using System;
using System.Linq;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// FR-28 常用CRC计算。
    /// </summary>
    public static class ChecksumService
    {
        public static string Calculate(ChecksumAlgorithm algorithm, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            switch (algorithm)
            {
                case ChecksumAlgorithm.Crc16Modbus:
                    return $"0x{CalculateCrc16Modbus(data):X4}";
                case ChecksumAlgorithm.Crc16CcittFalse:
                    return $"0x{CalculateCrc16CcittFalse(data):X4}";
                case ChecksumAlgorithm.Crc32:
                    return $"0x{CalculateCrc32(data):X8}";
                default:
                    throw new ArgumentOutOfRangeException(nameof(algorithm));
            }
        }

        /// <summary>
        /// 返回协议线上应追加的校验字节。Modbus按低字节在前，其余算法按高字节在前。
        /// </summary>
        public static byte[] GetChecksumBytes(ChecksumAlgorithm algorithm, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            switch (algorithm)
            {
                case ChecksumAlgorithm.Crc16Modbus:
                    ushort modbus = CalculateCrc16Modbus(data);
                    return new[] { (byte)(modbus & 0xFF), (byte)(modbus >> 8) };
                case ChecksumAlgorithm.Crc16CcittFalse:
                    ushort ccitt = CalculateCrc16CcittFalse(data);
                    return new[] { (byte)(ccitt >> 8), (byte)(ccitt & 0xFF) };
                case ChecksumAlgorithm.Crc32:
                    uint crc32 = CalculateCrc32(data);
                    return new[]
                    {
                        (byte)(crc32 >> 24), (byte)(crc32 >> 16),
                        (byte)(crc32 >> 8), (byte)crc32
                    };
                default:
                    throw new ArgumentOutOfRangeException(nameof(algorithm));
            }
        }

        /// <summary>
        /// 按指定字节序返回校验字节，供可配置的接收端自动验证使用。
        /// </summary>
        public static byte[] GetChecksumBytes(ChecksumAlgorithm algorithm, byte[] data, bool highByteFirst)
        {
            byte[] protocolOrder = GetChecksumBytes(algorithm, data);
            bool protocolIsHighByteFirst = algorithm != ChecksumAlgorithm.Crc16Modbus;
            if (protocolIsHighByteFirst == highByteFirst) return protocolOrder;

            byte[] reversed = (byte[])protocolOrder.Clone();
            Array.Reverse(reversed);
            return reversed;
        }

        public static byte[] AppendChecksum(ChecksumAlgorithm algorithm, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            byte[] checksum = GetChecksumBytes(algorithm, data);
            byte[] result = new byte[data.Length + checksum.Length];
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
            Buffer.BlockCopy(checksum, 0, result, data.Length, checksum.Length);
            return result;
        }

        public static bool VerifyAppendedChecksum(
            ChecksumAlgorithm algorithm,
            byte[] completeFrame,
            out byte[] expected,
            out byte[] actual)
        {
            int checksumLength = algorithm == ChecksumAlgorithm.Crc32 ? 4 : 2;
            if (completeFrame == null || completeFrame.Length <= checksumLength)
            {
                expected = new byte[0];
                actual = completeFrame == null ? new byte[0] : (byte[])completeFrame.Clone();
                return false;
            }

            int payloadLength = completeFrame.Length - checksumLength;
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(completeFrame, 0, payload, 0, payloadLength);
            actual = new byte[checksumLength];
            Buffer.BlockCopy(completeFrame, payloadLength, actual, 0, checksumLength);
            expected = GetChecksumBytes(algorithm, payload);
            return actual.SequenceEqual(expected);
        }

        public static bool VerifyAppendedChecksum(
            ChecksumAlgorithm algorithm,
            byte[] completeFrame,
            bool highByteFirst,
            out byte[] expected,
            out byte[] actual)
        {
            int checksumLength = algorithm == ChecksumAlgorithm.Crc32 ? 4 : 2;
            if (completeFrame == null || completeFrame.Length <= checksumLength)
            {
                expected = new byte[0];
                actual = completeFrame == null ? new byte[0] : (byte[])completeFrame.Clone();
                return false;
            }

            int payloadLength = completeFrame.Length - checksumLength;
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(completeFrame, 0, payload, 0, payloadLength);
            actual = new byte[checksumLength];
            Buffer.BlockCopy(completeFrame, payloadLength, actual, 0, checksumLength);
            expected = GetChecksumBytes(algorithm, payload, highByteFirst);
            return actual.SequenceEqual(expected);
        }

        public static string FormatBytes(byte[] data)
        {
            return data == null ? string.Empty : BitConverter.ToString(data).Replace("-", " ");
        }

        public static ushort CalculateCrc16Modbus(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }

        public static ushort CalculateCrc16CcittFalse(byte[] data)
        {
            ushort crc = 0xFFFF;
            foreach (byte value in data)
            {
                crc ^= (ushort)(value << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            return crc;
        }

        public static uint CalculateCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte value in data)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }
    }
}
