using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace BeetsBackup.Models;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that supports bulk mutation with a SINGLE
/// change notification instead of one per item.
///
/// WHY THIS EXISTS (for future maintainers):
///   A plain ObservableCollection raises one CollectionChanged event per Add. Loading a
///   large folder (or streaming search results) therefore fires N events — and every WPF
///   binding and ICollectionView attached to the collection reacts to each one, so a
///   50,000-item folder becomes 50,000 layout/refresh passes. That is the "jank" this type
///   removes: <see cref="AddRange"/> and <see cref="ReplaceAll"/> mutate the backing list
///   with notifications suppressed, then raise ONE Reset. WPF treats a Reset as "re-read
///   everything", which with UI virtualization only touches the visible rows — far cheaper
///   than N Adds.
///
/// WHY RESET (and not a range-Add event):
///   NotifyCollectionChangedAction.Add with multiple items is legal for the event args but
///   WPF's CollectionView throws NotSupportedException on it ("Range actions are not
///   supported"). Reset is the only bulk signal WPF's binding layer accepts. The one
///   trade-off Reset carries: it clears the ListView's current selection and can return the
///   scroll to the top. That is a non-issue for a one-shot load (the caller cleared the pane
///   first anyway), but callers streaming many batches into a list the user might be
///   scrolling should be aware of it — see the note on <see cref="AddRange"/>.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    // When true, base CollectionChanged is swallowed so a bulk mutation stays silent until
    // we raise the single Reset ourselves.
    private bool _suppressNotification;

    /// <summary>Creates an empty collection.</summary>
    public RangeObservableCollection() { }

    /// <summary>Creates a collection pre-populated from <paramref name="items"/>.</summary>
    public RangeObservableCollection(IEnumerable<T> items) : base(items) { }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification) base.OnCollectionChanged(e);
    }

    /// <summary>
    /// Clears the collection and refills it from <paramref name="items"/>, raising exactly
    /// one Reset. Use for a bulk swap (e.g. navigating a pane to a new folder).
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotification = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally { _suppressNotification = false; }
        RaiseReset();
    }

    /// <summary>
    /// Appends <paramref name="items"/> to the end, raising exactly one Reset for the whole
    /// batch instead of one Add per element. Use for streaming appends (e.g. search results).
    ///
    /// NOTE for streaming callers: because the signal is a Reset (see the type remarks on why
    /// a range-Add is not an option in WPF), each batch can bump a scrolled ListView back to
    /// the top. That is acceptable while results are still filling in, but it is the one
    /// behaviour difference from per-item Add — worth a glance during a manual search smoke test.
    /// </summary>
    public void AddRange(IEnumerable<T> items)
    {
        // Capture whether anything was actually added, so an empty batch stays completely
        // silent (no spurious Reset) — matters for the streaming search flush of an empty tail.
        bool added = false;
        _suppressNotification = true;
        try
        {
            foreach (var item in items) { Items.Add(item); added = true; }
        }
        finally { _suppressNotification = false; }
        if (added) RaiseReset();
    }

    // The full "everything changed" signal: the Reset event plus the Count / indexer property
    // notifications ObservableCollection normally raises alongside a mutation, so bindings to
    // Count and to Item[] update too.
    private void RaiseReset()
    {
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
