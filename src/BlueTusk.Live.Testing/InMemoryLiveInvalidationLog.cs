namespace BlueTusk.Live.Testing;

public sealed class InMemoryLiveInvalidationLog : ILiveInvalidationLog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DatabaseState> _databases = new(StringComparer.Ordinal);

    public LiveInvalidationCursor Append(
        string databaseIdentity,
        IEnumerable<LiveTableDependency> dependencies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentNullException.ThrowIfNull(dependencies);
        var distinct = dependencies.Distinct().ToArray();
        lock (_gate)
        {
            var state = GetOrCreate(databaseIdentity);
            var cursor = new LiveInvalidationCursor(checked(state.Cursor.Value + 1));
            state.Cursor = cursor;
            state.Entries.Add(new Entry(cursor, distinct));
            return cursor;
        }
    }

    public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
        string databaseIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _databases.TryGetValue(databaseIdentity, out var state)
                    ? state.Cursor
                    : new LiveInvalidationCursor(0));
        }
    }

    public ValueTask<bool> HasChangesAsync(
        string databaseIdentity,
        IReadOnlyCollection<LiveTableDependency> dependencies,
        LiveInvalidationCursor afterExclusive,
        LiveInvalidationCursor throughInclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseIdentity);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        if (throughInclusive < afterExclusive)
        {
            throw new ArgumentException("The through cursor cannot precede the after cursor.");
        }

        lock (_gate)
        {
            if (!_databases.TryGetValue(databaseIdentity, out var state))
            {
                if (throughInclusive.Value != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(throughInclusive));
                }

                return ValueTask.FromResult(false);
            }

            if (throughInclusive > state.Cursor)
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThan(
                    throughInclusive.Value,
                    state.Cursor.Value,
                    nameof(throughInclusive));
            }

            var requested = dependencies.ToHashSet();
            return ValueTask.FromResult(state.Entries.Any(entry =>
                entry.Cursor > afterExclusive &&
                entry.Cursor <= throughInclusive &&
                entry.Dependencies.Any(requested.Contains)));
        }
    }

    private DatabaseState GetOrCreate(string databaseIdentity)
    {
        if (!_databases.TryGetValue(databaseIdentity, out var state))
        {
            state = new DatabaseState();
            _databases.Add(databaseIdentity, state);
        }

        return state;
    }

    private sealed record Entry(
        LiveInvalidationCursor Cursor,
        IReadOnlyCollection<LiveTableDependency> Dependencies);

    private sealed class DatabaseState
    {
        public LiveInvalidationCursor Cursor { get; set; } = new(0);

        public List<Entry> Entries { get; } = [];
    }
}
