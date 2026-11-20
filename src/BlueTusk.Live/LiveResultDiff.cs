using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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

    [JsonPropertyName("sequence")]
    public long Sequence { get; }

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter<LiveEventKind>))]
    public LiveEventKind Kind { get; }

    [JsonPropertyName("key")]
    public TKey? Key { get; }

    [JsonPropertyName("row")]
    public T? Row { get; }

    [JsonPropertyName("previousIndex")]
    public int? PreviousIndex { get; }

    [JsonPropertyName("currentIndex")]
    public int? CurrentIndex { get; }

    [JsonPropertyName("rows")]
    public IReadOnlyList<T>? Rows { get; }

    [JsonPropertyName("order")]
    public IReadOnlyList<TKey>? Order { get; }

    [JsonPropertyName("resetReason")]
    [JsonConverter(typeof(JsonStringEnumConverter<LiveResetReason>))]
    public LiveResetReason? ResetReason { get; }
}

public sealed class LiveResultSnapshot<T, TKey>
    where TKey : notnull
{
    private readonly LiveSnapshotRows<T> _ownedRows;
    private readonly TKey[] _ownedKeys;
    private readonly ReadOnlyCollection<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyIndexes;

    private LiveResultSnapshot(
        LiveSnapshotRows<T> rows,
        TKey[] keys,
        Dictionary<TKey, int> keyIndexes)
    {
        _ownedRows = rows;
        _ownedKeys = keys;
        _keys = Array.AsReadOnly(keys);
        _keyIndexes = keyIndexes;
    }

    public IReadOnlyList<T> Rows => _ownedRows;

    public IReadOnlyList<TKey> Keys => _keys;

    internal Dictionary<TKey, int> KeyIndexes => _keyIndexes;

    internal LiveSnapshotRows<T> OwnedRows => _ownedRows;

    internal TKey[] OwnedKeys => _ownedKeys;

    internal IEqualityComparer<TKey> KeyComparer => _keyIndexes.Comparer;

    internal static LiveResultSnapshot<T, TKey> CreateOwned(
        LiveSnapshotRows<T> rows,
        TKey[] keys,
        Dictionary<TKey, int> keyIndexes) =>
        new(rows, keys, keyIndexes);
}

public sealed record LiveDiffOptions
{
    public int MaximumEventsPerRefresh { get; init; } = 1_024;

    internal void Validate() => BlueTuskGuard.NotNegativeOrZero(MaximumEventsPerRefresh);
}

