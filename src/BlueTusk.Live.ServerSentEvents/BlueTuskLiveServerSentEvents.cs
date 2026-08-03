using System.Text.Json;
using BlueTusk.Live.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace BlueTusk.Live.ServerSentEvents;

public static class BlueTuskLiveServerSentEventsEndpointRouteBuilderExtensions
{
    public static RouteHandlerBuilder MapBlueTuskLiveServerSentEvents(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/bluetusk/live/sse")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return endpoints.MapPost(pattern, StreamAsync);
    }

    private static async Task StreamAsync(
        HttpContext context,
        ILiveTransportSubscriptionResolver resolver,
        LiveResumeTokenProtector tokenProtector,
        LiveAspNetCoreOptions options)
    {
        if (context.Request.ContentLength > options.MaximumRequestBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var requestSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSize is { IsReadOnly: false })
        {
            requestSize.MaxRequestBodySize = options.MaximumRequestBytes;
        }

        LiveSubscriptionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<LiveSubscriptionRequest>(
                cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }
        if (request is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        LiveTransportConnection connection;
        try
        {
            connection = await LiveTransportSession.OpenAsync(
                request,
                context.User,
                resolver,
                tokenProtector,
                options,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (LiveTransportAuthorizationException)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        catch (LiveTransportRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        catch (LiveTransportConnectException exception)
        {
            context.Response.StatusCode = exception.Status switch
            {
                LiveSubscriptionConnectStatus.QuotaExceeded => StatusCodes.Status429TooManyRequests,
                LiveSubscriptionConnectStatus.NotStarted => StatusCodes.Status503ServiceUnavailable,
                LiveSubscriptionConnectStatus.InvalidResumeToken => StatusCodes.Status400BadRequest,
                LiveSubscriptionConnectStatus.ResumeTokenExpired or
                LiveSubscriptionConnectStatus.ReplayUnavailable or
                LiveSubscriptionConnectStatus.ReplayLimitExceeded => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await using (connection.ConfigureAwait(false))
        {
            foreach (var replay in connection.Replay)
            {
                await LiveSseWriter.WriteAsync(context.Response, replay, context.RequestAborted).ConfigureAwait(false);
            }

            await foreach (var message in connection.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await LiveSseWriter.WriteAsync(context.Response, message, context.RequestAborted).ConfigureAwait(false);
            }
        }
    }
}

public static class LiveSseWriter
{
    public static async ValueTask WriteAsync(
        HttpResponse response,
        LiveTransportMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(message);
        var eventName = message.Kind is LiveSubscriberMessageKind.Event ? "change" : "reset";
        if (message.Sequence is { } sequence)
        {
            await response.WriteAsync(
                $"id: {sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n",
                cancellationToken).ConfigureAwait(false);
        }

        await response.WriteAsync($"event: {eventName}\n", cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(message);
        await response.WriteAsync($"data: {payload}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
