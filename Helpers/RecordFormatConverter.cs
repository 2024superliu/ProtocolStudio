using System;
using System.Globalization;
using System.Windows.Data;
using WpfProtocolStudio.Enums;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// 根据当前选中的 DisplayFormat，将 DataRecord 转化为对应的格式化文本内容 (FR-13)
    /// </summary>
    public class RecordFormatConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values != null && values.Length >= 1 && values[0] is DataRecord record)
            {
                DisplayFormat format = record.Format;

                switch (format)
                {
                    case DisplayFormat.Ascii:
                        return record.AsciiContent;
                    case DisplayFormat.Binary:
                        return record.BinaryContent;
                    case DisplayFormat.HexAndAscii:
                        return record.HexAndAsciiContent;
                    case DisplayFormat.Hex:
                    default:
                        return record.HexContent;
                }
            }
            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
