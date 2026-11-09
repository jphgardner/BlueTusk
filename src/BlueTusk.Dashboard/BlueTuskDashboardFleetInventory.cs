using System.Globalization;
using System.Text;
using BlueTusk.ControlPlane;
using Microsoft.AspNetCore.Http;

namespace BlueTusk.Dashboard;

public static partial class BlueTuskDashboardEndpointRouteBuilderExtensions
{
    private static async Task<IResult> ReadFleetPageAsync(
        HttpContext context, IControlPlaneFleetQueryService queries, BlueTuskDashboardOptions options,
        bool html, CancellationToken cancellationToken)
    {
        try
        {
            var query = ReadFleetQuery(context.Request.Query);
            var page = await queries.GetDeploymentPageAsync(query, cancellationToken).ConfigureAwait(false);
            return html ? Html(RenderFleet(page, options, CanMutate(context.User, options),
                context.User.IsInRole(options.AdministratorRole), query)) : Json(Versioned(page));
        }
        catch (ArgumentException)
        {
            return Results.Problem("The deployment inventory filters or page size are invalid.", statusCode: 400);
        }
        catch (NotSupportedException)
        {
            return Results.Problem("The configured fleet service does not support bounded inventory queries.", statusCode: 501);
        }
    }

    private static async Task<IResult> ReadDeploymentAsync(
        string deploymentId, HttpContext context, IControlPlaneFleetQueryService queries,
        BlueTuskDashboardOptions options, bool html, CancellationToken cancellationToken)
    {
        try
        {
            var deployment = await queries.GetDeploymentAsync(deploymentId, cancellationToken).ConfigureAwait(false);
            // Catch-all routes can retain an escaped slash. Prefer an exact ID before decoding it.
            var decodedId = Uri.UnescapeDataString(deploymentId);
            if (deployment is null && !string.Equals(deploymentId, decodedId, StringComparison.Ordinal))
            {
                deployment = await queries.GetDeploymentAsync(decodedId, cancellationToken).ConfigureAwait(false);
            }
            return deployment is null ? Results.NotFound() : html
                ? Html(RenderDeployment(DateTimeOffset.UtcNow, deployment, options, CanMutate(context.User, options),
                    context.User.IsInRole(options.AdministratorRole))) : Json(Versioned(deployment));
        }
        catch (ArgumentException)
        {
            return Results.Problem("The deployment identifier is invalid.", statusCode: 400);
        }
        catch (NotSupportedException)
        {
            return Results.Problem("The configured fleet service does not support direct deployment lookup.", statusCode: 501);
        }
    }

    private static ManagedDeploymentQuery ReadFleetQuery(IQueryCollection values)
    {
        var size = Read("pageSize");
        var state = Read("state");
        var kind = Read("workloadKind");
        var query = new ManagedDeploymentQuery
        {
            PageSize = size is null ? 100 : int.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize)
                ? parsedSize : throw new ArgumentException("Invalid page size."),
            AfterDeploymentId = Read("after"),
            DeploymentIdPrefix = Read("prefix"),
            TenantId = Read("tenant"),
            Provider = Read("provider"),
            Region = Read("region"),
            State = state is null ? null : Enum.TryParse<ManagedDeploymentState>(state, true, out var parsedState)
                ? parsedState : throw new ArgumentException("Invalid deployment state."),
            WorkloadKind = kind is null ? null : Enum.TryParse<ManagedWorkloadKind>(kind, true, out var parsedKind)
                ? parsedKind : throw new ArgumentException("Invalid workload kind."),
        };
        query.Validate();
        return query;

