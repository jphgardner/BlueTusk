using System.Text;
using System.Text.Json;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.Grpc.Protocol;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace BlueTusk.Live.Grpc;

[Authorize]
public sealed class BlueTuskLiveGrpcService(
    ILiveTransportSubscriptionResolver resolver,
    LiveResumeTokenProtector tokenProtector,
    LiveAspNetCoreOptions options) : Protocol.BlueTuskLive.BlueTuskLiveBase
{
    public override async Task Subscribe(
        LiveGrpcSubscriptionRequest request,
        IServerStreamWriter<LiveGrpcTransportMessage> responseStream,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        var requestBytes =
            (long)Encoding.UTF8.GetByteCount(request.Query) +
            Encoding.UTF8.GetByteCount(request.ParametersJson) +
            Encoding.UTF8.GetByteCount(request.ResumeToken);
        if (requestBytes > options.MaximumRequestBytes)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "Live parameters exceed the configured request limit."));
        }

        JsonElement parameters;
        try
        {
            using var document = JsonDocument.Parse(request.ParametersJson);
            parameters = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Live parameters are not valid JSON."), exception.Message);
        }

        LiveTransportConnection connection;
        try
        {
            connection = await LiveTransportSession.OpenAsync(
                new LiveSubscriptionRequest
                {
                    Query = request.Query,
                    Parameters = parameters,
                    ResumeToken = string.IsNullOrWhiteSpace(request.ResumeToken) ? null : request.ResumeToken,
                },
                context.GetHttpContext().User,
                resolver,
                tokenProtector,
                options,
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (LiveTransportAuthorizationException exception)
        {
            throw Rpc(StatusCode.Unauthenticated, exception);
        }
        catch (LiveTransportRequestException exception)
        {
            throw Rpc(StatusCode.InvalidArgument, exception);
        }
        catch (LiveTransportConnectException exception)
        {
            throw Rpc(MapStatus(exception.Status), exception);
        }

        await using (connection.ConfigureAwait(false))
        {
            foreach (var replay in connection.Replay)
            {
                await responseStream.WriteAsync(
                    LiveGrpcMessageMapper.Map(replay),
                    context.CancellationToken).ConfigureAwait(false);
            }

            await foreach (var message in connection.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(
                    LiveGrpcMessageMapper.Map(message),
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static StatusCode MapStatus(LiveSubscriptionConnectStatus status) =>
        status switch
        {
            LiveSubscriptionConnectStatus.QuotaExceeded => StatusCode.ResourceExhausted,
            LiveSubscriptionConnectStatus.NotStarted => StatusCode.Unavailable,
            LiveSubscriptionConnectStatus.InvalidResumeToken => StatusCode.InvalidArgument,
            LiveSubscriptionConnectStatus.ResumeTokenExpired or
            LiveSubscriptionConnectStatus.ReplayUnavailable or
            LiveSubscriptionConnectStatus.ReplayLimitExceeded => StatusCode.FailedPrecondition,
            _ => StatusCode.Internal,
        };

    private static RpcException Rpc(StatusCode code, Exception exception) =>
        new(new Status(code, exception.Message));
}

internal static class LiveGrpcMessageMapper
{
    public static LiveGrpcTransportMessage Map(LiveTransportMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new LiveGrpcTransportMessage
        {
            Kind = message.Kind switch
            {
                LiveSubscriberMessageKind.Event => LiveGrpcMessageKind.Event,
                LiveSubscriberMessageKind.ResetRequired => LiveGrpcMessageKind.ResetRequired,
                _ => throw new LiveTransportException(
                    $"Unsupported Live subscriber message kind '{message.Kind}'."),
            },
            Sequence = message.Sequence ?? 0,
            ResumeToken = message.ResumeToken ?? string.Empty,
            EventJson = message.Event?.GetRawText() ?? string.Empty,
        };
    }
}

public static class BlueTuskLiveGrpcEndpointRouteBuilderExtensions
{
    public static GrpcServiceEndpointConventionBuilder MapBlueTuskLiveGrpc(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapGrpcService<BlueTuskLiveGrpcService>();
    }
}
