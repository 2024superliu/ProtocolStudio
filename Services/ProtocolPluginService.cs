using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using WpfProtocolStudio.Interfaces;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Services
{
    public sealed class ProtocolPluginLoadResult
    {
        public IList<IProtocolParser> Parsers { get; } = new List<IProtocolParser>();
        public IList<string> Errors { get; } = new List<string>();
        public int ScannedDllCount { get; internal set; }
        public int LoadedExternalParserCount { get; internal set; }
    }

    /// <summary>
    /// FR-29：从Plugins目录发现并实例化IProtocolParser实现。
    /// </summary>
    public static class ProtocolPluginService
    {
        public static ProtocolPluginLoadResult Load(string pluginDirectory)
        {
            var result = new ProtocolPluginLoadResult();
            result.Parsers.Add(new ModbusRtuProtocolParser());
            result.Parsers.Add(new RawBytesProtocolParser());

            try
            {
                Directory.CreateDirectory(pluginDirectory);
                foreach (string filePath in Directory.GetFiles(pluginDirectory, "*.dll"))
                {
                    result.ScannedDllCount++;
                    try
                    {
                        // 从字节加载，避免锁住Plugins目录中的原始DLL；运行中可覆盖同名插件后重新加载。
                        Assembly assembly = Assembly.Load(File.ReadAllBytes(filePath));
                        foreach (Type type in GetLoadableTypes(assembly))
                        {
                            if (type == null || type.IsAbstract || type.IsInterface ||
                                !typeof(IProtocolParser).IsAssignableFrom(type) ||
                                type.GetConstructor(Type.EmptyTypes) == null) continue;

                            var parser = (IProtocolParser)Activator.CreateInstance(type);
                            if (result.Parsers.All(existing =>
                                !string.Equals(existing.Name, parser.Name, StringComparison.OrdinalIgnoreCase)))
                            {
                                result.Parsers.Add(parser);
                                result.LoadedExternalParserCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{Path.GetFileName(filePath)}：{GetBaseMessage(ex)}");
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(GetBaseMessage(ex));
            }

            return result;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(type => type != null);
            }
        }

        private static string GetBaseMessage(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null) current = current.InnerException;
            return current.Message;
        }
    }

    /// <summary>
    /// 内置通用解析器，保证没有外部插件时扩展点也可直接验证。
    /// </summary>
    public sealed class RawBytesProtocolParser : IProtocolParser
    {
        public string Name => "通用字节解析";
        public string Description => "显示长度、HEX、ASCII及首尾字节，不假定具体协议。";
        public bool CanParse(byte[] data) => data != null;

        public ProtocolParseResult Parse(byte[] data)
        {
            byte[] payload = data ?? new byte[0];
            string hex = string.Join(" ", payload.Select(value => value.ToString("X2")));
            string ascii = Encoding.ASCII.GetString(payload)
                .Select(character => character >= 32 && character <= 126 ? character : '.')
                .Aggregate(new StringBuilder(), (builder, character) => builder.Append(character))
                .ToString();

            var result = new ProtocolParseResult
            {
                Success = true,
                Summary = $"通用数据，共 {payload.Length} 字节"
            };
            result.Fields["长度"] = payload.Length + " B";
            result.Fields["HEX"] = hex;
            result.Fields["ASCII"] = ascii;
            if (payload.Length > 0)
            {
                result.Fields["首字节"] = $"0x{payload[0]:X2}";
                result.Fields["尾字节"] = $"0x{payload[payload.Length - 1]:X2}";
            }
            return result;
        }
    }
}
