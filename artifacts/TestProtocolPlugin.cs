using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;

public sealed class TestProtocolParser : IProtocolParser
{
    public string Name => "Phase2TestProtocol";
    public string Description => "External protocol plugin discovery test.";
    public bool CanParse(byte[] data) => data != null && data.Length >= 2 && data[0] == 0xAA;

    public ProtocolParseResult Parse(byte[] data)
    {
        var result = new ProtocolParseResult { Success = true, Summary = "Parsed" };
        result.Fields["Command"] = $"0x{data[0]:X2}";
        result.Fields["Length"] = data[1].ToString();
        return result;
    }
}
