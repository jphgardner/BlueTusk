using System.Runtime.CompilerServices;

namespace BlueTusk.Edge.Http;

public enum EdgeHttpClientOwnership { Borrowed, Owned }
public sealed record EdgeHttpTransportOptions
{
    public int MaxRecordBytes { get; init; } = 512 * 1024;
    public int MaxBatchRecords { get; init; } = 512;
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
public sealed class EdgeHttpTransportException(int statusCode) : Exception($"The Edge transport returned HTTP {statusCode}; retained local mutations remain available for recovery.")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class HttpEdgeRemoteTransport : IEdgeRemoteTransport, IEdgeOrderedReceiptTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _endpoint;
    private readonly EdgeHttpClientOwnership _ownership;
    private int _disposed;
    public HttpEdgeRemoteTransport(HttpClient client, Uri endpoint, EdgeHttpTransportOptions? options = null, EdgeHttpClientOwnership ownership = EdgeHttpClientOwnership.Borrowed)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        { throw new ArgumentException("A trusted absolute HTTP(S) endpoint without credentials, query or fragment is required.", nameof(endpoint)); }
        Options = options ?? new EdgeHttpTransportOptions();
        if (!Enum.IsDefined(ownership)) { throw new ArgumentOutOfRangeException(nameof(ownership)); }
        if (Options.MaxRecordBytes is < 1 or > 64 * 1024 * 1024 || Options.MaxBatchRecords is < 1 or > 10_000 || Options.MaxRequestBytes < (Options.MaxRecordBytes + 2L) / 3 * 4 + 4096 ||
            Options.MaxResponseBytes < (Options.MaxRecordBytes + 2L) / 3 * 4 + 4096 || Options.MaxRequestBytes > 256 * 1024 * 1024 || Options.MaxResponseBytes > 256 * 1024 * 1024 ||
            Options.RequestTimeout <= TimeSpan.Zero || Options.RequestTimeout > TimeSpan.FromMinutes(5))
        { throw new ArgumentException("Edge HTTP limits exceed bounded transport capacities.", nameof(options)); }
        _client = client; _endpoint = endpoint.AbsoluteUri.TrimEnd('/'); _ownership = ownership;
    }
    public EdgeHttpTransportOptions Options { get; }
    public async ValueTask<EdgeSnapshot> BeginSnapshotAsync(EdgeScope scope, CancellationToken cancellationToken = default) =>
        EdgeWireCodec.DeserializeSnapshot((await RequestAsync(HttpMethod.Post, "snapshots" + Query(scope), null, cancellationToken).ConfigureAwait(false))!);
    public async IAsyncEnumerable<IReadOnlyList<EdgeRecord>> ReadSnapshotAsync(EdgeScope scope, EdgeSnapshot snapshot, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? after = null;
        try
        {
            var pages = 0;
            do
            {
                if (++pages > 100_000) { throw new EdgeCapacityException("Remote snapshot exceeded the bounded page count."); }
                var bytes = await RequestAsync(HttpMethod.Get, "snapshots/" + snapshot.Id.ToString("D") + Query(scope) + "&limit=" + Options.MaxBatchRecords + (after is null ? string.Empty : "&after=" + Uri.EscapeDataString(after)), null, cancellationToken).ConfigureAwait(false);
                var page = EdgeWireCodec.DeserializeSnapshotPage(bytes!, Options.MaxBatchRecords, Options.MaxRecordBytes);
                if (page.NextAfterId is not null && (page.Records.Count == 0 || page.NextAfterId == after)) { throw new EdgeHttpTransportException(200); }
                yield return page.Records;
                after = page.NextAfterId;
            } while (after is not null);
        }
        finally { _ = await RequestAsync(HttpMethod.Delete, "snapshots/" + snapshot.Id.ToString("D") + Query(scope), null, CancellationToken.None).ConfigureAwait(false); }
    }
    public async ValueTask<EdgeChangeBatch?> ReadChangesAsync(EdgeScope scope, long afterPosition, int maxRecords, CancellationToken cancellationToken = default)
    {
        if (maxRecords < 1 || maxRecords > Options.MaxBatchRecords) { throw new ArgumentOutOfRangeException(nameof(maxRecords)); }
        var bytes = await RequestAsync(HttpMethod.Get, "changes" + Query(scope) + "&after=" + EdgeWireCodec.Number(afterPosition) + "&limit=" + maxRecords, null, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : EdgeWireCodec.DeserializeChanges(bytes, maxRecords, Options.MaxRecordBytes);
    }
    public async ValueTask<EdgeMutationOutcome> ApplyMutationAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Payload.Length > Options.MaxRecordBytes) { throw new EdgeCapacityException("Mutation payload exceeds the transport byte limit."); }
        var bytes = await RequestAsync(HttpMethod.Post, "mutations" + Query(mutation.Scope), EdgeWireCodec.SerializeMutation(mutation), cancellationToken).ConfigureAwait(false);
        return EdgeWireCodec.DeserializeOutcome(bytes!, Options.MaxRecordBytes);
    }
    /// <summary>Call only after the matching outcome has been committed to durable local storage. A lost confirmation response can be retried.</summary>
    public async ValueTask FinalizeMutationReceiptAsync(EdgeMutation mutation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        if (mutation.Payload.Length > Options.MaxRecordBytes) { throw new EdgeCapacityException("Mutation payload exceeds the transport byte limit."); }
        _ = await RequestAsync(HttpMethod.Post, "mutations/confirm" + Query(mutation.Scope), EdgeWireCodec.SerializeMutation(mutation), cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Advance only after every ordered outcome in the prefix was durably acknowledged and confirmed. A lost response can be retried.</summary>
    public async ValueTask AdvanceOrderedReceiptHorizonAsync(EdgeScope scope, Guid throughMutationId, int maxReceipts = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!EdgeOrderedMutationId.TryParse(throughMutationId, out _, out var sequence) || sequence == 0) { throw new ArgumentException("An ordered mutation identity is required.", nameof(throughMutationId)); }
        if (maxReceipts is < 1 or > 10_000) { throw new ArgumentOutOfRangeException(nameof(maxReceipts)); }
        _ = await RequestAsync(HttpMethod.Post, "mutations/horizon" + Query(scope), EdgeWireCodec.SerializeOrderedReceiptHorizon(throughMutationId, maxReceipts), cancellationToken).ConfigureAwait(false);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownership is EdgeHttpClientOwnership.Owned) { _client.Dispose(); }
    }
    private async ValueTask<byte[]?> RequestAsync(HttpMethod method, string path, byte[]? payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.RequestTimeout);
        using var request = new HttpRequestMessage(method, new Uri(_endpoint + "/" + path));
        if (payload is not null)
        {
            if (payload.Length > Options.MaxRequestBytes) { throw new EdgeCapacityException("Encoded mutation exceeds the HTTP request byte budget."); }
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) { throw new EdgeHttpTransportException((int)response.StatusCode); }
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent) { return null; }
        if (response.Content.Headers.ContentLength > Options.MaxResponseBytes) { throw new EdgeCapacityException("HTTP response exceeds the byte budget."); }
        await using var incoming = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await incoming.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) { break; }
            if (memory.Length + count > Options.MaxResponseBytes) { throw new EdgeCapacityException("HTTP response exceeds the byte budget."); }
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    private static string Query(EdgeScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return "?tenant=" + Uri.EscapeDataString(scope.Tenant) + "&scope=" + Uri.EscapeDataString(scope.Id) + "&epoch=" + EdgeWireCodec.Number(scope.Epoch);
    }
}
