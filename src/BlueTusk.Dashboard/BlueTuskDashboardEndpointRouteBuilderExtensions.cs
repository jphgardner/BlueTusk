using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization.Metadata;
using BlueTusk.ControlPlane;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace BlueTusk.Dashboard;

public static partial class BlueTuskDashboardEndpointRouteBuilderExtensions
{
    private const string OperationIdHeader = "X-BlueTusk-Operation-Id";
    private const long MaximumOperationRequestBytes = 16 * 1024;
    private static readonly Action<ILogger, Guid, Exception?> OperationFailed =
        LoggerMessage.Define<Guid>(
            LogLevel.Error,
            new EventId(1, "ControlPlaneOperationFailed"),
            "BlueTusk control-plane operation {OperationId} failed.");
    private static readonly Action<ILogger, Guid, string, Exception?> GraphExecutionFailed =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Error,
            new EventId(2, "ContinuousGraphExecutionFailed"),
            "BlueTusk Continuous Graph execution {ExecutionId} for {QueryFingerprint} failed.");


    public static IEndpointRouteBuilder MapBlueTuskDashboard(
        this IEndpointRouteBuilder endpoints,
        Action<BlueTuskDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new BlueTuskDashboardOptions();
        configure?.Invoke(options);
        options.Validate();

        var group = endpoints.MapGroup(options.RoutePrefix)
            .RequireAuthorization(options.ReadAuthorizationPolicy);
        group.MapGet("/", () => Results.Redirect(options.RoutePrefix + "/overview"));
        group.MapGet(
            "/api/overview",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Json(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false)));
        group.MapGet(
            "/api/sync",
            async (IControlPlaneSyncQueryService queries, CancellationToken cancellationToken) =>
                Json(await queries.GetSyncOverviewAsync(cancellationToken).ConfigureAwait(false)));
        group.MapGet(
            "/api/live",
            async (IControlPlaneLiveQueryService queries, CancellationToken cancellationToken) =>
                Json(await queries.GetLiveOverviewAsync(cancellationToken).ConfigureAwait(false)));
        group.MapGet(
            "/api/graphs",
            async (IControlPlaneContinuousGraphQueryService queries, CancellationToken cancellationToken) =>
                Json(await queries.GetContinuousGraphOverviewAsync(cancellationToken).ConfigureAwait(false)));
        group.MapGet(
            "/api/fleet",
            async (IControlPlaneFleetQueryService queries, CancellationToken cancellationToken) =>
                Json(await queries.GetFleetOverviewAsync(cancellationToken).ConfigureAwait(false)));
        group.MapGet(
            "/api/capabilities",
            () => Json(ControlPlaneApiContract.Capabilities));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/overview",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Json(Versioned(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false))));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/sync",
            async (IControlPlaneSyncQueryService queries, CancellationToken cancellationToken) =>
                Json(Versioned(await queries.GetSyncOverviewAsync(cancellationToken).ConfigureAwait(false))));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/live",
            async (IControlPlaneLiveQueryService queries, CancellationToken cancellationToken) =>
                Json(Versioned(await queries.GetLiveOverviewAsync(cancellationToken).ConfigureAwait(false))));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/graphs",
            async (IControlPlaneContinuousGraphQueryService queries,
                    CancellationToken cancellationToken) =>
                Json(Versioned(
                    await queries.GetContinuousGraphOverviewAsync(cancellationToken)
                        .ConfigureAwait(false))));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/fleet",
            async (IControlPlaneFleetQueryService queries, CancellationToken cancellationToken) =>
                Json(Versioned(
                    await queries.GetFleetOverviewAsync(cancellationToken)
                        .ConfigureAwait(false))));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/fleet/deployments",
            (HttpContext context, IControlPlaneFleetQueryService queries, CancellationToken cancellationToken) =>
                ReadFleetPageAsync(context, queries, options, html: false, cancellationToken));
        group.MapGet(
            ControlPlaneApiContract.VersionedRoutePrefix + "/fleet/deployments/{deploymentId}",
            (string deploymentId, HttpContext context, IControlPlaneFleetQueryService queries, CancellationToken cancellationToken) =>
                ReadDeploymentAsync(deploymentId, context, queries, options, html: false, cancellationToken));
        group.MapPost(
                "/api/graphs/{queryFingerprint}/run",
                (string queryFingerprint,
                        HttpContext context,
                        IControlPlaneContinuousGraphExecutionService executions,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken) =>
                    ExecuteGraphAsync(
                        queryFingerprint,
                        context,
                        executions,
                        loggerFactory.CreateLogger("BlueTusk.Dashboard.ContinuousGraph"),
                        options,
                        versionedResponse: false,
                        cancellationToken))
            .RequireAuthorization(options.GraphExecutionAuthorizationPolicy);
        group.MapPost(
                ControlPlaneApiContract.VersionedRoutePrefix + "/graphs/{queryFingerprint}/run",
                (string queryFingerprint,
                        HttpContext context,
                        IControlPlaneContinuousGraphExecutionService executions,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken) =>
                    ExecuteGraphAsync(
                        queryFingerprint,
                        context,
                        executions,
                        loggerFactory.CreateLogger("BlueTusk.Dashboard.ContinuousGraph"),
                        options,
                        versionedResponse: true,
                        cancellationToken))
            .RequireAuthorization(options.GraphExecutionAuthorizationPolicy);
        group.MapGet(
            "/assets/dashboard.js",
            () => Results.Text(DashboardScript, "application/javascript; charset=utf-8"));
        group.MapGet(
            "/assets/bluetusk-mark.png",
            () => Results.Stream(OpenAsset("bluetusk-mark.png"), "image/png"));
        group.MapPost(
                "/api/operations",
                (HttpContext context,
                        ControlPlaneOperationExecutor executor,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken) =>
                    ExecuteOperationAsync(
                        context,
                        executor,
                        loggerFactory.CreateLogger("BlueTusk.Dashboard.Operations"),
                        options,
                        versionedResponse: false,
                        cancellationToken))
            .RequireAuthorization(options.MutationAuthorizationPolicy);
        group.MapPost(
                ControlPlaneApiContract.VersionedRoutePrefix + "/operations",
                (HttpContext context,
                        ControlPlaneOperationExecutor executor,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken) =>
                    ExecuteOperationAsync(
                        context,
                        executor,
                        loggerFactory.CreateLogger("BlueTusk.Dashboard.Operations"),
                        options,
                        versionedResponse: true,
                        cancellationToken))
            .RequireAuthorization(options.MutationAuthorizationPolicy);
        group.MapGet(
            "/overview",
            async (IControlPlaneQueryService sources,
                    IControlPlaneSyncQueryService sync,
                    IControlPlaneLiveQueryService live,
                    IControlPlaneContinuousGraphQueryService graphs,
                    IControlPlaneFleetQueryService fleet,
                    CancellationToken cancellationToken) =>
                Html(RenderOverview(
                    await sources.GetOverviewAsync(cancellationToken).ConfigureAwait(false),
                    await sync.GetSyncOverviewAsync(cancellationToken).ConfigureAwait(false),
                    await live.GetLiveOverviewAsync(cancellationToken).ConfigureAwait(false),
                    await graphs.GetContinuousGraphOverviewAsync(cancellationToken)
                        .ConfigureAwait(false),
                    await fleet.GetFleetOverviewAsync(cancellationToken).ConfigureAwait(false),
                    options)));
        group.MapGet(
            "/sources",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderSources(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false), options)));
        group.MapGet(
            "/sources/{sourceKey}",
            async (string sourceKey, IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var source = FindSource(overview, sourceKey);
                return source is null
                     ? Results.NotFound()
                     : Html(RenderSource(overview.ObservedAt, source, options));
            });
        group.MapGet(
            "/sources/{sourceKey}/consumer-groups/{groupName}",
            async (string sourceKey,
                    string groupName,
                    IControlPlaneQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var source = FindSource(overview, sourceKey);
                var consumerGroup = source?.ConsumerGroups.FirstOrDefault(
                    candidate => string.Equals(candidate.Name, groupName, StringComparison.Ordinal));
                return source is null || consumerGroup is null
                    ? Results.NotFound()
                    : Html(RenderConsumerGroup(overview.ObservedAt, source, consumerGroup, options));
            });
        group.MapGet(
            "/sources/{sourceKey}/snapshots/{snapshotEpoch}",
            async (string sourceKey,
                    string snapshotEpoch,
                    IControlPlaneQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var source = FindSource(overview, sourceKey);
                var snapshot = source?.SnapshotRuns.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.SnapshotEpoch,
                        snapshotEpoch,
                        StringComparison.Ordinal));
                return source is null || snapshot is null
                    ? Results.NotFound()
                    : Html(RenderSnapshot(overview.ObservedAt, source, snapshot, options));
            });
        group.MapGet(
            "/sources/{sourceKey}/checkpoints/{consumerGroup}",
            async (string sourceKey,
                    string consumerGroup,
                    IControlPlaneQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var source = FindSource(overview, sourceKey);
                var checkpoint = source?.Checkpoints.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.ConsumerGroup,
                        consumerGroup,
                        StringComparison.Ordinal));
                return source is null || checkpoint is null
                    ? Results.NotFound()
                    : Html(RenderCheckpoint(overview.ObservedAt, source, checkpoint, options));
            });
        group.MapGet(
            "/snapshots",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderSnapshots(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false), options)));
        group.MapGet(
            "/consumer-groups",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderGroups(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false), options)));
        group.MapGet(
            "/checkpoints",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderCheckpoints(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false), options)));
        group.MapGet(
            "/pipelines",
            async (HttpContext context,
                    IControlPlaneSyncQueryService queries,
                    CancellationToken cancellationToken) =>
                Html(RenderPipelines(
                    await queries.GetSyncOverviewAsync(cancellationToken).ConfigureAwait(false),
                    options,
                    CanMutate(context.User, options))));
        group.MapGet(
            "/pipelines/{pipelineId}",
            async (string pipelineId,
                    HttpContext context,
                    IControlPlaneSyncQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetSyncOverviewAsync(cancellationToken).ConfigureAwait(false);
                var pipeline = overview.Pipelines.FirstOrDefault(
                    candidate => string.Equals(candidate.PipelineId, pipelineId, StringComparison.Ordinal));
                return pipeline is null
                    ? Results.NotFound()
                    : Html(RenderPipeline(
                        overview.ObservedAt,
                        pipeline,
                        options,
                        CanMutate(context.User, options)));
            });
        group.MapGet(
            "/live",
            async (IControlPlaneLiveQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderLive(
                    await queries.GetLiveOverviewAsync(cancellationToken).ConfigureAwait(false),
                    options)));
        group.MapGet(
            "/live/{subscriptionFingerprint}",
            async (string subscriptionFingerprint,
                    IControlPlaneLiveQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetLiveOverviewAsync(cancellationToken).ConfigureAwait(false);
                var subscription = overview.Subscriptions.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.SubscriptionFingerprint,
                        subscriptionFingerprint,
                        StringComparison.Ordinal));
                return subscription is null
                    ? Results.NotFound()
                    : Html(RenderLiveSubscription(overview.ObservedAt, subscription, options));
            });
        group.MapGet(
            "/graphs",
            async (IControlPlaneContinuousGraphQueryService queries,
                    CancellationToken cancellationToken) =>
                Html(RenderContinuousGraphs(
                    await queries.GetContinuousGraphOverviewAsync(cancellationToken)
                        .ConfigureAwait(false),
                    options)));
        group.MapGet(
            "/graphs/{queryFingerprint}",
            async (string queryFingerprint,
                    HttpContext context,
                    IControlPlaneContinuousGraphQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetContinuousGraphOverviewAsync(cancellationToken)
                    .ConfigureAwait(false);
                var query = overview.Queries.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.QueryFingerprint,
                        queryFingerprint,
                        StringComparison.Ordinal));
                return query is null
                    ? Results.NotFound()
                    : Html(RenderContinuousGraphQuery(
                        overview.ObservedAt,
                        query,
                        options,
                        CanExecuteGraph(context.User, options)));
            });
        group.MapGet(
            "/deployments",
            async (HttpContext context,
                    IControlPlaneFleetQueryService queries,
                    CancellationToken cancellationToken) =>
                await ReadFleetPageAsync(context, queries, options, html: true, cancellationToken).ConfigureAwait(false));
        group.MapGet(
            "/deployments/{**deploymentId}",
            async (string deploymentId,
                    HttpContext context,
                    IControlPlaneFleetQueryService queries,
                    CancellationToken cancellationToken) =>
                await ReadDeploymentAsync(deploymentId, context, queries, options, html: true, cancellationToken).ConfigureAwait(false));
        return endpoints;
    }

    private static async Task<IResult> ExecuteOperationAsync(
        HttpContext context,
        ControlPlaneOperationExecutor executor,
        ILogger logger,
        BlueTuskDashboardOptions options,
        bool versionedResponse,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated is not true)
        {
            return Results.Unauthorized();
        }

        if (!context.Request.HasJsonContentType() ||
            context.Request.ContentLength is not (> 0 and <= MaximumOperationRequestBytes))
        {
            return Results.BadRequest(new { Code = "invalid-operation-body" });
        }

        ControlPlaneOperationRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<ControlPlaneOperationRequest>(
                BlueTuskDashboardJsonContext.Default.ControlPlaneOperationRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or
                                          NotSupportedException or
                                          BadHttpRequestException)
        {
            return Results.BadRequest(new { Code = "invalid-operation-body" });
        }

        if (request is null)
        {
            return Results.BadRequest(new { Code = "invalid-operation-body" });
        }

        if (!context.Request.Headers.TryGetValue(OperationIdHeader, out var values) ||
            values.Count != 1 ||
            !Guid.TryParseExact(values[0], "D", out var headerOperationId) ||
            headerOperationId != request.OperationId)
        {
            return Results.BadRequest(new { Code = "operation-id-header-mismatch" });
        }

        var actorId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
                      context.User.Identity.Name;
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Results.Forbid();
        }

        var roles = new HashSet<ControlPlaneRole>();
        if (context.User.IsInRole(options.ViewerRole))
        {
            roles.Add(ControlPlaneRole.Viewer);
        }

        if (context.User.IsInRole(options.OperatorRole))
        {
            roles.Add(ControlPlaneRole.Operator);
        }

        if (context.User.IsInRole(options.AdministratorRole))
        {
            roles.Add(ControlPlaneRole.Administrator);
        }

        try
        {
            await executor.ExecuteAsync(
                new ControlPlaneActor(actorId, roles),
                request,
                cancellationToken).ConfigureAwait(false);
            var response = new OperationSucceededResponse(request.OperationId, "succeeded");
            return versionedResponse
                ? Json(Versioned(response))
                : Json(response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ControlPlaneAuthorizationException)
        {
            return Results.Problem(
                title: "The operation is not authorized.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = "operation-denied" });
        }
        catch (ControlPlaneConfirmationException)
        {
            return Results.Problem(
                title: "The operation confirmation is invalid.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "confirmation-mismatch" });
        }
        catch (ArgumentException)
        {
            return Results.Problem(
                title: "The operation request is invalid.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid-operation-request" });
        }
        catch (Exception exception)
        {
            OperationFailed(logger, request.OperationId, exception);
            return Results.Problem(
                title: "The operation failed. Use its operation ID to inspect the audit trail.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["code"] = "operation-failed" });
        }
    }

    private static async Task<IResult> ExecuteGraphAsync(
        string queryFingerprint,
        HttpContext context,
        IControlPlaneContinuousGraphExecutionService executions,
        ILogger logger,
        BlueTuskDashboardOptions options,
        bool versionedResponse,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated is not true)
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(queryFingerprint) ||
            !context.Request.HasJsonContentType() ||
            context.Request.ContentLength is not (> 0 and <= MaximumOperationRequestBytes))
        {
            return GraphProblem(
                "invalid-graph-execution-request",
                "The graph execution request is invalid.",
                StatusCodes.Status400BadRequest);
        }

        ControlPlaneContinuousGraphRunRequest? request;
        try
        {
            request = await context.Request
                .ReadFromJsonAsync<ControlPlaneContinuousGraphRunRequest>(
                    BlueTuskDashboardJsonContext.Default.ControlPlaneContinuousGraphRunRequest,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or
                                          NotSupportedException or
                                          BadHttpRequestException)
        {
            return GraphProblem(
                "invalid-graph-execution-request",
                "The graph execution request is invalid.",
                StatusCodes.Status400BadRequest);
        }

        if (request?.Parameters is null || request.Parameters.Count > 64)
        {
            return GraphProblem(
                "invalid-graph-execution-request",
                "The graph execution request is invalid.",
                StatusCodes.Status400BadRequest);
        }

        var actor = CreateActor(context.User, options);
        if (actor is null)
        {
            return Results.Forbid();
        }

        var executionId = Guid.NewGuid();
        try
        {
            var result = await executions.ExecuteAsync(
                    queryFingerprint,
                    actor,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            return versionedResponse ? Json(Versioned(result)) : Json(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ControlPlaneContinuousGraphExecutionException exception)
        {
            var statusCode = exception.Code switch
            {
                "graph-query-not-executable" => StatusCodes.Status404NotFound,
                "graph-parameter-not-editable" or
                "graph-parameter-invalid" => StatusCodes.Status400BadRequest,
                "graph-execution-timeout" => StatusCodes.Status504GatewayTimeout,
                "graph-visualization-limit-exceeded" or
                "graph-result-row-limit-exceeded" => StatusCodes.Status422UnprocessableEntity,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (statusCode >= StatusCodes.Status500InternalServerError)
            {
                GraphExecutionFailed(logger, executionId, queryFingerprint, exception);
            }

            return GraphProblem(exception.Code, exception.Message, statusCode, executionId);
        }
        catch (Exception exception)
        {
            GraphExecutionFailed(logger, executionId, queryFingerprint, exception);
            return GraphProblem(
                "graph-execution-failed",
                "The graph query could not be executed. Use the execution ID to inspect server logs.",
                StatusCodes.Status500InternalServerError,
                executionId);
        }
    }

    private static IResult GraphProblem(
        string code,
        string title,
        int statusCode,
        Guid? executionId = null) => Results.Problem(
        title: title,
        statusCode: statusCode,
        extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["executionId"] = executionId,
        });

    private static IResult Html(string content) => Results.Content(
        content,
        "text/html; charset=utf-8",
        Encoding.UTF8,
        StatusCodes.Status200OK);

    private static ControlPlaneApiResponse<T> Versioned<T>(T data) =>
        new(ControlPlaneApiContract.CurrentVersion, data);

    private static IResult Json<T>(T value)
    {
        var typeInfo = BlueTuskDashboardJsonContext.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
            throw new InvalidOperationException(
                $"No source-generated dashboard JSON contract is registered for '{typeof(T)}'.");
        return Results.Json(value, typeInfo);
    }

    internal sealed record OperationSucceededResponse(Guid OperationId, string Status);

    private static string RenderSources(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var healthy = overview.Sources.Count(IsSourceHealthy);
        var refreshFailed = overview.Instances.Any(instance =>
            instance.Availability != ControlPlaneInstanceAvailability.Current);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Sources", null)))
            .Append(PageHeading(
                "Sources & Streams",
                "PostgreSQL capture, relay durability and consumer progress.",
                StatusBadge(
                    refreshFailed ? "Instance refresh needs attention" : overview.Sources.Count == 0
                        ? "Not connected"
                        : healthy == overview.Sources.Count
                            ? "All sources healthy"
                            : $"{overview.Sources.Count - healthy} need attention",
                    !refreshFailed && overview.Sources.Count > 0 && healthy == overview.Sources.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Sources", overview.Sources.Count.ToString(CultureInfo.InvariantCulture), "Configured capture sources"))
            .Append(MetricCard("Active slots", overview.Sources.Count(source => source.Slot.Active).ToString(CultureInfo.InvariantCulture), "Currently streaming"))
            .Append(MetricCard("WAL lag", Bytes(overview.Sources.Sum(source => source.Slot.WalLagBytes)), "Total retained WAL"))
            .Append(MetricCard("Relay storage", Bytes(overview.Sources.Sum(source => source.Relay.StorageBytes)), "Durable transaction history"))
            .Append("</div>")
            .Append(RenderInstanceAvailability(overview));
        if (overview.Sources.Count == 0)
        {
            body.Append(EmptyInventory(
                "No Streams sources are connected",
                "Register a live control-plane source to report capture, relay, consumer, snapshot, and checkpoint state."));
            return Layout("Sources", overview.ObservedAt, body.ToString(), options);
        }

        body.Append("<section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Inventory</p><h2>Capture sources</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search sources</span><input type=\"search\" placeholder=\"Instance, database or slot\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Instance</th><th>Database</th><th>Slot</th>")
            .Append("<th>Health</th><th>WAL lag</th><th>Relay</th><th>Groups</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var source in overview.Sources)
        {
            var isHealthy = IsSourceHealthy(source);
            var status = source.IsStale ? "Stale" : source.Slot.SourceReachable
                ? source.Slot.Exists ? source.Slot.Active ? "Active" : "Inactive" : "Missing"
                : "Unreachable";
            var href = DashboardPath(options, "sources", source.SourceKey);
            body.Append("<tr data-state=\"").Append(isHealthy ? "healthy" : "attention")
                .Append("\"><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(source.InstanceName)).Append("</a><small>").Append(E(source.SourceKey))
                .Append("</small></td><td>").Append(E(source.DatabaseName)).Append("</td><td><code>")
                .Append(E(source.SlotName)).Append("</code></td><td>")
                .Append(StatusBadge(status, isHealthy ? "ok" : source.Slot.SourceReachable ? "warn" : "critical"))
                .Append("</td><td>").Append(Bytes(source.Slot.WalLagBytes)).Append("</td><td>")
                .Append(source.Relay.TransactionCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" tx / ").Append(Bytes(source.Relay.StorageBytes)).Append("</td><td>")
                .Append(source.ConsumerGroups.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href))
                .Append("\" aria-label=\"Open ").Append(E(source.InstanceName)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div></section>");
        return Layout("Sources", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderSource(
        DateTimeOffset observedAt,
        ControlPlaneSourceSnapshot source,
        BlueTuskDashboardOptions options)
    {
        var healthy = IsSourceHealthy(source);
        var status = source.IsStale ? "Stale" : !source.Slot.SourceReachable
            ? "Unreachable"
            : !source.Slot.Exists ? "Slot missing" : !source.Slot.Active ? "Slot inactive" : "Streaming";
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Sources", DashboardPath(options, "sources")),
                (source.InstanceName, null)))
            .Append(PageHeading(
                source.InstanceName,
                $"{source.DatabaseName} through {source.SlotName}",
                StatusBadge(status, healthy ? "ok" : source.Slot.SourceReachable ? "warn" : "critical")))
            .Append(source.ObservedAt is null ? string.Empty : DetailsPanel(
                "Inventory observation",
                ("Observed at", Date(source.ObservedAt)),
                ("Availability", source.IsStale ? "Stale; showing last successful observation" : "Current")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("WAL lag", Bytes(source.Slot.WalLagBytes), "Retained by PostgreSQL"))
            .Append(MetricCard("Relay storage", Bytes(source.Relay.StorageBytes), $"{source.Relay.TransactionCount:N0} transactions"))
            .Append(MetricCard("Last sequence", source.LastSequence.ToString("N0", CultureInfo.InvariantCulture), "Relay head"))
            .Append(MetricCard("Unacknowledged age", Duration(source.Relay.OldestUnacknowledgedAge), "Oldest retained work"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Source identity",
                ("Source key", Copyable(source.SourceKey)),
                ("Instance", E(source.InstanceName)),
                ("Database", E(source.DatabaseName)),
                ("System identifier", Copyable(source.SystemIdentifier)),
                ("Source fingerprint", Copyable(source.SourceFingerprint)),
                ("Publication fingerprint", Copyable(source.PublicationFingerprint)),
                ("Source epoch", Number(source.SourceEpoch)),
                ("Last sequence", Number(source.LastSequence)),
                ("Last commit position", Copyable(source.LastCommitPosition))))
            .Append(DetailsPanel(
                "Replication slot",
                ("Slot", E(source.SlotName)),
                ("Reachable", YesNo(source.Slot.SourceReachable)),
                ("Exists", YesNo(source.Slot.Exists)),
                ("Active", YesNo(source.Slot.Active)),
                ("Output plugin", E(source.Slot.OutputPlugin ?? "—")),
                ("WAL status", E(source.Slot.WalStatus ?? "—")),
                ("Restart position", source.Slot.RestartPosition is null ? "—" : Copyable(source.Slot.RestartPosition)),
                ("Confirmed flush", source.Slot.ConfirmedFlushPosition is null ? "—" : Copyable(source.Slot.ConfirmedFlushPosition)),
                ("WAL lag", Bytes(source.Slot.WalLagBytes)),
                ("Diagnostic", E(source.Slot.DiagnosticCode ?? "None"))))
            .Append(DetailsPanel(
                "Durable relay",
                ("Transactions", Number(source.Relay.TransactionCount)),
                ("Storage", Bytes(source.Relay.StorageBytes)),
                ("First sequence", Number(source.Relay.FirstSequence)),
                ("Last sequence", Number(source.Relay.LastSequence)),
                ("Minimum checkpoint", Number(source.Relay.MinimumCheckpointSequence)),
                ("Oldest unacknowledged", Duration(source.Relay.OldestUnacknowledgedAge))))
            .Append(RenderGroupTable(source, options))
            .Append(RenderSnapshotTable(source, options))
            .Append(RenderCheckpointTable(source, options));
        return Layout(source.SlotName, observedAt, body.ToString(), options);
    }

    private static string RenderSnapshots(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Snapshots", null)))
            .Append(PageHeading(
                "Snapshots",
                "Initial-copy runs across every configured source.",
                StatusBadge(
                    overview.Sources.SelectMany(static source => source.SnapshotRuns)
                        .All(static snapshot => string.Equals(snapshot.State, "Complete", StringComparison.OrdinalIgnoreCase))
                        ? "All complete" : "Runs in progress",
                    overview.Sources.SelectMany(static source => source.SnapshotRuns)
                        .All(static snapshot => string.Equals(snapshot.State, "Complete", StringComparison.OrdinalIgnoreCase))
                        ? "ok" : "warn")));
        foreach (var source in overview.Sources)
        {
            body.Append(RenderSnapshotTable(source, options));
        }

        if (overview.Sources.Count == 0)
        {
            body.Append(EmptyInventory(
                "No snapshot telemetry is connected",
                "Snapshot runs appear here after a live Streams source is registered."));
        }

        return Layout("Snapshots", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderGroups(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var groups = overview.Sources.SelectMany(static source => source.ConsumerGroups).ToArray();
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Consumer groups", null)))
            .Append(PageHeading(
                "Consumer groups",
                "Durable consumers, leases, checkpoints and ownership fences.",
                StatusBadge($"{groups.Count(static group => group.IsActive)} active", "ok")));
        foreach (var source in overview.Sources)
        {
            body.Append(RenderGroupTable(source, options));
        }

        if (overview.Sources.Count == 0)
        {
            body.Append(EmptyInventory(
                "No consumer-group telemetry is connected",
                "Consumer ownership, leases, and lag appear here after a live Streams source is registered."));
        }

        return Layout("Consumer groups", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderCheckpoints(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var checkpoints = overview.Sources.Sum(static source => source.Checkpoints.Count);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Checkpoints", null)))
            .Append(PageHeading(
                "Direct checkpoints",
                "Exact durable PostgreSQL resume positions for every consumer.",
                StatusBadge($"{checkpoints} checkpoints", "ok")));
        foreach (var source in overview.Sources)
        {
            body.Append(RenderCheckpointTable(source, options));
        }

        if (overview.Sources.Count == 0)
        {
            body.Append(EmptyInventory(
                "No checkpoint telemetry is connected",
                "Durable resume positions appear here after a live Streams source is registered."));
        }

        return Layout("Checkpoints", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderPipelines(
        ControlPlaneSyncOverview overview,
        BlueTuskDashboardOptions options,
        bool canMutate)
    {
        var totalRate = overview.Pipelines
            .Where(static pipeline => pipeline.TransactionsPerSecond.HasValue)
            .Sum(static pipeline => pipeline.TransactionsPerSecond!.Value);
        var healthy = overview.Pipelines.Count(IsPipelineHealthy);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Sync pipelines", null)))
            .Append(PageHeading(
                "Sync pipelines",
                "Destination delivery, throughput, lag and recovery state.",
                StatusBadge(
                    overview.Pipelines.Count == 0
                        ? "Not connected"
                        : healthy == overview.Pipelines.Count
                            ? "All pipelines healthy"
                            : $"{overview.Pipelines.Count - healthy} need attention",
                    overview.Pipelines.Count > 0 && healthy == overview.Pipelines.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Pipelines", overview.Pipelines.Count.ToString(CultureInfo.InvariantCulture), "Configured destinations"))
            .Append(MetricCard("Running", overview.Pipelines.Count(static pipeline => pipeline.State == "Running").ToString(CultureInfo.InvariantCulture), "Workers currently active"))
            .Append(MetricCard("Throughput", totalRate.ToString("N1", CultureInfo.InvariantCulture) + " tx/s", "Combined recent rate"))
            .Append(MetricCard("Quarantined", overview.Pipelines.Sum(static pipeline => pipeline.QuarantinedTransactions).ToString("N0", CultureInfo.InvariantCulture), "Awaiting review"))
            .Append(MetricCard("Failures", overview.Pipelines.Sum(static pipeline => pipeline.FailureCount).ToString("N0", CultureInfo.InvariantCulture), "Recorded failures"))
            .Append("</div>");
        if (overview.Pipelines.Count == 0)
        {
            body.Append(EmptyInventory(
                "No Sync pipelines are connected",
                "Register a hosted pipeline status source to report destination delivery, lag, retry, and quarantine state."));
            return Layout("Sync pipelines", overview.ObservedAt, body.ToString(), options);
        }

        body.Append("<section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Delivery inventory</p><h2>All pipelines</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search pipelines</span><input type=\"search\" placeholder=\"Pipeline, state or diagnostic\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Pipeline</th><th>State</th><th>Throughput</th>")
            .Append("<th>Checkpoint lag</th><th>Applied</th><th>Snapshot rows</th>")
            .Append("<th>Retries</th><th>Quarantine</th><th>Diagnostic</th><th>Controls</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var pipeline in overview.Pipelines)
        {
            var isHealthy = IsPipelineHealthy(pipeline);
            var (status, tone) = PipelineStatus(pipeline);
            var href = DashboardPath(options, "pipelines", pipeline.PipelineId);
            body.Append("<tr data-state=\"").Append(isHealthy ? "healthy" : "attention")
                .Append("\"><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(pipeline.PipelineId)).Append("</a><small>")
                .Append(E(ShortFingerprint(pipeline.SourceFingerprint))).Append("</small></td><td>")
                .Append(StatusBadge(status, tone)).Append("</td><td>")
                .Append(pipeline.TransactionsPerSecond?.ToString("N1", CultureInfo.InvariantCulture) ?? "—")
                .Append(" tx/s")
                .Append("</td><td>")
                .Append(pipeline.CheckpointLagBytes is { } lag ? Bytes(lag) : "—")
                .Append("</td><td>")
                .Append(pipeline.AppliedTransactions.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(pipeline.SnapshotRows.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(pipeline.RetryAttempts.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(pipeline.QuarantinedTransactions.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(E(pipeline.DiagnosticCode ?? pipeline.LagDiagnosticCode ?? "—"))
                .Append("</td><td>")
                .Append(canMutate
                    ? PipelineControls(pipeline.PipelineId, pipeline.QuarantinedTransactions)
                    : "—")
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div></section>");
        return Layout("Sync pipelines", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderLive(
        ControlPlaneLiveOverview overview,
        BlueTuskDashboardOptions options)
    {
        var healthy = overview.Subscriptions.Count(IsLiveHealthy);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Live subscriptions", null)))
            .Append(PageHeading(
                "Live subscriptions",
                "Shared queries, connected clients, fan-out, replay and backpressure.",
                StatusBadge(
                    overview.Subscriptions.Count == 0
                        ? "Not connected"
                        : healthy == overview.Subscriptions.Count
                            ? "All subscriptions current"
                            : $"{overview.Subscriptions.Count - healthy} need attention",
                    overview.Subscriptions.Count > 0 && healthy == overview.Subscriptions.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Shared queries", overview.Registry.SharedSubscriptions.ToString(CultureInfo.InvariantCulture), $"Limit {overview.Registry.MaximumSharedSubscriptions:N0}"))
            .Append(MetricCard("Subscribers", overview.Subscriptions.Sum(static item => item.SubscriberCount).ToString("N0", CultureInfo.InvariantCulture), "Across every query"))
            .Append(MetricCard("Connected clients", overview.Subscriptions.Sum(static item => item.ConnectedClients).ToString("N0", CultureInfo.InvariantCulture), "Current transport connections"))
            .Append(MetricCard("Fan-out deliveries", overview.Subscriptions.Sum(static item => item.FanOutDeliveries).ToString("N0", CultureInfo.InvariantCulture), "Shared result deliveries"))
            .Append(MetricCard("Quota rejections", (overview.Registry.QuotaRejections + overview.Subscriptions.Sum(static item => item.QuotaRejections)).ToString("N0", CultureInfo.InvariantCulture), "Capacity protection"))
            .Append("</div>");
        if (overview.Subscriptions.Count == 0)
        {
            body.Append(EmptyInventory(
                "No Live subscriptions are connected",
                "Register a Live control-plane status source to report clients, fan-out, replay, and backpressure."));
            return Layout("Live subscriptions", overview.ObservedAt, body.ToString(), options);
        }

        body.Append("<section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Query inventory</p><h2>Shared subscriptions</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search subscriptions</span><input type=\"search\" placeholder=\"Query, scope or disconnect code\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Query</th><th>Scope</th><th>Clients</th>")
            .Append("<th>Fan-out</th><th>Invalidation lag</th><th>Replay</th>")
            .Append("<th>Resume rejected</th><th>Disconnect</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var subscription in overview.Subscriptions)
        {
            var isHealthy = IsLiveHealthy(subscription);
            var (status, tone) = LiveStatus(subscription);
            var href = DashboardPath(options, "live", subscription.SubscriptionFingerprint);
            body.Append("<tr data-state=\"").Append(isHealthy ? "healthy" : "attention")
                .Append("\"><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\"><code>")
                .Append(E(ShortFingerprint(subscription.QueryPlanFingerprint)))
                .Append("</code></a><small>").Append(StatusBadge(status, tone)).Append("</small></td><td>")
                .Append(E(subscription.SecurityScopeLabel))
                .Append("</td><td>")
                .Append(subscription.SubscriberCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(subscription.FanOutRatio.ToString("N1", CultureInfo.InvariantCulture))
                .Append("×</td><td>")
                .Append(subscription.InvalidationLag is { } lag
                    ? lag.ToString("N0", CultureInfo.InvariantCulture)
                    : E(subscription.LagDiagnosticCode ?? "—"))
                .Append("</td><td>")
                .Append(Bytes(subscription.ReplayBytesAppended))
                .Append(" / ")
                .Append(subscription.ReplayedEvents.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" events</td><td>")
                .Append(subscription.ResumeRejections.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(E(subscription.LastDisconnectCode ?? "—"))
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div></section>");
        return Layout("Live subscriptions", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderContinuousGraphs(
        ControlPlaneContinuousGraphOverview overview,
        BlueTuskDashboardOptions options)
    {
        var databaseCount = overview.Queries
            .Select(static query => query.DatabaseIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var dependencyCount = overview.Queries
            .SelectMany(static query => query.TableDependencies)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Continuous Graph", null)))
            .Append(PageHeading(
                "Continuous Graph",
                "Registered graph queries and the plans that keep their results current.",
                StatusBadge($"{overview.Queries.Count} queries registered", "ok")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Registered queries", overview.Queries.Count.ToString(CultureInfo.InvariantCulture), "Continuously maintained"))
            .Append(MetricCard("Databases", databaseCount.ToString(CultureInfo.InvariantCulture), "Distinct database identities"))
            .Append(MetricCard("Affected tables", dependencyCount.ToString(CultureInfo.InvariantCulture), "Tracked dependencies"))
            .Append("</div><section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Impact plans</p><h2>Registered queries</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search graph queries</span><input type=\"search\" placeholder=\"Name, graph, database or table\" data-table-filter></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Query</th><th>Graph</th><th>Database</th>")
            .Append("<th>Elements</th><th>Dependencies</th><th>Limit</th><th>Capabilities</th><th><span class=\"sr-only\">Open</span></th>")
            .Append("</tr></thead><tbody>");
        foreach (var query in overview.Queries)
        {
            var qualifiedGraph = string.IsNullOrWhiteSpace(query.GraphSchema)
                ? query.GraphName
                : query.GraphSchema + "." + query.GraphName;
            var href = DashboardPath(options, "graphs", query.QueryFingerprint);
            body.Append("<tr><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(query.Name)).Append("</a>")
                .Append("<br><code>")
                .Append(E(ShortFingerprint(query.QueryFingerprint)))
                .Append("</code></td><td>")
                .Append(E(qualifiedGraph))
                .Append("</td><td>")
                .Append(E(query.DatabaseIdentity))
                .Append("</td><td>")
                .Append(E(string.Join(", ", query.ElementTableAliases)))
                .Append("</td><td>")
                .Append(E(string.Join(", ", query.TableDependencies)))
                .Append("</td><td>")
                .Append(query.MaximumResultCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(E(query.Capabilities))
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div></section>");
        return Layout("Continuous Graph queries", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderGroupTable(
        ControlPlaneSourceSnapshot source,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<section class=\"panel table-panel\"><div class=\"section-heading\"><div><p class=\"eyebrow\">Streams</p><h2>Consumer groups — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2></div><span class=\"muted\">")
            .Append(source.ConsumerGroups.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" groups</span></div><div class=\"table-wrap\"><table><thead><tr><th>Group</th><th>State</th><th>Checkpoint</th>")
            .Append("<th>Behind relay</th><th>Generation</th><th>Lease</th><th>Fence</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var group in source.ConsumerGroups)
        {
            var href = DashboardPath(options, "sources", source.SourceKey, "consumer-groups", group.Name);
            body.Append("<tr><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(group.Name)).Append("</a></td><td>")
                .Append(StatusBadge(group.IsActive ? "Active" : "Removed", group.IsActive ? "ok" : "critical"))
                .Append("</td><td>")
                .Append(group.CheckpointSequence.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(Math.Max(0, source.Relay.LastSequence - group.CheckpointSequence)
                    .ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(group.StoreGeneration.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(group.IsLeased ? "leased" : "free")
                .Append("</td><td>").Append(group.LastFencingToken.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        return body.Append("</tbody></table></div></section>").ToString();
    }

    private static string RenderSnapshotTable(
        ControlPlaneSourceSnapshot source,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<section class=\"panel table-panel\"><div class=\"section-heading\"><div><p class=\"eyebrow\">Initial copy</p><h2>Snapshots — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2></div><span class=\"muted\">")
            .Append(source.SnapshotRuns.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" runs</span></div><div class=\"table-wrap\"><table><thead><tr><th>Epoch</th><th>State</th><th>Progress</th>")
            .Append("<th>Updated</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var snapshot in source.SnapshotRuns)
        {
            var href = DashboardPath(options, "sources", source.SourceKey, "snapshots", snapshot.SnapshotEpoch);
            var complete = string.Equals(snapshot.State, "Complete", StringComparison.OrdinalIgnoreCase);
            body.Append("<tr><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\"><code>")
                .Append(E(snapshot.SnapshotEpoch)).Append("</code></a></td><td>")
                .Append(StatusBadge(snapshot.State, complete ? "ok" : "warn")).Append("</td><td>")
                .Append(Bytes(snapshot.ProgressBytes)).Append("</td><td>")
                .Append(Date(snapshot.UpdatedAt)).Append("</td><td><a class=\"row-action\" href=\"")
                .Append(E(href)).Append("\">View →</a></td></tr>");
        }

        return body.Append("</tbody></table></div></section>").ToString();
    }

    private static string RenderCheckpointTable(
        ControlPlaneSourceSnapshot source,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<section class=\"panel table-panel\"><div class=\"section-heading\"><div><p class=\"eyebrow\">Durable progress</p><h2>Direct checkpoints — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2></div><span class=\"muted\">")
            .Append(source.Checkpoints.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" checkpoints</span></div><div class=\"table-wrap\"><table><thead><tr><th>Group</th><th>Position</th><th>Generation</th>")
            .Append("<th>Mapping</th><th>Lease</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var checkpoint in source.Checkpoints)
        {
            var href = DashboardPath(options, "sources", source.SourceKey, "checkpoints", checkpoint.ConsumerGroup);
            body.Append("<tr><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(checkpoint.ConsumerGroup)).Append("</a></td><td>")
                .Append(E(checkpoint.AcknowledgedPosition)).Append("</td><td>")
                .Append(checkpoint.StoreGeneration.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td><code>").Append(E(ShortFingerprint(checkpoint.MappingFingerprint)))
                .Append("</code></td><td>").Append(checkpoint.IsLeased ? "leased" : "free")
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        return body.Append("</tbody></table></div></section>").ToString();
    }

    private static string Layout(
        string title,
        DateTimeOffset observedAt,
        string body,
        BlueTuskDashboardOptions options) =>
        $$$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="color-scheme" content="dark">
        <meta name="theme-color" content="#15161a">
        <title>{{{E(title)}}} · BlueTusk</title>
        <link rel="icon" href="{{{E(DashboardPath(options, "assets", "bluetusk-mark.png"))}}}" type="image/png">
        <style>
        {{{DashboardStyle}}}
        </style>
        </head>
        <body>
        <a class="skip-link" href="#main-content">Skip to dashboard content</a>
        <div class="app-shell">
          <header class="topbar">
            <a class="brand" href="{{{E(DashboardPath(options, "overview"))}}}"><img class="brand-mark" src="{{{E(DashboardPath(options, "assets", "bluetusk-mark.png"))}}}" width="38" height="38" alt=""><span>BlueTusk<small>{{{E(options.BrandLabel)}}}</small></span></a>
            <div class="topbar-context"><span>OPERATIONS</span><time datetime="{{{E(observedAt.ToString("O", CultureInfo.InvariantCulture))}}}">Observed {{{E(observedAt.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}}}</time></div>
            <button class="nav-toggle secondary" type="button" data-nav-toggle aria-expanded="false" aria-controls="dashboard-navigation">Menu</button>
          </header>
          <div class="app-body">
            <aside class="sidebar" id="dashboard-navigation" data-navigation>
              <div class="nav-section"><span class="nav-label">Workspace</span><nav class="side-nav" aria-label="Dashboard">
                {{{DashboardNavLink(options, "overview", "Overview", "OV")}}}
                {{{DashboardNavLink(options, "deployments", "Deployments", "DP")}}}
              </nav></div>
              <div class="nav-section"><span class="nav-label">Data flow</span><nav class="side-nav" aria-label="Data flow">
                {{{DashboardNavLink(options, "sources", "Sources & Streams", "ST")}}}
                {{{DashboardNavLink(options, "pipelines", "Sync pipelines", "SY")}}}
                {{{DashboardNavLink(options, "live", "Live subscriptions", "LV")}}}
                {{{DashboardNavLink(options, "graphs", "Continuous Graph", "CG")}}}
              </nav></div>
              <div class="nav-section"><span class="nav-label">Recovery</span><nav class="side-nav" aria-label="Recovery">
                {{{DashboardNavLink(options, "snapshots", "Snapshots", "SN")}}}
                {{{DashboardNavLink(options, "consumer-groups", "Consumer groups", "GR")}}}
                {{{DashboardNavLink(options, "checkpoints", "Checkpoints", "CP")}}}
              </nav></div>
              <div class="security-note"><strong>Role-secured view</strong>Inventory is redacted by the control plane. Operations are shown only to authorised roles.</div>
            </aside>
            <main id="main-content">
              {{{DataProvenanceNotice(options)}}}
              {{{body}}}
              <footer class="footer"><span>Observed <time datetime="{{{E(observedAt.ToString("O", CultureInfo.InvariantCulture))}}}">{{{E(observedAt.ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}}}</time></span><span>BlueTusk {{{E(ProductVersion)}}} control plane</span></footer>
            </main>
          </div>
        </div>
        <script src="{{{E(DashboardPath(options, "assets", "dashboard.js"))}}}" defer></script>
        </body>
        </html>
        """;

    private static string DashboardNavLink(BlueTuskDashboardOptions options, string route, string label, string mark) =>
        $"<a href=\"{E(DashboardPath(options, route))}\"><span class=\"nav-mark\" aria-hidden=\"true\">{E(mark)}</span>{E(label)}</a>";

    private static string DataProvenanceNotice(BlueTuskDashboardOptions options) =>
        string.IsNullOrWhiteSpace(options.DataProvenanceNotice)
            ? string.Empty
            : $"<aside class=\"provenance-notice\"><div><strong>Data provenance</strong><span>{E(options.DataProvenanceNotice)}</span></div></aside>";

    private static string EmptyInventory(string title, string description) =>
        $"<section class=\"panel empty-state\"><strong>{E(title)}</strong><span>{E(description)}</span></section>";

    private static string PipelineControls(string pipelineId, long quarantinedTransactions)
    {
        var target = E("pipeline:" + pipelineId);
        var replay = quarantinedTransactions > 0
            ? OperationButton(ControlPlaneOperationKind.ReplayQuarantine, target, "Replay next quarantine")
            : string.Empty;
        return OperationButton(ControlPlaneOperationKind.RetryPipeline, target, "Retry") +
               OperationButton(ControlPlaneOperationKind.ReconcilePipeline, target, "Reconcile") +
               OperationButton(ControlPlaneOperationKind.RebuildPipeline, target, "Rebuild") +
               replay;
    }

    private static string DeploymentControls(
        ControlPlaneManagedDeploymentSnapshot deployment,
        bool canAdminister)
    {
        var target = E("deployment:" + deployment.DeploymentId);
        var pause = deployment.Paused
            ? OperationButton(ControlPlaneOperationKind.ResumeDeployment, target, "Resume")
            : OperationButton(ControlPlaneOperationKind.PauseDeployment, target, "Pause");
        var delete = canAdminister
            ? OperationButton(ControlPlaneOperationKind.DeleteDeployment, target, "Delete")
            : string.Empty;
        return pause +
               OperationButton(ControlPlaneOperationKind.ReconcileDeployment, target, "Reconcile") +
               OperationButton(ControlPlaneOperationKind.RebuildDeployment, target, "Rebuild") +
               delete;
    }

    private static string OperationButton(
        ControlPlaneOperationKind kind,
        string encodedTarget,
        string label) =>
        $"<button type=\"button\" data-operation-kind=\"{(int)kind}\" data-operation-name=\"{kind}\" data-operation-target=\"{encodedTarget}\">{E(label)}</button>";

    private static string Card(string label, string value) =>
        $"<div class=\"card\"><span>{E(label)}</span><strong>{E(value)}</strong></div>";

    private static string Bytes(long value)
    {
        string[] suffixes = ["B", "KiB", "MiB", "GiB", "TiB"];
        var scaled = (double)Math.Max(0, value);
        var suffix = 0;
        while (scaled >= 1024 && suffix < suffixes.Length - 1)
        {
            scaled /= 1024;
            suffix++;
        }

        return scaled.ToString(suffix == 0 ? "N0" : "N1", CultureInfo.InvariantCulture) + " " + suffixes[suffix];
    }

    private static string ShortFingerprint(string value) => value.Length <= 12 ? value : value[..12];

    private static string E(string value) => HtmlEncoder.Default.Encode(value);

    /// <summary>
    /// The released package version shown in the page footer, for example "1.1.0". It is
    /// read from the assembly so the label always matches the shipped package instead of a
    /// hand-maintained release-line literal.
    /// </summary>
    private static string ProductVersion { get; } = GetProductVersion(
        typeof(BlueTuskDashboardOptions).Assembly);

    private static string GetProductVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Drop SemVer build metadata such as the "+<commit>" source revision.
            var metadata = informational.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? informational : informational[..metadata];
        }

        var version = assembly.GetName().Version;
        return version is null
            ? "unknown"
            : version.ToString(3);
    }

    private static bool CanMutate(ClaimsPrincipal user, BlueTuskDashboardOptions options) =>
        user.IsInRole(options.OperatorRole) || user.IsInRole(options.AdministratorRole);

    private static bool CanExecuteGraph(
        ClaimsPrincipal user,
        BlueTuskDashboardOptions options) =>
        user.IsInRole(options.GraphExecutorRole) || user.IsInRole(options.AdministratorRole);

    private static ControlPlaneActor? CreateActor(
        ClaimsPrincipal user,
        BlueTuskDashboardOptions options)
    {
        var actorId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }

        var roles = new HashSet<ControlPlaneRole>();
        if (user.IsInRole(options.ViewerRole)) roles.Add(ControlPlaneRole.Viewer);
        if (user.IsInRole(options.OperatorRole)) roles.Add(ControlPlaneRole.Operator);
        if (user.IsInRole(options.AdministratorRole)) roles.Add(ControlPlaneRole.Administrator);
        return new ControlPlaneActor(actorId, roles);
    }
}
