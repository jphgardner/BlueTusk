using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Sync;

/// <summary>Defines the depth of a reconciliation run.</summary>
public enum SyncReconciliationMode
{
    /// <summary>Compares collection counts only.</summary>
    Count,

    /// <summary>Compares partitioned key sets without inspecting content.</summary>
    KeySet,

    /// <summary>Compares partitioned key sets and SHA-256 content hashes.</summary>
    PartitionedContentHash,
}

/// <summary>Classifies a difference between an authoritative source and a destination.</summary>
public enum SyncReconciliationDifferenceKind
{
    /// <summary>The authoritative source contains a key that the destination does not.</summary>
    MissingFromDestination,

    /// <summary>The destination contains a key that the authoritative source does not.</summary>
    ExtraInDestination,

    /// <summary>Both sides contain the key with different content hashes.</summary>
    ContentMismatch,
}

/// <summary>Defines the operation used to repair one materialized key.</summary>
public enum SyncRepairMutationKind
{
    /// <summary>Creates or replaces a materialized value.</summary>
    Upsert,

    /// <summary>Removes a materialized value.</summary>
    Delete,
}

/// <summary>Configures one bounded reconciliation run.</summary>
public sealed record SyncReconciliationRequest
{
    /// <summary>Gets the pipeline that owns the materialized collection.</summary>
    public required string PipelineId { get; init; }

    /// <summary>Gets the logical materialized collection.</summary>
    public required string Collection { get; init; }

    /// <summary>Gets the comparison depth.</summary>
    public SyncReconciliationMode Mode { get; init; } =
        SyncReconciliationMode.PartitionedContentHash;

    /// <summary>Gets the deterministic number of key partitions.</summary>
    public int PartitionCount { get; init; } = 256;

    /// <summary>Gets the maximum difference samples retained in the result.</summary>
    public int MaxReportedDifferences { get; init; } = 1_000;

    /// <summary>Gets whether observed differences are repaired after durable comparison.</summary>
    public bool Repair { get; init; }

    /// <summary>Gets the maximum repair mutations sent in one destination call.</summary>
    public int RepairBatchSize { get; init; } = 500;

    /// <summary>Gets the hard repair-memory ceiling for one partition.</summary>
    public int MaxBufferedRepairsPerPartition { get; init; } = 100_000;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(PipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Collection);
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(PartitionCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PartitionCount, 65_536);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxReportedDifferences);
        ArgumentOutOfRangeException.ThrowIfLessThan(RepairBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(RepairBatchSize, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBufferedRepairsPerPartition, 1);
        if (Repair && Mode is SyncReconciliationMode.Count)
        {
            throw new ArgumentException(
                "Count-only reconciliation cannot identify records to repair.",
                nameof(Repair));
        }
    }
}

/// <summary>Contains authoritative material needed to repair an upsert.</summary>
public sealed class SyncRepairDocument
{
    private readonly byte[] _content;

    /// <summary>Initializes repair material and takes an immutable copy of its content.</summary>
    public SyncRepairDocument(
        ReadOnlyMemory<byte> content,
        string contentType,
        string? partitionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _content = content.ToArray();
        ContentType = contentType;
        PartitionKey = partitionKey;
    }

    /// <summary>Gets the immutable materialized content.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Gets the content media type.</summary>
    public string ContentType { get; }

    /// <summary>Gets the optional destination partition key.</summary>
    public string? PartitionKey { get; }
}

/// <summary>Represents one key in a stable reconciliation view.</summary>
public sealed class SyncReconciliationEntry
{
    /// <summary>Initializes a key, its SHA-256 content hash, and optional repair material.</summary>
    public SyncReconciliationEntry(
        string key,
        string contentHash,
        SyncRepairDocument? repairDocument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length != 64 || !contentHash.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "A reconciliation content hash must be 64 hexadecimal SHA-256 characters.",
                nameof(contentHash));
        }

        Key = key;
        ContentHash = contentHash.ToLowerInvariant();
        RepairDocument = repairDocument;
    }

    /// <summary>Gets the logical document key.</summary>
    public string Key { get; }

    /// <summary>Gets the lowercase SHA-256 hash of the exact materialized content.</summary>
    public string ContentHash { get; }

    /// <summary>Gets optional authoritative content used when repair is enabled.</summary>
    public SyncRepairDocument? RepairDocument { get; }

    /// <summary>Creates an entry by hashing the supplied exact materialized content.</summary>
    public static SyncReconciliationEntry FromContent(
        string key,
        ReadOnlyMemory<byte> content,
        string? contentType = null,
        string? partitionKey = null) =>
        new(
            key,
            Convert.ToHexStringLower(SHA256.HashData(content.Span)),
            contentType is null
                ? null
                : new SyncRepairDocument(content, contentType, partitionKey));
}

