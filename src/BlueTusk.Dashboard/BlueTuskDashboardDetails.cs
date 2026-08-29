using System.Globalization;
using System.Text;
using BlueTusk.ControlPlane;

namespace BlueTusk.Dashboard;

public static partial class BlueTuskDashboardEndpointRouteBuilderExtensions
{
    private static ControlPlaneSourceSnapshot? FindSource(
        ControlPlaneOverview overview,
        string sourceKey)
    {
        var decodedSourceKey = Uri.UnescapeDataString(sourceKey);
        return
        overview.Sources.FirstOrDefault(
            candidate => string.Equals(
                candidate.SourceKey,
                decodedSourceKey,
                StringComparison.Ordinal));
    }

    private static string RenderOverview(
        ControlPlaneOverview sources,
        ControlPlaneSyncOverview sync,
        ControlPlaneLiveOverview live,
        ControlPlaneContinuousGraphOverview graphs,
        ControlPlaneFleetOverview fleet,
        BlueTuskDashboardOptions options)
    {
        var healthySources = sources.Sources.Count(IsSourceHealthy);
        var healthyPipelines = sync.Pipelines.Count(IsPipelineHealthy);
        var healthySubscriptions = live.Subscriptions.Count(IsLiveHealthy);
        var healthyDeployments = fleet.Deployments.Count(IsDeploymentHealthy);
        var attention = BuildAttentionItems(sources, sync, live, fleet, options);
        var overallTone = attention.Any(static item => item.Tone == "critical")
            ? "critical"
            : attention.Count > 0 ? "warn" : "ok";
        var observedAt = new[]
        {
            sources.ObservedAt,
            sync.ObservedAt,
            live.ObservedAt,
            graphs.ObservedAt,
            fleet.ObservedAt,
        }.Max();

        var body = new StringBuilder()
            .Append(Breadcrumbs(options, ("Overview", null)))
            .Append(PageHeading(
                "Operational overview",
                "Everything BlueTusk is running, in one place.",
                StatusBadge(
                    overallTone == "ok" ? "All systems healthy" : $"{attention.Count} need attention",
                    overallTone)))
            .Append("<div class=\"cards cards--wide\">")
            .Append(MetricCard("Sources healthy", $"{healthySources}/{sources.Sources.Count}", "PostgreSQL capture"))
            .Append(MetricCard("Pipelines healthy", $"{healthyPipelines}/{sync.Pipelines.Count}", "Destination delivery"))
            .Append(MetricCard(
                "Connected clients",
                live.Subscriptions.Sum(static item => item.ConnectedClients)
                    .ToString("N0", CultureInfo.InvariantCulture),
                $"{healthySubscriptions}/{live.Subscriptions.Count} subscriptions healthy"))
            .Append(MetricCard("Graph queries", graphs.Queries.Count.ToString("N0", CultureInfo.InvariantCulture), "Continuously maintained"))
            .Append(MetricCard("Deployments ready", $"{healthyDeployments}/{fleet.Deployments.Count}", "Managed fleet"))
            .Append("</div>")
            .Append("<section class=\"panel attention\"><div class=\"section-heading\"><div><p class=\"eyebrow\">Live assessment</p><h2>Needs attention</h2></div>")
            .Append(StatusBadge(attention.Count == 0 ? "Nothing to action" : $"{attention.Count} items", overallTone))
            .Append("</div>");
        if (attention.Count == 0)
        {
            body.Append("<div class=\"empty-state\"><strong>Everything is within its expected operating state.</strong><span>No current diagnostics, stopped workloads, or lag warnings were reported.</span></div>");
        }
        else
        {
            body.Append("<div class=\"attention-list\">");
            foreach (var item in attention)
            {
                body.Append("<a class=\"attention-item\" href=\"").Append(E(item.Href)).Append("\">")
                    .Append(StatusDot(item.Tone))
                    .Append("<span><strong>").Append(E(item.Title)).Append("</strong><small>")
                    .Append(E(item.Detail)).Append("</small></span><span aria-hidden=\"true\">→</span></a>");
            }

            body.Append("</div>");
        }

        body.Append("</section><section><div class=\"section-heading\"><div><p class=\"eyebrow\">Explore</p><h2>Products and infrastructure</h2></div></div><div class=\"product-grid\">")
            .Append(ProductCard(
                options,
                "Sources & Streams",
                "Capture health, WAL lag, relay storage, snapshots, consumer groups and checkpoints.",
                $"{sources.Sources.Count} sources",
                healthySources == sources.Sources.Count ? "ok" : "warn",
                "sources"))
            .Append(ProductCard(
                options,
                "Sync pipelines",
                "Delivery throughput, destination lag, retries, failures and quarantined transactions.",
                $"{sync.Pipelines.Count} pipelines",
                healthyPipelines == sync.Pipelines.Count ? "ok" : "warn",
                "pipelines"))
            .Append(ProductCard(
                options,
                "Live subscriptions",
                "Connected clients, shared-query fan-out, replay, invalidation lag and slow-client handling.",
                $"{live.Subscriptions.Count} subscriptions",
                healthySubscriptions == live.Subscriptions.Count ? "ok" : "warn",
                "live"))
            .Append(ProductCard(
                options,
                "Continuous Graph",
                "Registered graph queries, table impact, limits and available incremental maintenance tiers.",
                $"{graphs.Queries.Count} queries",
                "ok",
                "graphs"))
            .Append(ProductCard(
                options,
                "Managed deployments",
                "Fleet placement, generation convergence, workloads, capacity and protection state.",
                $"{fleet.Deployments.Count} deployments",
                healthyDeployments == fleet.Deployments.Count ? "ok" : "warn",
                "deployments"))
            .Append("</div></section>");

        return Layout("Operational overview", observedAt, body.ToString(), options);
    }

