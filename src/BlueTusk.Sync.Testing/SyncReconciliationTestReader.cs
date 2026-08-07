using System.Runtime.CompilerServices;

namespace BlueTusk.Sync.Testing;

/// <summary>Defines one authoritative document for reconciliation tests.</summary>
public sealed class SyncReconciliationTestDocument
{
    private readonly byte[] _content;

    /// <summary>Initializes an immutable authoritative test document.</summary>
    public SyncReconciliationTestDocument(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType = "application/json",
        string? partitionKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        Key = key;
        _content = content.ToArray();
        ContentType = contentType;
        PartitionKey = partitionKey;
    }

    /// <summary>Gets the logical document key.</summary>
    public string Key { get; }

    /// <summary>Gets immutable exact materialized content.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Gets the content media type.</summary>
    public string ContentType { get; }

    /// <summary>Gets the optional destination partition key.</summary>
    public string? PartitionKey { get; }
}

/// <summary>Provides a deterministic authoritative reconciliation view for connector tests.</summary>
public sealed class SyncReconciliationTestReader : ISyncReconciliationReader
{
    private readonly IReadOnlyDictionary<string, SyncReconciliationTestDocument> _documents;

    /// <summary>Initializes a stable immutable test view.</summary>
    public SyncReconciliationTestReader(
        IEnumerable<SyncReconciliationTestDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents.ToDictionary(
            static document => document.Key,
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public ValueTask<long> CountAsync(
        string pipelineId,
        string collection,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((long)_documents.Count);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SyncReconciliationEntry> ReadPartitionAsync(
        string pipelineId,
        string collection,
        int partitionIndex,
        int partitionCount,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (var document in _documents.Values
                     .Where(document =>
                         SyncReconciler.GetPartitionIndex(document.Key, partitionCount) ==
                         partitionIndex)
                     .OrderBy(static document => SyncReconciler.GetKeyHash(document.Key))
                     .ThenBy(static document => document.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return SyncReconciliationEntry.FromContent(
                document.Key,
                document.Content,
                document.ContentType,
                document.PartitionKey);
        }
    }
}