/// <summary>Represents one observed source/destination difference.</summary>
public sealed record SyncReconciliationDifference(
    string Key,
    SyncReconciliationDifferenceKind Kind,
    string? SourceContentHash,
    string? DestinationContentHash);

/// <summary>Represents one idempotent destination repair operation.</summary>
public sealed class SyncRepairMutation
{
    private SyncRepairMutation(
        SyncRepairMutationKind kind,
        string key,
        SyncRepairDocument? document)
    {
        Kind = kind;
        Key = key;
        Document = document;
    }

    /// <summary>Gets the repair operation.</summary>
    public SyncRepairMutationKind Kind { get; }

    /// <summary>Gets the logical materialized key.</summary>
    public string Key { get; }

    /// <summary>Gets replacement content for an upsert.</summary>
    public SyncRepairDocument? Document { get; }

    /// <summary>Creates an idempotent upsert repair.</summary>
    public static SyncRepairMutation Upsert(string key, SyncRepairDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(document);
        return new SyncRepairMutation(SyncRepairMutationKind.Upsert, key, document);
    }

    /// <summary>Creates an idempotent delete repair.</summary>
    public static SyncRepairMutation Delete(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new SyncRepairMutation(SyncRepairMutationKind.Delete, key, null);
    }
}

/// <summary>Contains one bounded, idempotent repair call.</summary>
public sealed class SyncRepairBatch
{
    private readonly ReadOnlyCollection<SyncRepairMutation> _mutations;

    /// <summary>Initializes a repair batch.</summary>
    public SyncRepairBatch(
        string pipelineId,
        string collection,
        IEnumerable<SyncRepairMutation> mutations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(mutations);
        _mutations = Array.AsReadOnly(mutations.ToArray());
        if (_mutations.Count == 0)
        {
            throw new ArgumentException("A repair batch cannot be empty.", nameof(mutations));
        }

        if (_mutations.Any(static mutation => mutation is null))
        {
            throw new ArgumentException("A repair mutation cannot be null.", nameof(mutations));
        }

        if (_mutations.Select(static mutation => mutation.Key).Distinct(StringComparer.Ordinal).Count() !=
            _mutations.Count)
        {
            throw new ArgumentException(
                "A repair batch cannot contain the same logical key more than once.",
                nameof(mutations));
        }

        PipelineId = pipelineId;
        Collection = collection;
    }

    /// <summary>Gets the pipeline being repaired.</summary>
    public string PipelineId { get; }

    /// <summary>Gets the collection being repaired.</summary>
    public string Collection { get; }

    /// <summary>Gets the idempotent operations in this bounded call.</summary>
    public IReadOnlyList<SyncRepairMutation> Mutations => _mutations;
}

/// <summary>Reads a stable, deterministically partitioned reconciliation view.</summary>
public interface ISyncReconciliationReader
{
    /// <summary>Counts a materialized collection in the reader's stable view.</summary>
    ValueTask<long> CountAsync(
        string pipelineId,
        string collection,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one partition ordered by deterministic key hash and then UTF-8 binary key. A reader
    /// must retain the same logical view for every call made by one reconciliation run.
    /// </summary>
    IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
        string pipelineId,
        string collection,
        int partitionIndex,
        int partitionCount,
        CancellationToken cancellationToken = default);
}

/// <summary>Applies bounded idempotent repairs without advancing the source CDC checkpoint.</summary>
public interface ISyncRepairSink
{
    /// <summary>Durably applies every operation or throws without claiming success.</summary>
    ValueTask ApplyRepairBatchAsync(
        SyncRepairBatch batch,
        CancellationToken cancellationToken = default);
}

/// <summary>Reports the observations and optional repair work from one reconciliation run.</summary>
public sealed class SyncReconciliationResult
{
    private readonly ReadOnlyCollection<SyncReconciliationDifference> _differences;

