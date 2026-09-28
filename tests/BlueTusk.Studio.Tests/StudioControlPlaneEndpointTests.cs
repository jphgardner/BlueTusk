using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlueTusk.ControlPlane;
using BlueTusk.Studio.ControlPlane;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Studio.Tests;

public sealed class StudioControlPlaneEndpointTests
{
    private static readonly ControlPlaneAuditStatus[] CompletedAudits = [ControlPlaneAuditStatus.Requested, ControlPlaneAuditStatus.Succeeded];
    private static readonly string[] CompletedStudioAudits = ["replay-attempt", "replay-completed"];
    [Fact]
    public async Task Inspection_is_principal_scoped_paginated_redacted_and_exact_for_Int64()
    {
        await using var host = await Host.StartAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("operations/live", TestContext.Current.CancellationToken)).StatusCode);
        host.User("reader");
        using var first = JsonDocument.Parse(await host.Client.GetStringAsync("operations/live?limit=1", TestContext.Current.CancellationToken));
        Assert.Equal(new string('a', 64), Assert.Single(first.RootElement.GetProperty("subscriptions").EnumerateArray()).GetProperty("fingerprint").GetString());
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), first.RootElement.GetProperty("subscriptions")[0].GetProperty("persistedSequence").GetString());
        Assert.Equal(new string('a', 64), first.RootElement.GetProperty("next").GetString());
        Assert.Equal("orders", Assert.Single(first.RootElement.GetProperty("replayTargets").EnumerateArray()).GetString());
        Assert.DoesNotContain("secret", first.RootElement.GetRawText(), StringComparison.Ordinal);
        using var second = JsonDocument.Parse(await host.Client.GetStringAsync("operations/live?limit=1&after=" + new string('a', 64), TestContext.Current.CancellationToken));
        Assert.Equal(new string('c', 64), Assert.Single(second.RootElement.GetProperty("subscriptions").EnumerateArray()).GetProperty("fingerprint").GetString());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("operations/live?after=private-secret", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("operations/live?limit=1001", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Replay_requires_policy_CSRF_authorized_alias_confirmation_and_durable_audits()
    {
        await using var host = await Host.StartAsync();
        var request = new StudioReplayRequest(Guid.NewGuid(), "orders", "ReplayQuarantine:orders", "Investigate a corrected handler.");
        host.User("reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("operations/replay", request, TestContext.Current.CancellationToken)).StatusCode);
        host.User("operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("operations/replay", request, TestContext.Current.CancellationToken)).StatusCode);
        await host.TokenAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsJsonAsync("operations/replay", request with { Confirmation = "wrong" }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("operations/replay", request with { Target = "other", Confirmation = "ReplayQuarantine:other" }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(host.State.Handled);
        var response = await host.Client.PostAsJsonAsync("operations/replay", request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var executed = Assert.Single(host.State.Handled);
        Assert.Equal("private-secret-source/pipeline", executed.Target);
        Assert.Equal("ReplayQuarantine:" + executed.Target, executed.Confirmation);
        Assert.Equal(request.OperationId, executed.OperationId);
        Assert.Equal(CompletedAudits, host.State.ControlAudit.Select(record => record.Status));
        Assert.Equal(CompletedStudioAudits, host.State.StudioAudit.Select(record => record.Outcome));
        host.State.DenyRole = true;
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.PostAsJsonAsync("operations/replay", request with { OperationId = Guid.NewGuid() }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Single(host.State.Handled);
        host.State.DenyRole = false;
        host.State.FailAudit = true;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await host.Client.PostAsJsonAsync("operations/replay", request with { OperationId = Guid.NewGuid() }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Single(host.State.Handled);
    }

    [Fact]
    public async Task Admission_is_nonqueued_and_oversized_provider_results_fail_without_data()
    {
        await using var host = await Host.StartAsync(new() { ReplayPolicy = "replay", MaximumConcurrentOperations = 1, MaximumSubscriptions = 1, MaximumAuthorizedSubscriptions = 3 });
        host.User("reader");
        host.State.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = host.Client.GetAsync("operations/live", TestContext.Current.CancellationToken);
        await host.State.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.Client.GetAsync("operations/live", TestContext.Current.CancellationToken)).StatusCode);
        host.State.Hold.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        host.State.Oversized = true;
        var oversized = await host.Client.GetAsync("operations/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(string.Empty, await oversized.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class State : IControlPlaneLiveQueryService, IControlPlaneAuditStore, IControlPlaneOperationHandler, IStudioAuditSink
    {
        internal List<ControlPlaneOperationRequest> Handled { get; } = [];
        internal List<ControlPlaneAuditRecord> ControlAudit { get; } = [];
        internal List<StudioAuditRecord> StudioAudit { get; } = [];
        internal bool DenyRole { get; set; }
        internal bool FailAudit { get; set; }
        internal bool Oversized { get; set; }
        internal TaskCompletionSource? Hold { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(CancellationToken cancellationToken = default)
        {
            if (Hold is { } held) { Entered.TrySetResult(); await held.Task.WaitAsync(cancellationToken); }
            return new(DateTimeOffset.UtcNow, new(3, 100, 0), Oversized ? [Subscription('a'), Subscription('b'), Subscription('c'), Subscription('d')] : [Subscription('c'), Subscription('b'), Subscription('a')]);
        }
        private static ControlPlaneLiveSubscriptionSnapshot Subscription(char key) => new(new string(key, 64), new string('d', 64), new string('e', 64),
            "tenant:private-secret", "secret-policy", 10, true, 3, 2.5, 2, 5, long.MaxValue, 2048, 2, 4, 4, 1, 0, 0, 0, 1, "private-secret-diagnostic", 10, 12, 2, null, 3, 2, 5);
        public ValueTask AppendAsync(ControlPlaneAuditRecord record, CancellationToken cancellationToken = default)
        {
            if (FailAudit) { throw new InvalidOperationException("secret-audit-failure"); }
            ControlAudit.Add(record);
            return ValueTask.CompletedTask;
        }
        public ValueTask ExecuteAsync(ControlPlaneOperationRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ControlPlaneAuditStatus.Requested, ControlAudit[^1].Status);
            Handled.Add(request);
            return ValueTask.CompletedTask;
        }
        public ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default)
        {
            StudioAudit.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Resolver(State state) : IStudioControlPlaneScopeResolver
    {
        public ValueTask<StudioControlPlaneScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => ValueTask.FromResult(
            new StudioControlPlaneScope(state, new(new RoleControlPlaneAuthorizer(), state, state),
                new(principal.FindFirstValue(ClaimTypes.NameIdentifier)!, state.DenyRole ? new HashSet<ControlPlaneRole> { ControlPlaneRole.Viewer } : new HashSet<ControlPlaneRole> { ControlPlaneRole.Operator }),
                new HashSet<string>(StringComparer.Ordinal) { new string('a', 64), new string('c', 64) }, new Dictionary<string, string>(StringComparer.Ordinal) { ["orders"] = "private-secret-source/pipeline" }));
    }
    private sealed class DatabaseResolver : IStudioScopeResolver
    {
        public ValueTask<StudioDatabaseScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No database query expected.");
    }
    private sealed class StudioAudit(State state) : IStudioAuditSink
    {
        public ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default) => state.RecordAsync(record, cancellationToken);
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-User"].ToString();
            if (user is not ("reader" or "operator")) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "principal:" + user) };
            if (user == "operator") { claims.Add(new("operator", "true")); }
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
    private sealed class Host(WebApplication app, HttpClient client, State state) : IAsyncDisposable
    {
        internal HttpClient Client { get; } = client;
        internal State State { get; } = state;
        internal static async Task<Host> StartAsync(StudioControlPlaneOptions? options = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var state = new State();
            builder.Services.AddSingleton(state);
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, Authentication>("fixture", _ => { });
            builder.Services.AddAuthorization(policies =>
            {
                policies.AddPolicy("read", policy => policy.RequireAuthenticatedUser());
                policies.AddPolicy("replay", policy => policy.RequireClaim("operator", "true"));
            });
            builder.Services.AddBlueTuskStudio<DatabaseResolver, StudioAudit>(new() { ReadPolicy = "read", QueryPolicy = "replay" });
            builder.Services.AddBlueTuskStudioControlPlane<Resolver>(options ?? new() { ReplayPolicy = "replay", MaximumSubscriptions = 1 });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapBlueTuskStudio();
            app.MapBlueTuskStudioControlPlane();
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new(address + "/bluetusk/studio/") }, state);
        }
        internal void User(string user) { Client.DefaultRequestHeaders.Remove("X-User"); Client.DefaultRequestHeaders.Add("X-User", user); }
        internal async Task TokenAsync()
        {
            using var json = JsonDocument.Parse(await Client.GetStringAsync("session", TestContext.Current.CancellationToken));
            Client.DefaultRequestHeaders.Add(json.RootElement.GetProperty("header").GetString()!, json.RootElement.GetProperty("token").GetString());
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.DisposeAsync(); }
    }
}
