using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// 常用报文模板持久化，保存到当前用户本地应用数据目录。
    /// </summary>
    public static class MessageTemplateManager
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly string TemplatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WpfProtocolStudio", "MessageTemplates.json");

        public static IList<MessageTemplate> Load()
        {
            try
            {
                if (!File.Exists(TemplatePath)) return new List<MessageTemplate>();
                string json = File.ReadAllText(TemplatePath, Encoding.UTF8);
                return Serializer.Deserialize<List<MessageTemplate>>(json) ?? new List<MessageTemplate>();
            }
            catch
            {
                return new List<MessageTemplate>();
            }
        }

        public static bool Save(IEnumerable<MessageTemplate> templates)
        {
            try
            {
                string directory = Path.GetDirectoryName(TemplatePath);
                Directory.CreateDirectory(directory);
                string json = Serializer.Serialize(templates);
                File.WriteAllText(TemplatePath, json, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
