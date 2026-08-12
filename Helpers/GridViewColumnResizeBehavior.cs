using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WpfProtocolStudio.Models;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// 根据 ListView 的可用宽度按比例调整 GridView 列宽，并为窄窗口保留最小列宽。
    /// </summary>
    public static class GridViewColumnResizeBehavior
    {
        private sealed class ContentWidthCache
        {
            public double Width { get; set; }
        }

        private static readonly ConditionalWeakTable<DataRecord, ContentWidthCache> ContentWidthCaches =
            new ConditionalWeakTable<DataRecord, ContentWidthCache>();

        private sealed class ResizeState
        {
            private INotifyCollectionChanged _collection;
            private bool _updatePending;

            public ListView ListView { get; set; }

            public void Attach()
            {
                INotifyCollectionChanged collection = ListView?.ItemsSource as INotifyCollectionChanged;
                if (ReferenceEquals(_collection, collection)) return;
                Detach();
                _collection = collection;
                if (_collection != null) _collection.CollectionChanged += OnCollectionChanged;
            }

            public void Detach()
            {
                if (_collection != null) _collection.CollectionChanged -= OnCollectionChanged;
                _collection = null;
            }

            private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                if (_updatePending || ListView == null) return;
                _updatePending = true;
                ListView.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    _updatePending = false;
                    UpdateColumns(ListView);
                }));
            }
        }

        private static readonly DependencyProperty ResizeStateProperty =
            DependencyProperty.RegisterAttached(
                "ResizeState",
                typeof(ResizeState),
                typeof(GridViewColumnResizeBehavior),
                new PropertyMetadata(null));

        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(GridViewColumnResizeBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty ColumnWeightsProperty =
            DependencyProperty.RegisterAttached(
                "ColumnWeights",
                typeof(string),
                typeof(GridViewColumnResizeBehavior),
                new PropertyMetadata(string.Empty, OnLayoutPropertyChanged));

        public static readonly DependencyProperty MinimumWidthsProperty =
            DependencyProperty.RegisterAttached(
                "MinimumWidths",
                typeof(string),
                typeof(GridViewColumnResizeBehavior),
                new PropertyMetadata(string.Empty, OnLayoutPropertyChanged));

        public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
        public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
        public static void SetColumnWeights(DependencyObject element, string value) => element.SetValue(ColumnWeightsProperty, value);
        public static string GetColumnWeights(DependencyObject element) => (string)element.GetValue(ColumnWeightsProperty);
        public static void SetMinimumWidths(DependencyObject element, string value) => element.SetValue(MinimumWidthsProperty, value);
        public static string GetMinimumWidths(DependencyObject element) => (string)element.GetValue(MinimumWidthsProperty);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ListView listView)) return;

            listView.Loaded -= OnListViewLoaded;
            listView.Unloaded -= OnListViewUnloaded;
            listView.SizeChanged -= OnListViewSizeChanged;
            DetachCollection(listView);

            if ((bool)e.NewValue)
            {
                listView.Loaded += OnListViewLoaded;
                listView.Unloaded += OnListViewUnloaded;
                listView.SizeChanged += OnListViewSizeChanged;
                if (listView.IsLoaded)
                {
                    AttachCollection(listView);
                    UpdateColumns(listView);
                }
            }
        }

        private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ListView listView && GetIsEnabled(listView) && listView.IsLoaded)
            {
                UpdateColumns(listView);
            }
        }

        private static void OnListViewLoaded(object sender, RoutedEventArgs e)
        {
            var listView = sender as ListView;
            AttachCollection(listView);
            UpdateColumns(listView);
        }

        private static void OnListViewUnloaded(object sender, RoutedEventArgs e)
        {
            DetachCollection(sender as ListView);
        }

        private static void OnListViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) UpdateColumns(sender as ListView);
        }

        private static void UpdateColumns(ListView listView)
        {
            if (!(listView?.View is GridView gridView) || gridView.Columns.Count == 0) return;

            double[] weights = ParseValues(GetColumnWeights(listView), gridView.Columns.Count, 1d);
            double[] minimums = ParseValues(GetMinimumWidths(listView), gridView.Columns.Count, 40d);
            double availableWidth = Math.Max(0d, listView.ActualWidth - 22d);
            double minimumTotal = minimums.Sum();
            double extraWidth = Math.Max(0d, availableWidth - minimumTotal);
            double weightTotal = weights.Sum();

            for (int i = 0; i < gridView.Columns.Count; i++)
            {
                double extra = weightTotal > 0d ? extraWidth * weights[i] / weightTotal : 0d;
                gridView.Columns[i].Width = minimums[i] + extra;
            }

            // 最后一列承载数据正文。内容较长时扩大这一列，让 ListView 自己只在底部
            // 显示一条公共横向滚动条，避免每条记录各出现一个滚动条。
            int contentColumnIndex = gridView.Columns.Count - 1;
            gridView.Columns[contentColumnIndex].Width = Math.Max(
                gridView.Columns[contentColumnIndex].Width,
                EstimateDataContentWidth(listView));
        }

        private static void AttachCollection(ListView listView)
        {
            if (listView == null) return;
            var state = (ResizeState)listView.GetValue(ResizeStateProperty);
            if (state == null)
            {
                state = new ResizeState { ListView = listView };
                listView.SetValue(ResizeStateProperty, state);
            }
            state.Attach();
        }

        private static void DetachCollection(ListView listView)
        {
            if (listView == null) return;
            var state = (ResizeState)listView.GetValue(ResizeStateProperty);
            state?.Detach();
        }

        private static double EstimateDataContentWidth(ListView listView)
        {
            double maximumWidth = 0d;
            double pixelsPerDip = VisualTreeHelper.GetDpi(listView).PixelsPerDip;
            foreach (object item in listView.Items)
            {
                if (!(item is DataRecord record)) continue;
                if (!ContentWidthCaches.TryGetValue(record, out ContentWidthCache cache))
                {
                    string displayText = record.DisplayText ?? string.Empty;
                    var formattedText = new FormattedText(
                        displayText,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Consolas"),
                        listView.FontSize,
                        Brushes.Black,
                        pixelsPerDip);
                    cache = new ContentWidthCache { Width = formattedText.WidthIncludingTrailingWhitespace + 20d };
                    ContentWidthCaches.Add(record, cache);
                }
                if (cache.Width > maximumWidth) maximumWidth = cache.Width;
            }

            // 使用实际显示文本的像素宽度，避免 ASCII/中文内容后出现多余空白区。
            return Math.Min(500000d, maximumWidth);
        }

        private static double[] ParseValues(string source, int count, double fallback)
        {
            string[] parts = (source ?? string.Empty).Split(',');
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                if (i >= parts.Length ||
                    !double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    value < 0d)
                {
                    value = fallback;
                }
                values[i] = value;
            }
            return values;
        }
    }
}
