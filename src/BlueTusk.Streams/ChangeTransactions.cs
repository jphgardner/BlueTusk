using System.Collections.ObjectModel;

namespace BlueTusk.Streams;

public readonly record struct ChangeId(
    ChangeSourceIdentity Source,
    BlueTuskLogSequenceNumber CommitEndPosition,
    uint TransactionId,
    int Ordinal);

public enum ChangeKind
{
    Insert,
    Update,
    Delete,
    Truncate,
    LogicalMessage,
}

public abstract record Change(ChangeId Id, ChangeKind Kind);

public sealed record InsertChange(ChangeId Id, ChangeRow NewRow)
    : Change(Id, ChangeKind.Insert);

public sealed record InsertChange<T>(ChangeId Id, ChangeRow<T> NewRow)
    : Change(Id, ChangeKind.Insert);

public sealed record UpdateChange(
    ChangeId Id,
    ChangeRow OldRow,
    ChangeRow NewRow,
    ChangedColumnSet ChangedColumns)
    : Change(Id, ChangeKind.Update);

public sealed record UpdateChange<T>(
    ChangeId Id,
    ChangeRow<T> OldRow,
    ChangeRow<T> NewRow,
    ChangedColumnSet ChangedColumns)
    : Change(Id, ChangeKind.Update);

public sealed record DeleteChange(ChangeId Id, ChangeRow OldRow)
    : Change(Id, ChangeKind.Delete);

public sealed record DeleteChange<T>(ChangeId Id, ChangeRow<T> OldRow)
    : Change(Id, ChangeKind.Delete);

public sealed record TruncateChange(
    ChangeId Id,
    IReadOnlyList<ChangeTable> Tables,
    bool Cascade,
    bool RestartIdentity)
    : Change(Id, ChangeKind.Truncate);

public sealed record TruncateChange<T>(
    ChangeId Id,
    IReadOnlyList<ChangeTable> Tables,
    bool Cascade,
    bool RestartIdentity)
    : Change(Id, ChangeKind.Truncate);

public sealed record LogicalMessageChange(
    ChangeId Id,
    bool IsTransactional,
    BlueTuskLogSequenceNumber Position,
    string Prefix,
    ReadOnlyMemory<byte> Content)
    : Change(Id, ChangeKind.LogicalMessage);

public sealed class ChangeSet : IAsyncEnumerable<Change>
{
    private readonly Func<CancellationToken, IAsyncEnumerable<Change>> _reader;

    internal ChangeSet(
        int count,
        long estimatedBytes,
        bool isSpooled,
        Func<CancellationToken, IAsyncEnumerable<Change>> reader)
    {
        Count = count;
        EstimatedBytes = estimatedBytes;
        IsSpooled = isSpooled;
        _reader = reader;
    }

    public int Count { get; }

    public long EstimatedBytes { get; }

    public bool IsSpooled { get; }

    public IAsyncEnumerator<Change> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _reader(cancellationToken).GetAsyncEnumerator(cancellationToken);

    public async ValueTask<IReadOnlyList<Change>> MaterializeAsync(CancellationToken cancellationToken = default)
    {
        var changes = new List<Change>(Count);
        await foreach (var change in this.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            changes.Add(change);
        }

        return new ReadOnlyCollection<Change>(changes);
    }
}

public enum ChangeTransactionOutcome
{
    Committed,
    Prepared,
    RolledBack,
}

public sealed class ChangeTransaction
{
    internal ChangeTransaction(
        ChangeSourceIdentity source,
        uint transactionId,
        BlueTuskLogSequenceNumber beginFinalPosition,
        BlueTuskLogSequenceNumber commitPosition,
        BlueTuskLogSequenceNumber commitEndPosition,
        DateTimeOffset commitTimestamp,
        string? origin,
        bool isSynthetic,
        ChangeTransactionOutcome outcome,
        string? globalTransactionId,
        ChangeSet changes)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (outcome is not ChangeTransactionOutcome.Committed &&
            string.IsNullOrWhiteSpace(globalTransactionId))
        {
            throw new ArgumentException(
                "Prepared and rolled-back lifecycle deliveries require a global transaction ID.",
                nameof(globalTransactionId));
        }

        if (globalTransactionId is not null && string.IsNullOrWhiteSpace(globalTransactionId))
        {
            throw new ArgumentException("A global transaction ID cannot be empty.", nameof(globalTransactionId));
        }

        if (isSynthetic && globalTransactionId is not null)
        {
            throw new ArgumentException("A synthetic transaction cannot be a two-phase transaction.", nameof(isSynthetic));
        }

        if (globalTransactionId is not null &&
            outcome is not ChangeTransactionOutcome.Prepared &&
            changes.Count != 0)
        {
            throw new ArgumentException(
                "Commit-prepared and rollback-prepared lifecycle deliveries cannot contain row changes.",
                nameof(changes));
        }

        Source = source;
        TransactionId = transactionId;
        BeginFinalPosition = beginFinalPosition;
        CommitPosition = commitPosition;
        CommitEndPosition = commitEndPosition;
        CommitTimestamp = commitTimestamp;
        Origin = origin;
        IsSynthetic = isSynthetic;
        Outcome = outcome;
        GlobalTransactionId = globalTransactionId;
        Changes = changes;
    }

    public ChangeSourceIdentity Source { get; }

    public uint TransactionId { get; }

    public BlueTuskLogSequenceNumber BeginFinalPosition { get; }

    public BlueTuskLogSequenceNumber CommitPosition { get; }

    public BlueTuskLogSequenceNumber CommitEndPosition { get; }

    public DateTimeOffset CommitTimestamp { get; }

    public string? Origin { get; }

    public bool IsSynthetic { get; }

    public ChangeTransactionOutcome Outcome { get; }

    public string? GlobalTransactionId { get; }

    public bool IsTwoPhase => GlobalTransactionId is not null;

    public ChangeSet Changes { get; }
}
