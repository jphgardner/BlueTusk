using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.AspNetCore;
using BlueTusk.Live.ServerSentEvents;
using BlueTusk.Projections;
using BlueTusk.Projections.Live;
using BlueTusk.Projections.Orders.Live.Sample;
using BlueTusk.TypeSystem;

var builder = WebApplication.CreateBuilder(args);
var connectionString = Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_CONNECTION_STRING") ?? throw new InvalidOperationException("BLUETUSK_SAMPLE_CONNECTION_STRING is required.");
var apiKey = Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_API_KEY") ?? throw new InvalidOperationException("BLUETUSK_SAMPLE_API_KEY is required.");
if (Encoding.UTF8.GetByteCount(apiKey) < 32) { throw new InvalidOperationException("The sample API key must contain at least 32 UTF-8 bytes."); }
var tokenSecret = Convert.FromBase64String(Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_RESUME_KEY") ?? throw new InvalidOperationException("BLUETUSK_SAMPLE_RESUME_KEY is required (base64, >=32 random bytes)."));
var recoveryIdSetting = Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_RECOVERY_ID");
var recoveryActiveSetting = Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_RECOVERY_ACTIVE_VERSION");
var recoveryReasonSetting = Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_RECOVERY_REASON");
var options = new SampleOptions(
    Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_TENANT") ?? "first",
    Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_SOURCE_SCHEMA") ?? "orders_sample",
    Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_PROJECTION_SCHEMA") ?? "orders_projection_sample",
    Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_SLOT") ?? "orders_live_sample",
    Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_PUBLICATION") ?? "orders_live_sample",
    int.Parse(Environment.GetEnvironmentVariable("BLUETUSK_SAMPLE_VERSION") ?? "1", System.Globalization.CultureInfo.InvariantCulture),
    recoveryIdSetting is null ? null : Guid.Parse(recoveryIdSetting),
    recoveryActiveSetting is null ? null : int.Parse(recoveryActiveSetting, System.Globalization.CultureInfo.InvariantCulture), recoveryReasonSetting);
if ((options.RecoveryId is not null || recoveryActiveSetting is not null || recoveryReasonSetting is not null) &&
    (options.RecoveryId is null || options.RecoveryId == Guid.Empty || options.RecoveryExpectedActiveVersion is not > 0 || string.IsNullOrWhiteSpace(options.RecoveryReason)))
{ throw new InvalidOperationException("Configured operator recovery requires a stable nonempty id, expected active version and permanent reason."); }
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(BlueTuskDataSource.Create(connectionString));
builder.Services.AddSingleton<SampleState>();
builder.Services.AddSingleton<SampleSubscriptions>();
builder.Services.AddSingleton<ILiveTransportSubscriptionResolver>(static services => services.GetRequiredService<SampleSubscriptions>());
builder.Services.AddHostedService<ProjectionWorker>();
builder.Services.AddHostedService<LiveRefreshWorker>();
builder.Services.AddBlueTuskLiveAspNetCore(new LiveResumeTokenProtector([new("sample-v1", tokenSecret, true)]));
var app = builder.Build();
var keyDigest = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-API-Key"].ToString();
    if (supplied.Length > 1024 || !CryptographicOperations.FixedTimeEquals(keyDigest, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
    {
        context.Response.StatusCode = 401;
        return;
    }
    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant", options.Tenant)], "sample-api-key"));
    try { await next(context); }
    catch (ProjectionLivePublisherFencedException)
    {
        if (context.Response.HasStarted) { context.Abort(); }
        else { context.Response.StatusCode = 503; context.Response.Headers.RetryAfter = "1"; }
    }
});
app.MapBlueTuskLiveServerSentEvents();
app.MapGet("/sample/status", async (SampleState state, CancellationToken token) =>
    await state.Store.ReadPublicationAsync("orders", token));
app.MapGet("/sample/total", async (SampleState state, CancellationToken token) =>
    await state.Store.ReadActiveAggregateAsync("orders", options.Tenant, "all", "total", token));
app.MapPost("/sample/promote", async (PromotionRequest request, SampleState state, CancellationToken token) =>
{
    await state.Ready.Task.WaitAsync(token);
    if (request.AllowEquivalentSourceLineage && request.ExpectedActiveVersion is { } active)
    {
        var evidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(state.DataSource, state.Lease!.Identity.Source, [options.Publication], token);
        var required = evidence.BarrierPosition.Value > request.RequiredPosition ? evidence.BarrierPosition.Value : request.RequiredPosition;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while ((await state.Store.ReadStateAsync(state.Lease.Identity, deadline.Token)).Checkpoint.Value < required)
        {
            await Task.Delay(20, deadline.Token);
        }
        await state.Store.PromoteWithLineageAsync(state.Lease!, new BlueTuskLogSequenceNumber(request.RequiredPosition), active, evidence, token);
    }
    else
    {
        await state.Store.PromoteAsync(state.Lease!, new BlueTuskLogSequenceNumber(request.RequiredPosition), request.ExpectedActiveVersion, token);
    }
    return Results.NoContent();
});
app.MapPost("/sample/barrier", async (SampleState state, CancellationToken token) => (await ProjectionSourceBarrier.EmitAsync(state.DataSource, token)).Value);
app.MapPost("/sample/maintenance", async (MaintenanceRequest request, SampleState state, CancellationToken token) =>
{
    await state.Store.FenceForMaintenanceAsync("orders", request.ExpectedActiveVersion, request.MaintenanceId, request.Reason, token);
    return Results.NoContent();
});
app.MapPost("/sample/recover", async (SampleState state, CancellationToken token) =>
{
    if (options.RecoveryId is not { } id) { return Results.BadRequest("This candidate was not configured for explicit operator recovery."); }
    await state.Ready.Task.WaitAsync(token);
    var evidence = await PostgreSqlProjectionLineage.CaptureForCutoverAsync(state.DataSource, state.Lease!.Identity.Source, [options.Publication], token);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
    while ((await state.Store.ReadStateAsync(state.Lease.Identity, deadline.Token)).Checkpoint < evidence.BarrierPosition) { await Task.Delay(20, deadline.Token); }
    await state.Store.CompleteRecoveryAsync(state.Lease, id, evidence, token);
    return Results.NoContent();
});
await app.RunAsync();

namespace BlueTusk.Projections.Orders.Live.Sample
{
    internal sealed record PromotionRequest(ulong RequiredPosition, int? ExpectedActiveVersion, bool AllowEquivalentSourceLineage = false);
    internal sealed record MaintenanceRequest(int ExpectedActiveVersion, Guid MaintenanceId, string Reason);
    internal sealed record SampleOptions(string Tenant, string SourceSchema, string ProjectionSchema, string Slot, string Publication, int Version,
        Guid? RecoveryId = null, int? RecoveryExpectedActiveVersion = null, string? RecoveryReason = null);
}
