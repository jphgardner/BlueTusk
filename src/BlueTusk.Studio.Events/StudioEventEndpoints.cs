using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.Studio.Events;

/// <summary>The host resolves authorized tenant/stream pairs to safe aliases. The event store is borrowed.</summary>
public sealed record StudioEventScope(PostgreSqlEventStore Store, IReadOnlyDictionary<string, EventStreamKey> Streams);

public interface IStudioEventScopeResolver
{
    ValueTask<StudioEventScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record StudioEventOptions
{
    public int MaximumEvents { get; init; } = 100;
    public int MaximumPayloadBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumReplyBytes { get; init; } = 1024 * 1024;
    public int MaximumConcurrentReads { get; init; } = 4;
    public int TimeoutSeconds { get; init; } = 10;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumEvents, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumPayloadBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumPayloadBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumReplyBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumReplyBytes, 16 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumConcurrentReads, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumConcurrentReads, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(TimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(TimeoutSeconds, 30);
    }
}

public static class StudioEventEndpoints
{
    public static IServiceCollection AddBlueTuskStudioEvents<TResolver>(this IServiceCollection services,
        StudioEventOptions options) where TResolver : class, IStudioEventScopeResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<StudioEventAdmission>();
        services.AddScoped<IStudioEventScopeResolver, TResolver>();
        return services;
    }

    public static RouteGroupBuilder MapBlueTuskStudioEvents(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var studio = endpoints.ServiceProvider.GetRequiredService<StudioOptions>();
        var group = endpoints.MapGroup(studio.Path + "/events").RequireAuthorization(studio.ReadPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return await next(context).ConfigureAwait(false);
        });
        group.MapGet("/streams", (HttpContext context, IStudioEventScopeResolver resolver, StudioEventOptions options,
            StudioEventAdmission admission, IStudioAuditSink audit) => ReadAsync(context, resolver, options, admission, audit, true));
        group.MapGet("/", (HttpContext context, IStudioEventScopeResolver resolver, StudioEventOptions options,
            StudioEventAdmission admission, IStudioAuditSink audit) => ReadAsync(context, resolver, options, admission, audit, false));
        return group;
    }

    private static async Task ReadAsync(HttpContext context, IStudioEventScopeResolver resolver, StudioEventOptions options,
        StudioEventAdmission admission, IStudioAuditSink audit, bool listStreams)
    {
        if (!await admission.Capacity.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var scope = await resolver.ResolveAsync(context.User, deadline.Token).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(scope.Store);
            ArgumentNullException.ThrowIfNull(scope.Streams);
            if (scope.Streams.Count > 1000) { throw new StudioReplyLimitException(); }
            foreach (var (alias, key) in scope.Streams)
            {
                if (!Alias(alias) || key is null) { throw new ArgumentException("Invalid authorized event stream."); }
            }
            using var buffer = new MemoryStream();
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            if (listStreams)
            {
                writer.WriteStartArray("streams");
                foreach (var alias in scope.Streams.Keys.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(alias);
                    Check(writer, options.MaximumReplyBytes);
                }
                writer.WriteEndArray();
            }
            else
            {
                var alias = context.Request.Query["stream"].ToString();
                if (!Alias(alias)) { throw new ArgumentException("Invalid stream alias."); }
                if (!scope.Streams.TryGetValue(alias, out var stream)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
                var afterText = context.Request.Query["after"].ToString();
                var after = 0L;
                if (afterText.Length != 0 && (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0 ||
                    after.ToString(CultureInfo.InvariantCulture) != afterText)) { throw new ArgumentException("Invalid event cursor."); }
                var limit = options.MaximumEvents;
                if (context.Request.Query.TryGetValue("limit", out var requested) && (!int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                    limit < 1 || limit > options.MaximumEvents)) { throw new ArgumentException("Invalid event count."); }
                var operation = Guid.CreateVersion7();
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
                if (string.IsNullOrWhiteSpace(actor)) { throw new UnauthorizedAccessException("A stable Studio audit actor is required."); }
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("events:" + alias + ":" + after.ToString(CultureInfo.InvariantCulture))));
                var scopeId = "events:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stream.TenantId + "\0" + stream.StreamId)));
                await audit.RecordAsync(new(operation, actor, fingerprint, "event-trace-attempt", 0) { ScopeId = scopeId }, deadline.Token).ConfigureAwait(false);
                var values = await scope.Store.ReadAsync(stream, after, limit, options.MaximumPayloadBytes, deadline.Token).ConfigureAwait(false);
                writer.WriteStartArray("events");
                foreach (var value in values)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", value.EventId);
                    writer.WriteString("sequence", value.Sequence.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("type", value.EventType);
                    writer.WriteNumber("version", value.Version);
                    writer.WriteString("occurredAt", value.OccurredAt);
                    writer.WriteNumber("payloadBytes", value.Payload.Length);
                    writer.WriteEndObject();
                    Check(writer, options.MaximumReplyBytes);
                }
                writer.WriteEndArray();
                // The payload-byte limit may end a page before the row limit. Any nonempty
                // page can be continued; only an empty page proves the current end of stream.
                if (values.Count == 0) { writer.WriteNull("next"); }
                else { writer.WriteString("next", values[^1].Sequence.ToString(CultureInfo.InvariantCulture)); }
                await audit.RecordAsync(new(operation, actor, fingerprint, "event-trace-completed", values.Count) { ScopeId = scopeId }, deadline.Token).ConfigureAwait(false);
            }
            writer.WriteEndObject();
            Check(writer, options.MaximumReplyBytes);
            writer.Flush();
            deadline.Token.ThrowIfCancellationRequested();
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            context.Response.StatusCode = exception switch
            {
                UnauthorizedAccessException => StatusCodes.Status403Forbidden,
                StudioAuditHorizonException => StatusCodes.Status409Conflict,
                ArgumentException => StatusCodes.Status400BadRequest,
                StudioReplyLimitException => StatusCodes.Status413PayloadTooLarge,
                OperationCanceledException => StatusCodes.Status408RequestTimeout,
                _ => StatusCodes.Status422UnprocessableEntity,
            };
        }
        finally { admission.Capacity.Release(); }
    }

    private static bool Alias(string value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static void Check(Utf8JsonWriter writer, int maximum)
    {
        if (writer.BytesCommitted + writer.BytesPending > maximum) { throw new StudioReplyLimitException(); }
    }
}

internal sealed class StudioEventAdmission(StudioEventOptions options) : IDisposable
{
    internal SemaphoreSlim Capacity { get; } = new(options.MaximumConcurrentReads);
    public void Dispose() => Capacity.Dispose();
}
