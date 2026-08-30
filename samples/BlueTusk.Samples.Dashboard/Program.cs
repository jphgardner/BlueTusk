using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTusk.ControlPlane;
using BlueTusk.Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

if (args.Contains("--collect-topology", StringComparer.Ordinal))
{
    await KubernetesTopologyCollectorHost.RunAsync(args);
    return;
}

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

builder.Services.AddSingleton<DashboardPublicPreviewQueries>();
builder.Services.AddSingleton<IControlPlaneQueryService>(
    services => services.GetRequiredService<DashboardPublicPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneSyncQueryService>(
    services => services.GetRequiredService<DashboardPublicPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneLiveQueryService>(
    services => services.GetRequiredService<DashboardPublicPreviewQueries>());
builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
    services => services.GetRequiredService<DashboardPublicPreviewQueries>());

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
    options.BrandLabel = "Public preview";
    options.DataProvenanceNotice =
        "Live data boundary: Continuous Graph executes the Kubernetes topology in PostgreSQL. " +
        "Streams, Sync, Live, and managed-deployment telemetry are not connected to this public environment, " +
        "so those inventories are intentionally empty.";
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
