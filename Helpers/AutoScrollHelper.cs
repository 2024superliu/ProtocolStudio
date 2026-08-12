using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// WPF ListView 自动滚动至最底部附加属性服务
    /// 当 ObservableCollection 列表追加新收发数据时，自动触发 ScrollIntoView
    /// </summary>
    public static class AutoScrollHelper
    {
        public static readonly DependencyProperty AutoScrollProperty =
            DependencyProperty.RegisterAttached(
                "AutoScroll",
                typeof(bool),
                typeof(AutoScrollHelper),
                new PropertyMetadata(false, OnAutoScrollChanged));

        public static bool GetAutoScroll(DependencyObject obj)
        {
            return (bool)obj.GetValue(AutoScrollProperty);
        }

        public static void SetAutoScroll(DependencyObject obj, bool value)
        {
            obj.SetValue(AutoScrollProperty, value);
        }

        private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ListView listView)
            {
                if ((bool)e.NewValue)
                {
                    ((INotifyCollectionChanged)listView.Items).CollectionChanged += (s, args) =>
                    {
                        if (args.Action == NotifyCollectionChangedAction.Add ||
                            args.Action == NotifyCollectionChangedAction.Reset)
                        {
                            if (listView.Items.Count > 0)
                            {
                                try
                                {
                                    listView.ScrollIntoView(listView.Items[listView.Items.Count - 1]);
                                }
                                catch { }
                            }
                        }
                    };
                }
            }
        }
    }
}
