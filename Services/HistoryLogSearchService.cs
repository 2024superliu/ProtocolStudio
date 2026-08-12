using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Services
{
    public static class HistoryLogSearchService
    {
        private const int BinaryBlockSize = 32;
        private const int MaximumBinaryPayloadLength = 64 * 1024 * 1024;
        private const int MaximumBinaryDescriptionLength = 4 * 1024 * 1024;
        private static readonly byte[] StructuredBinaryMagic = { (byte)'W', (byte)'P', (byte)'S', (byte)'B' };
        private static readonly Regex TxtRecordRegex = new Regex(
            @"^\[(?<time>[^\]]+)\]\[(?<direction>[^\]]+)\]Len:\[(?<length>\d+)\]\s*HEX:(?<hex>.*?)\s*\|\s*ASCII:(?<tail>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ExportTxtRecordRegex = new Regex(
            @"^\[(?<time>[^\]]+)\]\s*\[(?<direction>Channel[AB]_[RT]x)\]\s*\[长度:(?<length>\d+)B\]\s*HEX:\s*(?<hex>.*?)\s*\|\s*ASCII:\s*(?<tail>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly Regex Fr17TxtRecordRegex = new Regex(
            @"^\[时间戳:(?<time>[^\]]+)\]\[方向:(?<direction>[^\]]+)\]\[所属端:(?<endpoint>[^\]]+)\]\[数据长度:(?<length>\d+)B\]\s*数据内容:\s*HEX=(?<hex>.*?)\s*\|\s*ASCII=(?<tail>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SplitDirectionTxtRecordRegex = new Regex(
            @"^\[(?<time>[^\]]+)\]\s*\[(?<direction>Rx|Tx)\]\s*\[(?<endpoint>ChannelA|ChannelB)\]\s*\[(?:长度:)?(?<length>\d+)B\]\s*HEX:\s*(?<hex>.*?)\s*\|\s*ASCII:\s*(?<tail>.*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static Task<HistorySearchResult> SearchAsync(
            IEnumerable<string> filePaths,
            string keyword,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            string[] files = (filePaths ?? Enumerable.Empty<string>())
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return Task.Run(() => Search(files, keyword, Math.Max(1, maximumResults), cancellationToken), cancellationToken);
        }

        private static HistorySearchResult Search(
            IEnumerable<string> filePaths,
            string keyword,
            int maximumResults,
            CancellationToken cancellationToken)
        {
            var result = new HistorySearchResult();
            var records = new List<HistoryDataRecord>();

            foreach (string filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(filePath).ToLowerInvariant();

                if (extension == ".bin")
                {
                    SearchBinary(filePath, keyword, maximumResults, records, result, cancellationToken);
                }
                else if (extension == ".csv")
                {
                    SearchCsv(filePath, keyword, maximumResults, records, result, cancellationToken);
                }
                else
                {
                    SearchTxt(filePath, keyword, maximumResults, records, result, cancellationToken);
                }

                if (records.Count >= maximumResults)
                {
                    result.LimitReached = true;
                    break;
                }
            }

            result.Records = records;
            return result;
        }

        private static void SearchTxt(
            string filePath,
            string keyword,
            int maximumResults,
            ICollection<HistoryDataRecord> records,
            HistorySearchResult result,
            CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    result.ScannedRecordCount++;

                    HistoryDataRecord record = ParseTxtRecord(filePath, line);
                    if (record != null && Matches(record, line, keyword)) records.Add(record);
                    if (records.Count >= maximumResults) return;
                }
            }
        }

        private static void SearchCsv(
            string filePath,
            string keyword,
            int maximumResults,
            ICollection<HistoryDataRecord> records,
            HistorySearchResult result,
            CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IList<string> fields = ParseCsvLine(line);
                    if (fields.Count < 5) continue;

                    int length = 0;
                    bool isFr17Format = fields.Count >= 7 && int.TryParse(fields[3], out length);
                    if (!isFr17Format && !int.TryParse(fields[2], out length)) continue;
                    result.ScannedRecordCount++;

                    string rawDirection = fields[1];
                    string endpoint = isFr17Format ? fields[2] : GetEndpointText(rawDirection);
                    string hex = isFr17Format ? fields[4] : fields[3];
                    var record = new HistoryDataRecord
                    {
                        FileName = Path.GetFileName(filePath),
                        TimestampText = fields[0],
                        DirectionText = GetDirectionText(rawDirection),
                        EndpointText = endpoint,
                        Length = length,
                        HexContent = hex,
                        AsciiContent = isFr17Format ? fields[5] : fields[4],
                        BinaryContent = ToBinaryContent(hex),
                        Description = isFr17Format ? fields[6] : (fields.Count > 6 ? fields[6] : (fields.Count > 5 ? fields[5] : string.Empty))
                    };

                    if (Matches(record, line, keyword)) records.Add(record);
                    if (records.Count >= maximumResults) return;
                }
            }
        }

        private static void SearchBinary(
            string filePath,
            string keyword,
            int maximumResults,
            ICollection<HistoryDataRecord> records,
            HistorySearchResult result,
            CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (IsStructuredBinary(stream))
                {
                    SearchStructuredBinary(filePath, stream, keyword, maximumResults, records, result, cancellationToken);
                    return;
                }

                SearchLegacyRawBinary(filePath, stream, keyword, maximumResults, records, result, cancellationToken);
            }
        }

        private static void SearchStructuredBinary(
            string filePath,
            Stream stream,
            string keyword,
            int maximumResults,
            ICollection<HistoryDataRecord> records,
            HistorySearchResult result,
            CancellationToken cancellationToken)
        {
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                while (stream.Length - stream.Position >= 22)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] magic = reader.ReadBytes(StructuredBinaryMagic.Length);
                    if (!magic.SequenceEqual(StructuredBinaryMagic)) return;

                    byte version = reader.ReadByte();
                    if (version != LogService.BinaryRecordVersion) return;

                    long ticks = reader.ReadInt64();
                    byte directionValue = reader.ReadByte();
                    int payloadLength = reader.ReadInt32();
                    int descriptionLength = reader.ReadInt32();
                    if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks ||
                        directionValue > (byte)DataDirection.ChannelB_Tx ||
                        payloadLength < 0 || payloadLength > MaximumBinaryPayloadLength ||
                        descriptionLength < 0 || descriptionLength > MaximumBinaryDescriptionLength ||
                        stream.Length - stream.Position < (long)payloadLength + descriptionLength)
                    {
                        return;
                    }

                    byte[] data = reader.ReadBytes(payloadLength);
                    string description = Encoding.UTF8.GetString(reader.ReadBytes(descriptionLength));
                    DataDirection direction = (DataDirection)directionValue;
                    string rawDirection = direction.ToString();
                    string hex = string.Join(" ", data.Select(b => b.ToString("X2")));
                    string ascii = ToVisibleAscii(data);
                    result.ScannedRecordCount++;

                    var record = new HistoryDataRecord
                    {
                        FileName = Path.GetFileName(filePath),
                        TimestampText = new DateTime(ticks).ToString("yyyy-MM-dd HH:mm:ss.fff"),
                        DirectionText = GetDirectionText(rawDirection),
                        EndpointText = GetEndpointText(rawDirection),
                        Length = payloadLength,
                        HexContent = hex,
                        AsciiContent = ascii,
                        BinaryContent = string.Join(" ", data.Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))),
                        Description = description
                    };

                    if (Matches(record, hex + " " + ascii, keyword)) records.Add(record);
                    if (records.Count >= maximumResults) return;
                }
            }
        }

        private static void SearchLegacyRawBinary(
            string filePath,
            Stream stream,
            string keyword,
            int maximumResults,
            ICollection<HistoryDataRecord> records,
            HistorySearchResult result,
            CancellationToken cancellationToken)
        {
            stream.Position = 0;
            {
                var buffer = new byte[BinaryBlockSize];
                long offset = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.ScannedRecordCount++;

                    byte[] data = new byte[read];
                    Buffer.BlockCopy(buffer, 0, data, 0, read);
                    string hex = string.Join(" ", data.Select(b => b.ToString("X2")));
                    string ascii = ToVisibleAscii(data);
                    var record = new HistoryDataRecord
                    {
                        FileName = Path.GetFileName(filePath),
                        TimestampText = $"偏移 0x{offset:X8}",
                        DirectionText = GetDirectionText(Path.GetFileNameWithoutExtension(filePath)),
                        EndpointText = GetEndpointText(Path.GetFileNameWithoutExtension(filePath)),
                        Length = read,
                        HexContent = hex,
                        AsciiContent = ascii,
                        BinaryContent = string.Join(" ", data.Select(b => Convert.ToString(b, 2).PadLeft(8, '0'))),
                        Description = "BIN 原始字节块"
                    };

                    if (Matches(record, hex + " " + ascii, keyword)) records.Add(record);
                    if (records.Count >= maximumResults) return;
                    offset += read;
                }
            }
        }

        private static bool IsStructuredBinary(Stream stream)
        {
            if (stream.Length < StructuredBinaryMagic.Length + 1) return false;
            long originalPosition = stream.Position;
            try
            {
                stream.Position = 0;
                var header = new byte[StructuredBinaryMagic.Length];
                if (stream.Read(header, 0, header.Length) != header.Length) return false;
                int version = stream.ReadByte();
                return header.SequenceEqual(StructuredBinaryMagic) && version == LogService.BinaryRecordVersion;
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }

        private static HistoryDataRecord ParseTxtRecord(string filePath, string line)
        {
            Match match = TxtRecordRegex.Match(line);
            bool isExportRecord = false;
            bool isFr17Record = false;
            bool isSplitDirectionRecord = false;
            if (!match.Success)
            {
                match = ExportTxtRecordRegex.Match(line);
                isExportRecord = match.Success;
            }
            if (!match.Success)
            {
                match = Fr17TxtRecordRegex.Match(line);
                isFr17Record = match.Success;
            }
            if (!match.Success)
            {
                match = SplitDirectionTxtRecordRegex.Match(line);
                isSplitDirectionRecord = match.Success;
            }
            if (!match.Success || !int.TryParse(match.Groups["length"].Value, out int length)) return null;

            string tail = match.Groups["tail"].Value;
            string descriptionMarker = isExportRecord || isFr17Record || isSplitDirectionRecord ? " | 备注:" : " | ";
            int descriptionSeparator = tail.IndexOf(descriptionMarker, StringComparison.Ordinal);
            string ascii = descriptionSeparator >= 0 ? tail.Substring(0, descriptionSeparator) : tail;
            string description = descriptionSeparator >= 0
                ? tail.Substring(descriptionSeparator + descriptionMarker.Length).TrimStart()
                : string.Empty;
            string time = AddDateFromFileName(filePath, match.Groups["time"].Value);
            string hex = match.Groups["hex"].Value.Trim();
            string rawDirection = match.Groups["direction"].Value;

            return new HistoryDataRecord
            {
                FileName = Path.GetFileName(filePath),
                TimestampText = time,
                DirectionText = GetDirectionText(rawDirection),
                EndpointText = isFr17Record || isSplitDirectionRecord
                    ? GetEndpointText(match.Groups["endpoint"].Value)
                    : GetEndpointText(rawDirection),
                Length = length,
                HexContent = hex,
                AsciiContent = ascii,
                BinaryContent = ToBinaryContent(hex),
                Description = description
            };
        }

        private static IList<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var value = new StringBuilder();
            bool insideQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (insideQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        insideQuotes = !insideQuotes;
                    }
                }
                else if (c == ',' && !insideQuotes)
                {
                    fields.Add(value.ToString());
                    value.Clear();
                }
                else
                {
                    value.Append(c);
                }
            }

            fields.Add(value.ToString());
            return fields;
        }

        private static bool Matches(HistoryDataRecord record, string sourceLine, string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return true;
            string value = keyword.Trim();
            var fields = new[]
            {
                sourceLine,
                record.FileName,
                record.TimestampText,
                record.DirectionText,
                record.EndpointText,
                record.Length.ToString(CultureInfo.InvariantCulture),
                record.HexContent,
                record.AsciiContent,
                record.BinaryContent,
                record.Description
            };

            if (fields.Any(field => !string.IsNullOrEmpty(field) && field.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0))
                return true;

            string compactKeyword = RemoveWhitespace(value);
            string compactHex = RemoveWhitespace(record.HexContent);
            return compactKeyword.Length >= 2 &&
                   compactKeyword.All(Uri.IsHexDigit) &&
                   compactHex.IndexOf(compactKeyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RemoveWhitespace(string value)
        {
            return new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        private static string AddDateFromFileName(string filePath, string time)
        {
            if (Regex.IsMatch(time ?? string.Empty, @"^\d{4}-\d{2}-\d{2}")) return time;
            string name = Path.GetFileName(filePath);
            if (name.Length >= 10 && DateTime.TryParseExact(name.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return name.Substring(0, 10) + " " + time;
            return time;
        }

        private static string GetEndpointText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未知";
            if (value.IndexOf("ChannelA", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("A端", StringComparison.OrdinalIgnoreCase) >= 0) return "A端";
            if (value.IndexOf("ChannelB", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("B端", StringComparison.OrdinalIgnoreCase) >= 0) return "B端";
            return "未知";
        }

        private static string GetDirectionText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未知";
            if (value.Equals("Rx", StringComparison.OrdinalIgnoreCase) || value.IndexOf("_Rx", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("接收", StringComparison.OrdinalIgnoreCase) >= 0) return "接收";
            if (value.Equals("Tx", StringComparison.OrdinalIgnoreCase) || value.IndexOf("_Tx", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("发送", StringComparison.OrdinalIgnoreCase) >= 0) return "发送";
            return value;
        }

        private static string ToBinaryContent(string hex)
        {
            var bytes = new List<byte>();
            string[] parts = (hex ?? string.Empty).Split(new[] { ' ', '\t', '-' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value)) bytes.Add(value);
            }
            return string.Join(" ", bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        }

        private static string ToVisibleAscii(IEnumerable<byte> data)
        {
            var builder = new StringBuilder();
            foreach (byte value in data)
            {
                char c = (char)value;
                builder.Append(c >= 32 && c <= 126 ? c : '.');
            }
            return builder.ToString();
        }
    }
}
