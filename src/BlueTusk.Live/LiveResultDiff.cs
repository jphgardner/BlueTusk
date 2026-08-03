using System.Collections.ObjectModel;

namespace BlueTusk.Live;

public enum LiveEventKind
{
    InitialResult,
    RowAdded,
    RowUpdated,
    RowRemoved,
    ResultReordered,
    ResultReset,
}

public enum LiveResetReason
{
    ReplayExpired,
    DiffLimitExceeded,
    QueryShapeChanged,
    SchemaChanged,
    ServerRestart,
}

public sealed class LiveResultEvent<T, TKey>
    where TKey : notnull
{
    internal LiveResultEvent(
        long sequence,
        LiveEventKind kind,
        TKey? key,
        T? row,
        int? previousIndex,
        int? currentIndex,
        IReadOnlyList<T>? rows,
        IReadOnlyList<TKey>? order,
        LiveResetReason? resetReason)
    {
        Sequence = sequence;
        Kind = kind;
        Key = key;
        Row = row;
        PreviousIndex = previousIndex;
        CurrentIndex = currentIndex;
        Rows = rows;
        Order = order;
        ResetReason = resetReason;
    }

    public long Sequence { get; }

    public LiveEventKind Kind { get; }

    public TKey? Key { get; }

    public T? Row { get; }

    public int? PreviousIndex { get; }

    public int? CurrentIndex { get; }

    public IReadOnlyList<T>? Rows { get; }

    public IReadOnlyList<TKey>? Order { get; }

    public LiveResetReason? ResetReason { get; }
}

public sealed class LiveResultSnapshot<T, TKey>
    where TKey : notnull
{
    private readonly ReadOnlyCollection<T> _rows;
    private readonly ReadOnlyCollection<TKey> _keys;

    internal LiveResultSnapshot(IEnumerable<T> rows, IEnumerable<TKey> keys)
    {
        _rows = Array.AsReadOnly(rows.ToArray());
        _keys = Array.AsReadOnly(keys.ToArray());
    }

    public IReadOnlyList<T> Rows => _rows;

    public IReadOnlyList<TKey> Keys => _keys;
}

public sealed record LiveDiffOptions
{
    public int MaximumEventsPerRefresh { get; init; } = 1_024;

    internal void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEventsPerRefresh);
}

public sealed class LiveDiffBatch<T, TKey>
    where TKey : notnull
{
    internal LiveDiffBatch(
        LiveResultSnapshot<T, TKey> snapshot,
        IEnumerable<LiveResultEvent<T, TKey>> events)
    {
        Snapshot = snapshot;
        Events = Array.AsReadOnly(events.ToArray());
    }

    public LiveResultSnapshot<T, TKey> Snapshot { get; }

    public IReadOnlyList<LiveResultEvent<T, TKey>> Events { get; }

    public long LastSequence => Events.Count == 0 ? 0 : Events[^1].Sequence;
}

public static class LiveResultDiffer
{
    public static LiveDiffBatch<T, TKey> Initial<T, TKey>(
        IReadOnlyList<T> rows,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey>? keyComparer = null,
        long sequence = 1)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        var snapshot = CreateSnapshot(rows, keySelector, keyComparer ?? EqualityComparer<TKey>.Default);
        var initial = new LiveResultEvent<T, TKey>(
            sequence,
            LiveEventKind.InitialResult,
            default,
            default,
            null,
            null,
            snapshot.Rows,
            snapshot.Keys,
            null);
        return new LiveDiffBatch<T, TKey>(snapshot, [initial]);
    }

    public static LiveDiffBatch<T, TKey> Diff<T, TKey>(
        LiveResultSnapshot<T, TKey> previous,
        IReadOnlyList<T> currentRows,
        Func<T, TKey> keySelector,
        IEqualityComparer<T>? rowComparer = null,
        IEqualityComparer<TKey>? keyComparer = null,
        LiveDiffOptions? options = null,
        long nextSequence = 1)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(currentRows);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextSequence);
        options ??= new LiveDiffOptions();
        options.Validate();
        rowComparer ??= EqualityComparer<T>.Default;
        keyComparer ??= EqualityComparer<TKey>.Default;

        var current = CreateSnapshot(currentRows, keySelector, keyComparer);
        var oldByKey = previous.Keys
            .Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, keyComparer);
        var newByKey = current.Keys
            .Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, keyComparer);
        var events = new List<LiveResultEvent<T, TKey>>();
        var sequence = nextSequence;

        for (var index = 0; index < previous.Keys.Count; index++)
        {
            var key = previous.Keys[index];
            if (!newByKey.ContainsKey(key))
            {
                events.Add(new LiveResultEvent<T, TKey>(
                    sequence++,
                    LiveEventKind.RowRemoved,
                    key,
                    default,
                    index,
                    null,
                    null,
                    null,
                    null));
            }
        }

        for (var index = 0; index < current.Keys.Count; index++)
        {
            var key = current.Keys[index];
            if (!oldByKey.TryGetValue(key, out var oldIndex))
            {
                events.Add(new LiveResultEvent<T, TKey>(
                    sequence++,
                    LiveEventKind.RowAdded,
                    key,
                    current.Rows[index],
                    null,
                    index,
                    null,
                    null,
                    null));
            }
            else if (!rowComparer.Equals(previous.Rows[oldIndex], current.Rows[index]))
            {
                events.Add(new LiveResultEvent<T, TKey>(
                    sequence++,
                    LiveEventKind.RowUpdated,
                    key,
                    current.Rows[index],
                    oldIndex,
                    index,
                    null,
                    null,
                    null));
            }
        }

        var oldCommon = previous.Keys.Where(newByKey.ContainsKey);
        var newCommon = current.Keys.Where(oldByKey.ContainsKey);
        if (!oldCommon.SequenceEqual(newCommon, keyComparer))
        {
            events.Add(new LiveResultEvent<T, TKey>(
                sequence++,
                LiveEventKind.ResultReordered,
                default,
                default,
                null,
                null,
                null,
                current.Keys,
                null));
        }

        if (events.Count > options.MaximumEventsPerRefresh)
        {
            events.Clear();
            events.Add(new LiveResultEvent<T, TKey>(
                nextSequence,
                LiveEventKind.ResultReset,
                default,
                default,
                null,
                null,
                current.Rows,
                current.Keys,
                LiveResetReason.DiffLimitExceeded));
        }

        return new LiveDiffBatch<T, TKey>(current, events);
    }

    private static LiveResultSnapshot<T, TKey> CreateSnapshot<T, TKey>(
        IReadOnlyList<T> rows,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey> keyComparer)
        where TKey : notnull
    {
        var keys = new TKey[rows.Count];
        var seen = new HashSet<TKey>(keyComparer);
        for (var index = 0; index < rows.Count; index++)
        {
            var key = keySelector(rows[index]) ??
                throw new InvalidOperationException("A live query key selector returned null.");
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"A live query returned duplicate key '{key}'.");
            }

            keys[index] = key;
        }

        return new LiveResultSnapshot<T, TKey>(rows, keys);
    }
}
