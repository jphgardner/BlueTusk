using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.ControlPlane;
using BlueTusk.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

const string readPolicy = "DashboardPreview.Read";
const string mutationPolicy = "DashboardPreview.Mutate";
const string graphExecutionPolicy = "DashboardPreview.GraphExecute";

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
    options.AddPolicy(graphExecutionPolicy, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole("DashboardPreviewViewer"));
});

builder.Services.AddSingleton<DashboardPreviewQueries>();
builder.Services.AddSingleton<IControlPlaneQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneSyncQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneLiveQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
    services => services.GetRequiredService<DashboardPreviewQueries>());

var graphConnectionString = builder.Configuration["BlueTusk:Dashboard:GraphConnectionString"] ??
    Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_CONNECTION_STRING");
var graphRequired = builder.Configuration.GetValue<bool>("BlueTusk:Dashboard:GraphRequired") ||
    string.Equals(
        Environment.GetEnvironmentVariable("BLUETUSK_GRAPH_REQUIRED"),
        "true",
        StringComparison.OrdinalIgnoreCase);
var graphRuntime = await PostgreSqlDashboardGraphRuntime.CreateAsync(
    graphConnectionString,
    graphRequired);
builder.Services.AddSingleton(graphRuntime.QueryRegistry);
builder.Services.AddSingleton(graphRuntime.ExecutionRegistry);
builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(services =>
    new ExecutableContinuousGraphControlPlaneQueryService(
        services.GetRequiredService<BlueTusk.ContinuousGraph.ContinuousGraphQueryRegistry>(),
        services.GetRequiredService<ContinuousGraphControlPlaneExecutionRegistry>()));
builder.Services.AddSingleton<IControlPlaneContinuousGraphExecutionService>(services =>
    new HostedContinuousGraphControlPlaneExecutionService(
        services.GetRequiredService<ContinuousGraphControlPlaneExecutionRegistry>(),
        new ContinuousGraphControlPlaneExecutionOptions
        {
            ExecutionTimeout = TimeSpan.FromSeconds(10),
            MaximumConcurrentExecutions = 4,
            MaximumNodes = 1_000,
            MaximumEdges = 2_000,
        }));
builder.Services.AddSingleton(
    new ControlPlaneOperationExecutor(
        new DenyAllControlPlaneAuthorizer(),
        new PreviewDeniedAuditStore(),
        new DisabledOperationHandler()));

var app = builder.Build();
app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.IsHttps)
    {
        context.Response.Headers.StrictTransportSecurity =
            "max-age=63072000; includeSubDomains";
    }

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

