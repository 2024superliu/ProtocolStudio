using System;
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
