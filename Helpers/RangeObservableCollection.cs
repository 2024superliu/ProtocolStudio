using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace WpfProtocolStudio.Helpers
{
    /// <summary>
    /// 支持批量通知的 ObservableCollection，减少高频报文导致的 UI 刷新次数。
    /// </summary>
    public class RangeObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            if (items == null) return;
            CheckReentrancy();
            bool changed = false;
            foreach (T item in items)
            {
                Items.Add(item);
                changed = true;
            }
            if (changed) RaiseReset();
        }

        public void RemoveRangeFromStart(int count)
        {
            if (count <= 0) return;
            CheckReentrancy();
            int actual = Math.Min(count, Items.Count);
            if (actual <= 0) return;
            List<T> retainedItems = new List<T>(Items.Count - actual);
            for (int i = actual; i < Items.Count; i++) retainedItems.Add(Items[i]);
            Items.Clear();
            foreach (T item in retainedItems) Items.Add(item);
            if (actual > 0) RaiseReset();
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
