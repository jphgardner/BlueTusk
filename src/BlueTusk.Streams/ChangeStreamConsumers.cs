using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace BlueTusk.Streams;

public readonly record struct SnapshotEpoch(
    Guid Value,
    ChangeSourceIdentity Source,
    BlueTuskLogSequenceNumber ConsistentPosition,
    DateTimeOffset StartedAt)
{
    public static SnapshotEpoch Create(
        ChangeSourceIdentity source,
        BlueTuskLogSequenceNumber consistentPosition,
        TimeProvider? timeProvider = null) =>
        new(Guid.NewGuid(), source, consistentPosition, (timeProvider ?? TimeProvider.System).GetUtcNow());
}

public readonly record struct SnapshotRowId(
    Guid Epoch,
    string TableIdentity,
    string KeyIdentity)
{
    public static SnapshotRowId Create(
        SnapshotEpoch epoch,
        ChangeTable table,
        IEnumerable<ChangeColumnValue> keyValues)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keyValues);
        var tableIdentity = $"{table.Schema}.{table.Name}";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var key in keyValues)
        {
            hash.AppendData([(byte)key.State, (byte)key.Encoding]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, key.Data.Length);
            hash.AppendData(length);
            hash.AppendData(key.Data.Span);
        }

        return new SnapshotRowId(
            epoch.Value,
            tableIdentity,
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

public sealed record ChangeSnapshotRow(SnapshotRowId Id, ChangeRow Row);

public sealed class ChangeSnapshotBatch
{
    private readonly ReadOnlyCollection<ChangeSnapshotRow> _rows;

    public ChangeSnapshotBatch(
        SnapshotEpoch epoch,
        ChangeTable table,
        long sequence,
        IEnumerable<ChangeSnapshotRow> rows,
        bool isLastForTable)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Epoch = epoch;
        Table = table;
        Sequence = sequence;
        _rows = Array.AsReadOnly(rows.ToArray());
        IsLastForTable = isLastForTable;
        if (_rows.Any(row => row.Id.Epoch != epoch.Value))
        {
            throw new ArgumentException("Every snapshot row must belong to the batch epoch.", nameof(rows));
        }
    }

    public SnapshotEpoch Epoch { get; }

    public ChangeTable Table { get; }

    public long Sequence { get; }

    public IReadOnlyList<ChangeSnapshotRow> Rows => _rows;

    public bool IsLastForTable { get; }
}

public sealed record SnapshotReset(
    SnapshotEpoch Epoch,
    Guid? AbandonedEpoch,
    string Reason);

public sealed record SnapshotStart(
    SnapshotEpoch Epoch,
    int TableCount);

public sealed record SnapshotComplete(
    SnapshotEpoch Epoch,
    long RowCount,
    int TableCount);

public interface IChangeStreamConsumer
{
    ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default);

    ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default);

    ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default);

    ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default);

    ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default);
}
