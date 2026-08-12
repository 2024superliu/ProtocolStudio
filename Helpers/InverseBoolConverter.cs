using System;
using System.Globalization;
using System.Windows.Data;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// WPF 布尔值取反转换器 (用于 RadioButton 互斥状态绑定)
    /// </summary>
    public class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}
