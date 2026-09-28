using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Events;
using BlueTusk.Studio.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Studio.Tests;

public sealed class StudioEndpointTests
{
    [Fact]
    public async Task Event_trace_reads_only_authorized_stream_metadata_and_resumes_exact_pages()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var schema = "studio_events_" + Guid.NewGuid().ToString("N");
        var store = new PostgreSqlEventStore(dataSource, new() { Schema = schema });
        try
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await using (var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                await using var transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
                _ = await store.AppendAsync(connection, transaction, new("private-tenant-a", "private-orders"),
                    Enumerable.Range(0, 5).Select(_ => new EventWrite(Guid.NewGuid(), "<script>type</script>", 1,
                        DateTimeOffset.UtcNow, "{\"secret-payload\":true}"u8)).ToArray(), TestContext.Current.CancellationToken);
                _ = await store.AppendAsync(connection, transaction, new("private-tenant-b", "private-orders"),
                    [new EventWrite(Guid.NewGuid(), "other", 1, DateTimeOffset.UtcNow, "{}"u8)], TestContext.Current.CancellationToken);
                await transaction.CommitAsync(TestContext.Current.CancellationToken);
            }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(dataSource);
            builder.Services.AddSingleton(store);
            var audit = new FixtureAudit();
            builder.Services.AddSingleton(audit);
            builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
            builder.Services.AddAuthorization(options => options.AddPolicy("studio-read", policy => policy.RequireAuthenticatedUser()));
            builder.Services.AddBlueTuskStudio<FixtureScope, FixtureAuditAdapter>(StudioQueryServiceTests.Options());
            builder.Services.AddBlueTuskStudioEvents<FixtureEventScope>(new() { MaximumEvents = 2 });
            await using var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapBlueTuskStudioEvents();
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new(address + "/bluetusk/studio/events/") };
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("streams", TestContext.Current.CancellationToken)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Fixture-User", "reader");
            using var streams = JsonDocument.Parse(await client.GetStringAsync("streams", TestContext.Current.CancellationToken));
            Assert.Equal("orders", Assert.Single(streams.RootElement.GetProperty("streams").EnumerateArray()).GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("?stream=other", TestContext.Current.CancellationToken)).StatusCode);
            using var first = JsonDocument.Parse(await client.GetStringAsync("?stream=orders", TestContext.Current.CancellationToken));
            Assert.Equal(2, first.RootElement.GetProperty("events").GetArrayLength());
            Assert.Equal("1", first.RootElement.GetProperty("events")[0].GetProperty("sequence").GetString());
            Assert.Equal("<script>type</script>", first.RootElement.GetProperty("events")[0].GetProperty("type").GetString());
            Assert.DoesNotContain("secret-payload", first.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private-tenant", first.RootElement.GetRawText(), StringComparison.Ordinal);
            using var second = JsonDocument.Parse(await client.GetStringAsync("?stream=orders&after=2", TestContext.Current.CancellationToken));
            Assert.Equal("3", second.RootElement.GetProperty("events")[0].GetProperty("sequence").GetString());
            using var third = JsonDocument.Parse(await client.GetStringAsync("?stream=orders&after=4", TestContext.Current.CancellationToken));
            Assert.Single(third.RootElement.GetProperty("events").EnumerateArray());
            Assert.Equal("5", third.RootElement.GetProperty("next").GetString());
            using var end = JsonDocument.Parse(await client.GetStringAsync("?stream=orders&after=5", TestContext.Current.CancellationToken));
            Assert.Equal(JsonValueKind.Null, end.RootElement.GetProperty("next").ValueKind);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("?stream=orders&after=0005", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("?stream=orders&limit=3", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(8, audit.Records.Count);
            Assert.All(audit.Records, record => Assert.Equal(64, record.QueryFingerprint.Length));
        }
        finally
        {
            await using var drop = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
            _ = await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Authorization_antiforgery_assets_and_audit_are_enforced_over_HTTP()
    {
        await using var dataSource = BlueTuskDataSource.Create(StudioQueryServiceTests.ConnectionString());
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(dataSource);
        var audit = new FixtureAudit();
        builder.Services.AddSingleton(audit);
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("fixture", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("studio-read", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("studio-query", policy => policy.RequireClaim("query", "allowed"));
        });
        builder.Services.AddBlueTuskStudio<FixtureScope, FixtureAuditAdapter>(StudioQueryServiceTests.Options());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBlueTuskStudio();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(address + "/bluetusk/studio/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("", TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Fixture-User", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("query", new { Sql = "SELECT 1" }, TestContext.Current.CancellationToken)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Fixture-User");
        client.DefaultRequestHeaders.Add("X-Fixture-User", "operator");
        var page = await client.GetAsync("", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("BlueTusk", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.True(page.Headers.Contains("Content-Security-Policy"));
        var script = await client.GetAsync("assets/studio.js", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.DoesNotContain("innerHTML", await script.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("query", new { Sql = "SELECT 1" }, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(audit.Records);
        using var session = JsonDocument.Parse(await client.GetStringAsync("session", TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.Add(session.RootElement.GetProperty("header").GetString()!, session.RootElement.GetProperty("token").GetString());
        var query = await client.PostAsJsonAsync("query", new { Sql = "SELECT '<script>unsafe</script>'::text AS value" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        using var result = JsonDocument.Parse(await query.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("<script>unsafe</script>", result.RootElement.GetProperty("rows")[0][0].GetString());
        Assert.Equal(2, audit.Records.Count);
        Assert.Equal("attempt", audit.Records[0].Outcome);
        Assert.Equal("completed", audit.Records[1].Outcome);
        Assert.All(audit.Records, record => Assert.Equal(64, record.QueryFingerprint.Length));
        var invalid = await client.PostAsJsonAsync("query", new { Sql = "SELECT 1; COMMIT; DELETE FROM forbidden" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.DoesNotContain("forbidden", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FixtureScope(BlueTuskDataSource dataSource) : IStudioScopeResolver
    {
        public ValueTask<StudioDatabaseScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StudioDatabaseScope(dataSource, ["public"]));
    }

    private sealed class FixtureEventScope(PostgreSqlEventStore store) : IStudioEventScopeResolver
    {
        public ValueTask<StudioEventScope> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new StudioEventScope(store, new Dictionary<string, EventStreamKey>(StringComparer.Ordinal) { ["orders"] = new("private-tenant-a", "private-orders") }));
    }

    private sealed class FixtureAudit
    {
        public List<StudioAuditRecord> Records { get; } = [];
    }

    private sealed class FixtureAuditAdapter(FixtureAudit audit) : IStudioAuditSink
    {
        public ValueTask RecordAsync(StudioAuditRecord record, CancellationToken cancellationToken = default)
        {
            audit.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Fixture-User"].ToString();
            if (user is not ("reader" or "operator")) { return Task.FromResult(AuthenticateResult.NoResult()); }
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "fixture-user") };
            if (user == "operator") { claims.Add(new("query", "allowed")); }
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, "fixture")), "fixture")));
        }
    }
}
