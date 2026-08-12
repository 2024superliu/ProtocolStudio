# 协议解析插件

1. 新建 `.NET Framework 4.8` 类库项目。
2. 引用 `WpfProtocolStudio.exe`。
3. 实现 `WpfProtocolStudio.Interfaces.IProtocolParser`，并提供公共无参构造函数。
4. 编译后把插件 DLL 放入程序目录的 `Plugins` 文件夹。
5. 在“协议辅助”页点击“重新加载插件”。

```csharp
using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;

public sealed class ExampleParser : IProtocolParser
{
    public string Name => "示例协议";
    public string Description => "演示插件解析器";
    public bool CanParse(byte[] data) => data != null && data.Length >= 2;

    public ProtocolParseResult Parse(byte[] data)
    {
        var result = new ProtocolParseResult
        {
            Success = true,
            Summary = "示例协议解析成功"
        };
        result.Fields["命令"] = $"0x{data[0]:X2}";
        result.Fields["长度"] = data[1].ToString();
        return result;
    }
}
```
