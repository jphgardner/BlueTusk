using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.ServerSentEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.Live.Tests;

public sealed class LiveAspNetCoreTransportTests
{
    [Fact]
    public async Task Authenticated_transport_emits_json_and_subscription_bound_resume_token()
    {
        var (shared, resolver) = await CreateAsync();
        await using (shared)
        {
            var protector = Protector();
            using var parameters = JsonDocument.Parse("{\"tenant\":\"a\"}");
            var request = new LiveSubscriptionRequest
            {
                Query = "orders",
                Parameters = parameters.RootElement.Clone(),
            };
            await using var connection = await LiveTransportSession.OpenAsync(
                request,
                Principal(),
                resolver,
                protector,
                cancellationToken: TestContext.Current.CancellationToken);
            var initial = Assert.Single(connection.Replay);

            Assert.Equal(1, initial.Sequence);
            Assert.Equal(JsonValueKind.Object, initial.Event!.Value.ValueKind);
            Assert.NotNull(initial.ResumeToken);
            Assert.Equal(
                LiveResumeTokenValidationStatus.Valid,
                protector.Validate(initial.ResumeToken, shared.Identity).Status);
        }
    }

    [Fact]
    public async Task Transport_rejects_anonymous_or_cross_scope_resume_requests()
    {
        var (shared, resolver) = await CreateAsync();
        await using (shared)
        {
            var protector = Protector();
            using var parameters = JsonDocument.Parse("{}");
            var request = new LiveSubscriptionRequest
            {
                Query = "orders",
                Parameters = parameters.RootElement.Clone(),
            };
            await Assert.ThrowsAsync<LiveTransportAuthorizationException>(async () =>
                await LiveTransportSession.OpenAsync(
                    request,
                    new ClaimsPrincipal(new ClaimsIdentity()),
                    resolver,
                    protector,
                    cancellationToken: TestContext.Current.CancellationToken));

            var otherIdentity = new LiveSubscriptionIdentity(
                "database",
                new string('a', 64),
                new string('b', 64),
                "tenant:b",
                "policy:v1",
                10);
            var wrongToken = protector.Protect(otherIdentity, 1, TimeSpan.FromMinutes(5));
            await Assert.ThrowsAsync<LiveTransportConnectException>(async () =>
                await LiveTransportSession.OpenAsync(
                    request with { ResumeToken = wrongToken },
                    Principal(),
                    resolver,
                    protector,
                    cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Sse_writer_emits_sequence_event_and_single_line_json_data()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        using var json = JsonDocument.Parse("{\"kind\":\"initial\"}");
        var (_, resolver) = await CreateAsync();
        var protector = Protector();
        await using var connection = await LiveTransportSession.OpenAsync(
            new LiveSubscriptionRequest
            {
                Query = "orders",
                Parameters = json.RootElement.Clone(),
            },
            Principal(),
            resolver,
            protector,
            cancellationToken: TestContext.Current.CancellationToken);
        var message = Assert.Single(connection.Replay);

        await LiveSseWriter.WriteAsync(context.Response, message, TestContext.Current.CancellationToken);
        var text = Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("id: 1\n", text, StringComparison.Ordinal);
        Assert.Contains("event: change\n", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Event\"", text, StringComparison.Ordinal);
        Assert.Contains("\"sequence\":1", text, StringComparison.Ordinal);
        Assert.Contains("\"resumeToken\":", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AspNetCore_registration_reuses_explicit_transport_configuration()
    {
        var services = new ServiceCollection();
        var protector = Protector();
        var options = new LiveAspNetCoreOptions { MaximumRequestBytes = 1024 };

        services.AddBlueTuskLiveAspNetCore(protector, options);
        using var provider = services.BuildServiceProvider();

        Assert.Same(protector, provider.GetRequiredService<LiveResumeTokenProtector>());
        Assert.Same(options, provider.GetRequiredService<LiveAspNetCoreOptions>());
    }

    private static async ValueTask<(LiveSharedSubscription<Row, int> Shared, Resolver Resolver)> CreateAsync()
    {
        var invalidations = new NoChangesLog();
        var plan = new LiveQueryPlan<Row, int>(
            "orders",
            "database",
            new string('a', 64),
            LiveQueryCapabilities.SingleTable |
                LiveQueryCapabilities.TenantFilter |
                LiveQueryCapabilities.DeterministicOrdering |
                LiveQueryCapabilities.BoundedTake,
            [new LiveTableDependency("sales", "orders")],
            [],
            10,
            (_, _) => ValueTask.FromResult<IReadOnlyList<Row>>([new Row(1, "one")]),
            static row => row.Id);
        var session = new LiveQuerySession<Row, int>(
            plan,
            LiveQueryArguments.Create([], new Dictionary<string, object?>()),
            new LiveSecurityScope("tenant:a", "policy:v1"),
            invalidations);
        var shared = new LiveSharedSubscription<Row, int>(session, new ReplayStore());
        await shared.StartAsync(TestContext.Current.CancellationToken);
        return (shared, new Resolver(shared));
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-a")], "test"));

    private static LiveResumeTokenProtector Protector() =>
        new([new LiveResumeTokenKey("primary", new byte[32], isPrimary: true)]);

    private sealed record Row(int Id, string Value);

    private sealed class Resolver(ILiveSharedSubscription subscription) : ILiveTransportSubscriptionResolver
    {
        public ValueTask<ILiveSharedSubscription> ResolveAsync(
            string query,
            JsonElement parameters,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(subscription);
    }

    private sealed class NoChangesLog : ILiveInvalidationLog
    {
        public ValueTask<LiveInvalidationCursor> GetCurrentCursorAsync(
            string databaseIdentity,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LiveInvalidationCursor(0));

        public ValueTask<bool> HasChangesAsync(
            string databaseIdentity,
            IReadOnlyCollection<LiveTableDependency> dependencies,
            LiveInvalidationCursor afterExclusive,
            LiveInvalidationCursor throughInclusive,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class ReplayStore : ILiveReplayStore
    {
        private readonly List<LiveReplayEvent> _events = [];

        public ValueTask<LiveReplayAppendResult> AppendAsync(
            LiveReplayAppendRequest request,
            CancellationToken cancellationToken = default)
        {
            _events.AddRange(request.Events);
            return ValueTask.FromResult(new LiveReplayAppendResult(
                LiveReplayAppendStatus.Stored,
                _events.Count));
        }

        public ValueTask<LiveReplayReadResult> ReadAsync(
            LiveSubscriptionIdentity identity,
            long afterSequence,
            int maximumEvents,
            CancellationToken cancellationToken = default)
        {
            var available = _events.Where(item => item.Sequence > afterSequence).Take(maximumEvents).ToArray();
            return ValueTask.FromResult(new LiveReplayReadResult(
                available.Length == 0 ? LiveReplayReadStatus.Current : LiveReplayReadStatus.Available,
                1,
                _events.Count,
                available));
        }

        public ValueTask<int> PruneAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
    }
}
