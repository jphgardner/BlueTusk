using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.ControlPlane;
using BlueTusk.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

const string readPolicy = "DashboardPreview.Read";
const string mutationPolicy = "DashboardPreview.Mutate";

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedHost |
        ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
});
builder.Services
    .AddAuthentication(DashboardPreviewAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DashboardPreviewAuthenticationHandler>(
        DashboardPreviewAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(readPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("DashboardPreviewViewer"));
    options.AddPolicy(mutationPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("DashboardPreviewOperatorDisabled"));
});

builder.Services.AddSingleton<DashboardPreviewQueries>();
builder.Services.AddSingleton<IControlPlaneQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneSyncQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneLiveQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton(
    new ControlPlaneOperationExecutor(
        new DenyAllControlPlaneAuthorizer(),
        new PreviewDeniedAuditStore(),
        new DisabledOperationHandler()));

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    await next().ConfigureAwait(false);
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/bluetusk/sources"));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/preview", () => Results.Ok(new
{
    release = "1.2.0-rc.1",
    mode = "read-only representative data",
    mutationsEnabled = false,
}));
app.MapBlueTuskDashboard(options =>
{
    options.ReadAuthorizationPolicy = readPolicy;
    options.MutationAuthorizationPolicy = mutationPolicy;
    options.ViewerRole = "DashboardPreviewViewer";
    options.OperatorRole = "DashboardPreviewOperatorDisabled";
    options.AdministratorRole = "DashboardPreviewAdministratorDisabled";
});

await app.RunAsync();

public partial class Program;

internal sealed class DashboardPreviewAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "DashboardPreview";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "public-dashboard-preview"),
             new Claim(ClaimTypes.Role, "DashboardPreviewViewer")],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName)));
    }
}

