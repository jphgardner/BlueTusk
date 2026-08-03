using System.Runtime.CompilerServices;
using BlueTusk.Live.AspNetCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace BlueTusk.Live.SignalR;

[Authorize]
public sealed class BlueTuskLiveHub(
    ILiveTransportSubscriptionResolver resolver,
    LiveResumeTokenProtector tokenProtector,
    LiveAspNetCoreOptions options) : Hub
{
    public async IAsyncEnumerable<LiveTransportMessage> SubscribeAsync(
        LiveSubscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LiveTransportConnection connection;
        try
        {
            connection = await LiveTransportSession.OpenAsync(
                request,
                Context.User ?? new System.Security.Claims.ClaimsPrincipal(),
                resolver,
                tokenProtector,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (LiveTransportException exception)
        {
            throw new HubException(exception.Message);
        }

        await using (connection.ConfigureAwait(false))
        {
            foreach (var replay in connection.Replay)
            {
                yield return replay;
            }

            await foreach (var message in connection.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }
}

public static class BlueTuskLiveSignalREndpointRouteBuilderExtensions
{
    public static HubEndpointConventionBuilder MapBlueTuskLiveHub(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/bluetusk/live")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return endpoints.MapHub<BlueTuskLiveHub>(pattern);
    }
}
