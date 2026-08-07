using BlueTusk.Streams;
using BlueTusk.Streams.Testing;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sync.Testing;

/// <summary>Identifies a durable-state checkpoint in the shared destination scenario.</summary>
public enum SyncDestinationConformanceStage
{
    /// <summary>The snapshot lifecycle completed.</summary>
    SnapshotApplied,

    /// <summary>A new destination instance observed the completed snapshot.</summary>
    SnapshotRestart,

    /// <summary>The first transaction and same-instance redelivery completed.</summary>
    TransactionApplied,

    /// <summary>A new destination instance safely redelivered the transaction.</summary>
    RestartRedelivery,
}

/// <summary>Adapts a real or simulated destination to the shared Sync conformance scenario.</summary>
public interface ISyncDestinationConformanceHarness
{
    /// <summary>Gets the unique pipeline identifier used by this scenario.</summary>
    string PipelineId { get; }

    /// <summary>Gets the stable source identity used by this scenario.</summary>
    ChangeSourceIdentity Source { get; }

    /// <summary>Creates a destination instance over the same durable backing service.</summary>
    ValueTask<ISyncDestination> CreateDestinationAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Verifies connector-specific durable state at a shared scenario boundary.</summary>
    ValueTask VerifyDurableStateAsync(
        SyncDestinationConformanceStage stage,
        ISyncDestination destination,
        CancellationToken cancellationToken = default);
}

/// <summary>Reports the guarantees observed by the shared destination scenario.</summary>
public sealed class SyncDestinationConformanceResult
{
    internal SyncDestinationConformanceResult(
        string destinationName,
        SyncDestinationCapabilities capabilities,
        bool quarantineVerified)
    {
        DestinationName = destinationName;
        Capabilities = capabilities;
        QuarantineVerified = quarantineVerified;
    }

    /// <summary>Gets the destination name reported by the implementation.</summary>
    public string DestinationName { get; }

    /// <summary>Gets the capabilities reported by the implementation.</summary>
    public SyncDestinationCapabilities Capabilities { get; }

    /// <summary>Gets whether the destination exposed and passed durable quarantine checks.</summary>
    public bool QuarantineVerified { get; }
}

