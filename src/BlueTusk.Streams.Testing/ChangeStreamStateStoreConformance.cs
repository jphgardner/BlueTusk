using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Testing;

public sealed record ChangeStreamStateStoreConformanceOptions
{
    public TimeSpan ExpiringLeaseDuration { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan ExpirationWait { get; init; } = TimeSpan.FromMilliseconds(500);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            ExpiringLeaseDuration,
            TimeSpan.Zero);
        if (ExpirationWait <= ExpiringLeaseDuration)
        {
            throw new ArgumentException(
                "The expiration wait must be longer than the lease duration.",
                nameof(ExpirationWait));
        }
    }
}

public sealed record ChangeStreamStateStoreConformanceReport(
    string StoreName,
    int Assertions,
    TimeSpan Elapsed);

public static class ChangeStreamStateStoreConformance
{
    public static async ValueTask<ChangeStreamStateStoreConformanceReport> RunAsync(
        IChangeStreamStateStore store,
        string storeName,
        ChangeStreamStateStoreConformanceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        var effectiveOptions = options ?? new ChangeStreamStateStoreConformanceOptions();
        effectiveOptions.Validate();
        var started = TimeProvider.System.GetTimestamp();
        var assertionCount = 0;
        var source = new ChangeSourceIdentity(
            "conformance-" + Guid.NewGuid().ToString("N"),
            "database",
            "slot",
            "public:orders");
        var identity = ChangeStreamCheckpoint.CreateInitial(
            source,
            "database-system-id",
            "pgoutput",
            "mapping-v1");

        var checkpointKey = ChangeStreamStateKey.Create(source, "checkpoint-cas");
        var checkpointLease = RequireLease(
            await store.AcquireAsync(
                checkpointKey,
                "owner-a",
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false),
            "acquire checkpoint lease");
        assertionCount++;

        var checkpoint = identity.MoveTo(new BlueTuskLogSequenceNumber(100), 0);
        var stored = await store.CompareExchangeAsync(
            checkpointKey,
            -1,
            checkpoint,
            checkpointLease,
            cancellationToken).ConfigureAwait(false);
        Require(
            stored.Status == ChangeCheckpointWriteStatus.Stored,
            "The first compare-and-swap write was not stored.");
        assertionCount++;

        var read = await store.ReadAsync(checkpointKey, cancellationToken).ConfigureAwait(false);
        Require(read == checkpoint, "The stored checkpoint did not round-trip exactly.");
        assertionCount++;

        var conflict = await store.CompareExchangeAsync(
            checkpointKey,
            -1,
            identity.MoveTo(new BlueTuskLogSequenceNumber(125), 0),
            checkpointLease,
            cancellationToken).ConfigureAwait(false);
        Require(
            conflict.Status == ChangeCheckpointWriteStatus.Conflict,
            "A stale generation was not rejected as a conflict.");
        assertionCount++;

        var backwards = await store.CompareExchangeAsync(
            checkpointKey,
            0,
            identity.MoveTo(new BlueTuskLogSequenceNumber(50), 1),
            checkpointLease,
            cancellationToken).ConfigureAwait(false);
        Require(
            backwards.Status == ChangeCheckpointWriteStatus.BackwardMovement,
            "A backward checkpoint movement was not rejected.");
        assertionCount++;

        var changedMapping = ChangeStreamCheckpoint.CreateInitial(
                source,
                identity.DatabaseIdentity,
                identity.OutputPlugin,
                "mapping-v2")
            .MoveTo(new BlueTuskLogSequenceNumber(125), 1);
        var incompatible = await store.CompareExchangeAsync(
            checkpointKey,
            0,
            changedMapping,
            checkpointLease,
            cancellationToken).ConfigureAwait(false);
        Require(
            incompatible.Status == ChangeCheckpointWriteStatus.Incompatible,
            "An incompatible mapping identity was not rejected.");
        assertionCount++;

        var held = await store.AcquireAsync(
            checkpointKey,
            "owner-b",
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);
        Require(
            held.Status == ChangeLeaseAcquireStatus.HeldByAnotherOwner,
            "A second owner acquired an active lease.");
        assertionCount++;

        Require(
            await store.ReleaseAsync(checkpointLease, cancellationToken).ConfigureAwait(false),
            "The active owner could not release its lease.");
        var replacementLease = RequireLease(
            await store.AcquireAsync(
                checkpointKey,
                "owner-b",
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false),
            "acquire replacement lease");
        Require(
            replacementLease.FencingToken > checkpointLease.FencingToken,
            "A replacement lease did not advance the fencing token.");
        assertionCount += 2;

        var fenced = await store.CompareExchangeAsync(
            checkpointKey,
            0,
            identity.MoveTo(new BlueTuskLogSequenceNumber(150), 1),
            checkpointLease,
            cancellationToken).ConfigureAwait(false);
        Require(
            fenced.Status == ChangeCheckpointWriteStatus.Fenced,
            "A superseded lease was not fenced from checkpoint writes.");
        assertionCount++;

        var independentKey = ChangeStreamStateKey.Create(source, "independent-group");
        var independent = await store.AcquireAsync(
            independentKey,
            "owner-c",
            TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);
        Require(
            independent.Status == ChangeLeaseAcquireStatus.Acquired,
            "An independent consumer group did not have independent ownership.");
        assertionCount++;

        var expiryKey = ChangeStreamStateKey.Create(source, "expiry");
        var expiring = RequireLease(
            await store.AcquireAsync(
                expiryKey,
                "owner-d",
                effectiveOptions.ExpiringLeaseDuration,
                cancellationToken).ConfigureAwait(false),
            "acquire expiring lease");
        await Task.Delay(effectiveOptions.ExpirationWait, cancellationToken).ConfigureAwait(false);
        var afterExpiry = RequireLease(
            await store.AcquireAsync(
                expiryKey,
                "owner-e",
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false),
            "acquire after expiry");
        Require(
            afterExpiry.FencingToken > expiring.FencingToken,
            "Lease expiry did not advance the fencing token for the new owner.");
        assertionCount += 2;

        return new ChangeStreamStateStoreConformanceReport(
            storeName,
            assertionCount,
            TimeProvider.System.GetElapsedTime(started));
    }

    private static ChangeStreamLease RequireLease(
        ChangeLeaseAcquireResult result,
        string operation)
    {
        Require(
            result.Status == ChangeLeaseAcquireStatus.Acquired && result.Lease is not null,
            $"The conformance operation '{operation}' did not acquire a lease.");
        return result.Lease!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ChangeStreamStateStoreConformanceException(message);
        }
    }
}

public sealed class ChangeStreamStateStoreConformanceException : Exception
{
    public ChangeStreamStateStoreConformanceException(string message)
        : base(message)
    {
    }
}