public sealed class LiveDiffBatch<T, TKey>
    where TKey : notnull
{
    internal LiveDiffBatch(
        LiveResultSnapshot<T, TKey> snapshot,
        IEnumerable<LiveResultEvent<T, TKey>> events)
        : this(snapshot, events.ToArray())
    {
    }

    private LiveDiffBatch(
        LiveResultSnapshot<T, TKey> snapshot,
        LiveResultEvent<T, TKey>[] events)
    {
        Snapshot = snapshot;
        Events = Array.AsReadOnly(events);
    }

    public LiveResultSnapshot<T, TKey> Snapshot { get; }

    public IReadOnlyList<LiveResultEvent<T, TKey>> Events { get; }

    public long LastSequence => Events.Count == 0 ? 0 : Events[^1].Sequence;

    internal static LiveDiffBatch<T, TKey> CreateOwned(
        LiveResultSnapshot<T, TKey> snapshot,
        LiveResultEvent<T, TKey>[] events)
    {
        BlueTuskGuard.NotNull(snapshot);
        BlueTuskGuard.NotNull(events);
        return new LiveDiffBatch<T, TKey>(snapshot, events);
    }
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
        BlueTuskGuard.NotNull(rows);
        BlueTuskGuard.NotNull(keySelector);
        BlueTuskGuard.NotNegativeOrZero(sequence);
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
        return LiveDiffBatch<T, TKey>.CreateOwned(snapshot, [initial]);
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
        BlueTuskGuard.NotNull(previous);
        BlueTuskGuard.NotNull(currentRows);
        BlueTuskGuard.NotNull(keySelector);
        BlueTuskGuard.NotNegativeOrZero(nextSequence);
        options ??= new LiveDiffOptions();
        options.Validate();
        rowComparer ??= EqualityComparer<T>.Default;
        keyComparer ??= EqualityComparer<TKey>.Default;

        var current = CreateSnapshot(currentRows, keySelector, keyComparer, previous);
        var oldByKey = GetKeyIndexes(previous, keyComparer);
        var newByKey = current.KeyIndexes;
        var sameKeyPositions = ReferenceEquals(oldByKey, newByKey);
        var events = new List<LiveResultEvent<T, TKey>>();
        var sequence = nextSequence;

        if (!sameKeyPositions)
        {
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
        }

        var previousCommonIndex = -1;
        var orderChanged = false;
        for (var index = 0; index < current.Keys.Count; index++)
        {
            var key = current.Keys[index];
            var oldIndex = index;
            if (!sameKeyPositions && !oldByKey.TryGetValue(key, out oldIndex))
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
                continue;
            }

            // The shared keys retain their order when their positions in the old result increase.
            orderChanged |= oldIndex < previousCommonIndex;
            previousCommonIndex = oldIndex;
            if (!rowComparer.Equals(previous.Rows[oldIndex], current.Rows[index]))
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

        if (orderChanged)
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

        return LiveDiffBatch<T, TKey>.CreateOwned(current, events.ToArray());
    }

    /// <summary>
    /// Applies replacements for an explicitly affected set whose result membership and order are unchanged.
    /// Rows use persistent storage and the existing key index is shared, so replacements
    /// copy only tree paths and preserve every historical snapshot without cloning all rows.
    /// </summary>
    public static LiveDiffBatch<T, TKey> DiffAffected<T, TKey>(
        LiveResultSnapshot<T, TKey> previous,
        IReadOnlyList<T> affectedRows,
        Func<T, TKey> keySelector,
        IEqualityComparer<T>? rowComparer = null,
        LiveDiffOptions? options = null,
        long nextSequence = 1)
        where TKey : notnull
    {
        BlueTuskGuard.NotNull(previous);
        BlueTuskGuard.NotNull(affectedRows);
        BlueTuskGuard.NotNull(keySelector);
        BlueTuskGuard.NotNegativeOrZero(nextSequence);
        options ??= new LiveDiffOptions();
        options.Validate();
        rowComparer ??= EqualityComparer<T>.Default;

        var rows = previous.OwnedRows.ToBuilder();
        var events = new List<LiveResultEvent<T, TKey>>(
            Math.Min(affectedRows.Count, options.MaximumEventsPerRefresh + 1));
        var seen = new HashSet<TKey>(previous.KeyComparer);
        var sequence = nextSequence;
        foreach (var row in affectedRows)
        {
            BlueTuskGuard.NotNull(row);
            var key = keySelector(row) ??
                throw new InvalidOperationException("A live query key selector returned null.");
            if (!seen.Add(key))
            {
                throw new InvalidOperationException($"An affected live result contained duplicate key '{key}'.");
            }

            if (!previous.KeyIndexes.TryGetValue(key, out var index))
            {
                throw new InvalidOperationException(
                    $"Affected-key snapshot mutation cannot add result key '{key}' or change result order.");
            }

            if (rowComparer.Equals(previous.Rows[index], row))
            {
                continue;
            }

            rows[index] = row;
            events.Add(new LiveResultEvent<T, TKey>(
                sequence++,
                LiveEventKind.RowUpdated,
                key,
                row,
                index,
                index,
                null,
                null,
                null));
        }

        var snapshot = events.Count == 0 ? previous : LiveResultSnapshot<T, TKey>.CreateOwned(
            rows.ToImmutable(),
            previous.OwnedKeys,
            previous.KeyIndexes);
        if (events.Count > options.MaximumEventsPerRefresh)
        {
            var reset = new LiveResultEvent<T, TKey>(
                nextSequence,
                LiveEventKind.ResultReset,
                default,
                default,
                null,
                null,
                snapshot.Rows,
                snapshot.Keys,
                LiveResetReason.DiffLimitExceeded);
            return LiveDiffBatch<T, TKey>.CreateOwned(snapshot, [reset]);
        }

        return LiveDiffBatch<T, TKey>.CreateOwned(snapshot, events.ToArray());
    }

    private static LiveResultSnapshot<T, TKey> CreateSnapshot<T, TKey>(
        IReadOnlyList<T> rows,
        Func<T, TKey> keySelector,
        IEqualityComparer<TKey> keyComparer,
        LiveResultSnapshot<T, TKey>? previous = null)
        where TKey : notnull
    {
        var ownedRows = LiveSnapshotRows<T>.CreateBuilder(rows.Count);
        var keys = new TKey[rows.Count];
        // Reuse the index when each new key compares equal at its existing position. Keep the
        // new keys themselves, since a comparer can treat distinct representations as equal.
        var reusableKeys = previous is not null && previous.Keys.Count == rows.Count &&
            ReferenceEquals(previous.KeyComparer, keyComparer) ? previous.OwnedKeys : null;
        var keyIndexes = reusableKeys is null ? new Dictionary<TKey, int>(rows.Count, keyComparer) : null;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var key = keySelector(row) ??
                throw new InvalidOperationException("A live query key selector returned null.");
            if (keyIndexes is null && !keyComparer.Equals(reusableKeys![index], key))
            {
                keyIndexes = new Dictionary<TKey, int>(rows.Count, keyComparer);
                for (var previousIndex = 0; previousIndex < index; previousIndex++)
                {
                    keyIndexes.Add(keys[previousIndex], previousIndex);
                }
            }
            if (keyIndexes is not null && !keyIndexes.TryAdd(key, index))
            {
                throw new InvalidOperationException($"A live query returned duplicate key '{key}'.");
            }

            ownedRows[index] = row;
            keys[index] = key;
        }

        return LiveResultSnapshot<T, TKey>.CreateOwned(ownedRows.ToImmutable(), keys, keyIndexes ?? previous!.KeyIndexes);
    }

    private static Dictionary<TKey, int> GetKeyIndexes<T, TKey>(
        LiveResultSnapshot<T, TKey> snapshot,
        IEqualityComparer<TKey> keyComparer)
        where TKey : notnull
    {
        if (ReferenceEquals(snapshot.KeyComparer, keyComparer) ||
            snapshot.KeyComparer.Equals(keyComparer))
        {
            return snapshot.KeyIndexes;
        }

        var keyIndexes = new Dictionary<TKey, int>(snapshot.Keys.Count, keyComparer);
        for (var index = 0; index < snapshot.Keys.Count; index++)
        {
            keyIndexes.Add(snapshot.Keys[index], index);
        }

        return keyIndexes;
    }
}
