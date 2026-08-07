using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Storage.PostgreSql;

/// <summary>Versions the integrity-checked relay snapshot progress payload.</summary>
public static class ChangeRelaySnapshotRunFormat
{
    public const int CurrentVersion = 1;

    public const int MinimumSupportedVersion = 1;
}

/// <summary>Describes the durable lifecycle of a relay-protected snapshot bootstrap.</summary>
public enum ChangeRelaySnapshotRunState
{
    Reserved,
    Completed,
    Abandoned,
}

/// <summary>Identifies a versioned snapshot attempt protected by one relay group.</summary>
public sealed record ChangeRelaySnapshotRun(
    string SourceFingerprint,
    long SourceEpoch,
    string ConsumerGroup,
    Guid SnapshotEpoch,
    BlueTuskLogSequenceNumber ConsistentPosition,
    string CompatibilityFingerprint,
    ChangeRelaySnapshotRunState State,
    long StoreGeneration,
    DateTimeOffset UpdatedAt);

public sealed partial class PostgreSqlDurableChangeRelay
{
    private const int SnapshotRunMaximumProgressBytes = 4096;
    private const string SnapshotRunStatePrefix = "bluetusk-bootstrap";
    private static readonly byte[] SnapshotRunMagic = "BTSR"u8.ToArray();

