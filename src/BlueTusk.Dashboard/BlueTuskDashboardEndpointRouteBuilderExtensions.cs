using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using BlueTusk.ControlPlane;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace BlueTusk.Dashboard;

public static class BlueTuskDashboardEndpointRouteBuilderExtensions
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
          const operationUrl = new URL('../api/operations', document.currentScript.src);
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
        group.MapGet("/", () => Results.Redirect(options.RoutePrefix + "/sources"));
        group.MapGet(
            "/api/overview",
            (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                queries.GetOverviewAsync(cancellationToken));
        group.MapGet(
            "/api/sync",
            (IControlPlaneSyncQueryService queries, CancellationToken cancellationToken) =>
                queries.GetSyncOverviewAsync(cancellationToken));
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
                        cancellationToken))
            .RequireAuthorization(options.MutationAuthorizationPolicy);
        group.MapGet(
            "/sources",
            async (IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
                Html(RenderSources(await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false), options)));
        group.MapGet(
            "/sources/{sourceKey}",
            async (string sourceKey, IControlPlaneQueryService queries, CancellationToken cancellationToken) =>
            {
                var overview = await queries.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
                var source = overview.Sources.FirstOrDefault(
                    candidate => string.Equals(candidate.SourceKey, sourceKey, StringComparison.Ordinal));
                return source is null
                    ? Results.NotFound()
                    : Html(RenderSource(overview.ObservedAt, source, options));
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
        return endpoints;
    }

    private static async Task<IResult> ExecuteOperationAsync(
        HttpContext context,
        ControlPlaneOperationExecutor executor,
        ILogger logger,
        BlueTuskDashboardOptions options,
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
            return Results.Ok(new { request.OperationId, Status = "succeeded" });
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

    private static string RenderSources(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder();
        body.Append("<h1>Sources</h1><div class=\"cards\">")
            .Append(Card("Sources", overview.Sources.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Card(
                "Active slots",
                overview.Sources.Count(source => source.Slot.Active).ToString(CultureInfo.InvariantCulture)))
            .Append(Card(
                "Relay bytes",
                overview.Sources.Sum(source => source.Relay.StorageBytes).ToString("N0", CultureInfo.InvariantCulture)))
            .Append("</div><table><thead><tr><th>Instance</th><th>Database</th><th>Slot</th>")
            .Append("<th>Slot state</th><th>WAL lag</th><th>Relay</th><th>Groups</th></tr></thead><tbody>");
        foreach (var source in overview.Sources)
        {
            body.Append("<tr><td>").Append(E(source.InstanceName)).Append("</td><td>")
                .Append(E(source.DatabaseName)).Append("</td><td><a href=\"")
                .Append(E(options.RoutePrefix)).Append("/sources/")
                .Append(Uri.EscapeDataString(source.SourceKey)).Append("\">")
                .Append(E(source.SlotName)).Append("</a></td><td>")
                .Append(source.Slot.SourceReachable
                    ? source.Slot.Exists ? source.Slot.Active ? "active" : "inactive" : "missing"
                    : "unreachable")
                .Append("</td><td>").Append(Bytes(source.Slot.WalLagBytes)).Append("</td><td>")
                .Append(source.Relay.TransactionCount.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" tx / ").Append(Bytes(source.Relay.StorageBytes)).Append("</td><td>")
                .Append(source.ConsumerGroups.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</td></tr>");
        }

        body.Append("</tbody></table>");
        return Layout("Sources", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderSource(
        DateTimeOffset observedAt,
        ControlPlaneSourceSnapshot source,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder();
        body.Append("<h1>").Append(E(source.DatabaseName)).Append(" / ")
            .Append(E(source.SlotName)).Append("</h1><div class=\"cards\">")
            .Append(Card("WAL lag", Bytes(source.Slot.WalLagBytes)))
            .Append(Card("Relay storage", Bytes(source.Relay.StorageBytes)))
            .Append(Card("Last sequence", source.LastSequence.ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Card("Last commit", E(source.LastCommitPosition))).Append("</div>")
            .Append("<h2>Slot</h2><dl><dt>Reachable</dt><dd>")
            .Append(source.Slot.SourceReachable).Append("</dd><dt>Exists</dt><dd>")
            .Append(source.Slot.Exists).Append("</dd><dt>Active</dt><dd>")
            .Append(source.Slot.Active).Append("</dd><dt>Plugin</dt><dd>")
            .Append(E(source.Slot.OutputPlugin ?? "—")).Append("</dd><dt>WAL status</dt><dd>")
            .Append(E(source.Slot.WalStatus ?? source.Slot.DiagnosticCode ?? "—")).Append("</dd></dl>")
            .Append(RenderGroupTable(source))
            .Append(RenderSnapshotTable(source))
            .Append(RenderCheckpointTable(source));
        return Layout(source.SlotName, observedAt, body.ToString(), options);
    }

    private static string RenderSnapshots(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<h1>Snapshots</h1>");
        foreach (var source in overview.Sources)
        {
            body.Append(RenderSnapshotTable(source));
        }

        return Layout("Snapshots", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderGroups(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<h1>Consumer groups</h1>");
        foreach (var source in overview.Sources)
        {
            body.Append(RenderGroupTable(source));
        }

        return Layout("Consumer groups", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderCheckpoints(
        ControlPlaneOverview overview,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<h1>Direct checkpoints</h1>");
        foreach (var source in overview.Sources)
        {
            body.Append(RenderCheckpointTable(source));
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
        var body = new StringBuilder("<h1>Sync pipelines</h1><div class=\"cards\">")
            .Append(Card("Pipelines", overview.Pipelines.Count.ToString(CultureInfo.InvariantCulture)))
            .Append(Card(
                "Running",
                overview.Pipelines.Count(static pipeline => pipeline.State == "Running")
                    .ToString(CultureInfo.InvariantCulture)))
            .Append(Card("Throughput", totalRate.ToString("N1", CultureInfo.InvariantCulture) + " tx/s"))
            .Append(Card(
                "Quarantined",
                overview.Pipelines.Sum(static pipeline => pipeline.QuarantinedTransactions)
                    .ToString("N0", CultureInfo.InvariantCulture)))
            .Append(Card(
                "Failures",
                overview.Pipelines.Sum(static pipeline => pipeline.FailureCount)
                    .ToString("N0", CultureInfo.InvariantCulture)))
            .Append("</div><table><thead><tr><th>Pipeline</th><th>State</th><th>Throughput</th>")
            .Append("<th>Checkpoint lag</th><th>Applied</th><th>Snapshot rows</th>")
            .Append("<th>Retries</th><th>Quarantine</th><th>Diagnostic</th><th>Controls</th></tr></thead><tbody>");
        foreach (var pipeline in overview.Pipelines)
        {
            body.Append("<tr><td>").Append(E(pipeline.PipelineId)).Append("</td><td>")
                .Append(E(pipeline.State)).Append("</td><td>")
                .Append(pipeline.TransactionsPerSecond?.ToString("N1", CultureInfo.InvariantCulture) ?? "—")
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
                .Append(canMutate ? PipelineControls(pipeline.PipelineId) : "—")
                .Append("</td></tr>");
        }

        body.Append("</tbody></table>");
        return Layout("Sync pipelines", overview.ObservedAt, body.ToString(), options);
    }

    private static string RenderGroupTable(ControlPlaneSourceSnapshot source)
    {
        var body = new StringBuilder("<h2>Relay groups — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2><table><thead><tr><th>Group</th><th>State</th><th>Checkpoint</th>")
            .Append("<th>Generation</th><th>Lease</th><th>Fence</th></tr></thead><tbody>");
        foreach (var group in source.ConsumerGroups)
        {
            body.Append("<tr><td>").Append(E(group.Name)).Append("</td><td>")
                .Append(group.IsActive ? "active" : "removed").Append("</td><td>")
                .Append(group.CheckpointSequence.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(group.StoreGeneration.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(group.IsLeased ? "leased" : "free")
                .Append("</td><td>").Append(group.LastFencingToken.ToString(CultureInfo.InvariantCulture))
                .Append("</td></tr>");
        }

        return body.Append("</tbody></table>").ToString();
    }

    private static string RenderSnapshotTable(ControlPlaneSourceSnapshot source)
    {
        var body = new StringBuilder("<h2>Snapshots — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2><table><thead><tr><th>Epoch</th><th>State</th><th>Progress bytes</th>")
            .Append("<th>Updated</th></tr></thead><tbody>");
        foreach (var snapshot in source.SnapshotRuns)
        {
            body.Append("<tr><td>").Append(E(snapshot.SnapshotEpoch)).Append("</td><td>")
                .Append(E(snapshot.State)).Append("</td><td>")
                .Append(snapshot.ProgressBytes.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(E(snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append("</td></tr>");
        }

        return body.Append("</tbody></table>").ToString();
    }

    private static string RenderCheckpointTable(ControlPlaneSourceSnapshot source)
    {
        var body = new StringBuilder("<h2>Direct checkpoints — ")
            .Append(E(source.InstanceName)).Append(" / ").Append(E(source.SlotName))
            .Append("</h2><table><thead><tr><th>Group</th><th>Position</th><th>Generation</th>")
            .Append("<th>Mapping</th><th>Lease</th></tr></thead><tbody>");
        foreach (var checkpoint in source.Checkpoints)
        {
            body.Append("<tr><td>").Append(E(checkpoint.ConsumerGroup)).Append("</td><td>")
                .Append(E(checkpoint.AcknowledgedPosition)).Append("</td><td>")
                .Append(checkpoint.StoreGeneration.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td><code>").Append(E(ShortFingerprint(checkpoint.MappingFingerprint)))
                .Append("</code></td><td>").Append(checkpoint.IsLeased ? "leased" : "free")
                .Append("</td></tr>");
        }

        return body.Append("</tbody></table>").ToString();
    }

    private static string Layout(
        string title,
        DateTimeOffset observedAt,
        string body,
        BlueTuskDashboardOptions options) =>
        $$$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{{E(title)}}} · BlueTusk</title><style>
        :root{color-scheme:light dark;font:15px system-ui,sans-serif}body{margin:0;background:#10151b;color:#e9f1f7}
        nav{padding:1rem 2rem;background:#17212b;display:flex;gap:1rem}nav a{color:#8dd8ff;text-decoration:none}
        main{max-width:1200px;margin:auto;padding:2rem}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:1rem}
        .card{background:#17212b;border:1px solid #2b3c4b;border-radius:8px;padding:1rem}.card strong{display:block;font-size:1.5rem}
        table{width:100%;border-collapse:collapse;margin:1rem 0 2rem}th,td{padding:.65rem;text-align:left;border-bottom:1px solid #2b3c4b}
        th,dt{color:#9fb2c2}a{color:#8dd8ff}dl{display:grid;grid-template-columns:max-content 1fr;gap:.5rem 1rem}
        footer{color:#9fb2c2;margin-top:2rem}code{font-size:.85em}
        button{margin:.15rem;padding:.35rem .55rem;border:1px solid #4b687d;border-radius:5px;background:#21313e;color:#e9f1f7;cursor:pointer}
        </style></head><body><nav><strong>BlueTusk</strong>
        <a href="{{{E(options.RoutePrefix)}}}/sources">Sources</a>
        <a href="{{{E(options.RoutePrefix)}}}/snapshots">Snapshots</a>
        <a href="{{{E(options.RoutePrefix)}}}/consumer-groups">Consumer groups</a>
        <a href="{{{E(options.RoutePrefix)}}}/checkpoints">Checkpoints</a>
        <a href="{{{E(options.RoutePrefix)}}}/pipelines">Sync pipelines</a></nav>
        <main>{{{body}}}<footer>Observed {{{E(observedAt.ToString("O", CultureInfo.InvariantCulture))}}}</footer></main>
        <script src="{{{E(options.RoutePrefix)}}}/assets/dashboard.js" defer></script>
        </body></html>
        """;

    private static string PipelineControls(string pipelineId)
    {
        var target = E("pipeline:" + pipelineId);
        return OperationButton(ControlPlaneOperationKind.RetryPipeline, target, "Retry") +
               OperationButton(ControlPlaneOperationKind.ReconcilePipeline, target, "Reconcile") +
               OperationButton(ControlPlaneOperationKind.RebuildPipeline, target, "Rebuild");
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
