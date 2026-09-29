using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.ControlPlane;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.Studio.ControlPlane;

public static class StudioControlPlaneEndpoints
{
    public static IServiceCollection AddBlueTuskStudioControlPlane<TResolver>(this IServiceCollection services,
        StudioControlPlaneOptions options) where TResolver : class, IStudioControlPlaneScopeResolver
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<StudioControlPlaneAdmission>();
        services.AddScoped<IStudioControlPlaneScopeResolver, TResolver>();
        return services;
    }

    public static RouteGroupBuilder MapBlueTuskStudioControlPlane(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var studio = endpoints.ServiceProvider.GetRequiredService<StudioOptions>();
        var options = endpoints.ServiceProvider.GetRequiredService<StudioControlPlaneOptions>();
        var group = endpoints.MapGroup(studio.Path + "/operations").RequireAuthorization(studio.ReadPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff";
            return await next(context).ConfigureAwait(false);
        });
        group.MapGet("/live", InspectAsync);
        group.MapPost("/replay", ReplayAsync).RequireAuthorization(options.ReplayPolicy);
        return group;
    }

    private static async Task InspectAsync(HttpContext context, IStudioControlPlaneScopeResolver resolver,
        StudioControlPlaneOptions options, StudioControlPlaneAdmission admission)
    {
        if (!await admission.Capacity.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            var after = context.Request.Query["after"].ToString();
            if (after.Length != 0 && !Fingerprint(after)) { throw new ArgumentException("Invalid page cursor."); }
            var limit = options.MaximumSubscriptions;
            if (context.Request.Query.TryGetValue("limit", out var requested) &&
                (!int.TryParse(requested, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > options.MaximumSubscriptions))
            {
                throw new ArgumentException("Invalid page limit.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var scope = await resolver.ResolveAsync(context.User, deadline.Token).ConfigureAwait(false);
            ValidateScope(scope, options);
            var overview = await scope.LiveQueries.GetLiveOverviewAsync(deadline.Token).ConfigureAwait(false);
            // The host must configure a bounded Control Plane registry. Reject an oversized
            // provider result instead of silently taking an unbounded operation-wide snapshot.
            if (overview.Subscriptions.Count > options.MaximumAuthorizedSubscriptions) { throw new StudioReplyLimitException(); }
            var permitted = new List<ControlPlaneLiveSubscriptionSnapshot>();
            foreach (var subscription in overview.Subscriptions)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!Fingerprint(subscription.SubscriptionFingerprint)) { throw new InvalidOperationException("Invalid subscription identity."); }
                if (scope.SubscriptionFingerprints.Contains(subscription.SubscriptionFingerprint) &&
                    string.CompareOrdinal(subscription.SubscriptionFingerprint, after) > 0)
                {
                    permitted.Add(subscription);
                }
            }
            permitted.Sort((left, right) => string.CompareOrdinal(left.SubscriptionFingerprint, right.SubscriptionFingerprint));
            var count = Math.Min(permitted.Count, limit);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("observedAt", overview.ObservedAt);
                writer.WriteStartArray("subscriptions");
                for (var index = 0; index < count; index++)
                {
                    var value = permitted[index];
                    writer.WriteStartObject();
                    writer.WriteString("fingerprint", value.SubscriptionFingerprint);
                    writer.WriteBoolean("started", value.IsStarted);
                    writer.WriteNumber("subscribers", value.SubscriberCount);
                    writer.WriteNumber("resultCount", value.ResultCount);
                    // All Int64 metrics use decimal strings so JavaScript never rounds them.
                    Integer(writer, "persistedSequence", value.PersistedSequence);
                    Integer(writer, "publishedEvents", value.PublishedEvents);
                    Integer(writer, "replayedEvents", value.ReplayedEvents);
                    Integer(writer, "resumeRejections", value.ResumeRejections);
                    Integer(writer, "slowClientDisconnects", value.SlowClientDisconnects);
                    Integer(writer, "authoritativeQueries", value.AuthoritativeQueryCount);
                    Integer(writer, "coalescedInvalidations", value.CoalescedInvalidationCount);
                    if (value.InvalidationLag is { } lag) { Integer(writer, "invalidationLag", lag); }
                    else { writer.WriteNull("invalidationLag"); }
                    writer.WriteEndObject();
                    Check(writer, options.MaximumReplyBytes);
                }
                writer.WriteEndArray();
                if (permitted.Count > count) { writer.WriteString("next", permitted[count - 1].SubscriptionFingerprint); }
                else { writer.WriteNull("next"); }
                writer.WriteStartArray("replayTargets");
                foreach (var alias in scope.ReplayTargets.Keys.Order(StringComparer.Ordinal))
                {
                    writer.WriteStringValue(alias);
                    Check(writer, options.MaximumReplyBytes);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                Check(writer, options.MaximumReplyBytes);
                writer.Flush();
            }
            deadline.Token.ThrowIfCancellationRequested();
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Failure(context, exception); }
        finally { admission.Capacity.Release(); }
    }

    private static async Task ReplayAsync(HttpContext context, IAntiforgery antiforgery,
        IStudioControlPlaneScopeResolver resolver, IStudioAuditSink audit, StudioControlPlaneOptions options,
        StudioOptions studio, StudioControlPlaneAdmission admission)
    {
        try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
        catch (AntiforgeryValidationException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        if (!await admission.Capacity.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var bytes = await ReadAsync(context.Request, Math.Min(studio.MaximumRequestBytes, 16 * 1024), deadline.Token).ConfigureAwait(false);
            var request = JsonSerializer.Deserialize(bytes, StudioControlPlaneJsonContext.Default.StudioReplayRequest) ?? throw new JsonException();
            if (request.OperationId == Guid.Empty || !Alias(request.Target) || string.IsNullOrWhiteSpace(request.Reason) ||
                request.Reason.Length > 2048 || request.Confirmation != "ReplayQuarantine:" + request.Target)
            {
                throw new ArgumentException("Invalid replay request.");
            }
            var scope = await resolver.ResolveAsync(context.User, deadline.Token).ConfigureAwait(false);
            ValidateScope(scope, options);
            if (!scope.ReplayTargets.TryGetValue(request.Target, out var target)) { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("ReplayQuarantine:" + request.Target)));
            var scopeId = "replay:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(target)));
            await audit.RecordAsync(new(request.OperationId, scope.Actor.ActorId, fingerprint, "replay-attempt", 0) { ScopeId = scopeId }, deadline.Token).ConfigureAwait(false);
            try
            {
                // The Control Plane executor separately enforces actor roles, exact target
                // confirmation and durable requested/completion audits before any handler runs.
                await scope.Operations.ExecuteAsync(scope.Actor, new(request.OperationId, ControlPlaneOperationKind.ReplayQuarantine,
                    target, "ReplayQuarantine:" + target, request.Reason), deadline.Token).ConfigureAwait(false);
                await audit.RecordAsync(new(request.OperationId, scope.Actor.ActorId, fingerprint, "replay-completed", 0) { ScopeId = scopeId }, deadline.Token).ConfigureAwait(false);
            }
            catch
            {
                using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
                await audit.RecordAsync(new(request.OperationId, scope.Actor.ActorId, fingerprint, "replay-failed", 0) { ScopeId = scopeId }, completionDeadline.Token).ConfigureAwait(false);
                throw;
            }
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Failure(context, exception); }
        finally { admission.Capacity.Release(); }
    }

    private static void ValidateScope(StudioControlPlaneScope scope, StudioControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(scope.LiveQueries);
        ArgumentNullException.ThrowIfNull(scope.Operations);
        ArgumentNullException.ThrowIfNull(scope.Actor);
        ArgumentNullException.ThrowIfNull(scope.SubscriptionFingerprints);
        ArgumentNullException.ThrowIfNull(scope.ReplayTargets);
        if (scope.SubscriptionFingerprints.Count > options.MaximumAuthorizedSubscriptions || scope.ReplayTargets.Count > 1000)
        {
            throw new StudioReplyLimitException();
        }
        foreach (var identity in scope.SubscriptionFingerprints)
        {
            if (!Fingerprint(identity)) { throw new ArgumentException("Invalid authorized subscription identity."); }
        }
        foreach (var (alias, target) in scope.ReplayTargets)
        {
            if (!Alias(alias) || string.IsNullOrWhiteSpace(target) || target.Length > 1024) { throw new ArgumentException("Invalid replay target."); }
        }
    }

    private static bool Alias(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');
    private static bool Fingerprint(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void Integer(Utf8JsonWriter writer, string name, long value) => writer.WriteString(name, value.ToString(CultureInfo.InvariantCulture));
    private static void Check(Utf8JsonWriter writer, int maximum)
    {
        if (writer.BytesCommitted + writer.BytesPending > maximum) { throw new StudioReplyLimitException(); }
    }
    private static void Failure(HttpContext context, Exception exception) => context.Response.StatusCode = exception switch
    {
        ControlPlaneAuthorizationException or UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        ArgumentException or JsonException or ControlPlaneConfirmationException => StatusCodes.Status400BadRequest,
        StudioReplyLimitException => StatusCodes.Status413PayloadTooLarge,
        OperationCanceledException => StatusCodes.Status408RequestTimeout,
        _ => StatusCodes.Status422UnprocessableEntity,
    };
    private static async Task<byte[]> ReadAsync(HttpRequest request, int maximum, CancellationToken token)
    {
        if (request.ContentLength > maximum) { throw new ArgumentException("Replay request exceeds its byte bound."); }
        using var stream = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (stream.Length + read > maximum) { throw new ArgumentException("Replay request exceeds its byte bound."); }
                stream.Write(buffer, 0, read);
            }
            return stream.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
}

internal sealed class StudioControlPlaneAdmission(StudioControlPlaneOptions options) : IDisposable
{
    internal SemaphoreSlim Capacity { get; } = new(options.MaximumConcurrentOperations);
    public void Dispose() => Capacity.Dispose();
}

[JsonSourceGenerationOptions(MaxDepth = 4, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StudioReplayRequest))]
internal sealed partial class StudioControlPlaneJsonContext : JsonSerializerContext;
