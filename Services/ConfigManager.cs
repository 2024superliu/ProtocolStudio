using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Services
{
    /// <summary>
    /// 配置文件持久化管理器 (保存/加载配置 FR-5)
    /// </summary>
    public static class ConfigManager
    {
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config.json");
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        /// <summary>
        /// 将配置导出保存为文件
        /// </summary>
        public static bool SaveProfile(ChannelConfigProfile profile,string filePath = null)
        {
            try
            {
                string path = filePath ?? ConfigPath;
                string json = Serializer.Serialize(profile);
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, json, Encoding.UTF8);
                return true;
            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// 从文件加载配置文件
        /// </summary>
        public static ChannelConfigProfile LoadProfile(string filePath = null)
        {
            try
            {
                string path = filePath ?? ConfigPath;
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                return Serializer.Deserialize<ChannelConfigProfile>(json);

            }
            catch
            {
                return null;
            }
        }
    }
}