        string? Read(string key)
        {
            if (!values.TryGetValue(key, out var value) || value.Count == 0) return null;
            if (value.Count != 1) throw new ArgumentException("Repeated inventory filters are invalid.");
            return string.IsNullOrEmpty(value[0]) ? null : value[0];
        }
    }

    private static string RenderFleetFilters(ManagedDeploymentQuery query, BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<section class=\"panel fleet-filter-panel\"><div class=\"section-heading\"><div><p class=\"eyebrow\">FLEET SEARCH</p><h2>Filter deployments</h2></div></div><form method=\"get\" class=\"table-tools fleet-filters\" action=\"")
            .Append(E(DashboardPath(options, "deployments"))).Append("\">");
        Input("Deployment ID prefix", "prefix", query.DeploymentIdPrefix);
        Input("Tenant", "tenant", query.TenantId);
        Input("Provider", "provider", query.Provider);
        Input("Region", "region", query.Region);
        Select("State", "state", query.State);
        Select("Workload", "workloadKind", query.WorkloadKind);
        body.Append("<label><span>Page size</span><input type=\"number\" name=\"pageSize\" min=\"1\" max=\"500\" value=\"")
            .Append(query.PageSize.ToString(CultureInfo.InvariantCulture)).Append("\"></label>")
            .Append("<div class=\"filter-actions\"><button class=\"button\" type=\"submit\">Apply filters</button><a href=\"")
            .Append(E(DashboardPath(options, "deployments"))).Append("\">Clear filters</a></div></form></section>");
        return body.ToString();

        void Input(string label, string name, string? value) => body.Append("<label><span>").Append(E(label))
            .Append("</span><input type=\"text\" maxlength=\"128\" name=\"").Append(E(name))
            .Append("\" value=\"").Append(E(value ?? string.Empty)).Append("\"></label>");

        void Select<T>(string label, string name, T? selected) where T : struct, Enum
        {
            body.Append("<label><span>").Append(E(label)).Append("</span><select name=\"").Append(E(name))
                .Append("\"><option value=\"\">All</option>");
            foreach (var value in Enum.GetValues<T>())
            {
                body.Append("<option value=\"").Append(E(value.ToString())).Append('"');
                if (EqualityComparer<T?>.Default.Equals(selected, value)) body.Append(" selected");
                body.Append('>').Append(E(value.ToString())).Append("</option>");
            }
            body.Append("</select></label>");
        }
    }

    private static string RenderFleetPagination(
        ControlPlaneFleetOverview page, ManagedDeploymentQuery query, BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder("<nav aria-label=\"Deployment pages\" class=\"fleet-pagination\">");
        if (query.AfterDeploymentId is not null)
            body.Append("<a href=\"").Append(E(PageUrl(null))).Append("\">First page</a>");
        if (page.NextAfterDeploymentId is not null)
            body.Append("<a rel=\"next\" href=\"").Append(E(PageUrl(page.NextAfterDeploymentId))).Append("\">Next page</a>");
        return body.Append("</nav>").ToString();

        string PageUrl(string? after) => DashboardPath(options, "deployments") + QueryString.Create(
            new Dictionary<string, string?>
            {
                ["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture),
                ["after"] = after,
                ["prefix"] = query.DeploymentIdPrefix,
                ["tenant"] = query.TenantId,
                ["provider"] = query.Provider,
                ["region"] = query.Region,
                ["state"] = query.State?.ToString(),
                ["workloadKind"] = query.WorkloadKind?.ToString(),
            });
    }

    private static string RenderFleet(
        ControlPlaneFleetOverview overview,
        BlueTuskDashboardOptions options,
        bool canMutate,
        bool canAdminister,
        ManagedDeploymentQuery query)
    {
        var healthy = overview.Deployments.Count(IsDeploymentHealthy);
        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Managed deployments", null)))
            .Append(PageHeading(
                "Managed deployments",
                "Fleet state, generation convergence, placement and requested capacity.",
                StatusBadge(
                    overview.Deployments.Count == 0
                        ? "No deployments shown"
                        : healthy == overview.Deployments.Count
                            ? "Page ready"
                            : $"{overview.Deployments.Count - healthy} need attention",
                    overview.Deployments.Count > 0 && healthy == overview.Deployments.Count ? "ok" : "warn")))
            .Append("<div class=\"cards overview-metrics\" aria-label=\"Metrics for this page\">")
            .Append(MetricCard("Deployments", overview.Deployments.Count.ToString(CultureInfo.InvariantCulture), "On this page"))
            .Append(MetricCard("Ready", healthy.ToString(CultureInfo.InvariantCulture), "Converged generations"))
            .Append(MetricCard("Replicas", overview.Deployments.Sum(static deployment => deployment.Replicas).ToString("N0", CultureInfo.InvariantCulture), "Requested workload replicas"))
            .Append(MetricCard("Requested CPU", overview.Deployments.Sum(static deployment => deployment.CpuMillicores).ToString("N0", CultureInfo.InvariantCulture) + "m", "On this page"))
            .Append(MetricCard("Requested memory", Bytes(overview.Deployments.Sum(static deployment => deployment.MemoryBytes)), "On this page"))
            .Append("</div>")
            .Append(RenderFleetFilters(query, options));
        if (overview.Deployments.Count == 0)
        {
            body.Append(EmptyInventory(
                "No deployments match this page",
                "Change the filters or return to the first page."));
            body.Append(RenderFleetPagination(overview, query, options));
            return Layout("Managed deployments", overview.ObservedAt, body.ToString(), options);
        }

        body.Append("<section class=\"panel table-panel fleet-table\" data-filter-panel><div class=\"section-heading\"><div><p class=\"eyebrow\">Fleet inventory</p><h2>Deployment page</h2></div><span class=\"muted\" data-filter-count></span></div>")
            .Append("<div class=\"table-tools\"><label><span>Filter this page</span><input type=\"search\" placeholder=\"Deployment, tenant, region or workload\" data-table-filter></label>")
            .Append("<label><span>Health</span><select data-state-filter><option value=\"\">All states</option><option value=\"healthy\">Healthy</option><option value=\"attention\">Needs attention</option></select></label></div>")
            .Append("<div class=\"table-wrap\" tabindex=\"0\" role=\"region\" aria-label=\"Deployment inventory table\"><table><thead><tr><th>Deployment</th><th>State</th><th>Tenant</th><th>Placement</th>")
            .Append("<th>Generation</th><th>Workloads</th><th>Replicas</th>")
            .Append("<th>Storage</th><th>Protection</th><th>Diagnostic</th>")
            .Append(canMutate ? "<th>Controls</th>" : string.Empty)
            .Append("<th><span class=\"sr-only\">Open</span></th></tr></thead><tbody>");
        foreach (var deployment in overview.Deployments)
        {
            var isHealthy = IsDeploymentHealthy(deployment);
            var (status, tone) = DeploymentStatus(deployment);
            var href = DashboardPath(options, "deployments", deployment.DeploymentId);
            body.Append("<tr data-state=\"").Append(isHealthy ? "healthy" : "attention")
                .Append("\"><td><a class=\"primary-link\" href=\"").Append(E(href)).Append("\">")
                .Append(E(deployment.DeploymentId)).Append("</a></td><td>").Append(StatusBadge(status, tone))
                .Append("</td><td>")
                .Append(E(deployment.TenantId)).Append("</td><td>")
                .Append(E(deployment.Provider)).Append(" / ").Append(E(deployment.Region))
                .Append("</td><td>")
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
                .Append("</td><td>").Append(E(deployment.DiagnosticCode ?? "—")).Append("</td>")
                .Append(canMutate
                    ? "<td>" + DeploymentControls(deployment, canAdminister) + "</td>"
                    : string.Empty)
                .Append("<td><a class=\"row-action\" href=\"").Append(E(href)).Append("\">View →</a></td></tr>");
        }

        body.Append("</tbody></table></div><p class=\"empty-state\" data-filter-empty hidden>No deployments on this page match the current view.</p></section>")
            .Append(RenderFleetPagination(overview, query, options));
        return Layout("Managed deployments", overview.ObservedAt, body.ToString(), options);
    }

}
