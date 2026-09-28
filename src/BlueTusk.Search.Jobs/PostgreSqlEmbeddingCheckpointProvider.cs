using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Search.Jobs;

/// <summary>Persists completed vectors and database-clock ownership. External provider calls remain at least once.</summary>
public sealed partial class PostgreSqlEmbeddingCheckpointProvider : IScopedSearchEmbeddingProvider, IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DbDataSource _source;
    private readonly ISearchEmbeddingProvider _provider;
    private readonly SearchDataSourceOwnership _ownership;
    private readonly SemaphoreSlim _operations;
    private readonly string _schema;
    private int _disposed;
    private int _initialized;

    public PostgreSqlEmbeddingCheckpointProvider(DbDataSource source, ISearchEmbeddingProvider provider, SearchEmbeddingCheckpointOptions? options = null,
        SearchDataSourceOwnership ownership = SearchDataSourceOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(provider);
        if (!Enum.IsDefined(ownership)) { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        Options = options ?? new SearchEmbeddingCheckpointOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Schema); ArgumentException.ThrowIfNullOrWhiteSpace(provider.ModelIdentity);
        if (Options.Schema.Contains('\0', StringComparison.Ordinal) || StrictUtf8.GetByteCount(Options.Schema) > 63 ||
            provider.ModelIdentity.Contains('\0', StringComparison.Ordinal) || StrictUtf8.GetByteCount(provider.ModelIdentity) > 256 ||
            Options.Dimensions is < 1 or > 4096 || Options.MaxModels is < 1 or > 10_000 || Options.MaxRecords is < 1 or > 1_000_000 ||
            Options.MaxReservedVectorBytes < Options.Dimensions * 4L || Options.MaxBatchTexts is < 1 or > 256 ||
            Options.MaxTextBytes is < 1 or > 1024 * 1024 || Options.MaxBatchInputBytes < Options.MaxTextBytes || Options.MaxBatchInputBytes > 64 * 1024 * 1024 ||
            Options.MaxConcurrentOperations is < 1 or > 256 || Options.MaxActiveProviderBatches is < 1 or > 256 ||
            Options.MaxActiveProviderBatchesPerTenant < 1 || Options.MaxActiveProviderBatchesPerTenant > Options.MaxActiveProviderBatches ||
            Options.ProviderTimeout <= TimeSpan.Zero || Options.ProviderTimeout > TimeSpan.FromMinutes(5) || Options.LeaseDuration <= Options.ProviderTimeout || Options.LeaseDuration > TimeSpan.FromHours(1) ||
            Options.Retention <= TimeSpan.Zero || Options.Retention > TimeSpan.FromDays(365) || Options.CommandTimeoutSeconds is < 1 or > 300)
        { throw new ArgumentException("Embedding checkpoint options exceed supported storage, input, lease or admission bounds.", nameof(options)); }
        _source = source; _provider = provider; _ownership = ownership; ModelIdentity = provider.ModelIdentity;
        _schema = '"' + Options.Schema.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        _operations = new(Options.MaxConcurrentOperations, Options.MaxConcurrentOperations);
    }
    public string ModelIdentity { get; }
    public SearchEmbeddingCheckpointOptions Options { get; }

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Durable embeddings require an explicit tenant/index scope through EmbedForScopeAsync.");

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedForScopeAsync(SearchScope scope, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(texts);
        var count = texts.Count;
        if (count is < 1 || count > Options.MaxBatchTexts) { throw new ArgumentException("Embedding batch exceeds its text count budget.", nameof(texts)); }
        if (_provider.ModelIdentity != ModelIdentity) { throw new InvalidOperationException("The embedding model identity changed after checkpoint construction."); }
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var hashes = new string[count];
        long bytes = 0;
        for (var i = 0; i < count; i++)
        {
            var text = texts[i];
            ArgumentNullException.ThrowIfNull(text);
            var length = StrictUtf8.GetByteCount(text); bytes += length;
            if (length > Options.MaxTextBytes || bytes > Options.MaxBatchInputBytes) { throw new ArgumentException("Embedding batch exceeds its input byte budget.", nameof(texts)); }
            var encoded = StrictUtf8.GetBytes(text);
            hashes[i] = Convert.ToHexString(SHA256.HashData(encoded)); inputs.TryAdd(hashes[i], text);
        }
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        var owner = Guid.NewGuid();
        try
        {
            var claim = await ClaimAsync(scope, inputs.Keys.Order(StringComparer.Ordinal).ToArray(), owner, cancellationToken).ConfigureAwait(false);
            if (claim.Missing.Count != 0)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(Options.ProviderTimeout);
                var batch = claim.Missing.Select(item => inputs[item.Hash]).ToArray();
                var operation = _provider is IScopedSearchEmbeddingProvider scoped
                    ? scoped.EmbedForScopeAsync(scope, batch, deadline.Token).AsTask()
                    : _provider.EmbedAsync(batch, deadline.Token).AsTask();
                IReadOnlyList<ReadOnlyMemory<float>> vectors;
                try { vectors = await operation.WaitAsync(deadline.Token).ConfigureAwait(false); }
                catch
                {
                    _ = operation.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
                if (_provider.ModelIdentity != ModelIdentity) { throw new InvalidOperationException("The embedding model identity changed during provider work."); }
                if (vectors.Count != batch.Length) { throw new InvalidOperationException("Embedding provider result count differs from its requested batch."); }
                var encoded = vectors.Select(Encode).ToArray();
                await CompleteAsync(scope, owner, claim.Missing, encoded, cancellationToken).ConfigureAwait(false);
                for (var i = 0; i < claim.Missing.Count; i++) { claim.Completed.Add(claim.Missing[i].Hash, Decode(encoded[i])); }
            }
            return Array.AsReadOnly(hashes.Select(hash => claim.Completed[hash]).ToArray());
        }
        catch
        {
            try { await ReleaseAsync(scope, owner).ConfigureAwait(false); }
            catch (Exception exception) when (exception is DbException or ObjectDisposedException or OperationCanceledException) { }
            throw;
        }
        finally { _operations.Release(); }
    }
    private byte[] Encode(ReadOnlyMemory<float> vector)
    {
        if (vector.Length != Options.Dimensions) { throw new ArgumentException("Embedding vector dimension differs from its checkpoint contract.", nameof(vector)); }
        var payload = new byte[vector.Length * 4]; double norm = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            var value = vector.Span[i]; if (!float.IsFinite(value)) { throw new ArgumentException("Embedding vectors must contain finite values.", nameof(vector)); }
            norm += (double)value * value; BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(i * 4, 4), value);
        }
        if (norm == 0) { throw new ArgumentException("Cosine embeddings require a nonzero norm.", nameof(vector)); }
        return payload;
    }
    private ReadOnlyMemory<float> Decode(byte[] payload)
    {
        if (payload.Length != Options.Dimensions * 4) { throw new InvalidOperationException("Checkpoint vector format differs from its installed dimension contract."); }
        var vector = new float[Options.Dimensions];
        for (var i = 0; i < vector.Length; i++) { vector[i] = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(i * 4, 4)); }
        _ = Encode(vector); return vector;
    }
    private async ValueTask EnterAsync(CancellationToken cancellationToken, bool requireInitialized = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (requireInitialized && Volatile.Read(ref _initialized) == 0) { throw new InvalidOperationException("Initialize embedding checkpoints before use."); }
        if (!await _operations.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { throw new SearchBackpressureException(); }
        if (Volatile.Read(ref _disposed) != 0) { _operations.Release(); throw new ObjectDisposedException(nameof(PostgreSqlEmbeddingCheckpointProvider)); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        for (var i = 0; i < Options.MaxConcurrentOperations; i++) { await _operations.WaitAsync().ConfigureAwait(false); }
        _operations.Dispose();
        if (_ownership is SearchDataSourceOwnership.Owned) { await _source.DisposeAsync().ConfigureAwait(false); }
    }
    private sealed record Pending(string Hash, long Fence);
    private sealed record Claim(Dictionary<string, ReadOnlyMemory<float>> Completed, List<Pending> Missing);
}
