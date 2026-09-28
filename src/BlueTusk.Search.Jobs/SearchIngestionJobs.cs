using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Data;
using BlueTusk.Jobs;

namespace BlueTusk.Search.Jobs;

public sealed record SearchIngestionJobOptions
{
    public string Queue { get; init; } = "search-ingestion";
    public string JobType { get; init; } = "bluetusk.search.ingest.v1";
    /// <summary>Versioned host identity for the target schema, chunking, vector dimensions and embedding model.</summary>
    public required string IndexContract { get; init; }
    public int MaxPayloadBytes { get; init; } = 1024 * 1024;
    public int MaximumAttempts { get; init; } = 5;
}

/// <summary>Durably snapshots source versions and ACLs before embedding. Jobs own delivery leases; Search owns version/tombstone fencing.</summary>
public sealed class SearchIngestionJobs
{
    private readonly PostgreSqlJobStore _jobs;
    private readonly PostgreSqlSearchStore _search;

    public SearchIngestionJobs(PostgreSqlJobStore jobs, PostgreSqlSearchStore search, SearchIngestionJobOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(options);
        ValidateName(options.Queue);
        ValidateName(options.JobType);
        ValidateName(options.IndexContract);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxPayloadBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxPayloadBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaximumAttempts, 10_000);
        _jobs = jobs;
        _search = search;
        Options = options;
    }

    public SearchIngestionJobOptions Options { get; }

    public ValueTask<Guid> EnqueueUpsertAsync(SearchDocument document, CancellationToken cancellationToken = default) =>
        _jobs.EnqueueAsync(UpsertRequest(document), cancellationToken);

    /// <summary>Colocates durable ingestion admission with an application transaction in the Jobs database.</summary>
    public ValueTask<Guid> EnqueueUpsertInTransactionAsync(SearchDocument document, BlueTuskTransaction transaction, CancellationToken cancellationToken = default) =>
        _jobs.EnqueueAsync(UpsertRequest(document), transaction, cancellationToken);

    public ValueTask<Guid> EnqueueDeleteAsync(string tenant, string index, string id, long version, CancellationToken cancellationToken = default) =>
        _jobs.EnqueueAsync(DeleteRequest(tenant, index, id, version), cancellationToken);

    public ValueTask<Guid> EnqueueDeleteInTransactionAsync(string tenant, string index, string id, long version, BlueTuskTransaction transaction, CancellationToken cancellationToken = default) =>
        _jobs.EnqueueAsync(DeleteRequest(tenant, index, id, version), transaction, cancellationToken);

    public JobHandlerRegistry RegisterHandlers(JobHandlerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register(Options.JobType, SearchJobJsonContext.Default.SearchJobPayload,
            (payload, context, cancellationToken) => ExecuteAsync(payload, context.Lease ?? throw new InvalidOperationException("A leased Jobs execution context is required."), cancellationToken));
    }

    /// <summary>Executes an already claimed lease. The worker completes/retries it separately; replay is version-idempotent.</summary>
    public async ValueTask ProcessLeaseAsync(JobLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Payload.Length > Options.MaxPayloadBytes) { throw new JobHandlerException("search_payload_limit", retryable: false); }
        SearchJobPayload? payload;
        try { payload = JsonSerializer.Deserialize(lease.Payload.Span, SearchJobJsonContext.Default.SearchJobPayload); }
        catch (JsonException) { throw new JobHandlerException("search_invalid_payload", retryable: false); }
        if (payload is null) { throw new JobHandlerException("search_invalid_payload", retryable: false); }
        await ExecuteAsync(payload, lease, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ExecuteAsync(SearchJobPayload payload, JobLease lease, CancellationToken cancellationToken)
    {
        if (lease.Payload.Length > Options.MaxPayloadBytes) { throw new JobHandlerException("search_payload_limit", retryable: false); }
        if (lease.JobType != Options.JobType || lease.Scope.Queue != Options.Queue || lease.Scope.Tenant != payload.Tenant ||
            payload.Format != 1 || payload.IndexContract != Options.IndexContract || payload.Version <= 0)
        {
            throw new JobHandlerException("search_contract_mismatch", retryable: false);
        }

        // Reject a replaced/expired lease before external embedding work. Search rechecks source versions at commit.
        var fence = await _jobs.ExecuteFencedAsync(lease, static (_, _, _) => ValueTask.FromResult(true), cancellationToken).ConfigureAwait(false);
        if (!fence.Executed) { throw new JobHandlerException("search_lease_lost"); }
        try
        {
            if (payload.Deleted)
            {
                _ = await _search.DeleteAsync(payload.Tenant, payload.Index, payload.Id, payload.Version, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var metadata = JsonDocument.Parse(payload.MetadataJson);
                _ = await _search.UpsertAsync(new SearchDocument(payload.Tenant, payload.Index, payload.Id, payload.Version,
                    payload.Title, payload.Content, payload.Public, payload.Principals, metadata.RootElement), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SearchVersionConflictException) { throw new JobHandlerException("search_version_conflict", retryable: false); }
        catch (SearchBackpressureException) { throw new JobHandlerException("search_backpressure"); }
        catch (SearchEmbeddingCheckpointOwnershipException) { throw new JobHandlerException("search_embedding_owner_lost"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new JobHandlerException("search_embedding_timeout"); }
        catch (ArgumentException) { throw new JobHandlerException("search_invalid_payload", retryable: false); }
        catch (JsonException) { throw new JobHandlerException("search_invalid_payload", retryable: false); }
    }

    private JobRequest UpsertRequest(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Encoding.UTF8.GetByteCount(document.Content) > _search.Options.MaxDocumentBytes ||
            Encoding.UTF8.GetByteCount(document.Title) > _search.Options.MaxTitleBytes)
        {
            throw new ArgumentException("Document exceeds the target Search ingestion limits.", nameof(document));
        }

        return Request(new(1, Options.IndexContract, document.Scope.Tenant, document.Scope.Index, document.Id, document.Version,
            false, document.Title, document.Content, document.IsPublic, document.Scope.Principals.ToArray(), document.Metadata.GetRawText()));
    }

    private JobRequest DeleteRequest(string tenant, string index, string id, long version)
    {
        // Apply the same identity limits as direct Search ingestion.
        var validated = new SearchDocument(tenant, index, id, version, string.Empty, string.Empty);
        return Request(new(1, Options.IndexContract, validated.Scope.Tenant, validated.Scope.Index, validated.Id, validated.Version,
            true, string.Empty, string.Empty, false, [], "{}"));
    }

    private JobRequest Request(SearchJobPayload payload)
    {
        using var stream = new PayloadStream(Options.MaxPayloadBytes);
        JsonSerializer.Serialize(stream, payload, SearchJobJsonContext.Default.SearchJobPayload);
        var identity = string.Join('\0', Options.IndexContract, payload.Index, payload.Id, payload.Version.ToString(CultureInfo.InvariantCulture));
        // One source identity/version admits either an identical upsert or an identical deletion, never incompatible operations.
        return new JobRequest
        {
            Scope = new JobScope(payload.Tenant, Options.Queue),
            JobType = Options.JobType,
            Payload = stream.ToArray(),
            MaximumAttempts = Options.MaximumAttempts,
            DeduplicationKey = "search:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
        };
    }

    private static void ValidateName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 200 || value.Contains('\0', StringComparison.Ordinal)) { throw new ArgumentException("Job name/contract exceeds its bounded identity limit.", nameof(value)); }
    }

    private sealed class PayloadStream(int maximumBytes) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > maximumBytes) { throw new ArgumentException("Durable Search job exceeds its serialized payload byte budget."); }
            base.Write(buffer);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > maximumBytes) { throw new ArgumentException("Durable Search job exceeds its serialized payload byte budget."); }
            base.Write(buffer, offset, count);
        }
    }
}

internal sealed record SearchJobPayload(int Format, string IndexContract, string Tenant, string Index, string Id, long Version,
    bool Deleted, string Title, string Content, bool Public, string[] Principals, string MetadataJson);
[JsonSerializable(typeof(SearchJobPayload))]
internal sealed partial class SearchJobJsonContext : JsonSerializerContext;
