using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WpfProtocolStudio.Enums;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// 根据当前选中的通信类型，动态控制对应参数输入框的显示与隐藏 (FR-3)
    /// </summary>
    public class ChannelTypeVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChannelType currentType && parameter is string allowedTypesStr)
            {
                // 支持逗号分隔的多个匹配项，例如 parameter="TcpServer,TcpClient,Udp"
                string[] allowedTypes = allowedTypesStr.Split(',');
                string currentStr = currentType.ToString();

                foreach (string type in allowedTypes)
                {
                    if (currentStr.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return Visibility.Visible;
                    }
                }
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
