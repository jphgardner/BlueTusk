using System.Data.Common;
using System.Text.Json;
using BlueTusk.Edge.Http;
using BlueTusk.Edge.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace BlueTusk.Edge.AspNetCore;

public sealed record EdgeEndpointOptions
{
    public string Prefix { get; init; } = "/edge";
    /// <summary>Host policy must require authenticated token/bearer access; cookie-only authorization requires a separate CSRF policy.</summary>
    public required string AuthorizationPolicy { get; init; }
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Optional first-apply database effects, executed in the same owned transaction as the Edge record/feed/receipt. No external I/O or transaction ownership transfer.</summary>
    public Func<DbConnection, DbTransaction, EdgeMutation, EdgeRecord, CancellationToken, ValueTask>? WriteBusinessAsync { get; init; }
}

public static class EdgeEndpointExtensions
{
    public static RouteGroupBuilder MapBlueTuskEdge(this IEndpointRouteBuilder endpoints, PostgreSqlEdgeServerStore store,
        Func<HttpContext, EdgeScope, CancellationToken, ValueTask<bool>> authorizeScope, EdgeEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(authorizeScope); ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AuthorizationPolicy);
        if (!options.Prefix.StartsWith('/') || options.Prefix.Contains('{', StringComparison.Ordinal) || options.Prefix.Contains('?', StringComparison.Ordinal) ||
            options.MaxRequestBytes < (store.Options.MaxRecordBytes + 2L) / 3 * 4 + 4096 || options.MaxResponseBytes < (store.Options.MaxBatchBytes + 2L) / 3 * 4 + store.Options.MaxBatchRecords * 4096L || options.MaxRequestBytes > 256 * 1024 * 1024 || options.MaxResponseBytes > 256 * 1024 * 1024)
        { throw new ArgumentException("Endpoint prefix/limits are incompatible with the server store.", nameof(options)); }
        var group = endpoints.MapGroup(options.Prefix).RequireAuthorization(options.AuthorizationPolicy);
        group.MapPost("/snapshots", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
            EdgeWireCodec.SerializeSnapshot(await store.BeginSnapshotAsync(scope, context.RequestAborted).ConfigureAwait(false))));
        group.MapGet("/snapshots/{snapshotId:guid}", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            var page = await store.ReadSnapshotPageAsync(scope, Guid.Parse((string)context.Request.RouteValues["snapshotId"]!), Limit(context, store.Options.MaxBatchRecords),
                context.Request.Query.TryGetValue("after", out var after) ? after.ToString() : null, context.RequestAborted).ConfigureAwait(false);
            return EdgeWireCodec.SerializeSnapshotPage(page.Records, page.NextAfterId);
        }));
        group.MapDelete("/snapshots/{snapshotId:guid}", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            await store.ReleaseSnapshotAsync(scope, Guid.Parse((string)context.Request.RouteValues["snapshotId"]!), context.RequestAborted).ConfigureAwait(false); return null;
        }));
        group.MapGet("/changes", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            var batch = await store.ReadChangesAsync(scope, EdgeWireCodec.ParseNumber(context.Request.Query["after"].ToString()), Limit(context, store.Options.MaxBatchRecords), context.RequestAborted).ConfigureAwait(false);
            return batch is null ? null : EdgeWireCodec.SerializeChanges(batch);
        }));
        group.MapPost("/mutations", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            var body = await BodyAsync(context.Request, options.MaxRequestBytes, context.RequestAborted).ConfigureAwait(false);
            var mutation = EdgeWireCodec.DeserializeMutation(scope, body, store.Options.MaxRecordBytes);
            var outcome = options.WriteBusinessAsync is null
                ? await store.ApplyMutationAsync(mutation, context.RequestAborted).ConfigureAwait(false)
                : await store.ApplyMutationWithBusinessAsync(mutation, options.WriteBusinessAsync, context.RequestAborted).ConfigureAwait(false);
            return EdgeWireCodec.SerializeOutcome(outcome);
        }));
        group.MapPost("/mutations/confirm", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            var body = await BodyAsync(context.Request, options.MaxRequestBytes, context.RequestAborted).ConfigureAwait(false);
            var mutation = EdgeWireCodec.DeserializeMutation(scope, body, store.Options.MaxRecordBytes);
            await store.FinalizeMutationReceiptAsync(mutation, context.RequestAborted).ConfigureAwait(false);
            return null;
        }));
        group.MapPost("/mutations/horizon", (HttpContext context) => HandleAsync(context, store, authorizeScope, options, async scope =>
        {
            var body = await BodyAsync(context.Request, options.MaxRequestBytes, context.RequestAborted).ConfigureAwait(false);
            var (through, maximum) = EdgeWireCodec.DeserializeOrderedReceiptHorizon(body);
            _ = await store.AdvanceOrderedReceiptHorizonAsync(scope, through, maximum, context.RequestAborted).ConfigureAwait(false);
            return null;
        }));
        return group;
    }

    private static async Task HandleAsync(HttpContext context, PostgreSqlEdgeServerStore store,
        Func<HttpContext, EdgeScope, CancellationToken, ValueTask<bool>> authorize, EdgeEndpointOptions options, Func<EdgeScope, ValueTask<byte[]?>> action)
    {
        try
        {
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) { feature.MaxRequestBodySize = options.MaxRequestBytes; }
            var scope = new EdgeScope(context.Request.Query["tenant"].ToString(), context.Request.Query["scope"].ToString(), EdgeWireCodec.ParseNumber(context.Request.Query["epoch"].ToString()));
            if (!await authorize(context, scope, context.RequestAborted).ConfigureAwait(false)) { context.Response.StatusCode = 403; return; }
            var payload = await action(scope).ConfigureAwait(false);
            if (payload is null) { context.Response.StatusCode = 204; return; }
            if (payload.Length > options.MaxResponseBytes) { throw new EdgeCapacityException("Encoded response exceeds its byte limit."); }
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = payload.Length;
            await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        }
        catch (EdgeServerReplayExpiredException) { context.Response.StatusCode = 410; }
        catch (EdgeServerSnapshotExpiredException) { context.Response.StatusCode = 410; }
        catch (EdgeServerReceiptFinalizedException) { context.Response.StatusCode = 410; }
        catch (EdgeServerReceiptMissingException) { context.Response.StatusCode = 404; }
        catch (EdgeScopeMismatchException) { context.Response.StatusCode = 409; }
        catch (EdgeCheckpointMismatchException) { context.Response.StatusCode = 409; }
        catch (EdgeRevisionConflictException) { context.Response.StatusCode = 409; }
        catch (EdgeMutationIdentityException) { context.Response.StatusCode = 409; }
        catch (EdgeCapacityException) { context.Response.StatusCode = 429; }
        catch (JsonException) { context.Response.StatusCode = 400; }
        catch (FormatException) { context.Response.StatusCode = 400; }
        catch (OverflowException) { context.Response.StatusCode = 400; }
        catch (ArgumentException) { context.Response.StatusCode = 400; }
        catch (DbException) { context.Response.StatusCode = 500; }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }
    private static int Limit(HttpContext context, int maximum)
    {
        var text = context.Request.Query["limit"].ToString();
        return text.Length == 0 ? maximum : checked((int)EdgeWireCodec.ParseNumber(text));
    }
    private static async ValueTask<byte[]> BodyAsync(HttpRequest request, int maximum, CancellationToken cancellationToken)
    {
        if (request.ContentType is null || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) { throw new ArgumentException("Mutations require JSON content type.", nameof(request)); }
        if (request.ContentLength > maximum) { throw new EdgeCapacityException("Encoded request exceeds the byte limit."); }
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) { return memory.ToArray(); }
            if (memory.Length + count > maximum) { throw new EdgeCapacityException("Encoded request exceeds the byte limit."); }
            memory.Write(buffer, 0, count);
        }
    }
}
