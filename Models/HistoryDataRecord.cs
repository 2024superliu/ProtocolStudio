using System.Collections.Generic;

namespace WpfProtocolStudio.Models
{
    public sealed class HistoryDataRecord
    {
        public string FileName { get; set; }
        public string TimestampText { get; set; }
        public string DirectionText { get; set; }
        public string EndpointText { get; set; }
        public int Length { get; set; }
        public string HexContent { get; set; }
        public string AsciiContent { get; set; }
        public string BinaryContent { get; set; }
        public string Description { get; set; }
    }

    public sealed class HistorySearchResult
    {
        public IList<HistoryDataRecord> Records { get; set; } = new List<HistoryDataRecord>();
        public long ScannedRecordCount { get; set; }
        public bool LimitReached { get; set; }
    }
}
