using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTusk.Testing.OperatorHealth;

internal sealed record HealthDatabase(BlueTuskDataSource Source, string ConnectionString, string SchemaName, string RoleName)
{
    internal string Schema => '"' + SchemaName + '"';
    internal string Role => '"' + RoleName + '"';
    internal static async Task RunAsync(string prefix, Func<HealthDatabase, Task> test)
    {
        var connection = Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("Configure disposable product health PostgreSQL.");
        await using var source = BlueTuskDataSource.Create(connection);
        var id = Guid.NewGuid().ToString("N"); var database = new HealthDatabase(source, connection, prefix + id, "health_reader_" + id);
        try { await test(database); }
        finally { await database.ExecuteAsync($"DROP SCHEMA IF EXISTS {database.Schema} CASCADE; DROP ROLE IF EXISTS {database.Role}"); }
    }
    internal async Task ExecuteAsync(string sql) { await using var command = Source.CreateCommand(sql); _ = await command.ExecuteNonQueryAsync(); }
    internal async Task<object?> ScalarAsync(string sql) { await using var command = Source.CreateCommand(sql); return await command.ExecuteScalarAsync(); }
    internal async Task WithReaderRoleAsync(string grants, Func<DbDataSource, Task> test)
    {
        await ExecuteAsync($"CREATE ROLE {Role} NOLOGIN; GRANT USAGE ON SCHEMA {Schema} TO {Role}; {grants}");
        var settings = new BlueTuskConnectionStringBuilder(ConnectionString) { Pooling = false };
        await using var unpooled = BlueTuskDataSource.Create(settings.ConnectionString);
        await using var roleSource = new ReaderSource(unpooled, Role);
        await test(roleSource);
    }
    internal async Task WithMetadataLockAsync(Func<Task> test)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var hold = connection.CreateCommand())
        { hold.Transaction = transaction; hold.CommandText = $"LOCK TABLE {Schema}.storage_metadata IN ACCESS EXCLUSIVE MODE"; _ = await hold.ExecuteNonQueryAsync(); }
        try { await test(); }
        finally { await transaction.RollbackAsync(); }
    }
    private sealed class ReaderSource(BlueTuskDataSource source, string quotedRole) : DbDataSource
    {
        public override string ConnectionString => source.ConnectionString;
        protected override DbConnection CreateDbConnection() => source.CreateConnection();
        protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await source.OpenConnectionAsync(cancellationToken);
            try { await using var role = connection.CreateCommand(); role.CommandText = "SET ROLE " + quotedRole; _ = await role.ExecuteNonQueryAsync(cancellationToken); return connection; }
            catch { await connection.DisposeAsync(); throw; }
        }
    }
}

internal sealed class OperatorHealthHost(WebApplication application) : IAsyncDisposable
{
    internal Uri Endpoint => new(application.Urls.Single());
    internal static async Task<OperatorHealthHost> StartAsync(Action<IHealthChecksBuilder> register)
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("operator-test").AddScheme<AuthenticationSchemeOptions, OperatorAuthentication>("operator-test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("operators", policy => policy.RequireAuthenticatedUser().RequireRole("operator")));
        register(builder.Services.AddHealthChecks());
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapHealthChecks("/ops/ready", new HealthCheckOptions { ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable } }).RequireAuthorization("operators");
        await app.StartAsync(); return new(app);
    }
    internal async Task AssertRolesAsync()
    {
        using var http = new HttpClient { BaseAddress = Endpoint };
        using (var noAuth = await http.GetAsync("/ops/ready")) { Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "reader-test");
        using (var wrongRole = await http.GetAsync("/ops/ready")) { Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test");
        using var authorized = await http.GetAsync("/ops/ready?tenant=untrusted&index=untrusted");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode); Assert.Equal("Healthy", await authorized.Content.ReadAsStringAsync());
    }
    internal async Task AssertStatusAsync(HttpStatusCode status, string text)
    {
        using var http = new HttpClient { BaseAddress = Endpoint }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "operator-test");
        using var response = await http.GetAsync("/ops/ready"); Assert.Equal(status, response.StatusCode); Assert.Equal(text, await response.Content.ReadAsStringAsync());
    }
    public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }
    private sealed class OperatorAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers.Authorization.ToString() switch { "Bearer operator-test" => "operator", "Bearer reader-test" => "reader", _ => null };
            return Task.FromResult(role is null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], Scheme.Name)), Scheme.Name)));
        }
    }
}

internal sealed class StatusMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<KeyValuePair<string, object?>> _tags = [];
    internal StatusMetrics(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) => { if (instrument.Meter.Name == meterName) { listener.EnableMeasurementEvents(instrument); } };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => { foreach (var tag in tags) { _tags.Add(tag); } });
        _listener.SetMeasurementEventCallback<double>((_, _, tags, _) => { foreach (var tag in tags) { _tags.Add(tag); } });
        _listener.Start();
    }
    internal void AssertOnlyFixedStatus()
    { Assert.NotEmpty(_tags); Assert.All(_tags, static tag => { Assert.Equal("status", tag.Key); Assert.Contains(tag.Value, new object[] { "healthy", "degraded", "unhealthy", "cancelled" }); }); }
    public void Dispose() => _listener.Dispose();
}

internal sealed class LateOpenSource(BlueTuskDataSource source) : DbDataSource
{
    private int _opens;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override string ConnectionString => source.ConnectionString;
    protected override DbConnection CreateDbConnection() => source.CreateConnection();
    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _opens) == 1)
        { _started.TrySetResult(); await _release.Task; return await source.OpenConnectionAsync(CancellationToken.None); }
        return await source.OpenConnectionAsync(cancellationToken);
    }
    internal async Task AssertBoundedAsync(IHealthCheck check, string prefix)
    {
        var first = check.CheckHealthAsync(new HealthCheckContext()); await _started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(prefix + "_health_probe_timeout", (await first.WaitAsync(TimeSpan.FromSeconds(5))).Description);
        for (var i = 0; i < 3; i++) { Assert.Equal(prefix + "_health_probe_saturated", (await check.CheckHealthAsync(new HealthCheckContext())).Description); }
        Assert.Equal(1, Volatile.Read(ref _opens)); _release.TrySetResult();
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(5)); HealthCheckResult result;
        do { await Task.Delay(10, recovery.Token); result = await check.CheckHealthAsync(new HealthCheckContext(), recovery.Token); } while (result.Status is not HealthStatus.Healthy);
        Assert.Equal(2, Volatile.Read(ref _opens));
    }
}