internal sealed class DashboardPreviewQueries :
    IControlPlaneQueryService,
    IControlPlaneSyncQueryService,
    IControlPlaneLiveQueryService,
    IControlPlaneContinuousGraphQueryService,
    IControlPlaneFleetQueryService
{
    private const string SourceFingerprint =
        "7a886a68eb289b44bd63f100bb56f2a3ffbff2f393b819b4c806bd2485a1da11";

    public ValueTask<ControlPlaneOverview> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new ControlPlaneOverview(
            observedAt,
            [new ControlPlaneSourceSnapshot(
                "production-eu/orders",
                "orders-primary",
                SourceFingerprint,
                "7584930762130849661",
                "orders",
                "bluetusk_orders_relay",
                "e36d8f7bf1c2",
                12,
                18_424_901,
                "4D2/9A7829F8",
                new ControlPlaneSlotSnapshot(
                    SourceReachable: true,
                    Exists: true,
                    Active: true,
                    OutputPlugin: "pgoutput",
                    RestartPosition: "4D2/9A76F440",
                    ConfirmedFlushPosition: "4D2/9A7829F8",
                    WalStatus: "reserved",
                    WalLagBytes: 13_752,
                    DiagnosticCode: null),
                new ControlPlaneRelaySnapshot(
                    TransactionCount: 18_424_901,
                    StorageBytes: 684_195_840,
                    FirstSequence: 18_401_220,
                    LastSequence: 18_424_901,
                    MinimumCheckpointSequence: 18_424_740,
                    OldestUnacknowledgedAge: TimeSpan.FromSeconds(2.8)),
                [new ControlPlaneConsumerGroupSnapshot(
                    "order-projections",
                    18_401_220,
                    18_424_740,
                    8,
                    true,
                    true,
                    observedAt.AddSeconds(20),
                    94,
                    null,
                    null)],
                [new ControlPlaneSnapshotRunSnapshot(
                    "b40b135a-8c0e-48ab-bced-a70763180f75",
                    "Complete",
                    2_048,
                    observedAt.AddHours(-7))],
                [new ControlPlaneCheckpointSnapshot(
                    "order-projections",
                    2,
                    "bluetusk_orders_relay",
                    "pgoutput",
                    "8155ed54e3de",
                    "4D2/9A77F610",
                    8,
                    true,
                    observedAt.AddSeconds(20),
                    94)])]));
    }

    public ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(new ControlPlaneSyncOverview(
            observedAt,
            [new ControlPlaneSyncPipelineSnapshot(
                "orders-to-kafka",
                SourceFingerprint,
                "Running",
                observedAt.AddHours(-19),
                18_424_740,
                2_842.6,
                41,
                2_615_304,
                0,
                0,
                7,
                TimeSpan.FromMilliseconds(14),
                "4D2/9A77F610",
                13_752,
                null,
                null,
                true,
                null),
             new ControlPlaneSyncPipelineSnapshot(
                "orders-to-lake",
                SourceFingerprint,
                "Running",
                observedAt.AddHours(-19),
                18_424_702,
                1_936.4,
                41,
                2_615_304,
                0,
                0,
                3,
                TimeSpan.FromMilliseconds(22),
                "4D2/9A77D970",
                20_040,
                null,
                null,
                true,
                null)]));
    }

    public ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneLiveOverview(
            DateTimeOffset.UtcNow,
            new ControlPlaneLiveRegistrySnapshot(3, 1_000, 0),
            [new ControlPlaneLiveSubscriptionSnapshot(
                "8dd40fa0d2dd728114e70d6ff6c606eef07473556d8d4a59e2d998114c14aef0",
                "9eddf5bb389fc4e6ac70ed19f672541fc0d2c299c5a651f30d9dc4006d655cab",
                "2c9a5e650c2d6e3260ea221a6700d4b2f4f5a1bfc050d742b6531460918c9d91",
                "tenant:pilot#2d17a2b3",
                "orders-live:v1",
                1_000,
                true,
                64,
                61.8,
                284_440,
                17_577_340,
                284_440,
                74_158_080,
                421,
                71,
                64,
                18,
                0,
                0,
                0,
                2,
                "slow-client-timeout",
                18_424_899,
                18_424_901,
                2,
                null,
                8_412,
                276_028,
                342)]));
    }

    public ValueTask<ControlPlaneContinuousGraphOverview>
        GetContinuousGraphOverviewAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneContinuousGraphOverview(
            DateTimeOffset.UtcNow,
            [new ControlPlaneContinuousGraphQuerySnapshot(
                "account-risk-top-100",
                "risk-primary",
                "52af299250cff0989985389299dd0f667fd67db598614eba82006735dc6dcd65",
                "fraud_network",
                "risk",
                ["account", "transfer"],
                ["risk.accounts", "risk.transfers"],
                100,
                "TrustedCdcDelta, AuthoritativeScopedDelta, FullRepair") ]));
    }

    public ValueTask<ControlPlaneFleetOverview> GetFleetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ControlPlaneFleetOverview(
            DateTimeOffset.UtcNow,
            [new ControlPlaneManagedDeploymentSnapshot(
                "orders-production-eu",
                "pilot",
                "kubernetes",
                "lon1",
                17,
                17,
                42,
                ManagedDeploymentState.Ready,
                false,
                true,
                6,
                [ManagedWorkloadKind.Streams,
                 ManagedWorkloadKind.Sync,
                 ManagedWorkloadKind.Live,
                 ManagedWorkloadKind.ControlPlane,
                 ManagedWorkloadKind.Dashboard,
                 ManagedWorkloadKind.ContinuousGraph],
                11,
                6_400,
                12L * 1024 * 1024 * 1024,
                120L * 1024 * 1024 * 1024,
                null,
                DateTimeOffset.UtcNow.AddSeconds(-8))]));
    }
}

internal sealed class DenyAllControlPlaneAuthorizer : IControlPlaneAuthorizer
{
    public ValueTask<bool> AuthorizeAsync(
        ControlPlaneActor actor,
        ControlPlaneRole requiredRole,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

internal sealed class PreviewDeniedAuditStore : IControlPlaneAuditStore
{
    public ValueTask AppendAsync(
        ControlPlaneAuditRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class DisabledOperationHandler : IControlPlaneOperationHandler
{
    public ValueTask ExecuteAsync(
        ControlPlaneOperationRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException(new InvalidOperationException(
            "The public Dashboard preview never permits control-plane mutations."));
}
