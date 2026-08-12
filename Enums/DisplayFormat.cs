using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfProtocolStudio.Enums
{
    /// <summary>
    /// 数据显示格式枚举(fr-13)
    /// </summary>
    public enum DisplayFormat
    {
        /// <summary>
        /// 十六进制
        /// </summary>
        Hex,
        /// <summary>
        /// ASCII文本
        /// </summary>
        Ascii,
        /// <summary>
        /// 十六进制+ASCIi组合
        /// </summary>
        HexAndAscii,
        /// <summary>
        /// 二进制
        /// </summary>
        Binary,
    }
}