app.MapGet("/", () => Results.Redirect("/bluetusk/overview"));
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/preview", () => Results.Ok(new
{
    release = "1.2.0-rc.1",
    mode = "read-only public preview",
    mutationsEnabled = false,
    graph = new
    {
        graphRuntime.Mode,
        graphRuntime.DatabaseIdentity,
        graphRuntime.DataClassification,
        graphRuntime.QueryFingerprint,
    },
}));
app.MapBlueTuskDashboard(options =>
{
    options.ReadAuthorizationPolicy = readPolicy;
    options.MutationAuthorizationPolicy = mutationPolicy;
    options.GraphExecutionAuthorizationPolicy = graphExecutionPolicy;
    options.ViewerRole = "DashboardPreviewViewer";
    options.OperatorRole = "DashboardPreviewOperatorDisabled";
    options.AdministratorRole = "DashboardPreviewAdministratorDisabled";
    options.GraphExecutorRole = "DashboardPreviewViewer";
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
    IControlPlaneFleetQueryService
{
    private const string SourceFingerprint =
        "7a886a68eb289b44bd63f100bb56f2a3ffbff2f393b819b4c806bd2485a1da11";
    private const string PaymentsFingerprint =
        "28cb9c981247f9128d06840948593592e661d595dd270341b195dc702540042e";
    private const string AnalyticsFingerprint =
        "f5af2dd84266f0fcae48e15021a6bf891bdb6bb56ab4311ec7b77cfe73fc5343";

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
                    94)]),
             new ControlPlaneSourceSnapshot(
                 "production-eu/payments",
                 "payments-primary",
                 PaymentsFingerprint,
                 "7584930762130849662",
                 "payments",
                 "bluetusk_payments_relay",
                 "56f47fb80e4d",
                 6,
                 8_772_120,
                 "2A1/7B1139C0",
                 new ControlPlaneSlotSnapshot(
                     SourceReachable: true,
                     Exists: true,
                     Active: true,
                     OutputPlugin: "pgoutput",
                     RestartPosition: "2A1/7A91A020",
                     ConfirmedFlushPosition: "2A1/7B1139C0",
                     WalStatus: "reserved",
                     WalLagBytes: 8_388_608,
                     DiagnosticCode: "consumer-catching-up"),
                 new ControlPlaneRelaySnapshot(
                     TransactionCount: 8_772_120,
                     StorageBytes: 392_167_424,
                     FirstSequence: 8_751_882,
                     LastSequence: 8_772_120,
                     MinimumCheckpointSequence: 8_768_420,
                     OldestUnacknowledgedAge: TimeSpan.FromMinutes(4.2)),
                 [new ControlPlaneConsumerGroupSnapshot(
                     "payment-search",
                     8_751_882,
                     8_768_420,
                     4,
                     true,
                     true,
                     observedAt.AddSeconds(18),
                     38,
                     null,
                     null),
                  new ControlPlaneConsumerGroupSnapshot(
                     "payment-audit",
                     8_751_882,
                     8_771_998,
                     7,
                     true,
                     true,
                     observedAt.AddSeconds(24),
                     71,
                     null,
                     null)],
                 [new ControlPlaneSnapshotRunSnapshot(
                     "f70df6a8-1c4f-4cc8-8bf7-0d32c24f3a8d",
                     "Copying",
                     48_229_632,
                     observedAt.AddSeconds(-12))],
                 [new ControlPlaneCheckpointSnapshot(
                     "payment-search",
                     2,
                     "bluetusk_payments_relay",
                     "pgoutput",
                     "39d92068c4bb",
                     "2A1/7AF11210",
                     4,
                     true,
                     observedAt.AddSeconds(18),
                     38),
                  new ControlPlaneCheckpointSnapshot(
                     "payment-audit",
                     2,
                     "bluetusk_payments_relay",
                     "pgoutput",
                     "d54ff7849a1e",
                     "2A1/7B111CC0",
                     7,
                     true,
                     observedAt.AddSeconds(24),
                     71)]),
             new ControlPlaneSourceSnapshot(
                 "analytics-dr/events",
                 "analytics-dr",
                 AnalyticsFingerprint,
                 "7584930762130849701",
                 "analytics",
                 "bluetusk_analytics_relay",
                 "af69ed47dd12",
                 3,
                 1_320_514,
                 "81/04A12280",
                 new ControlPlaneSlotSnapshot(
                     SourceReachable: true,
                     Exists: true,
                     Active: false,
                     OutputPlugin: "pgoutput",
                     RestartPosition: "81/04A12280",
                     ConfirmedFlushPosition: "81/04A12280",
                     WalStatus: "reserved",
                     WalLagBytes: 1_048_576,
                     DiagnosticCode: "standby-slot-inactive"),
                 new ControlPlaneRelaySnapshot(
                     TransactionCount: 1_320_514,
                     StorageBytes: 96_468_992,
                     FirstSequence: 1_310_002,
                     LastSequence: 1_320_514,
                     MinimumCheckpointSequence: 1_320_514,
                     OldestUnacknowledgedAge: TimeSpan.Zero),
                 [],
                 [new ControlPlaneSnapshotRunSnapshot(
                     "278f26a6-c32d-4e55-a638-54df90555130",
                     "Complete",
                     1_024,
                     observedAt.AddDays(-2))],
                 [])]));
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
                 null),
             new ControlPlaneSyncPipelineSnapshot(
                 "payments-to-opensearch",
                 PaymentsFingerprint,
                 "Throttled",
                 observedAt.AddMinutes(-11),
                 8_768_420,
                 428.7,
                 12,
                 741_882,
                 1,
                 2,
                 36,
                 TimeSpan.FromMilliseconds(420),
                 "2A1/7AF11210",
                 8_388_608,
                 null,
                 null,
                 true,
                 "destination-backpressure") ]));
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
                 342),
             new ControlPlaneLiveSubscriptionSnapshot(
                 "1c47e2c81e4cbd20562fd57aaac9a26c579704f8335346f6739477d2a7d83a5c",
                 "19815ed476172b1f68e342c35b7ea2f3397369c8839671406117481c13f52c6c",
                 "62478ca67af73d163347f6b41e387f91fcb22d6a36f64f67ac241aa47193854e",
                 "tenant:pilot#2d17a2b3",
                 "inventory-live:v1",
                 250,
                 true,
                 18,
                 17.7,
                 1_000,
                 17_700,
                 1_000,
                 1_048_576,
                 6,
                 22,
                 18,
                 3,
                 0,
                 0,
                 0,
                 0,
                 null,
                 8_772_120,
                 8_772_120,
                 0,
                 null,
                 445,
                 555,
                 128),
             new ControlPlaneLiveSubscriptionSnapshot(
                 "3925a07a14edc04a34894841dc046919ece2fd1cb11abf0a4c34e29ba7dc8a04",
                 "b36c727ff6b8cd5e2232b075fda08506f69c2d85bd356a5c4af96cc949fc2ed5",
                 "f45aa29872da2c7899f7ec4f40b00ce548b00ce0171b0bd45b87156c305ac4c1",
                 "tenant:risk#915ffd12",
                 "risk-live:v2",
                 100,
                 false,
                 0,
                 0,
                 4_820,
                 61_442,
                 4_820,
                 4_194_304,
                 44,
                 31,
                 0,
                 7,
                 1,
                 0,
                 0,
                 1,
                 "maintenance-drain",
                 18_424_896,
                 18_424_901,
                 5,
                 "maintenance-paused",
                 781,
                 4_039,
                 100)]));
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
                 DateTimeOffset.UtcNow.AddSeconds(-8)),
             new ControlPlaneManagedDeploymentSnapshot(
                 "payments-production-eu",
                 "pilot",
                 "kubernetes",
                 "lon1",
                 18,
                 17,
                 57,
                 ManagedDeploymentState.Applying,
                 false,
                 true,
                 4,
                 [ManagedWorkloadKind.Streams,
                  ManagedWorkloadKind.Sync,
                  ManagedWorkloadKind.ControlPlane,
                  ManagedWorkloadKind.Dashboard],
                 7,
                 3_600,
                 8L * 1024 * 1024 * 1024,
                 80L * 1024 * 1024 * 1024,
                 null,
                 DateTimeOffset.UtcNow.AddSeconds(-22)),
             new ControlPlaneManagedDeploymentSnapshot(
                 "analytics-dr",
                 "pilot",
                 "kubernetes",
                 "ams3",
                 9,
                 9,
                 31,
                 ManagedDeploymentState.Degraded,
                 false,
                 true,
                 3,
                 [ManagedWorkloadKind.Streams,
                  ManagedWorkloadKind.Sync,
                  ManagedWorkloadKind.ControlPlane],
                 4,
                 1_800,
                 4L * 1024 * 1024 * 1024,
                 48L * 1024 * 1024 * 1024,
                 "persistent-volume-pressure",
                 DateTimeOffset.UtcNow.AddMinutes(-3))]));
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
