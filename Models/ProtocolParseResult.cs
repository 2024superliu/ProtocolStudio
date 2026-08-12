using System.Collections.Generic;

namespace WpfProtocolStudio.Models
{
    public sealed class ProtocolParseResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public IDictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }
}