    /// <summary>Reads the latest bootstrap state owned by a leased consumer group.</summary>
    public async ValueTask<ChangeRelaySnapshotRun?> GetLatestSnapshotRunAsync(
        ChangeRelayGroupLease lease,
        string compatibilityFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateCompatibilityFingerprint(compatibilityFingerprint);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EnsureConsumerGroupLeaseAsync(
            connection,
            transaction: null,
            lease,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        var run = await ReadLatestSnapshotRunAsync(
            connection,
            transaction: null,
            lease,
            forUpdate: false,
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotCompatibility(run, compatibilityFingerprint);
        return run;
    }

    /// <summary>
    /// Reserves a new snapshot epoch while retaining all transactions for the leased group.
    /// </summary>
    public async ValueTask<ChangeRelaySnapshotRun> BeginSnapshotRunAsync(
        ChangeRelayGroupLease lease,
        SnapshotEpoch snapshot,
        string compatibilityFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateCompatibilityFingerprint(compatibilityFingerprint);
        if (!string.Equals(
                snapshot.Source.Fingerprint,
                lease.SourceFingerprint,
                StringComparison.Ordinal))
        {
            throw new ChangeRelaySnapshotRunException(
                "The snapshot epoch belongs to a different relay source.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await EnsureConsumerGroupLeaseAsync(
            connection,
            transaction,
            lease,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        var previous = await ReadLatestSnapshotRunAsync(
            connection,
            transaction,
            lease,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        EnsureSnapshotCompatibility(previous, compatibilityFingerprint);
        if (previous is { State: ChangeRelaySnapshotRunState.Reserved })
        {
            await SetSnapshotRunStateAsync(
                connection,
                transaction,
                previous,
                ChangeRelaySnapshotRunState.Abandoned,
                cancellationToken).ConfigureAwait(false);
        }

        var progress = EncodeSnapshotRunProgress(
            lease.ConsumerGroup,
            compatibilityFingerprint,
            snapshot.ConsistentPosition,
            storeGeneration: 0);
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             INSERT INTO {_schema}.snapshot_runs (
                 source_fingerprint, source_epoch, snapshot_epoch, state, progress)
             VALUES (@source, @epoch, @snapshot, @state, @progress)
             RETURNING updated_at
             """);
        AddParameter(command, "source", lease.SourceFingerprint);
        AddParameter(command, "epoch", lease.SourceEpoch);
        AddParameter(command, "snapshot", snapshot.Value.ToString("D"));
        AddParameter(
            command,
            "state",
            SnapshotRunState(lease.ConsumerGroup, ChangeRelaySnapshotRunState.Reserved));
        AddParameter(command, "progress", progress);
        var updatedAt = ReadTimestamp(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySnapshotRunException(
                "The relay did not persist the snapshot-run reservation."));

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ChangeRelaySnapshotRun(
            lease.SourceFingerprint,
            lease.SourceEpoch,
            lease.ConsumerGroup,
            snapshot.Value,
            snapshot.ConsistentPosition,
            compatibilityFingerprint,
            ChangeRelaySnapshotRunState.Reserved,
            0,
            updatedAt);
    }

    /// <summary>Marks a reserved snapshot durable after its destination completion succeeds.</summary>
    public ValueTask<ChangeRelaySnapshotRun> CompleteSnapshotRunAsync(
        ChangeRelayGroupLease lease,
        ChangeRelaySnapshotRun run,
        CancellationToken cancellationToken = default) =>
        TransitionSnapshotRunAsync(
            lease,
            run,
            ChangeRelaySnapshotRunState.Reserved,
            ChangeRelaySnapshotRunState.Completed,
            cancellationToken);

    /// <summary>Marks a failed snapshot attempt abandoned before a new epoch is exported.</summary>
    public ValueTask<ChangeRelaySnapshotRun> AbandonSnapshotRunAsync(
        ChangeRelayGroupLease lease,
        ChangeRelaySnapshotRun run,
        CancellationToken cancellationToken = default) =>
        TransitionSnapshotRunAsync(
            lease,
            run,
            ChangeRelaySnapshotRunState.Reserved,
            ChangeRelaySnapshotRunState.Abandoned,
            cancellationToken);

    private async ValueTask<ChangeRelaySnapshotRun> TransitionSnapshotRunAsync(
        ChangeRelayGroupLease lease,
        ChangeRelaySnapshotRun run,
        ChangeRelaySnapshotRunState expectedState,
        ChangeRelaySnapshotRunState newState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(run);
        EnsureRunOwnedByLease(run, lease);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await EnsureConsumerGroupLeaseAsync(
            connection,
            transaction,
            lease,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false);
        var current = await ReadSnapshotRunAsync(
            connection,
            transaction,
            lease,
            run.SnapshotEpoch,
            forUpdate: true,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySnapshotRunException(
                $"Snapshot epoch '{run.SnapshotEpoch}' does not exist.");
        if (current.StoreGeneration != run.StoreGeneration)
        {
            throw new ChangeRelaySnapshotRunException(
                $"Snapshot epoch '{run.SnapshotEpoch}' changed from generation {run.StoreGeneration} to {current.StoreGeneration}.");
        }

        if (current.State != expectedState)
        {
            throw new ChangeRelaySnapshotRunException(
                $"Snapshot epoch '{run.SnapshotEpoch}' is '{current.State}', not '{expectedState}'.");
        }

        var updated = await SetSnapshotRunStateAsync(
            connection,
            transaction,
            current,
            newState,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask<ChangeRelaySnapshotRun> SetSnapshotRunStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelaySnapshotRun current,
        ChangeRelaySnapshotRunState state,
        CancellationToken cancellationToken)
    {
        var generation = checked(current.StoreGeneration + 1);
        var progress = EncodeSnapshotRunProgress(
            current.ConsumerGroup,
            current.CompatibilityFingerprint,
            current.ConsistentPosition,
            generation);
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             UPDATE {_schema}.snapshot_runs
             SET state = @state, progress = @progress, updated_at = clock_timestamp()
             WHERE source_fingerprint = @source AND source_epoch = @epoch
               AND snapshot_epoch = @snapshot
             RETURNING updated_at
             """);
        AddParameter(command, "state", SnapshotRunState(current.ConsumerGroup, state));
        AddParameter(command, "progress", progress);
        AddParameter(command, "source", current.SourceFingerprint);
        AddParameter(command, "epoch", current.SourceEpoch);
        AddParameter(command, "snapshot", current.SnapshotEpoch.ToString("D"));
        var updatedAt = ReadTimestamp(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelaySnapshotRunException(
                $"Snapshot epoch '{current.SnapshotEpoch}' disappeared while locked."));
        return current with
        {
            State = state,
            StoreGeneration = generation,
            UpdatedAt = updatedAt,
        };
    }

    private async ValueTask<ChangeRelaySnapshotRun?> ReadLatestSnapshotRunAsync(
        DbConnection connection,
        DbTransaction? transaction,
        ChangeRelayGroupLease lease,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var states = Enum.GetValues<ChangeRelaySnapshotRunState>()
            .Select(state => SnapshotRunState(lease.ConsumerGroup, state))
            .ToArray();
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             SELECT snapshot_epoch, state, progress, updated_at
             FROM {_schema}.snapshot_runs
             WHERE source_fingerprint = @source AND source_epoch = @epoch
               AND state IN (@reserved, @completed, @abandoned)
             ORDER BY updated_at DESC, snapshot_epoch DESC
             LIMIT 1
             {(forUpdate ? "FOR UPDATE" : string.Empty)}
             """);
        AddParameter(command, "source", lease.SourceFingerprint);
        AddParameter(command, "epoch", lease.SourceEpoch);
        AddParameter(command, "reserved", states[(int)ChangeRelaySnapshotRunState.Reserved]);
        AddParameter(command, "completed", states[(int)ChangeRelaySnapshotRunState.Completed]);
        AddParameter(command, "abandoned", states[(int)ChangeRelaySnapshotRunState.Abandoned]);
        return await ReadSnapshotRunAsync(command, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ChangeRelaySnapshotRun?> ReadSnapshotRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        ChangeRelayGroupLease lease,
        Guid snapshotEpoch,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            $"""
             SELECT snapshot_epoch, state, progress, updated_at
             FROM {_schema}.snapshot_runs
             WHERE source_fingerprint = @source AND source_epoch = @epoch
               AND snapshot_epoch = @snapshot
             {(forUpdate ? "FOR UPDATE" : string.Empty)}
             """);
        AddParameter(command, "source", lease.SourceFingerprint);
        AddParameter(command, "epoch", lease.SourceEpoch);
        AddParameter(command, "snapshot", snapshotEpoch.ToString("D"));
        return await ReadSnapshotRunAsync(command, lease, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ChangeRelaySnapshotRun?> ReadSnapshotRunAsync(
        DbCommand command,
        ChangeRelayGroupLease lease,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (reader.IsDBNull(2))
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run progress payload is missing.");
        }

        var progress = DecodeSnapshotRunProgress(reader.GetFieldValue<byte[]>(2));
        if (!string.Equals(progress.ConsumerGroup, lease.ConsumerGroup, StringComparison.Ordinal))
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run payload belongs to a different consumer group.");
        }

        if (!Guid.TryParseExact(reader.GetString(0), "D", out var snapshotEpoch))
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run epoch is invalid.");
        }

        return new ChangeRelaySnapshotRun(
            lease.SourceFingerprint,
            lease.SourceEpoch,
            progress.ConsumerGroup,
            snapshotEpoch,
            progress.ConsistentPosition,
            progress.CompatibilityFingerprint,
            ParseSnapshotRunState(reader.GetString(1), lease.ConsumerGroup),
            progress.StoreGeneration,
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async ValueTask EnsureConsumerGroupLeaseAsync(
        DbConnection connection,
        DbTransaction? transaction,
        ChangeRelayGroupLease lease,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var groupState = await ReadGroupAsync(
            connection,
            transaction,
            lease.SourceFingerprint,
            lease.SourceEpoch,
            lease.ConsumerGroup,
            forUpdate,
            cancellationToken).ConfigureAwait(false) ??
            throw new ChangeRelayConsumerGroupException(
                "The relay consumer group does not exist.");
        if (!groupState.Group.IsActive ||
            !groupState.IsLeaseActive ||
            !GroupLeaseMatches(groupState.Lease, lease))
        {
            throw new ChangeRelayLeaseLostException(
                "The relay consumer-group lease was lost during snapshot orchestration.");
        }
    }

    private static byte[] EncodeSnapshotRunProgress(
        string consumerGroup,
        string compatibilityFingerprint,
        BlueTuskLogSequenceNumber consistentPosition,
        long storeGeneration)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(SnapshotRunMagic);
            writer.Write((byte)ChangeRelaySnapshotRunFormat.CurrentVersion);
            writer.Write(consumerGroup);
            writer.Write(compatibilityFingerprint);
            writer.Write(consistentPosition.Value);
            writer.Write(storeGeneration);
            writer.Flush();
        }

        var payload = stream.ToArray();
        var digest = SHA256.HashData(payload);
        var protectedProgress = new byte[checked(payload.Length + digest.Length)];
        payload.CopyTo(protectedProgress, 0);
        digest.CopyTo(protectedProgress, payload.Length);
        if (protectedProgress.Length > SnapshotRunMaximumProgressBytes)
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run progress payload exceeds its storage bound.");
        }

        return protectedProgress;
    }

    private static SnapshotRunProgress DecodeSnapshotRunProgress(byte[] protectedProgress)
    {
        if (protectedProgress.Length <= SHA256.HashSizeInBytes ||
            protectedProgress.Length > SnapshotRunMaximumProgressBytes)
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run progress payload length is invalid.");
        }

        var payload = protectedProgress.AsSpan(0, protectedProgress.Length - SHA256.HashSizeInBytes);
        var expected = protectedProgress.AsSpan(payload.Length);
        var actual = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run progress integrity hash is invalid.");
        }

        try
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!reader.ReadBytes(SnapshotRunMagic.Length).AsSpan().SequenceEqual(SnapshotRunMagic) ||
                reader.ReadByte() != ChangeRelaySnapshotRunFormat.CurrentVersion)
            {
                throw new ChangeRelaySnapshotRunException(
                    "The relay snapshot-run progress format is not supported.");
            }

            var consumerGroup = reader.ReadString();
            var fingerprint = reader.ReadString();
            var position = new BlueTuskLogSequenceNumber(reader.ReadUInt64());
            var generation = reader.ReadInt64();
            if (stream.Position != stream.Length ||
                generation < 0 ||
                Encoding.UTF8.GetByteCount(consumerGroup) > 512)
            {
                throw new ChangeRelaySnapshotRunException(
                    "The relay snapshot-run progress payload is invalid.");
            }

            ValidateCompatibilityFingerprint(fingerprint);
            return new SnapshotRunProgress(consumerGroup, fingerprint, position, generation);
        }
        catch (ChangeRelaySnapshotRunException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or IOException or FormatException or ArgumentException or OverflowException)
        {
            throw new ChangeRelaySnapshotRunException(
                "The relay snapshot-run progress payload could not be decoded.",
                exception);
        }
    }

    private static void EnsureRunOwnedByLease(
        ChangeRelaySnapshotRun run,
        ChangeRelayGroupLease lease)
    {
        if (!string.Equals(run.SourceFingerprint, lease.SourceFingerprint, StringComparison.Ordinal) ||
            run.SourceEpoch != lease.SourceEpoch ||
            !string.Equals(run.ConsumerGroup, lease.ConsumerGroup, StringComparison.Ordinal))
        {
            throw new ChangeRelaySnapshotRunException(
                "The snapshot run belongs to a different relay consumer-group lease.");
        }
    }

    private static void EnsureSnapshotCompatibility(
        ChangeRelaySnapshotRun? run,
        string compatibilityFingerprint)
    {
        if (run is not null && !string.Equals(
                run.CompatibilityFingerprint,
                compatibilityFingerprint,
                StringComparison.Ordinal))
        {
            throw new ChangeRelaySnapshotCompatibilityException(
                run.CompatibilityFingerprint,
                compatibilityFingerprint);
        }
    }

    private static void ValidateCompatibilityFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A snapshot compatibility fingerprint must be 64 hexadecimal SHA-256 characters.",
                nameof(fingerprint));
        }
    }

    private static string SnapshotRunState(
        string consumerGroup,
        ChangeRelaySnapshotRunState state)
    {
        var groupHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(consumerGroup)));
        return $"{SnapshotRunStatePrefix}:{state.ToString().ToLowerInvariant()}:{groupHash}";
    }

    private static ChangeRelaySnapshotRunState ParseSnapshotRunState(
        string stored,
        string consumerGroup)
    {
        foreach (var state in Enum.GetValues<ChangeRelaySnapshotRunState>())
        {
            if (string.Equals(stored, SnapshotRunState(consumerGroup, state), StringComparison.Ordinal))
            {
                return state;
            }
        }

        throw new ChangeRelaySnapshotRunException(
            $"Relay snapshot-run state '{stored}' is invalid.");
    }

    private sealed record SnapshotRunProgress(
        string ConsumerGroup,
        string CompatibilityFingerprint,
        BlueTuskLogSequenceNumber ConsistentPosition,
        long StoreGeneration);
}

public class ChangeRelaySnapshotRunException : ChangeRelayException
{
    public ChangeRelaySnapshotRunException(string message)
        : base(message)
    {
    }

    public ChangeRelaySnapshotRunException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ChangeRelaySnapshotCompatibilityException : ChangeRelaySnapshotRunException
{
    public ChangeRelaySnapshotCompatibilityException(string current, string requested)
        : base(
            $"Snapshot compatibility fingerprint '{current}' does not match requested fingerprint '{requested}'. An explicit rebuild is required.")
    {
        CurrentFingerprint = current;
        RequestedFingerprint = requested;
    }

    public string CurrentFingerprint { get; }

    public string RequestedFingerprint { get; }
}
