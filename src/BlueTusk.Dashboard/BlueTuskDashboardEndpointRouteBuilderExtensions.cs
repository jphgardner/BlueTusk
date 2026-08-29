using System.Globalization;
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
    private const string DashboardScript = """
        (() => {
          const operationUrl = new URL('../api/v1/operations', document.currentScript.src);
          const navToggle = document.querySelector('[data-nav-toggle]');
          const navigation = document.querySelector('[data-navigation]');
          navToggle?.addEventListener('click', () => {
            const expanded = navToggle.getAttribute('aria-expanded') === 'true';
            navToggle.setAttribute('aria-expanded', String(!expanded));
            navigation?.toggleAttribute('data-open', !expanded);
          });

          const currentPath = window.location.pathname.replace(/\/$/, '');
          document.querySelectorAll('[data-navigation] a').forEach(link => {
            const linkPath = new URL(link.href).pathname.replace(/\/$/, '');
            if (currentPath === linkPath || currentPath.startsWith(linkPath + '/')) {
              link.setAttribute('aria-current', 'page');
            }
          });

          document.querySelectorAll('[data-table-filter]').forEach(input => {
            const panel = input.closest('[data-filter-panel]');
            const rows = panel?.querySelectorAll('tbody tr') ?? [];
            const state = panel?.querySelector('[data-state-filter]');
            const count = panel?.querySelector('[data-filter-count]');
            const apply = () => {
              const query = input.value.trim().toLocaleLowerCase();
              const selectedState = state?.value ?? '';
              let visible = 0;
              rows.forEach(row => {
                const matchesQuery = !query || row.textContent.toLocaleLowerCase().includes(query);
                const matchesState = !selectedState || row.dataset.state === selectedState;
                const show = matchesQuery && matchesState;
                row.hidden = !show;
                if (show) visible++;
              });
              if (count) count.textContent = `${visible} of ${rows.length}`;
            };
            input.addEventListener('input', apply);
            state?.addEventListener('change', apply);
            apply();
          });

          document.querySelectorAll('[data-copy]').forEach(button =>
            button.addEventListener('click', async () => {
              const value = button.dataset.copy;
              if (!value) return;
              try {
                await navigator.clipboard.writeText(value);
                const original = button.textContent;
                button.textContent = 'Copied';
                window.setTimeout(() => { button.textContent = original; }, 1200);
              } catch {
                window.prompt('Copy this value:', value);
              }
            }));

          document.querySelectorAll('[data-refresh]').forEach(button =>
            button.addEventListener('click', () => window.location.reload()));

          document.querySelectorAll('button[data-operation-kind]').forEach(button =>
            button.addEventListener('click', async () => {
              const expected = button.dataset.operationName + ':' + button.dataset.operationTarget;
              const confirmation = window.prompt('Type exactly to confirm:\n' + expected);
              if (confirmation === null) return;
              const reason = window.prompt('Reason for this operation:');
              if (!reason || !reason.trim()) return;
              const operationId = crypto.randomUUID();
              const response = await fetch(operationUrl, {
                method: 'POST',
                headers: {
                  'Content-Type': 'application/json',
                  'X-BlueTusk-Operation-Id': operationId
                },
                body: JSON.stringify({
                  operationId,
                  kind: Number(button.dataset.operationKind),
                  target: button.dataset.operationTarget,
                  confirmation,
                  reason
                })
              });
              if (response.ok) window.location.reload();
              else window.alert('Operation rejected. Reference ' + operationId);
            }));
        })();
        """;

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
            "/assets/dashboard.js",
            () => Results.Text(DashboardScript, "application/javascript; charset=utf-8"));
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
                    : Html(RenderContinuousGraphQuery(overview.ObservedAt, query, options));
            });
        group.MapGet(
            "/deployments",
            async (HttpContext context,
                    IControlPlaneFleetQueryService queries,
                    CancellationToken cancellationToken) =>
                Html(RenderFleet(
                    await queries.GetFleetOverviewAsync(cancellationToken).ConfigureAwait(false),
                    options,
                    CanMutate(context.User, options),
                    context.User.IsInRole(options.AdministratorRole))));
        group.MapGet(
            "/deployments/{deploymentId}",
            async (string deploymentId,
                    HttpContext context,
                    IControlPlaneFleetQueryService queries,
                    CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetFleetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var deployment = overview.Deployments.FirstOrDefault(
                    candidate => string.Equals(
                        candidate.DeploymentId,
                        deploymentId,
                        StringComparison.Ordinal));
                return deployment is null
                    ? Results.NotFound()
                    : Html(RenderDeployment(
                        overview.ObservedAt,
                        deployment,
                        options,
                        CanMutate(context.User, options),
                        context.User.IsInRole(options.AdministratorRole)));
            });
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
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Sources", null)))
            .Append(PageHeading(
                "Sources & Streams",
                "PostgreSQL capture, relay durability and consumer progress.",
                StatusBadge(
                    healthy == overview.Sources.Count ? "All sources healthy" : $"{overview.Sources.Count - healthy} need attention",
                    healthy == overview.Sources.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Sources", overview.Sources.Count.ToString(CultureInfo.InvariantCulture), "Configured capture sources"))
            .Append(MetricCard("Active slots", overview.Sources.Count(source => source.Slot.Active).ToString(CultureInfo.InvariantCulture), "Currently streaming"))
            .Append(MetricCard("WAL lag", Bytes(overview.Sources.Sum(source => source.Slot.WalLagBytes)), "Total retained WAL"))
            .Append(MetricCard("Relay storage", Bytes(overview.Sources.Sum(source => source.Relay.StorageBytes)), "Durable transaction history"))
            .Append("</div><section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Inventory</p><h2>Capture sources</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search sources</span><input type=\"search\" placeholder=\"Instance, database or slot\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Instance</th><th>Database</th><th>Slot</th>")
            .Append("<th>Health</th><th>WAL lag</th><th>Relay</th><th>Groups</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var source in overview.Sources)
        {
            var isHealthy = IsSourceHealthy(source);
            var status = source.Slot.SourceReachable
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
        var status = !source.Slot.SourceReachable
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
                    healthy == overview.Pipelines.Count ? "All pipelines healthy" : $"{overview.Pipelines.Count - healthy} need attention",
                    healthy == overview.Pipelines.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Pipelines", overview.Pipelines.Count.ToString(CultureInfo.InvariantCulture), "Configured destinations"))
            .Append(MetricCard("Running", overview.Pipelines.Count(static pipeline => pipeline.State == "Running").ToString(CultureInfo.InvariantCulture), "Workers currently active"))
            .Append(MetricCard("Throughput", totalRate.ToString("N1", CultureInfo.InvariantCulture) + " tx/s", "Combined recent rate"))
            .Append(MetricCard("Quarantined", overview.Pipelines.Sum(static pipeline => pipeline.QuarantinedTransactions).ToString("N0", CultureInfo.InvariantCulture), "Awaiting review"))
            .Append(MetricCard("Failures", overview.Pipelines.Sum(static pipeline => pipeline.FailureCount).ToString("N0", CultureInfo.InvariantCulture), "Recorded failures"))
            .Append("</div><section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Delivery inventory</p><h2>All pipelines</h2></div><span class=\"muted\" data-filter-count></span></div>")
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
                    healthy == overview.Subscriptions.Count ? "All subscriptions current" : $"{overview.Subscriptions.Count - healthy} need attention",
                    healthy == overview.Subscriptions.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Shared queries", overview.Registry.SharedSubscriptions.ToString(CultureInfo.InvariantCulture), $"Limit {overview.Registry.MaximumSharedSubscriptions:N0}"))
            .Append(MetricCard("Subscribers", overview.Subscriptions.Sum(static item => item.SubscriberCount).ToString("N0", CultureInfo.InvariantCulture), "Across every query"))
            .Append(MetricCard("Connected clients", overview.Subscriptions.Sum(static item => item.ConnectedClients).ToString("N0", CultureInfo.InvariantCulture), "Current transport connections"))
            .Append(MetricCard("Fan-out deliveries", overview.Subscriptions.Sum(static item => item.FanOutDeliveries).ToString("N0", CultureInfo.InvariantCulture), "Shared result deliveries"))
            .Append(MetricCard("Quota rejections", (overview.Registry.QuotaRejections + overview.Subscriptions.Sum(static item => item.QuotaRejections)).ToString("N0", CultureInfo.InvariantCulture), "Capacity protection"))
            .Append("</div><section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Query inventory</p><h2>Shared subscriptions</h2></div><span class=\"muted\" data-filter-count></span></div>")
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

    private static string RenderFleet(
        ControlPlaneFleetOverview overview,
        BlueTuskDashboardOptions options,
        bool canMutate,
        bool canAdminister)
    {
        var healthy = overview.Deployments.Count(IsDeploymentHealthy);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Managed deployments", null)))
            .Append(PageHeading(
                "Managed deployments",
                "Fleet state, generation convergence, placement and requested capacity.",
                StatusBadge(
                    healthy == overview.Deployments.Count ? "Fleet ready" : $"{overview.Deployments.Count - healthy} need attention",
                    healthy == overview.Deployments.Count ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Deployments", overview.Deployments.Count.ToString(CultureInfo.InvariantCulture), "Managed environments"))
            .Append(MetricCard("Ready", healthy.ToString(CultureInfo.InvariantCulture), "Converged generations"))
            .Append(MetricCard("Replicas", overview.Deployments.Sum(static deployment => deployment.Replicas).ToString("N0", CultureInfo.InvariantCulture), "Requested workload replicas"))
            .Append(MetricCard("Requested CPU", overview.Deployments.Sum(static deployment => deployment.CpuMillicores).ToString("N0", CultureInfo.InvariantCulture) + "m", "Across the fleet"))
            .Append(MetricCard("Requested memory", Bytes(overview.Deployments.Sum(static deployment => deployment.MemoryBytes)), "Across the fleet"))
            .Append("</div><section class=\"panel table-panel\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Fleet inventory</p><h2>All deployments</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Search deployments</span><input type=\"search\" placeholder=\"Deployment, tenant, region or workload\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\"><table><thead><tr><th>Deployment</th><th>Tenant</th><th>Placement</th>")
            .Append("<th>State</th><th>Generation</th><th>Workloads</th><th>Replicas</th>")
            .Append("<th>Storage</th><th>Protection</th><th>Diagnostic</th><th>Controls</th><th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var deployment in overview.Deployments)
        {
            var isHealthy = IsDeploymentHealthy(deployment);
            var (status, tone) = DeploymentStatus(deployment);
            var href = DashboardPath(options, "deployments", deployment.DeploymentId);
            body.Append("<tr data-state=\"").Append(isHealthy ? "healthy" : "attention")
                .Append("\"><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(deployment.DeploymentId)).Append("</a></td><td>")
                .Append(E(deployment.TenantId)).Append("</td><td>")
                .Append(E(deployment.Provider)).Append(" / ").Append(E(deployment.Region))
                .Append("</td><td>").Append(StatusBadge(status, tone)).Append("</td><td>")
                .Append(deployment.ObservedGeneration.ToString(CultureInfo.InvariantCulture))
                .Append(" / ")
                .Append(deployment.DesiredGeneration.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>")
                .Append(E(string.Join(", ", deployment.WorkloadKinds)))
                .Append("</td><td>")
                .Append(deployment.Replicas.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(Bytes(deployment.StorageBytes)).Append("</td><td>")
                .Append(deployment.DeleteProtection ? "delete protected" : "unprotected")
                .Append(deployment.Paused ? " / paused" : string.Empty)
                .Append("</td><td>").Append(E(deployment.DiagnosticCode ?? "—")).Append("</td><td>")
                .Append(canMutate
                    ? DeploymentControls(deployment, canAdminister)
                    : "—")
                .Append("</td><td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div></section>");
        return Layout("Managed deployments", overview.ObservedAt, body.ToString(), options);
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
        <title>{{{E(title)}}} · BlueTusk</title>
        <style>
        :root{color-scheme:dark;font:15px/1.5 Inter,ui-sans-serif,system-ui,-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;--bg:#07101d;--surface:#0c1828;--surface-2:#101f32;--surface-3:#142840;--border:#233a53;--border-soft:#192d43;--text:#f1f7fb;--muted:#90a7bb;--blue:#48b9ff;--blue-strong:#169eea;--cyan:#5ce1d1;--ok:#4ade9a;--warn:#f6c85f;--critical:#ff6f7d;--shadow:0 18px 48px rgba(0,0,0,.24)}
        *{box-sizing:border-box}html{background:var(--bg);scroll-behavior:smooth}body{margin:0;background:radial-gradient(circle at 82% -10%,rgba(32,151,220,.15),transparent 28rem),var(--bg);color:var(--text);min-height:100vh}button,input,select{font:inherit}a{color:var(--blue);text-decoration:none}a:hover{text-decoration:underline}code{font:500 .86em ui-monospace,SFMono-Regular,Consolas,monospace;color:#bfe8ff;overflow-wrap:anywhere}h1,h2,h3,p{margin-top:0}h1{font-size:clamp(2rem,4vw,3rem);line-height:1.08;letter-spacing:-.04em;margin-bottom:.75rem}h2{font-size:1.28rem;letter-spacing:-.02em;margin-bottom:.35rem}small{display:block;color:var(--muted)}
        .skip-link{position:fixed;left:1rem;top:-5rem;z-index:100;padding:.65rem 1rem;background:var(--blue);color:#00111d;border-radius:.5rem}.skip-link:focus{top:1rem}.app-shell{min-height:100vh}.topbar{height:72px;display:flex;align-items:center;justify-content:space-between;padding:0 1.5rem;border-bottom:1px solid var(--border-soft);background:rgba(7,16,29,.88);backdrop-filter:blur(18px);position:sticky;top:0;z-index:30}.brand{display:inline-flex;align-items:center;gap:.72rem;color:var(--text);font-weight:750;font-size:1.12rem;letter-spacing:-.02em}.brand:hover{text-decoration:none}.brand-mark{display:grid;place-items:center;width:36px;height:36px;border-radius:11px;background:linear-gradient(145deg,var(--blue),var(--cyan));color:#05121d;box-shadow:0 8px 25px rgba(45,183,244,.28);font-weight:900}.brand small{display:inline;color:var(--muted);font-weight:500;margin-left:.22rem}.nav-toggle{display:none}.app-body{display:grid;grid-template-columns:250px minmax(0,1fr);min-height:calc(100vh - 72px)}.sidebar{border-right:1px solid var(--border-soft);background:rgba(9,20,34,.76);padding:1.35rem 1rem;position:sticky;top:72px;height:calc(100vh - 72px);overflow:auto}.nav-section{margin-bottom:1.3rem}.nav-label{display:block;padding:0 .7rem .45rem;color:#647f97;text-transform:uppercase;letter-spacing:.12em;font-size:.68rem;font-weight:750}.side-nav{display:grid;gap:.18rem}.side-nav a{display:flex;align-items:center;gap:.62rem;color:#a9bed0;padding:.58rem .7rem;border-radius:.55rem;font-weight:550}.side-nav a::before{content:"";width:7px;height:7px;border:1px solid #59748b;border-radius:50%}.side-nav a:hover{background:var(--surface-2);color:var(--text);text-decoration:none}.side-nav a[aria-current=page]{background:linear-gradient(90deg,rgba(41,169,237,.18),rgba(41,169,237,.06));color:#eaf8ff}.side-nav a[aria-current=page]::before{border-color:var(--blue);background:var(--blue);box-shadow:0 0 0 4px rgba(72,185,255,.1)}.security-note{margin-top:2rem;padding:.8rem;border:1px solid var(--border);border-radius:.7rem;background:rgba(15,31,50,.7);color:var(--muted);font-size:.78rem}.security-note strong{display:block;color:var(--text);margin-bottom:.2rem}
        main{width:100%;max-width:1500px;margin:0 auto;padding:2rem clamp(1.1rem,3vw,3.2rem) 3rem;min-width:0}.breadcrumbs{display:flex;align-items:center;gap:.5rem;color:var(--muted);font-size:.82rem;margin-bottom:1.55rem;overflow:auto;white-space:nowrap}.breadcrumbs a{color:var(--muted)}.page-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:2rem;margin-bottom:1.8rem}.page-heading>div:first-child{max-width:760px}.page-heading p:not(.eyebrow){color:var(--muted);font-size:1.02rem;margin-bottom:0}.page-actions{display:flex;align-items:center;gap:.7rem;flex-wrap:wrap;justify-content:flex-end}.eyebrow{color:var(--cyan);font-size:.72rem;text-transform:uppercase;letter-spacing:.12em;font-weight:800;margin-bottom:.45rem}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(175px,1fr));gap:.8rem;margin-bottom:1.4rem}.cards--wide{grid-template-columns:repeat(auto-fit,minmax(195px,1fr))}.card{min-height:124px;background:linear-gradient(145deg,rgba(16,34,55,.96),rgba(11,25,42,.96));border:1px solid var(--border);border-radius:.8rem;padding:1rem;box-shadow:0 10px 28px rgba(0,0,0,.12)}.card>span{display:block;color:var(--muted);font-size:.78rem;font-weight:650}.card strong{display:block;font-size:1.62rem;letter-spacing:-.04em;margin:.32rem 0 .15rem;overflow-wrap:anywhere}.card small{font-size:.75rem}.panel{background:linear-gradient(155deg,rgba(13,29,47,.98),rgba(9,22,38,.98));border:1px solid var(--border);border-radius:.85rem;padding:1.15rem;margin:0 0 1.2rem;box-shadow:var(--shadow)}.section-heading{display:flex;align-items:center;justify-content:space-between;gap:1rem;margin-bottom:.9rem}.section-heading h2{margin:0}.muted{color:var(--muted)}.status-badge{display:inline-flex;align-items:center;gap:.42rem;width:max-content;max-width:100%;padding:.28rem .58rem;border-radius:999px;border:1px solid var(--border);background:var(--surface-2);color:#c5d5e2;font-size:.75rem;font-weight:700;white-space:nowrap}.status-dot,.product-icon{display:inline-block;width:8px;height:8px;border-radius:50%;background:var(--muted);box-shadow:0 0 0 3px rgba(144,167,187,.1)}[data-tone=ok]{--tone:var(--ok)}[data-tone=warn]{--tone:var(--warn)}[data-tone=critical]{--tone:var(--critical)}.status-dot[data-tone],.product-icon[data-tone]{background:var(--tone);box-shadow:0 0 0 3px color-mix(in srgb,var(--tone) 16%,transparent)}.status-badge[data-tone=ok]{border-color:rgba(74,222,154,.25);color:#9af1c6;background:rgba(74,222,154,.08)}.status-badge[data-tone=warn]{border-color:rgba(246,200,95,.26);color:#ffe09a;background:rgba(246,200,95,.08)}.status-badge[data-tone=critical]{border-color:rgba(255,111,125,.3);color:#ffabb3;background:rgba(255,111,125,.08)}
        .product-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:.8rem}.product-card{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:.85rem;padding:1rem;border:1px solid var(--border);border-radius:.8rem;background:var(--surface);color:var(--text)}.product-card:hover{border-color:#366c91;background:var(--surface-2);text-decoration:none}.product-card .product-icon{width:10px;height:10px}.product-card strong,.related-link strong{display:block;margin-bottom:.2rem}.product-card small,.related-link small{font-size:.78rem}.product-count{color:var(--blue);font-size:.8rem;white-space:nowrap}.attention-list{display:grid;gap:.45rem}.attention-item{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:.8rem;padding:.72rem;border-radius:.6rem;color:var(--text);background:rgba(14,32,52,.7)}.attention-item:hover{background:var(--surface-3);text-decoration:none}.attention-item small{font-size:.78rem}.empty-state{display:flex;flex-direction:column;align-items:center;text-align:center;padding:1.6rem;color:var(--muted)}.empty-state strong{color:var(--text);margin-bottom:.3rem}.table-panel{padding-bottom:.35rem}.table-tools{display:flex;align-items:end;gap:.8rem;flex-wrap:wrap;margin-bottom:.85rem}.table-tools label{display:grid;gap:.3rem;color:var(--muted);font-size:.72rem;font-weight:650}.table-tools label:first-child{flex:1 1 280px}.table-tools input,.table-tools select{width:100%;min-height:40px;border:1px solid var(--border);border-radius:.52rem;background:#081624;color:var(--text);padding:.48rem .65rem;outline:none}.table-tools input:focus,.table-tools select:focus{border-color:var(--blue);box-shadow:0 0 0 3px rgba(72,185,255,.12)}.table-wrap{overflow:auto;margin:0 -1.15rem}.table-wrap table{min-width:760px}table{width:100%;border-collapse:collapse}th,td{padding:.75rem .82rem;text-align:left;border-bottom:1px solid var(--border-soft);vertical-align:middle}th{color:#7892a8;font-size:.68rem;text-transform:uppercase;letter-spacing:.08em;font-weight:800;background:rgba(8,20,34,.7);white-space:nowrap}td{font-size:.84rem;color:#c7d6e3}tbody tr:hover{background:rgba(31,73,102,.12)}tbody tr:last-child td{border-bottom:0}td small{margin-top:.18rem;font-size:.7rem}.primary-link{color:#e7f7ff;font-weight:720}.row-action{white-space:nowrap;font-size:.78rem;font-weight:700}.detail-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0;margin:0}.detail-grid>div{padding:.75rem;border-bottom:1px solid var(--border-soft);min-width:0}.detail-grid>div:nth-last-child(-n+2){border-bottom:0}.detail-grid dt{color:var(--muted);font-size:.72rem;margin-bottom:.18rem}.detail-grid dd{margin:0;color:#dce8f1;overflow-wrap:anywhere}.tag-list{display:flex;flex-wrap:wrap;gap:.5rem}.tag-list code{padding:.35rem .55rem;border:1px solid var(--border);border-radius:.45rem;background:#081624}.copy-value{display:inline-flex;align-items:center;gap:.45rem;max-width:100%}.copy-button{padding:.18rem .42rem;font-size:.67rem}.related-link{display:flex;align-items:center;justify-content:space-between;color:var(--text)}.related-link:hover{text-decoration:none;border-color:#366c91}.steps{display:grid;gap:.7rem;margin:1rem 0 0;padding:0;list-style:none;counter-reset:steps}.steps li{display:grid;grid-template-columns:auto 1fr;gap:.7rem;align-items:start;color:var(--muted);counter-increment:steps}.steps li::before{content:counter(steps);display:grid;place-items:center;width:27px;height:27px;border-radius:50%;background:rgba(72,185,255,.12);color:var(--blue);font-weight:800}.steps strong,.steps span{display:block}.steps strong{color:var(--text)}.button-row{display:flex;flex-wrap:wrap;gap:.45rem}.read-only{border-color:rgba(72,185,255,.22)}.danger-zone{border-color:rgba(246,200,95,.25)}
        button{appearance:none;border:1px solid #365773;border-radius:.5rem;background:var(--surface-3);color:var(--text);padding:.45rem .7rem;cursor:pointer;font-weight:650}button:hover{border-color:var(--blue);background:#193853}button.secondary{background:transparent}.footer{display:flex;justify-content:space-between;gap:1rem;flex-wrap:wrap;color:#637e94;font-size:.75rem;margin-top:2rem;padding-top:1rem;border-top:1px solid var(--border-soft)}.sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}[hidden]{display:none!important}
        @media(max-width:980px){.topbar{height:64px;padding:0 1rem}.brand small{display:none}.nav-toggle{display:inline-flex}.app-body{display:block;min-height:calc(100vh - 64px)}.sidebar{display:none;position:sticky;top:64px;width:100%;height:auto;max-height:calc(100vh - 64px);z-index:25;border-right:0;border-bottom:1px solid var(--border);padding:.8rem 1rem;background:#081421}.sidebar[data-open]{display:block}.side-nav{grid-template-columns:repeat(2,minmax(0,1fr))}.security-note{display:none}main{padding-top:1.35rem}}
        @media(max-width:680px){h1{font-size:2rem}.page-heading{display:block}.page-actions{justify-content:flex-start;margin-top:1rem}.cards,.cards--wide,.product-grid{grid-template-columns:1fr}.card{min-height:0}.product-card{grid-template-columns:auto 1fr}.product-count{grid-column:2}.section-heading{align-items:flex-start}.table-tools{display:grid}.detail-grid{grid-template-columns:1fr}.detail-grid>div:nth-last-child(2){border-bottom:1px solid var(--border-soft)}.side-nav{grid-template-columns:1fr}.topbar{position:sticky}.panel{padding:1rem}.table-wrap{margin:0 -1rem}.footer{display:block}.footer span{display:block;margin-bottom:.25rem}}
        @media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}*{transition:none!important}}
        </style>
        </head>
        <body>
        <a class="skip-link" href="#main-content">Skip to dashboard content</a>
        <div class="app-shell">
          <header class="topbar">
            <a class="brand" href="{{{E(DashboardPath(options, "overview"))}}}"><span class="brand-mark" aria-hidden="true">B</span><span>BlueTusk <small>Control plane</small></span></a>
            <button class="nav-toggle secondary" type="button" data-nav-toggle aria-expanded="false" aria-controls="dashboard-navigation">Menu</button>
          </header>
          <div class="app-body">
            <aside class="sidebar" id="dashboard-navigation" data-navigation>
              <div class="nav-section"><span class="nav-label">Workspace</span><nav class="side-nav" aria-label="Dashboard"><a href="{{{E(DashboardPath(options, "overview"))}}}">Overview</a></nav></div>
              <div class="nav-section"><span class="nav-label">Data flow</span><nav class="side-nav" aria-label="Data flow"><a href="{{{E(DashboardPath(options, "sources"))}}}">Sources & Streams</a><a href="{{{E(DashboardPath(options, "pipelines"))}}}">Sync pipelines</a><a href="{{{E(DashboardPath(options, "live"))}}}">Live subscriptions</a><a href="{{{E(DashboardPath(options, "graphs"))}}}">Continuous Graph</a></nav></div>
              <div class="nav-section"><span class="nav-label">Operations</span><nav class="side-nav" aria-label="Operations"><a href="{{{E(DashboardPath(options, "snapshots"))}}}">Snapshots</a><a href="{{{E(DashboardPath(options, "consumer-groups"))}}}">Consumer groups</a><a href="{{{E(DashboardPath(options, "checkpoints"))}}}">Checkpoints</a><a href="{{{E(DashboardPath(options, "deployments"))}}}">Deployments</a></nav></div>
              <div class="security-note"><strong>Role-secured view</strong>Inventory is redacted by the control plane. Operations are shown only to authorised roles.</div>
            </aside>
            <main id="main-content">
              {{{body}}}
              <footer class="footer"><span>Observed <time datetime="{{{E(observedAt.ToString("O", CultureInfo.InvariantCulture))}}}">{{{E(observedAt.ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}}}</time></span><span>BlueTusk 1.2 control plane</span></footer>
            </main>
          </div>
        </div>
        <script src="{{{E(DashboardPath(options, "assets", "dashboard.js"))}}}" defer></script>
        </body>
        </html>
        """;

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

    private static bool CanMutate(ClaimsPrincipal user, BlueTuskDashboardOptions options) =>
        user.IsInRole(options.OperatorRole) || user.IsInRole(options.AdministratorRole);
}
