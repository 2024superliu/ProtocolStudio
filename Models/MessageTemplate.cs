using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Models
{
    /// <summary>
    /// 常用报文模板数据模型 (FR-23)
    /// </summary>
    public class MessageTemplate
    {
        // 模版名称
        public string Name { get; set; }
        // 报文内容
        public string Content { get; set; }
        // 是否为Hex格式
        public bool IsHex { get; set; } = true;
        public override string ToString()=>$"[{(IsHex?"Hex":"ASCII")}]{Name}";
    }
}