    internal SyncReconciliationResult(
        SyncReconciliationRequest request,
        long sourceCount,
        long destinationCount,
        long matchedKeys,
        long missingFromDestination,
        long extraInDestination,
        long contentMismatches,
        long repairedDifferences,
        IEnumerable<SyncReconciliationDifference> differences,
        bool differenceReportTruncated)
    {
        Request = request;
        SourceCount = sourceCount;
        DestinationCount = destinationCount;
        MatchedKeys = matchedKeys;
        MissingFromDestination = missingFromDestination;
        ExtraInDestination = extraInDestination;
        ContentMismatches = contentMismatches;
        RepairedDifferences = repairedDifferences;
        _differences = Array.AsReadOnly(differences.ToArray());
        DifferenceReportTruncated = differenceReportTruncated;
    }

    /// <summary>Gets the request that produced this result.</summary>
    public SyncReconciliationRequest Request { get; }

    /// <summary>Gets the source count observed by the run.</summary>
    public long SourceCount { get; }

    /// <summary>Gets the destination count observed by the run.</summary>
    public long DestinationCount { get; }

    /// <summary>Gets the number of keys present on both sides and equal at the requested depth.</summary>
    public long MatchedKeys { get; }

    /// <summary>Gets the number of source keys missing from the destination.</summary>
    public long MissingFromDestination { get; }

    /// <summary>Gets the number of destination keys absent from the source.</summary>
    public long ExtraInDestination { get; }

    /// <summary>Gets the number of equal keys whose exact content hashes differ.</summary>
    public long ContentMismatches { get; }

    /// <summary>Gets the number of differences sent to the repair sink.</summary>
    public long RepairedDifferences { get; }

    /// <summary>Gets bounded representative differences.</summary>
    public IReadOnlyList<SyncReconciliationDifference> Differences => _differences;

    /// <summary>Gets whether more differences existed than the result retained.</summary>
    public bool DifferenceReportTruncated { get; }

    /// <summary>Gets whether the observed data matched at the requested comparison depth.</summary>
    public bool IsMatch =>
        Request.Mode is SyncReconciliationMode.Count
            ? SourceCount == DestinationCount
            : MissingFromDestination == 0 &&
              ExtraInDestination == 0 &&
              ContentMismatches == 0;

    /// <summary>Gets whether repairs were applied and a verification rerun is required.</summary>
    public bool RequiresVerification => RepairedDifferences != 0;
}

/// <summary>Computes bounded count, key-set, or partitioned content-hash reconciliation.</summary>
public static class SyncReconciler
{
    /// <summary>Compares an authoritative source with a reconciliation-capable destination.</summary>
    public static async ValueTask<SyncReconciliationResult> ReconcileAsync(
        SyncReconciliationRequest request,
        ISyncReconciliationReader source,
        ISyncDestination destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.Capabilities.HasFlag(SyncDestinationCapabilities.Reconciliation) ||
            destination is not ISyncReconciliationReader destinationReader)
        {
            throw new SyncReconciliationException(
                $"Destination '{destination.Name}' does not expose the reconciliation contract.");
        }

        var repairSink = request.Repair
            ? destination as ISyncRepairSink ??
              throw new SyncReconciliationException(
                  $"Destination '{destination.Name}' does not expose the repair contract.")
            : null;

        var sourceCountTask = source.CountAsync(
            request.PipelineId,
            request.Collection,
            cancellationToken).AsTask();
        var destinationCountTask = destinationReader.CountAsync(
            request.PipelineId,
            request.Collection,
            cancellationToken).AsTask();
        await Task.WhenAll(sourceCountTask, destinationCountTask).ConfigureAwait(false);
        var expectedSourceCount = await sourceCountTask.ConfigureAwait(false);
        var expectedDestinationCount = await destinationCountTask.ConfigureAwait(false);
        if (request.Mode is SyncReconciliationMode.Count)
        {
            return new SyncReconciliationResult(
                request,
                expectedSourceCount,
                expectedDestinationCount,
                0,
                0,
                0,
                0,
                0,
                [],
                false);
        }

        var state = new ReconciliationState(request, repairSink);
        for (var partition = 0; partition < request.PartitionCount; partition++)
        {
            await ComparePartitionAsync(
                request,
                partition,
                source,
                destinationReader,
                state,
                cancellationToken).ConfigureAwait(false);
            await state.FlushRepairsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (state.SourceCount != expectedSourceCount ||
            state.DestinationCount != expectedDestinationCount)
        {
            throw new SyncReconciliationException(
                $"A reconciliation reader changed or returned an incomplete view: source counted {expectedSourceCount} and streamed {state.SourceCount}; destination counted {expectedDestinationCount} and streamed {state.DestinationCount}.");
        }

        return state.CreateResult();
    }