/// <summary>Runs the same snapshot-plus-stream recovery contract against every destination.</summary>
public static class SyncDestinationConformanceSuite
{
    /// <summary>
    /// Verifies snapshot lifecycle, duplicate delivery, restart recovery, transform drift, durable
    /// positions, connector-specific state, and quarantine when supported.
    /// </summary>
    public static async ValueTask<SyncDestinationConformanceResult> VerifyAsync(
        ISyncDestinationConformanceHarness harness,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentException.ThrowIfNullOrWhiteSpace(harness.PipelineId);
        var transform = SyncTransformVersion.Create("conformance", "v1");
        var destination = await harness.CreateDestinationAsync(cancellationToken)
            .ConfigureAwait(false) ??
            throw Failure("The harness returned a null destination.");
        var provision = await destination.ProvisionAsync(
            new SyncProvisionRequest(harness.PipelineId, harness.Source, transform),
            cancellationToken).ConfigureAwait(false);
        Require(
            provision.Status is SyncProvisionStatus.Ready,
            destination,
            $"initial provisioning returned {provision.Status}");

        var epoch = new SnapshotEpoch(
            Guid.NewGuid(),
            harness.Source,
            new BlueTuskLogSequenceNumber(100),
            DateTimeOffset.UtcNow);
        var table = new ChangeTable(
            7,
            "public",
            "conformance",
            'd',
            [new ChangeColumn(0, "id", 23, -1, true)]);
        var snapshotBatch = new SyncSnapshotBatch(
            harness.PipelineId,
            transform,
            new ChangeSnapshotBatch(epoch, table, 0, [], true),
            [new SyncSnapshotMutation(
                new SnapshotRowId(epoch.Value, "public.conformance", "42"),
                "conformance",
                "42",
                "{\"stage\":\"snapshot\"}"u8.ToArray(),
                "application/json")]);
        await destination.ResetSnapshotAsync(
            harness.PipelineId,
            new SnapshotReset(epoch, null, "shared conformance bootstrap"),
            cancellationToken).ConfigureAwait(false);
        await destination.StartSnapshotAsync(
            harness.PipelineId,
            new SnapshotStart(epoch, 1),
            transform,
            cancellationToken).ConfigureAwait(false);
        await destination.ApplySnapshotBatchAsync(snapshotBatch, cancellationToken)
            .ConfigureAwait(false);
        await destination.ApplySnapshotBatchAsync(snapshotBatch, cancellationToken)
            .ConfigureAwait(false);
        await destination.CompleteSnapshotAsync(
            harness.PipelineId,
            new SnapshotComplete(epoch, 1, 1),
            transform,
            cancellationToken).ConfigureAwait(false);
        await harness.VerifyDurableStateAsync(
            SyncDestinationConformanceStage.SnapshotApplied,
            destination,
            cancellationToken).ConfigureAwait(false);

        var afterSnapshotRestart = await harness.CreateDestinationAsync(cancellationToken)
            .ConfigureAwait(false) ??
            throw Failure("The harness returned a null post-snapshot destination.");
        provision = await afterSnapshotRestart.ProvisionAsync(
            new SyncProvisionRequest(harness.PipelineId, harness.Source, transform),
            cancellationToken).ConfigureAwait(false);
        Require(
            provision.Status is SyncProvisionStatus.Ready,
            afterSnapshotRestart,
            $"post-snapshot provisioning returned {provision.Status}");
        await harness.VerifyDurableStateAsync(
            SyncDestinationConformanceStage.SnapshotRestart,
            afterSnapshotRestart,
            cancellationToken).ConfigureAwait(false);

        var position = new BlueTuskLogSequenceNumber(105);
        await using var delivery = ChangeDeliveryTestFactory.CreateCommitted(
            harness.Source,
            42,
            position);
        var mutation = new SyncMutation(
            new ChangeId(harness.Source, position, 42, 0),
            SyncMutationKind.Upsert,
            "conformance",
            "42",
            "{\"stage\":\"transaction\"}"u8.ToArray(),
            "application/json");
        var transaction = new SyncTransactionBatch(
            harness.PipelineId,
            transform,
            delivery.Transaction,
            [mutation]);
        var applied = await afterSnapshotRestart.ApplyTransactionAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        RequireApplied(applied, position, afterSnapshotRestart, "initial transaction");

        var changedDuplicate = new SyncTransactionBatch(
            harness.PipelineId,
            transform,
            delivery.Transaction,
            [new SyncMutation(
                mutation.ChangeId,
                mutation.Kind,
                mutation.Collection,
                mutation.Key,
                "{\"stage\":\"wrong\"}"u8.ToArray(),
                mutation.ContentType,
                mutation.PartitionKey)]);
        var duplicate = await afterSnapshotRestart.ApplyTransactionAsync(
            changedDuplicate,
            cancellationToken).ConfigureAwait(false);
        RequireAlreadyApplied(
            duplicate,
            position,
            afterSnapshotRestart,
            "same-instance redelivery");
        await harness.VerifyDurableStateAsync(
            SyncDestinationConformanceStage.TransactionApplied,
            afterSnapshotRestart,
            cancellationToken).ConfigureAwait(false);

        var restarted = await harness.CreateDestinationAsync(cancellationToken)
            .ConfigureAwait(false) ??
            throw Failure("The harness returned a null restarted destination.");
        provision = await restarted.ProvisionAsync(
            new SyncProvisionRequest(harness.PipelineId, harness.Source, transform),
            cancellationToken).ConfigureAwait(false);
        Require(
            provision.Status is SyncProvisionStatus.Ready,
            restarted,
            $"restart provisioning returned {provision.Status}");
        var restartReplay = await restarted.ApplyTransactionAsync(
            changedDuplicate,
            cancellationToken).ConfigureAwait(false);
        RequireAlreadyApplied(restartReplay, position, restarted, "restart redelivery");
        await harness.VerifyDurableStateAsync(
            SyncDestinationConformanceStage.RestartRedelivery,
            restarted,
            cancellationToken).ConfigureAwait(false);

        var changedTransform = SyncTransformVersion.Create("conformance", "v2");
        var replacement = await harness.CreateDestinationAsync(cancellationToken)
            .ConfigureAwait(false) ??
            throw Failure("The harness returned a null replacement destination.");
        var mismatch = await replacement.ProvisionAsync(
            new SyncProvisionRequest(harness.PipelineId, harness.Source, changedTransform),
            cancellationToken).ConfigureAwait(false);
        Require(
            mismatch.Status is SyncProvisionStatus.RebuildRequired &&
            string.Equals(
                mismatch.ExistingTransformFingerprint,
                transform.Fingerprint,
                StringComparison.Ordinal),
            replacement,
            "transform drift did not require an explicit rebuild with the existing fingerprint");

        var quarantineVerified = false;
        if (restarted is ISyncQuarantineSink quarantine)
        {
            var record = new SyncQuarantineRecord(
                harness.PipelineId,
                transform,
                harness.Source,
                42,
                position,
                "conformance",
                "poison",
                DateTimeOffset.UtcNow);
            var first = await quarantine.StoreAsync(record, cancellationToken).ConfigureAwait(false);
            var second = await quarantine.StoreAsync(record, cancellationToken).ConfigureAwait(false);
            Require(
                first && second,
                restarted,
                "durable quarantine was not idempotent across redelivery");
            quarantineVerified = true;
        }

        return new SyncDestinationConformanceResult(
            destination.Name,
            destination.Capabilities,
            quarantineVerified);
    }

    private static void RequireApplied(
        SyncApplyResult result,
        BlueTuskLogSequenceNumber expected,
        ISyncDestination destination,
        string boundary) =>
        Require(
            result.Status is SyncApplyStatus.Applied && result.DurablePosition == expected,
            destination,
            $"{boundary} did not return Applied at exact durable position {expected}");

    private static void RequireAlreadyApplied(
        SyncApplyResult result,
        BlueTuskLogSequenceNumber expected,
        ISyncDestination destination,
        string boundary) =>
        Require(
            result.Status is SyncApplyStatus.AlreadyApplied && result.DurablePosition == expected,
            destination,
            $"{boundary} did not return AlreadyApplied at exact durable position {expected}");

    private static void Require(
        bool condition,
        ISyncDestination destination,
        string message)
    {
        if (!condition)
        {
            throw Failure($"Destination '{destination.Name}' {message}.");
        }
    }

    private static SyncDestinationConformanceException Failure(string message) => new(message);
}

/// <summary>Indicates that a destination violated the shared Sync conformance contract.</summary>
public sealed class SyncDestinationConformanceException : Exception
{
    /// <summary>Initializes a new conformance failure.</summary>
    public SyncDestinationConformanceException(string message)
        : base(message)
    {
    }
}
