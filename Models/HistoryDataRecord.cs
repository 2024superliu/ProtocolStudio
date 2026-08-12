using System.Collections.Generic;

namespace WpfProtocolStudio.Models
{
    /// <summary>
    /// 历史数据记录
    /// </summary>
    public class HistoryDataRecord
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
    /// <summary>
    /// 历史数据查询结果
    /// </summary>
    public class HistorySearchResult
    {
        // 历史数据查询
        public IList<HistoryDataRecord> Records { get; set; } = new List<HistoryDataRecord>();
        // 查询数量
        public long ScannedRecordCount { get; set; }
        // 限制查询
        public bool LimitReached { get; set; }
    }
}
