namespace BlueTusk.TypeSystem;

/// <summary>A PostgreSQL 32-bit transaction identifier (<c>xid</c>).</summary>
public readonly record struct BlueTuskTransactionId(uint Value);

/// <summary>A PostgreSQL command identifier (<c>cid</c>).</summary>
public readonly record struct BlueTuskCommandId(uint Value);

/// <summary>A PostgreSQL non-wrapping 64-bit transaction identifier (<c>xid8</c>).</summary>
public readonly record struct BlueTuskFullTransactionId(ulong Value);

/// <summary>A PostgreSQL 64-bit object identifier (<c>oid8</c>).</summary>
public readonly record struct BlueTuskObjectIdentifier64(ulong Value);

/// <summary>A PostgreSQL transaction visibility snapshot.</summary>
public sealed class BlueTuskTransactionSnapshot :
    IEquatable<BlueTuskTransactionSnapshot>
{
    private readonly ulong[] _inProgressTransactionIds;

    public BlueTuskTransactionSnapshot(
        ulong minimumTransactionId,
        ulong maximumTransactionId,
        IEnumerable<ulong> inProgressTransactionIds)
    {
        ArgumentNullException.ThrowIfNull(inProgressTransactionIds);
        if (minimumTransactionId > maximumTransactionId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumTransactionId),
                "The minimum transaction ID cannot exceed the maximum transaction ID.");
        }

        _inProgressTransactionIds = inProgressTransactionIds.ToArray();
        for (var index = 0; index < _inProgressTransactionIds.Length; index++)
        {
            var value = _inProgressTransactionIds[index];
            if (value < minimumTransactionId || value >= maximumTransactionId)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inProgressTransactionIds),
                    $"In-progress transaction ID {value} is outside [{minimumTransactionId}, {maximumTransactionId}).");
            }

            if (index != 0 && value <= _inProgressTransactionIds[index - 1])
            {
                throw new ArgumentException(
                    "In-progress transaction IDs must be strictly increasing.",
                    nameof(inProgressTransactionIds));
            }
        }

        MinimumTransactionId = minimumTransactionId;
        MaximumTransactionId = maximumTransactionId;
    }

    public ulong MinimumTransactionId { get; }

    public ulong MaximumTransactionId { get; }

    public IReadOnlyList<ulong> InProgressTransactionIds => _inProgressTransactionIds;

    public bool Equals(BlueTuskTransactionSnapshot? other) =>
        other is not null &&
        MinimumTransactionId == other.MinimumTransactionId &&
        MaximumTransactionId == other.MaximumTransactionId &&
        _inProgressTransactionIds.AsSpan().SequenceEqual(other._inProgressTransactionIds);

    public override bool Equals(object? obj) =>
        obj is BlueTuskTransactionSnapshot other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MinimumTransactionId);
        hash.Add(MaximumTransactionId);
        foreach (var value in _inProgressTransactionIds)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

}
