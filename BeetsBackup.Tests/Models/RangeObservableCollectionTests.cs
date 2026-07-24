using System.Collections.Specialized;
using System.ComponentModel;
using BeetsBackup.Models;
using FluentAssertions;

namespace BeetsBackup.Tests.Models;

/// <summary>
/// Verifies the bulk-mutation contract of <see cref="RangeObservableCollection{T}"/>:
/// one Reset per bulk operation (never a per-item storm), correct end state, and the
/// Count / indexer property notifications a bound ListView relies on. These are the
/// guarantees Wave 2.2 depends on — a regression here reintroduces the O(N) notification
/// storm the type exists to remove.
/// </summary>
public class RangeObservableCollectionTests
{
    // Records every CollectionChanged the collection raises, so a test can assert the
    // EXACT number and kind of notifications (the whole point of the type).
    private static List<NotifyCollectionChangedEventArgs> RecordCollectionChanged(
        RangeObservableCollection<int> col)
    {
        var events = new List<NotifyCollectionChangedEventArgs>();
        col.CollectionChanged += (_, e) => events.Add(e);
        return events;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddRange_AppendsItems_AndEndsWithExpectedContents()
    {
        var col = new RangeObservableCollection<int> { 1, 2 };

        col.AddRange(new[] { 3, 4, 5 });

        col.Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddRange_RaisesExactlyOneReset_NotOnePerItem()
    {
        var col = new RangeObservableCollection<int>();
        var events = RecordCollectionChanged(col);

        col.AddRange(new[] { 1, 2, 3, 4, 5 });

        events.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AddRange_EmptySequence_RaisesNoNotification()
    {
        // The streaming search flushes a trailing batch that may be empty; an empty flush
        // must stay completely silent so it can't bump a scrolled ListView for nothing.
        var col = new RangeObservableCollection<int> { 1, 2 };
        var events = RecordCollectionChanged(col);

        col.AddRange(Array.Empty<int>());

        events.Should().BeEmpty();
        col.Should().Equal(1, 2);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReplaceAll_ReplacesContents_AndEndsWithExpectedItems()
    {
        var col = new RangeObservableCollection<int> { 1, 2, 3 };

        col.ReplaceAll(new[] { 9, 8 });

        col.Should().Equal(9, 8);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReplaceAll_RaisesExactlyOneReset()
    {
        var col = new RangeObservableCollection<int> { 1, 2, 3 };
        var events = RecordCollectionChanged(col);

        col.ReplaceAll(new[] { 4, 5, 6, 7 });

        events.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ReplaceAll_WithEmptySequence_ClearsAndRaisesOneReset()
    {
        var col = new RangeObservableCollection<int> { 1, 2, 3 };
        var events = RecordCollectionChanged(col);

        col.ReplaceAll(Array.Empty<int>());

        col.Should().BeEmpty();
        events.Should().ContainSingle()
            .Which.Action.Should().Be(NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void BulkOps_RaiseCountAndIndexerPropertyChanged()
    {
        // A bound ListView / ICollectionView reads Count and Item[]; both must be signalled
        // on a bulk change or displayed counts and rows go stale.
        var col = new RangeObservableCollection<int>();
        var props = new List<string?>();
        ((INotifyPropertyChanged)col).PropertyChanged += (_, e) => props.Add(e.PropertyName);

        col.AddRange(new[] { 1, 2, 3 });

        props.Should().Contain("Count");
        props.Should().Contain("Item[]");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Ctor_FromSequence_PrepopulatesInOrder()
    {
        var col = new RangeObservableCollection<int>(new[] { 5, 6, 7 });

        col.Should().Equal(5, 6, 7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InheritedAddAndClear_StillWorkNormally()
    {
        // Existing call sites use Clear()/Add()/Count/indexing unchanged; confirm the
        // suppression flag never leaks and single-item operations behave like the base type.
        var col = new RangeObservableCollection<int>();
        var events = RecordCollectionChanged(col);

        col.Add(1);
        col.Add(2);
        col.Clear();

        // 2 Adds + 1 Reset(from Clear) = 3 individual notifications, none suppressed.
        events.Should().HaveCount(3);
        events[0].Action.Should().Be(NotifyCollectionChangedAction.Add);
        events[1].Action.Should().Be(NotifyCollectionChangedAction.Add);
        events[2].Action.Should().Be(NotifyCollectionChangedAction.Reset);
        col.Should().BeEmpty();
    }
}
