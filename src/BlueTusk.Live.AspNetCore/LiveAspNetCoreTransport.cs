using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BlueTusk.Live.AspNetCore;

public sealed record LiveSubscriptionRequest
{
    public required string Query { get; init; }

    public required JsonElement Parameters { get; init; }

    public string? ResumeToken { get; init; }
}

public interface ILiveTransportSubscriptionResolver
{
    ValueTask<ILiveSharedSubscription> ResolveAsync(
        string query,
        JsonElement parameters,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

public sealed record LiveAspNetCoreOptions
{
    public TimeSpan ResumeTokenLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public long MaximumRequestBytes { get; init; } = 64 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ResumeTokenLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRequestBytes);
    }
}

public sealed class LiveTransportMessage
{
    internal LiveTransportMessage(
        LiveSubscriberMessageKind kind,
        long? sequence,
        string? resumeToken,
        JsonElement? liveEvent)
    {
        Kind = kind;
        Sequence = sequence;
        ResumeToken = resumeToken;
        Event = liveEvent;
    }

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter<LiveSubscriberMessageKind>))]
    public LiveSubscriberMessageKind Kind { get; }

    [JsonPropertyName("sequence")]
    public long? Sequence { get; }

    [JsonPropertyName("resumeToken")]
    public string? ResumeToken { get; }

    [JsonPropertyName("event")]
    public JsonElement? Event { get; }
}

public static class BlueTuskLiveAspNetCoreServiceCollectionExtensions
{
    public static IServiceCollection AddBlueTuskLiveAspNetCore(
        this IServiceCollection services,
        LiveResumeTokenProtector tokenProtector,
        LiveAspNetCoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenProtector);
        options ??= new LiveAspNetCoreOptions();
        options.Validate();
        services.TryAddSingleton(tokenProtector);
        services.TryAddSingleton(options);
        return services;
    }
}

public sealed class LiveTransportConnection : IAsyncDisposable
{
    private readonly LiveSubscriptionConnection _connection;
    private readonly LiveSubscriptionIdentity _identity;
    private readonly LiveResumeTokenProtector _tokenProtector;
    private readonly TimeSpan _tokenLifetime;
    private readonly ReadOnlyCollection<LiveTransportMessage> _replay;

    internal LiveTransportConnection(
        LiveSubscriptionConnection connection,
        LiveSubscriptionIdentity identity,
        LiveResumeTokenProtector tokenProtector,
        TimeSpan tokenLifetime)
    {
        _connection = connection;
        _identity = identity;
        _tokenProtector = tokenProtector;
        _tokenLifetime = tokenLifetime;
        _replay = Array.AsReadOnly(connection.Replay.Select(ToTransportMessage).ToArray());
    }

    public IReadOnlyList<LiveTransportMessage> Replay => _replay;

    public IAsyncEnumerable<LiveTransportMessage> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(cancellationToken);

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private async IAsyncEnumerable<LiveTransportMessage> ReadCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var message in _connection.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message.Kind is LiveSubscriberMessageKind.Event
                ? ToTransportMessage(message.Event!)
                : new LiveTransportMessage(message.Kind, null, null, null);
        }
    }

    private LiveTransportMessage ToTransportMessage(LiveReplayEvent replayEvent)
    {
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(replayEvent.Payload);
            payload = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new LiveTransportException(
                $"Live replay event {replayEvent.Sequence} is not valid JSON.",
                exception);
        }

        var token = _tokenProtector.Protect(_identity, replayEvent.Sequence, _tokenLifetime);
        return new LiveTransportMessage(
            LiveSubscriberMessageKind.Event,
            replayEvent.Sequence,
            token,
            payload);
    }
}

public static class LiveTransportSession
{
    public static async ValueTask<LiveTransportConnection> OpenAsync(
        LiveSubscriptionRequest request,
        ClaimsPrincipal principal,
        ILiveTransportSubscriptionResolver resolver,
        LiveResumeTokenProtector tokenProtector,
        LiveAspNetCoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(tokenProtector);
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new LiveTransportAuthorizationException("An authenticated principal is required for a Live subscription.");
        }

        if (request.Parameters.ValueKind is not JsonValueKind.Object)
        {
            throw new LiveTransportRequestException("Live subscription parameters must be a JSON object.");
        }

        options ??= new LiveAspNetCoreOptions();
        options.Validate();
        var subscription = await resolver.ResolveAsync(
            request.Query,
            request.Parameters,
            principal,
            cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(subscription);
        var result = string.IsNullOrWhiteSpace(request.ResumeToken)
            ? await subscription.ConnectAsync(0, cancellationToken).ConfigureAwait(false)
            : await subscription.ConnectWithTokenAsync(
                request.ResumeToken,
                tokenProtector,
                cancellationToken).ConfigureAwait(false);
        if (result.Status is not LiveSubscriptionConnectStatus.Connected || result.Connection is null)
        {
            throw new LiveTransportConnectException(result.Status, result.TokenStatus);
        }

        return new LiveTransportConnection(
            result.Connection,
            subscription.Identity,
            tokenProtector,
            options.ResumeTokenLifetime);
    }
}

public class LiveTransportException : LiveSubscriptionException
{
    public LiveTransportException(string message)
        : base(message)
    {
    }

    public LiveTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class LiveTransportAuthorizationException : LiveTransportException
{
    public LiveTransportAuthorizationException(string message)
        : base(message)
    {
    }
}

public sealed class LiveTransportRequestException : LiveTransportException
{
    public LiveTransportRequestException(string message)
        : base(message)
    {
    }
}

public sealed class LiveTransportConnectException : LiveTransportException
{
    public LiveTransportConnectException(
        LiveSubscriptionConnectStatus status,
        LiveResumeTokenValidationStatus? tokenStatus = null)
        : base($"Live subscription connection failed with status '{status}'.")
    {
        Status = status;
        TokenStatus = tokenStatus;
    }

    public LiveSubscriptionConnectStatus Status { get; }

    public LiveResumeTokenValidationStatus? TokenStatus { get; }
}