    private static async ValueTask ComparePartitionAsync(
        SyncReconciliationRequest request,
        int partition,
        ISyncReconciliationReader source,
        ISyncReconciliationReader destination,
        ReconciliationState state,
        CancellationToken cancellationToken)
    {
        await using var sourceEntries = source.ReadPartitionAsync(
            request.PipelineId,
            request.Collection,
            partition,
            request.PartitionCount,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using var destinationEntries = destination.ReadPartitionAsync(
            request.PipelineId,
            request.Collection,
            partition,
            request.PartitionCount,
            cancellationToken).GetAsyncEnumerator(cancellationToken);
        string? previousSourceKey = null;
        string? previousDestinationKey = null;
        var hasSource = await sourceEntries.MoveNextAsync().ConfigureAwait(false);
        var hasDestination = await destinationEntries.MoveNextAsync().ConfigureAwait(false);
        var sourceValidated = false;
        var destinationValidated = false;
        while (hasSource || hasDestination)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasSource && !sourceValidated)
            {
                ValidateEntry(sourceEntries.Current, previousSourceKey, partition, request.PartitionCount);
                previousSourceKey = sourceEntries.Current.Key;
                sourceValidated = true;
            }

            if (hasDestination && !destinationValidated)
            {
                ValidateEntry(
                    destinationEntries.Current,
                    previousDestinationKey,
                    partition,
                    request.PartitionCount);
                previousDestinationKey = destinationEntries.Current.Key;
                destinationValidated = true;
            }

            var comparison = !hasSource
                ? 1
                : !hasDestination
                    ? -1
                    : CompareEntries(sourceEntries.Current, destinationEntries.Current);
            if (comparison < 0)
            {
                state.SourceCount++;
                state.RecordMissing(sourceEntries.Current);
                hasSource = await sourceEntries.MoveNextAsync().ConfigureAwait(false);
                sourceValidated = false;
            }
            else if (comparison > 0)
            {
                state.DestinationCount++;
                state.RecordExtra(destinationEntries.Current);
                hasDestination = await destinationEntries.MoveNextAsync().ConfigureAwait(false);
                destinationValidated = false;
            }
            else
            {
                state.SourceCount++;
                state.DestinationCount++;
                if (request.Mode is SyncReconciliationMode.PartitionedContentHash &&
                    !string.Equals(
                        sourceEntries.Current.ContentHash,
                        destinationEntries.Current.ContentHash,
                        StringComparison.Ordinal))
                {
                    state.RecordMismatch(
                        sourceEntries.Current,
                        destinationEntries.Current);
                }
                else
                {
                    state.MatchedKeys++;
                }

                hasSource = await sourceEntries.MoveNextAsync().ConfigureAwait(false);
                hasDestination = await destinationEntries.MoveNextAsync().ConfigureAwait(false);
                sourceValidated = false;
                destinationValidated = false;
            }
        }
    }

    private static void ValidateEntry(
        SyncReconciliationEntry entry,
        string? previousKey,
        int expectedPartition,
        int partitionCount)
    {
        if (entry is null)
        {
            throw new SyncReconciliationException("A reconciliation reader returned a null entry.");
        }

        if (previousKey is not null && CompareKeys(previousKey, entry.Key) >= 0)
        {
            throw new SyncReconciliationException(
                $"Reconciliation keys must be unique and deterministically ordered; '{entry.Key}' followed '{previousKey}'.");
        }

        var actualPartition = GetPartitionIndex(entry.Key, partitionCount);
        if (actualPartition != expectedPartition)
        {
            throw new SyncReconciliationException(
                $"Reconciliation key '{entry.Key}' belongs to partition {actualPartition}, not {expectedPartition}.");
        }
    }

    /// <summary>Returns the deterministic SHA-256 partition for a logical key.</summary>
    public static int GetPartitionIndex(string key, int partitionCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionCount, 1);
        return (int)(((ulong)GetKeyHash(key) * (uint)partitionCount) >> 32);
    }

    /// <summary>Returns the unsigned high 32 bits of a logical key's SHA-256 hash.</summary>
    public static uint GetKeyHash(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        _ = SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        return BinaryPrimitives.ReadUInt32BigEndian(hash);
    }

    private static int CompareEntries(
        SyncReconciliationEntry left,
        SyncReconciliationEntry right) => CompareKeys(left.Key, right.Key);

    private static int CompareKeys(string left, string right)
    {
        var hashComparison = GetKeyHash(left).CompareTo(GetKeyHash(right));
        if (hashComparison != 0)
        {
            return hashComparison;
        }

        var leftRunes = left.EnumerateRunes().GetEnumerator();
        var rightRunes = right.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext();
            var hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft ? 1 : hasRight ? -1 : 0;
            }

            var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
            if (comparison != 0)
            {
                return comparison;
            }
        }
    }

    private sealed class ReconciliationState(
        SyncReconciliationRequest request,
        ISyncRepairSink? repairSink)
    {
        private readonly List<SyncReconciliationDifference> _differences = [];
        private readonly List<SyncRepairMutation> _repairs = [];
        private bool _differenceReportTruncated;

        public long SourceCount { get; set; }

        public long DestinationCount { get; set; }

        public long MatchedKeys { get; set; }

        public long MissingFromDestination { get; private set; }

        public long ExtraInDestination { get; private set; }

        public long ContentMismatches { get; private set; }

        public long RepairedDifferences { get; private set; }

        public void RecordMissing(SyncReconciliationEntry source)
        {
            MissingFromDestination++;
            Report(new SyncReconciliationDifference(
                source.Key,
                SyncReconciliationDifferenceKind.MissingFromDestination,
                source.ContentHash,
                null));
            if (repairSink is not null)
            {
                var document = source.RepairDocument ??
                    throw new SyncReconciliationException(
                        $"Authoritative key '{source.Key}' did not include content required for repair.");
                QueueRepair(SyncRepairMutation.Upsert(source.Key, document));
            }
        }

        public void RecordExtra(SyncReconciliationEntry destination)
        {
            ExtraInDestination++;
            Report(new SyncReconciliationDifference(
                destination.Key,
                SyncReconciliationDifferenceKind.ExtraInDestination,
                null,
                destination.ContentHash));
            if (repairSink is not null)
            {
                QueueRepair(SyncRepairMutation.Delete(destination.Key));
            }
        }

        public void RecordMismatch(
            SyncReconciliationEntry source,
            SyncReconciliationEntry destination)
        {
            ContentMismatches++;
            Report(new SyncReconciliationDifference(
                source.Key,
                SyncReconciliationDifferenceKind.ContentMismatch,
                source.ContentHash,
                destination.ContentHash));
            if (repairSink is not null)
            {
                var document = source.RepairDocument ??
                    throw new SyncReconciliationException(
                        $"Authoritative key '{source.Key}' did not include content required for repair.");
                QueueRepair(SyncRepairMutation.Upsert(source.Key, document));
            }
        }

        public async ValueTask FlushRepairsAsync(CancellationToken cancellationToken)
        {
            if (_repairs.Count == 0)
            {
                return;
            }

            while (_repairs.Count != 0)
            {
                var count = Math.Min(_repairs.Count, request.RepairBatchSize);
                var batch = _repairs.GetRange(0, count);
                await repairSink!.ApplyRepairBatchAsync(
                    new SyncRepairBatch(request.PipelineId, request.Collection, batch),
                    cancellationToken).ConfigureAwait(false);
                RepairedDifferences += count;
                _repairs.RemoveRange(0, count);
            }
        }

        public SyncReconciliationResult CreateResult() =>
            new(
                request,
                SourceCount,
                DestinationCount,
                MatchedKeys,
                MissingFromDestination,
                ExtraInDestination,
                ContentMismatches,
                RepairedDifferences,
                _differences,
                _differenceReportTruncated);

        private void QueueRepair(SyncRepairMutation mutation)
        {
            _repairs.Add(mutation);
            if (_repairs.Count > request.MaxBufferedRepairsPerPartition)
            {
                throw new SyncReconciliationException(
                    $"Partition repair count exceeds the configured {request.MaxBufferedRepairsPerPartition}-mutation memory ceiling. Increase the partition count or the explicit ceiling.");
            }
        }

        private void Report(SyncReconciliationDifference difference)
        {
            if (_differences.Count < request.MaxReportedDifferences)
            {
                _differences.Add(difference);
            }
            else
            {
                _differenceReportTruncated = true;
            }
        }
    }
}

/// <summary>Indicates a reconciliation contract or execution failure.</summary>
public sealed class SyncReconciliationException : Exception
{
    /// <summary>Initializes a reconciliation failure.</summary>
    public SyncReconciliationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a reconciliation failure with its cause.</summary>
    public SyncReconciliationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
