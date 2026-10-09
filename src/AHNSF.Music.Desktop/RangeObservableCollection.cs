using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AHNSF.Music.Desktop;
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceRange(IEnumerable<T> items)
    {
        var incoming = items.ToArray();
        if (Items.SequenceEqual(incoming)) return;
        Items.Clear(); foreach (var item in incoming) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
