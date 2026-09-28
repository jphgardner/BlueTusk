using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Projections.Live;

namespace BlueTusk.Projections.Orders.Live.Sample;

internal sealed class SampleSubscriptions(SampleState state) : ILiveTransportSubscriptionResolver, IAsyncDisposable
{
    private readonly Dictionary<decimal, ProjectionLiveSubscription<OrderView>> _subscriptions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _ownerId = "sample-node-" + Guid.NewGuid().ToString("N");
    private readonly PostgreSqlProjectionLiveReplayStore _replay = new(state.DataSource, new PostgreSqlProjectionLiveReplayOptions
    {
        Schema = state.Options.ProjectionSchema,
        RetentionWindow = TimeSpan.FromHours(1),
        MaximumEventBytes = 1_048_576
    });

    public async ValueTask<ILiveSharedSubscription> ResolveAsync(string query, JsonElement parameters, ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (query != "orders" || principal.Identity?.IsAuthenticated != true || principal.FindFirstValue("tenant") != state.Options.Tenant)
        {
            throw new LiveTransportAuthorizationException("The principal cannot access this tenant's registered orders query.");
        }
        if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any(static value => value.Name != "minimumAmount"))
        {
            throw new LiveTransportRequestException("Only the registered minimumAmount parameter is accepted.");
        }
        decimal minimum = 0;
        if (parameters.TryGetProperty("minimumAmount", out var amount) && (!amount.TryGetDecimal(out minimum) || minimum is < 0 or > 1_000_000))
        {
            throw new LiveTransportRequestException("minimumAmount must be a decimal between 0 and 1000000.");
        }
        await state.Ready.Task.WaitAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_subscriptions.TryGetValue(minimum, out var existing)) { return existing; }
            if (_subscriptions.Count >= 32) { throw new LiveTransportRequestException("The sample supports at most 32 registered parameter variants."); }
            var plan = new ProjectionLiveQuery<OrderView>(state.Store, "orders", state.Options.Tenant, new("tenant:" + state.Options.Tenant, "sample-policy-v1"),
                "orders", "orders-sample", "window-v1-minimum:" + minimum.ToString(CultureInfo.InvariantCulture), ProjectionJson.Default.OrderView,
                new ProjectionLiveQueryOptions { MaximumDocuments = 100, MaximumPayloadBytes = 1_048_576 }, value => value.Amount >= minimum);
            var metadata = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<LiveResultEvent<ProjectionLiveRow<OrderView>, string>>)
                OrdersLiveJson.Default.GetTypeInfo(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))!;
            await _replay.InitializeAsync(cancellationToken);
            await using var identitySession = plan.CreateSession();
            var publisher = await _replay.AcquireAsync(identitySession.Identity, _ownerId, TimeSpan.FromSeconds(30), cancellationToken)
                ?? throw new ProjectionLivePublisherFencedException();
            var created = new ProjectionLiveSubscription<OrderView>(plan, publisher, metadata, subscriptionOptions: new LiveSharedSubscriptionOptions
            {
                MaximumSubscribers = 1000,
                SubscriberBufferCapacity = 64,
                MaximumReplayEventsPerConnect = 1024
            });
            try { await created.StartAsync(cancellationToken); }
            catch { await created.DisposeAsync(); throw; }
            _subscriptions.Add(minimum, created);
            return created;
        }
        finally { _gate.Release(); }
    }

    internal async Task RefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            foreach (var (parameter, subscription) in _subscriptions.ToArray())
            {
                try { await subscription.RefreshAsync(token); }
                catch (ProjectionLivePublisherFencedException)
                {
                    _subscriptions.Remove(parameter);
                    await subscription.DisposeAsync();
                }
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { foreach (var subscription in _subscriptions.Values) { await subscription.DisposeAsync(); } }
        finally { _gate.Release(); _gate.Dispose(); }
    }
}

internal sealed class LiveRefreshWorker(SampleSubscriptions subscriptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(stoppingToken)) { await subscriptions.RefreshAsync(stoppingToken); }
    }
}

[JsonSerializable(typeof(LiveResultEvent<ProjectionLiveRow<OrderView>, string>))]
internal sealed partial class OrdersLiveJson : JsonSerializerContext;
