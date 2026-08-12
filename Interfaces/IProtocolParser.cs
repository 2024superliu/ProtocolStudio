using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Interfaces
{
    /// <summary>
    /// FR-29 协议解析插件扩展点。插件DLL引用本程序程序集并实现此接口即可加载。
    /// </summary>
    public interface IProtocolParser
    {
        string Name { get; }
        string Description { get; }
        bool CanParse(byte[] data);
        ProtocolParseResult Parse(byte[] data);
    }
}