    private static string RenderConsumerGroup(
        DateTimeOffset observedAt,
        ControlPlaneSourceSnapshot source,
        ControlPlaneConsumerGroupSnapshot consumerGroup,
        BlueTuskDashboardOptions options)
    {
        var tone = !consumerGroup.IsActive ? "critical" : consumerGroup.IsLeased ? "ok" : "warn";
        var status = !consumerGroup.IsActive ? "Removed" : consumerGroup.IsLeased ? "Processing" : "Waiting for lease";
        var checkpoint = source.Checkpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.ConsumerGroup, consumerGroup.Name, StringComparison.Ordinal));
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Sources", DashboardPath(options, "sources")),
                (source.InstanceName, DashboardPath(options, "sources", source.SourceKey)),
                (consumerGroup.Name, null)))
            .Append(PageHeading(
                consumerGroup.Name,
                "Consumer group progress, ownership and fencing state.",
                StatusBadge(status, tone)))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Checkpoint", consumerGroup.CheckpointSequence.ToString("N0", CultureInfo.InvariantCulture), "Last acknowledged relay sequence"))
            .Append(MetricCard("Behind relay", Math.Max(0, source.Relay.LastSequence - consumerGroup.CheckpointSequence).ToString("N0", CultureInfo.InvariantCulture), "Transactions awaiting acknowledgement"))
            .Append(MetricCard("Generation", consumerGroup.StoreGeneration.ToString(CultureInfo.InvariantCulture), "Durable store generation"))
            .Append(MetricCard("Fence token", consumerGroup.LastFencingToken.ToString(CultureInfo.InvariantCulture), "Latest ownership token"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Consumer group state",
                ("Source", LinkTo(source.SourceKey, options, "sources", source.SourceKey)),
                ("Start sequence", Number(consumerGroup.StartSequence)),
                ("Checkpoint sequence", Number(consumerGroup.CheckpointSequence)),
                ("Store generation", Number(consumerGroup.StoreGeneration)),
                ("Active", YesNo(consumerGroup.IsActive)),
                ("Lease", consumerGroup.IsLeased ? "Held" : "Available"),
                ("Lease expires", Date(consumerGroup.LeaseExpiresAt)),
                ("Last fencing token", Number(consumerGroup.LastFencingToken)),
                ("Removed", Date(consumerGroup.RemovedAt)),
                ("Retention protected until", Date(consumerGroup.RetentionProtectedUntil))))
            .Append(checkpoint is null
                ? ""
                : RelatedLink(
                    "Direct checkpoint",
                    "Inspect the exact PostgreSQL position and mapping contract.",
                    DashboardPath(options, "sources", source.SourceKey, "checkpoints", checkpoint.ConsumerGroup)));
        return Layout(consumerGroup.Name, observedAt, body.ToString(), options);
    }

    private static string RenderSnapshot(
        DateTimeOffset observedAt,
        ControlPlaneSourceSnapshot source,
        ControlPlaneSnapshotRunSnapshot snapshot,
        BlueTuskDashboardOptions options)
    {
        var tone = string.Equals(snapshot.State, "Complete", StringComparison.OrdinalIgnoreCase)
            ? "ok"
            : string.Equals(snapshot.State, "Failed", StringComparison.OrdinalIgnoreCase)
                ? "critical"
                : "warn";
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Sources", DashboardPath(options, "sources")),
                (source.InstanceName, DashboardPath(options, "sources", source.SourceKey)),
                ("Snapshot " + snapshot.SnapshotEpoch, null)))
            .Append(PageHeading(
                "Snapshot run",
                "Initial data-copy progress for this source epoch.",
                StatusBadge(snapshot.State, tone)))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("State", snapshot.State, "Current snapshot phase"))
            .Append(MetricCard("Progress", Bytes(snapshot.ProgressBytes), "Reported progress payload"))
            .Append(MetricCard("Updated", RelativeAge(observedAt, snapshot.UpdatedAt), "Time since last update"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Snapshot identity",
                ("Epoch", Copyable(snapshot.SnapshotEpoch)),
                ("Source", LinkTo(source.SourceKey, options, "sources", source.SourceKey)),
                ("Database", E(source.DatabaseName)),
                ("Slot", E(source.SlotName)),
                ("State", E(snapshot.State)),
                ("Progress bytes", Number(snapshot.ProgressBytes)),
                ("Updated at", Date(snapshot.UpdatedAt))));
        return Layout("Snapshot " + snapshot.SnapshotEpoch, observedAt, body.ToString(), options);
    }

    private static string RenderCheckpoint(
        DateTimeOffset observedAt,
        ControlPlaneSourceSnapshot source,
        ControlPlaneCheckpointSnapshot checkpoint,
        BlueTuskDashboardOptions options)
    {
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Sources", DashboardPath(options, "sources")),
                (source.InstanceName, DashboardPath(options, "sources", source.SourceKey)),
                (checkpoint.ConsumerGroup + " checkpoint", null)))
            .Append(PageHeading(
                checkpoint.ConsumerGroup + " checkpoint",
                "The durable position BlueTusk will resume from after restart.",
                StatusBadge(checkpoint.IsLeased ? "Lease held" : "Lease available", checkpoint.IsLeased ? "ok" : "warn")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Position", checkpoint.AcknowledgedPosition, "Last safely acknowledged commit"))
            .Append(MetricCard("Generation", checkpoint.StoreGeneration.ToString(CultureInfo.InvariantCulture), "Checkpoint store generation"))
            .Append(MetricCard("Format", "v" + checkpoint.FormatVersion.ToString(CultureInfo.InvariantCulture), "Persisted contract version"))
            .Append(MetricCard("Fence token", checkpoint.LastFencingToken.ToString(CultureInfo.InvariantCulture), "Current ownership boundary"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Checkpoint contract",
                ("Consumer group", LinkTo(checkpoint.ConsumerGroup, options, "sources", source.SourceKey, "consumer-groups", checkpoint.ConsumerGroup)),
                ("Slot", E(checkpoint.SlotName)),
                ("Output plugin", E(checkpoint.OutputPlugin)),
                ("Acknowledged position", Copyable(checkpoint.AcknowledgedPosition)),
                ("Mapping fingerprint", Copyable(checkpoint.MappingFingerprint)),
                ("Format version", Number(checkpoint.FormatVersion)),
                ("Store generation", Number(checkpoint.StoreGeneration)),
                ("Lease", checkpoint.IsLeased ? "Held" : "Available"),
                ("Lease expires", Date(checkpoint.LeaseExpiresAt)),
                ("Last fencing token", Number(checkpoint.LastFencingToken))));
        return Layout(checkpoint.ConsumerGroup + " checkpoint", observedAt, body.ToString(), options);
    }

    private static string RenderPipeline(
        DateTimeOffset observedAt,
        ControlPlaneSyncPipelineSnapshot pipeline,
        BlueTuskDashboardOptions options,
        bool canMutate)
    {
        var (status, tone) = PipelineStatus(pipeline);
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Sync pipelines", DashboardPath(options, "pipelines")),
                (pipeline.PipelineId, null)))
            .Append(PageHeading(
                pipeline.PipelineId,
                "Destination delivery, recovery and checkpoint health.",
                StatusBadge(status, tone)))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Throughput", pipeline.TransactionsPerSecond?.ToString("N1", CultureInfo.InvariantCulture) + " tx/s" ?? "Measuring", "Recent transaction rate"))
            .Append(MetricCard("Checkpoint lag", pipeline.CheckpointLagBytes is { } lag ? Bytes(lag) : "Unknown", pipeline.LagDiagnosticCode ?? "Distance from source head"))
            .Append(MetricCard("Applied", NumberText(pipeline.AppliedTransactions), "Transactions delivered"))
            .Append(MetricCard("Quarantined", NumberText(pipeline.QuarantinedTransactions), "Transactions awaiting operator review"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Pipeline state",
                ("State", E(pipeline.State)),
                ("State changed", Date(pipeline.ChangedAt)),
                ("Source fingerprint", Copyable(pipeline.SourceFingerprint)),
                ("Last commit position", Copyable(pipeline.LastCommitPosition)),
                ("Checkpoint lag", pipeline.CheckpointLagBytes is { } checkpointLag ? Bytes(checkpointLag) : E(pipeline.LagDiagnosticCode ?? "Unavailable")),
                ("Applied transactions", Number(pipeline.AppliedTransactions)),
                ("Transactions per second", pipeline.TransactionsPerSecond?.ToString("N1", CultureInfo.InvariantCulture) ?? "Measuring"),
                ("Snapshot batches", Number(pipeline.AppliedSnapshotBatches)),
                ("Snapshot rows", Number(pipeline.SnapshotRows)),
                ("Snapshot epoch", pipeline.SnapshotEpoch is { } epoch ? Copyable(epoch.ToString("D")) : "—"),
                ("Handoff committed", YesNo(pipeline.HandoffCommitted)),
                ("Retry attempts", Number(pipeline.RetryAttempts)),
                ("Throttle delay", Duration(pipeline.ThrottleDelay)),
                ("Quarantined transactions", Number(pipeline.QuarantinedTransactions)),
                ("Failures", Number(pipeline.FailureCount)),
                ("Diagnostic", E(pipeline.DiagnosticCode ?? pipeline.LagDiagnosticCode ?? "None"))))
            .Append(canMutate
                ? "<section class=\"panel danger-zone\"><p class=\"eyebrow\">Operator controls</p><h2>Recovery actions</h2><p>Every action requires an exact typed confirmation and an audit reason.</p><div class=\"button-row\">" + PipelineControls(pipeline.PipelineId, pipeline.QuarantinedTransactions) + "</div></section>"
                : ReadOnlyNotice());
        return Layout(pipeline.PipelineId, observedAt, body.ToString(), options);
    }

    private static string RenderLiveSubscription(
        DateTimeOffset observedAt,
        ControlPlaneLiveSubscriptionSnapshot subscription,
        BlueTuskDashboardOptions options)
    {
        var (status, tone) = LiveStatus(subscription);
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Live subscriptions", DashboardPath(options, "live")),
                (ShortFingerprint(subscription.QueryPlanFingerprint), null)))
            .Append(PageHeading(
                "Live query " + ShortFingerprint(subscription.QueryPlanFingerprint),
                "Shared-query fan-out, replay, invalidation and client-pressure details.",
                StatusBadge(status, tone)))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Connected clients", NumberText(subscription.ConnectedClients), $"{subscription.SubscriberCount:N0} subscribers"))
            .Append(MetricCard("Fan-out", subscription.FanOutRatio.ToString("N1", CultureInfo.InvariantCulture) + "×", "Deliveries per published result"))
            .Append(MetricCard("Invalidation lag", subscription.InvalidationLag?.ToString("N0", CultureInfo.InvariantCulture) ?? "Unknown", subscription.LagDiagnosticCode ?? "Changes behind head"))
            .Append(MetricCard("Results", NumberText(subscription.ResultCount), $"Limit {subscription.ResultLimit:N0}"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Query identity",
                ("Subscription fingerprint", Copyable(subscription.SubscriptionFingerprint)),
                ("Query plan fingerprint", Copyable(subscription.QueryPlanFingerprint)),
                ("Parameter fingerprint", Copyable(subscription.ParameterFingerprint)),
                ("Security scope", E(subscription.SecurityScopeLabel)),
                ("Authorization policy", E(subscription.AuthorizationPolicyVersion)),
                ("Started", YesNo(subscription.IsStarted)),
                ("Result count / limit", $"{Number(subscription.ResultCount)} / {Number(subscription.ResultLimit)}")))
            .Append(DetailsPanel(
                "Delivery and clients",
                ("Subscribers", Number(subscription.SubscriberCount)),
                ("Connected clients", Number(subscription.ConnectedClients)),
                ("Connection attempts", Number(subscription.ConnectionOpenAttempts)),
                ("Published events", Number(subscription.PublishedEvents)),
                ("Fan-out deliveries", Number(subscription.FanOutDeliveries)),
                ("Fan-out ratio", subscription.FanOutRatio.ToString("N1", CultureInfo.InvariantCulture) + "×"),
                ("Persisted sequence", Number(subscription.PersistedSequence)),
                ("Slow-client disconnects", Number(subscription.SlowClientDisconnects)),
                ("Last disconnect", E(subscription.LastDisconnectCode ?? "None"))))
            .Append(DetailsPanel(
                "Replay and invalidation",
                ("Replay bytes appended", Bytes(subscription.ReplayBytesAppended)),
                ("Replayed events", Number(subscription.ReplayedEvents)),
                ("Resume attempts", Number(subscription.ResumeAttempts)),
                ("Resume rejections", Number(subscription.ResumeRejections)),
                ("Replay rejections", Number(subscription.ReplayRejections)),
                ("Quota rejections", Number(subscription.QuotaRejections)),
                ("Invalidation cursor", Number(subscription.InvalidationCursor)),
                ("Invalidation head", Number(subscription.InvalidationHead)),
                ("Invalidation lag", subscription.InvalidationLag?.ToString("N0", CultureInfo.InvariantCulture) ?? E(subscription.LagDiagnosticCode ?? "Unavailable")),
                ("Authoritative queries", Number(subscription.AuthoritativeQueryCount)),
                ("Coalesced invalidations", Number(subscription.CoalescedInvalidationCount))));
        return Layout("Live query " + ShortFingerprint(subscription.QueryPlanFingerprint), observedAt, body.ToString(), options);
    }

    private static string RenderContinuousGraphQuery(
        DateTimeOffset observedAt,
        ControlPlaneContinuousGraphQuerySnapshot query,
        BlueTuskDashboardOptions options)
    {
        var qualifiedGraph = string.IsNullOrWhiteSpace(query.GraphSchema)
            ? query.GraphName
            : query.GraphSchema + "." + query.GraphName;
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Continuous Graph", DashboardPath(options, "graphs")),
                (query.Name, null)))
            .Append(PageHeading(
                query.Name,
                "Compiled graph impact plan and incremental-maintenance capabilities.",
                StatusBadge("Registered", "ok")))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Graph", qualifiedGraph, "PostgreSQL graph"))
            .Append(MetricCard("Result limit", NumberText(query.MaximumResultCount), "Maximum maintained rows"))
            .Append(MetricCard("Elements", NumberText(query.ElementTableAliases.Count), "Pattern aliases"))
            .Append(MetricCard("Dependencies", NumberText(query.TableDependencies.Count), "Tables that can invalidate the query"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Query identity",
                ("Name", E(query.Name)),
                ("Fingerprint", Copyable(query.QueryFingerprint)),
                ("Database identity", Copyable(query.DatabaseIdentity)),
                ("Graph", E(qualifiedGraph)),
                ("Maximum result count", Number(query.MaximumResultCount))))
            .Append(ListPanel("Element aliases", query.ElementTableAliases))
            .Append(ListPanel("Table dependencies", query.TableDependencies))
            .Append(ListPanel(
                "Maintenance capabilities",
                query.Capabilities.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
            .Append("<section class=\"panel explainer\"><p class=\"eyebrow\">Correctness boundary</p><h2>How this query stays current</h2><ol class=\"steps\"><li><strong>Trusted CDC delta</strong><span>Apply a proven row-level change directly when the projector and change tuple are complete.</span></li><li><strong>Authoritative scoped delta</strong><span>Re-run a prepared, key-scoped GRAPH_TABLE query under the original security scope.</span></li><li><strong>Full repair</strong><span>Re-run PostgreSQL's authoritative query whenever correctness cannot be proven.</span></li></ol></section>");
        return Layout(query.Name, observedAt, body.ToString(), options);
    }

    private static string RenderDeployment(
        DateTimeOffset observedAt,
        ControlPlaneManagedDeploymentSnapshot deployment,
        BlueTuskDashboardOptions options,
        bool canMutate,
        bool canAdminister)
    {
        var (status, tone) = DeploymentStatus(deployment);
        var body = new StringBuilder()
            .Append(Breadcrumbs(
                options,
                ("Managed deployments", DashboardPath(options, "deployments")),
                (deployment.DeploymentId, null)))
            .Append(PageHeading(
                deployment.DeploymentId,
                "Desired state, observed state, placement and requested capacity.",
                StatusBadge(status, tone)))
            .Append("<div class=\"cards\">")
            .Append(MetricCard("Replicas", NumberText(deployment.Replicas), $"Across {deployment.WorkloadCount:N0} workloads"))
            .Append(MetricCard("CPU", deployment.CpuMillicores.ToString("N0", CultureInfo.InvariantCulture) + "m", "Total requested"))
            .Append(MetricCard("Memory", Bytes(deployment.MemoryBytes), "Total requested"))
            .Append(MetricCard("Storage", Bytes(deployment.StorageBytes), "Total requested"))
            .Append("</div>")
            .Append(DetailsPanel(
                "Deployment state",
                ("Deployment ID", Copyable(deployment.DeploymentId)),
                ("Tenant", E(deployment.TenantId)),
                ("Provider", E(deployment.Provider)),
                ("Region", E(deployment.Region)),
                ("State", E(deployment.State.ToString())),
                ("Desired generation", Number(deployment.DesiredGeneration)),
                ("Observed generation", Number(deployment.ObservedGeneration)),
                ("Status revision", Number(deployment.StatusRevision)),
                ("Paused", YesNo(deployment.Paused)),
                ("Delete protection", deployment.DeleteProtection ? "Enabled" : "Disabled"),
                ("Diagnostic", E(deployment.DiagnosticCode ?? "None")),
                ("Updated", Date(deployment.UpdatedAt))))
            .Append(ListPanel(
                "Workloads",
                deployment.WorkloadKinds.Select(static workload => workload.ToString())))
            .Append(canMutate
                ? "<section class=\"panel danger-zone\"><p class=\"eyebrow\">Operator controls</p><h2>Deployment actions</h2><p>Every action requires an exact typed confirmation and an audit reason.</p><div class=\"button-row\">" + DeploymentControls(deployment, canAdminister) + "</div></section>"
                : ReadOnlyNotice());
        return Layout(deployment.DeploymentId, observedAt, body.ToString(), options);
    }

    private static List<AttentionItem> BuildAttentionItems(
        ControlPlaneOverview sources,
        ControlPlaneSyncOverview sync,
        ControlPlaneLiveOverview live,
        ControlPlaneFleetOverview fleet,
        BlueTuskDashboardOptions options)
    {
        var items = new List<AttentionItem>();
        foreach (var source in sources.Sources.Where(source => !IsSourceHealthy(source)))
        {
            var critical = !source.Slot.SourceReachable || !source.Slot.Exists;
            items.Add(new AttentionItem(
                source.InstanceName + " / " + source.SlotName,
                source.Slot.DiagnosticCode ?? (source.Slot.Active ? "WAL lag requires review" : "Replication slot is not active"),
                critical ? "critical" : "warn",
                DashboardPath(options, "sources", source.SourceKey)));
        }

        foreach (var pipeline in sync.Pipelines.Where(pipeline => !IsPipelineHealthy(pipeline)))
        {
            var (_, tone) = PipelineStatus(pipeline);
            items.Add(new AttentionItem(
                pipeline.PipelineId,
                pipeline.DiagnosticCode ?? pipeline.LagDiagnosticCode ??
                    $"{pipeline.QuarantinedTransactions:N0} quarantined transactions",
                tone,
                DashboardPath(options, "pipelines", pipeline.PipelineId)));
        }

        foreach (var subscription in live.Subscriptions.Where(subscription => !IsLiveHealthy(subscription)))
        {
            var (_, tone) = LiveStatus(subscription);
            items.Add(new AttentionItem(
                "Live query " + ShortFingerprint(subscription.QueryPlanFingerprint),
                subscription.LagDiagnosticCode ?? (!subscription.IsStarted
                    ? "Subscription is stopped"
                    : $"Invalidation lag is {subscription.InvalidationLag:N0}"),
                tone,
                DashboardPath(options, "live", subscription.SubscriptionFingerprint)));
        }

        foreach (var deployment in fleet.Deployments.Where(deployment => !IsDeploymentHealthy(deployment)))
        {
            var (_, tone) = DeploymentStatus(deployment);
            items.Add(new AttentionItem(
                deployment.DeploymentId,
                deployment.DiagnosticCode ??
                    $"Observed generation {deployment.ObservedGeneration} of {deployment.DesiredGeneration}",
                tone,
                DashboardPath(options, "deployments", deployment.DeploymentId)));
        }

        return items;
    }

    private static bool IsSourceHealthy(ControlPlaneSourceSnapshot source) =>
        source.Slot.SourceReachable &&
        source.Slot.Exists &&
        source.Slot.Active &&
        string.IsNullOrWhiteSpace(source.Slot.DiagnosticCode);

    private static bool IsPipelineHealthy(ControlPlaneSyncPipelineSnapshot pipeline) =>
        string.Equals(pipeline.State, "Running", StringComparison.OrdinalIgnoreCase) &&
        pipeline.QuarantinedTransactions == 0 &&
        string.IsNullOrWhiteSpace(pipeline.DiagnosticCode) &&
        string.IsNullOrWhiteSpace(pipeline.LagDiagnosticCode);

    private static bool IsLiveHealthy(ControlPlaneLiveSubscriptionSnapshot subscription) =>
        subscription.IsStarted &&
        subscription.InvalidationLag is 0 &&
        string.IsNullOrWhiteSpace(subscription.LagDiagnosticCode);

    private static bool IsDeploymentHealthy(ControlPlaneManagedDeploymentSnapshot deployment) =>
        deployment.State == ManagedDeploymentState.Ready &&
        !deployment.Paused &&
        deployment.ObservedGeneration == deployment.DesiredGeneration &&
        string.IsNullOrWhiteSpace(deployment.DiagnosticCode);

    private static (string Status, string Tone) PipelineStatus(
        ControlPlaneSyncPipelineSnapshot pipeline)
    {
        if (string.Equals(pipeline.State, "Faulted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pipeline.State, "Failed", StringComparison.OrdinalIgnoreCase))
        {
            return ("Action required", "critical");
        }

        return IsPipelineHealthy(pipeline)
            ? ("Running normally", "ok")
            : (pipeline.State, "warn");
    }

    private static (string Status, string Tone) LiveStatus(
        ControlPlaneLiveSubscriptionSnapshot subscription)
    {
        if (!subscription.IsStarted)
        {
            return ("Stopped", "critical");
        }

        return IsLiveHealthy(subscription)
            ? ("Live", "ok")
            : (subscription.LagDiagnosticCode ?? "Catching up", "warn");
    }

    private static (string Status, string Tone) DeploymentStatus(
        ControlPlaneManagedDeploymentSnapshot deployment)
    {
        if (deployment.State is ManagedDeploymentState.Failed or ManagedDeploymentState.Degraded)
        {
            return (deployment.State.ToString(), "critical");
        }

        return IsDeploymentHealthy(deployment)
            ? ("Ready", "ok")
            : (deployment.Paused ? "Paused" : deployment.State.ToString(), "warn");
    }

    private static string Breadcrumbs(
        BlueTuskDashboardOptions options,
        params (string Label, string? Href)[] items)
    {
        var body = new StringBuilder("<nav class=\"breadcrumbs\" aria-label=\"Breadcrumb\"><a href=\"")
            .Append(E(DashboardPath(options, "overview"))).Append("\">Overview</a>");
        foreach (var item in items)
        {
            if (string.Equals(item.Label, "Overview", StringComparison.Ordinal) && item.Href is null)
            {
                continue;
            }

            body.Append("<span aria-hidden=\"true\">/</span>");
            if (item.Href is null)
            {
                body.Append("<span aria-current=\"page\">").Append(E(item.Label)).Append("</span>");
            }
            else
            {
                body.Append("<a href=\"").Append(E(item.Href)).Append("\">")
                    .Append(E(item.Label)).Append("</a>");
            }
        }

        return body.Append("</nav>").ToString();
    }

    private static string PageHeading(string title, string description, string status) =>
        $"<header class=\"page-heading\"><div><p class=\"eyebrow\">BlueTusk control plane</p><h1>{E(title)}</h1><p>{E(description)}</p></div><div class=\"page-actions\">{status}<button class=\"secondary\" type=\"button\" data-refresh>Refresh</button></div></header>";

    private static string MetricCard(string label, string value, string detail) =>
        $"<div class=\"card\"><span>{E(label)}</span><strong>{E(value)}</strong><small>{E(detail)}</small></div>";

    private static string ProductCard(
        BlueTuskDashboardOptions options,
        string title,
        string description,
        string count,
        string tone,
        params string[] path) =>
        $"<a class=\"product-card\" href=\"{E(DashboardPath(options, path))}\"><span class=\"product-icon\" data-tone=\"{tone}\" aria-hidden=\"true\"></span><span><strong>{E(title)}</strong><small>{E(description)}</small></span><span class=\"product-count\">{E(count)} →</span></a>";

    private static string DetailsPanel(string title, params (string Label, string Value)[] rows)
    {
        var body = new StringBuilder("<section class=\"panel\"><div class=\"section-heading\"><h2>")
            .Append(E(title)).Append("</h2></div><dl class=\"detail-grid\">");
        foreach (var row in rows)
        {
            body.Append("<div><dt>").Append(E(row.Label)).Append("</dt><dd>")
                .Append(row.Value).Append("</dd></div>");
        }

        return body.Append("</dl></section>").ToString();
    }

    private static string ListPanel(string title, IEnumerable<string> values)
    {
        var items = values.ToArray();
        var body = new StringBuilder("<section class=\"panel\"><div class=\"section-heading\"><h2>")
            .Append(E(title)).Append("</h2><span class=\"muted\">")
            .Append(items.Length.ToString(CultureInfo.InvariantCulture)).Append(" items</span></div><div class=\"tag-list\">");
        foreach (var item in items)
        {
            body.Append("<code>").Append(E(item)).Append("</code>");
        }

        return body.Append("</div></section>").ToString();
    }

    private static string RelatedLink(string title, string description, string href) =>
        $"<a class=\"related-link panel\" href=\"{E(href)}\"><span><strong>{E(title)}</strong><small>{E(description)}</small></span><span aria-hidden=\"true\">→</span></a>";

    private static string ReadOnlyNotice() =>
        "<section class=\"panel read-only\"><p class=\"eyebrow\">Viewer access</p><h2>Read-only</h2><p>Your role can inspect this resource but cannot run control-plane operations.</p></section>";

    private static string StatusBadge(string label, string tone) =>
        $"<span class=\"status-badge\" data-tone=\"{tone}\">{StatusDot(tone)}{E(label)}</span>";

    private static string StatusDot(string tone) =>
        $"<span class=\"status-dot\" data-tone=\"{tone}\" aria-hidden=\"true\"></span>";

    private static string Copyable(string value) =>
        $"<span class=\"copy-value\"><code>{E(value)}</code><button class=\"copy-button\" type=\"button\" data-copy=\"{E(value)}\">Copy</button></span>";

    private static string LinkTo(
        string label,
        BlueTuskDashboardOptions options,
        params string[] path) =>
        $"<a href=\"{E(DashboardPath(options, path))}\">{E(label)}</a>";

    private static string DashboardPath(
        BlueTuskDashboardOptions options,
        params string[] segments) =>
        options.RoutePrefix + "/" + string.Join('/', segments.Select(Uri.EscapeDataString));

    private static string Number(long value) =>
        E(value.ToString("N0", CultureInfo.InvariantCulture));

    private static string NumberText(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string Date(DateTimeOffset? value) => value is null
        ? "—"
        : $"<time datetime=\"{E(value.Value.ToString("O", CultureInfo.InvariantCulture))}\">{E(value.Value.ToString("dd MMM yyyy, HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}</time>";

    private static string Duration(TimeSpan value) =>
        E(value.TotalMilliseconds < 1000
            ? value.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms"
            : value.TotalSeconds.ToString("N1", CultureInfo.InvariantCulture) + " s");

    private static string RelativeAge(DateTimeOffset observedAt, DateTimeOffset value)
    {
        var age = observedAt - value;
        if (age < TimeSpan.Zero)
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return age.TotalSeconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return age.TotalMinutes.ToString("N0", CultureInfo.InvariantCulture) + " minutes ago";
        }

        return age.TotalHours.ToString("N1", CultureInfo.InvariantCulture) + " hours ago";
    }

    private sealed record AttentionItem(string Title, string Detail, string Tone, string Href);
}
