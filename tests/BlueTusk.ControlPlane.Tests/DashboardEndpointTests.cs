using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BlueTusk.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.ControlPlane.Tests;

public sealed class DashboardEndpointTests
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Dashboard_maps_authorized_pages_and_HTML_encodes_inventory_values()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var audit = new RecordingAuditStore();
        var handler = new RecordingOperationHandler();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IControlPlaneQueryService>(new FakeQueryService());
        builder.Services.AddSingleton<IControlPlaneSyncQueryService>(new FakeSyncQueryService());
        builder.Services.AddSingleton<IControlPlaneLiveQueryService>(new FakeLiveQueryService());
        builder.Services.AddSingleton<IControlPlaneContinuousGraphQueryService>(
            new FakeContinuousGraphQueryService());
        builder.Services.AddSingleton<IControlPlaneContinuousGraphExecutionService>(
            new FakeContinuousGraphExecutionService());
        builder.Services.AddSingleton<IControlPlaneFleetQueryService>(
            new FakeFleetQueryService());
        builder.Services.AddSingleton(
            new ControlPlaneOperationExecutor(
                new RoleControlPlaneAuthorizer(),
                audit,
                handler));
        await using var application = builder.Build();
        application.MapBlueTuskDashboard(options =>
        {
            options.RoutePrefix = "/operations";
            options.ReadAuthorizationPolicy = "ops-read";
            options.MutationAuthorizationPolicy = "ops-mutate";
            options.GraphExecutionAuthorizationPolicy = "ops-graph-execute";
        });

        var endpoints = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(34, endpoints.Length);
        Assert.All(
            endpoints,
            endpoint => Assert.Contains(
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                metadata => metadata.Policy == "ops-read"));
        var sources = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/sources");
        var context = new DefaultHttpContext
        {
            RequestServices = application.Services,
            Response = { Body = new MemoryStream() },
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator@example.invalid"),
                 new Claim(ClaimTypes.Role, "BlueTuskOperator")],
                "test")),
        };

        await sources.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td><script>", html, StringComparison.Ordinal);
        Assert.Contains(">Missing<", html, StringComparison.Ordinal);

        var overview = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/overview");
        var overviewHtml = await InvokeHtmlAsync(
            overview,
            application.Services,
            context.User);
        Assert.Contains("Operational overview", overviewHtml, StringComparison.Ordinal);
        Assert.Contains("Needs attention", overviewHtml, StringComparison.Ordinal);

        var sourceDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/sources/{sourceKey}");
        var sourceHtml = await InvokeHtmlAsync(
            sourceDetail,
            application.Services,
            context.User,
            new RouteValueDictionary { ["sourceKey"] = "primary%2Ffingerprint" });
        Assert.Contains("Source identity", sourceHtml, StringComparison.Ordinal);
        Assert.Contains("Replication slot", sourceHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", sourceHtml, StringComparison.Ordinal);

        var groupDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/sources/{sourceKey}/consumer-groups/{groupName}");
        var groupHtml = await InvokeHtmlAsync(
            groupDetail,
            application.Services,
            context.User,
            new RouteValueDictionary
            {
                ["sourceKey"] = "primary%2Ffingerprint",
                ["groupName"] = "group<script>",
            });
        Assert.Contains("group&lt;script&gt;", groupHtml, StringComparison.Ordinal);
        Assert.Contains("Consumer group state", groupHtml, StringComparison.Ordinal);

        var snapshotDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/sources/{sourceKey}/snapshots/{snapshotEpoch}");
        var snapshotHtml = await InvokeHtmlAsync(
            snapshotDetail,
            application.Services,
            context.User,
            new RouteValueDictionary
            {
                ["sourceKey"] = "primary%2Ffingerprint",
                ["snapshotEpoch"] = "snapshot<img>",
            });
        Assert.Contains("snapshot&lt;img&gt;", snapshotHtml, StringComparison.Ordinal);
        Assert.Contains("Snapshot identity", snapshotHtml, StringComparison.Ordinal);

        var checkpointDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/sources/{sourceKey}/checkpoints/{consumerGroup}");
        var checkpointHtml = await InvokeHtmlAsync(
            checkpointDetail,
            application.Services,
            context.User,
            new RouteValueDictionary
            {
                ["sourceKey"] = "primary%2Ffingerprint",
                ["consumerGroup"] = "group<script>",
            });
        Assert.Contains("Checkpoint contract", checkpointHtml, StringComparison.Ordinal);
        Assert.Contains("group&lt;script&gt; checkpoint", checkpointHtml, StringComparison.Ordinal);

        var pipelines = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/pipelines");
        context.Response.Body = new MemoryStream();
        await pipelines.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var pipelineReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var pipelineHtml = await pipelineReader.ReadToEndAsync();
        Assert.Contains("&lt;img src=x&gt;", pipelineHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<img src=x>", pipelineHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", pipelineHtml, StringComparison.Ordinal);
        Assert.Contains("data-operation-name=\"RebuildPipeline\"", pipelineHtml, StringComparison.Ordinal);
        Assert.Contains("data-operation-name=\"ReplayQuarantine\"", pipelineHtml, StringComparison.Ordinal);

        var pipelineDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/pipelines/{pipelineId}");
        var pipelineDetailHtml = await InvokeHtmlAsync(
            pipelineDetail,
            application.Services,
            context.User,
            new RouteValueDictionary { ["pipelineId"] = "<img src=x>" });
        Assert.Contains("Pipeline state", pipelineDetailHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x&gt;", pipelineDetailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", pipelineDetailHtml, StringComparison.OrdinalIgnoreCase);

        var live = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/live");
        context.Response.Body = new MemoryStream();
        await live.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var liveReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var liveHtml = await liveReader.ReadToEndAsync();
        Assert.Contains("&lt;tenant&gt;", liveHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<tenant>", liveHtml, StringComparison.Ordinal);
        Assert.Contains("slow-client-reset", liveHtml, StringComparison.Ordinal);

        var liveDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/live/{subscriptionFingerprint}");
        var liveDetailHtml = await InvokeHtmlAsync(
            liveDetail,
            application.Services,
            context.User,
            new RouteValueDictionary { ["subscriptionFingerprint"] = new string('c', 64) });
        Assert.Contains("Delivery and clients", liveDetailHtml, StringComparison.Ordinal);
        Assert.Contains("Replay and invalidation", liveDetailHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;tenant&gt;", liveDetailHtml, StringComparison.Ordinal);

        var graphs = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/graphs");
        context.Response.Body = new MemoryStream();
        await graphs.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var graphReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var graphHtml = await graphReader.ReadToEndAsync();
        Assert.Contains("&lt;graph&gt;", graphHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<graph>", graphHtml, StringComparison.Ordinal);
        Assert.Contains("risk.&lt;transfers&gt;", graphHtml, StringComparison.Ordinal);

        var graphDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/graphs/{queryFingerprint}");
        var graphDetailHtml = await InvokeHtmlAsync(
            graphDetail,
            application.Services,
            context.User,
            new RouteValueDictionary { ["queryFingerprint"] = new string('d', 64) });
        Assert.Contains("How this query stays current", graphDetailHtml, StringComparison.Ordinal);
        Assert.Contains("Authoritative scoped delta", graphDetailHtml, StringComparison.Ordinal);
        Assert.Contains("risk.&lt;transfers&gt;", graphDetailHtml, StringComparison.Ordinal);
        Assert.Contains("Run and inspect the complete result", graphDetailHtml, StringComparison.Ordinal);
        Assert.Contains("data-graph-canvas", graphDetailHtml, StringComparison.Ordinal);
        Assert.Contains("All nodes", graphDetailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-value", graphDetailHtml, StringComparison.Ordinal);

        var graphExecution = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/api/v1/graphs/{queryFingerprint}/run");
        Assert.Contains(
            graphExecution.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "ops-graph-execute");
        var graphRequest = new ControlPlaneContinuousGraphRunRequest(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["minimumRisk"] = "0.80",
            });
        var graphRequestBody = JsonSerializer.SerializeToUtf8Bytes(graphRequest, WebJson);
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = graphRequestBody.Length;
        context.Request.Body = new MemoryStream(graphRequestBody);
        context.Request.RouteValues = new RouteValueDictionary
        {
            ["queryFingerprint"] = new string('d', 64),
        };
        context.Response.Body = new MemoryStream();
        await graphExecution.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var graphResponse = await JsonSerializer.DeserializeAsync<
            ControlPlaneApiResponse<ControlPlaneContinuousGraphRunResult>>(
                context.Response.Body,
                WebJson);
        Assert.NotNull(graphResponse);
        Assert.Equal(ControlPlaneApiContract.CurrentVersion, graphResponse.ContractVersion);
        Assert.Equal("account:1", Assert.Single(graphResponse.Data.Nodes).Id);
        Assert.Equal("transfer:1", Assert.Single(graphResponse.Data.Edges).Id);

        var deployments = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/deployments");
        context.Response.Body = new MemoryStream();
        await deployments.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var fleetReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var fleetHtml = await fleetReader.ReadToEndAsync();
        Assert.Contains("&lt;deployment&gt;", fleetHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<deployment>", fleetHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", fleetHtml, StringComparison.Ordinal);
        Assert.Contains("data-operation-name=\"ReconcileDeployment\"", fleetHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-operation-name=\"DeleteDeployment\"", fleetHtml, StringComparison.Ordinal);

        var deploymentDetail = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/operations/deployments/{deploymentId}");
        var deploymentDetailHtml = await InvokeHtmlAsync(
            deploymentDetail,
            application.Services,
            context.User,
            new RouteValueDictionary { ["deploymentId"] = "<deployment>" });
        Assert.Contains("Deployment state", deploymentDetailHtml, StringComparison.Ordinal);
        Assert.Contains("&lt;deployment&gt;", deploymentDetailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-name", deploymentDetailHtml, StringComparison.Ordinal);

        var legacyOperations = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/api/operations");
        var operations = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/api/v1/operations");
        Assert.Contains(
            legacyOperations.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "ops-mutate");
        Assert.Contains(
            operations.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "ops-mutate");

        var capabilities = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/api/capabilities");
        context.Response.Body = new MemoryStream();
        await capabilities.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var capabilityResponse =
            await JsonSerializer.DeserializeAsync<ControlPlaneApiCapabilities>(
                context.Response.Body,
                WebJson);
        Assert.NotNull(capabilityResponse);
        Assert.Equal(ControlPlaneApiContract.CurrentVersion, capabilityResponse.CurrentVersion);
        Assert.Equal(
            [ControlPlaneApiContract.CurrentVersion],
            capabilityResponse.SupportedVersions);

        var versionedOverview = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/api/v1/overview");
        context.Response.Body = new MemoryStream();
        await versionedOverview.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        var overviewResponse =
            await JsonSerializer.DeserializeAsync<ControlPlaneApiResponse<ControlPlaneOverview>>(
                context.Response.Body,
                WebJson);
        Assert.NotNull(overviewResponse);
        Assert.Equal(ControlPlaneApiContract.CurrentVersion, overviewResponse.ContractVersion);
        Assert.Single(overviewResponse.Data.Sources);

        var request = new ControlPlaneOperationRequest(
            Guid.NewGuid(),
            ControlPlaneOperationKind.ReconcilePipeline,
            "pipeline:search",
            "ReconcilePipeline:pipeline:search",
            "Operator acceptance test");
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/json";
        context.Request.Headers["X-BlueTusk-Operation-Id"] = Guid.NewGuid().ToString("D");
        var requestBody = JsonSerializer.SerializeToUtf8Bytes(request);
        context.Request.ContentLength = requestBody.Length;
        context.Request.Body = new MemoryStream(requestBody);
        context.Response.Body = new MemoryStream();

        await operations.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal(0, handler.ExecutionCount);

        context.Request.Headers["X-BlueTusk-Operation-Id"] = request.OperationId.ToString("D");
        context.Request.Body = new MemoryStream(requestBody);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Body = new MemoryStream();

        await operations.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var operationReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var operationResponse = await operationReader.ReadToEndAsync();
        Assert.True(
            context.Response.StatusCode == StatusCodes.Status200OK,
            $"Expected operation success but received {context.Response.StatusCode}: {operationResponse}");
        using (var responseJson = JsonDocument.Parse(operationResponse))
        {
            Assert.Equal(
                ControlPlaneApiContract.CurrentVersion,
                responseJson.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(
                request.OperationId,
                responseJson.RootElement.GetProperty("data").GetProperty("operationId").GetGuid());
        }
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal(
            [ControlPlaneAuditStatus.Requested, ControlPlaneAuditStatus.Succeeded],
            audit.Records.Select(static record => record.Status));

        handler.Failure = new InvalidOperationException("sensitive destination detail");
        var failedRequest = request with
        {
            OperationId = Guid.NewGuid(),
            Kind = ControlPlaneOperationKind.RetryPipeline,
            Confirmation = "RetryPipeline:pipeline:search",
        };
        var failedBody = JsonSerializer.SerializeToUtf8Bytes(failedRequest);
        context.Request.ContentLength = failedBody.Length;
        context.Request.Headers["X-BlueTusk-Operation-Id"] = failedRequest.OperationId.ToString("D");
        context.Request.Body = new MemoryStream(failedBody);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Body = new MemoryStream();

        await operations.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var failureReader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var failureResponse = await failureReader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.DoesNotContain("sensitive", failureResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operation-failed", failureResponse, StringComparison.Ordinal);
    }

    private sealed class FakeQueryService : IControlPlaneQueryService
    {
        public ValueTask<ControlPlaneOverview> GetOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    [new ControlPlaneSourceSnapshot(
                        "primary/fingerprint",
                        "<script>",
                        "fingerprint",
                        "system",
                        "app",
                        "orders_slot",
                        "publication",
                        1,
                        0,
                        "0/0",
                        new ControlPlaneSlotSnapshot(
                            SourceReachable: true,
                            Exists: false,
                            Active: false,
                            OutputPlugin: null,
                            RestartPosition: null,
                            ConfirmedFlushPosition: null,
                            WalStatus: null,
                            WalLagBytes: 0,
                            DiagnosticCode: "slot-missing"),
                        new ControlPlaneRelaySnapshot(0, 0, 0, 0, 0, TimeSpan.Zero),
                        [new ControlPlaneConsumerGroupSnapshot(
                            "group<script>",
                            0,
                            0,
                            1,
                            true,
                            true,
                            new DateTimeOffset(2026, 8, 3, 16, 1, 0, TimeSpan.Zero),
                            2,
                            null,
                            null)],
                        [new ControlPlaneSnapshotRunSnapshot(
                            "snapshot<img>",
                            "Complete",
                            128,
                            new DateTimeOffset(2026, 8, 3, 15, 59, 0, TimeSpan.Zero))],
                        [new ControlPlaneCheckpointSnapshot(
                            "group<script>",
                            2,
                            "orders_slot",
                            "pgoutput",
                            "mapping<script>",
                            "0/0",
                            1,
                            true,
                            new DateTimeOffset(2026, 8, 3, 16, 1, 0, TimeSpan.Zero),
                            2)])]));
        }
    }

    private static async Task<string> InvokeHtmlAsync(
        RouteEndpoint endpoint,
        IServiceProvider services,
        ClaimsPrincipal user,
        RouteValueDictionary? routeValues = null)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
            User = user,
        };
        context.Request.RouteValues = routeValues ?? new RouteValueDictionary();
        await endpoint.RequestDelegate!(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeSyncQueryService : IControlPlaneSyncQueryService
    {
        public ValueTask<ControlPlaneSyncOverview> GetSyncOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneSyncOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    [new ControlPlaneSyncPipelineSnapshot(
                        "<img src=x>",
                        "fingerprint",
                        "Faulted",
                        new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                        10,
                        2.5,
                        1,
                        100,
                        1,
                        1,
                        2,
                        TimeSpan.FromSeconds(1),
                        "0/80",
                        128,
                        null,
                        null,
                        false,
                        "pipeline-fault")]));
        }
    }

    private sealed class FakeLiveQueryService : IControlPlaneLiveQueryService
    {
        public ValueTask<ControlPlaneLiveOverview> GetLiveOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneLiveOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    new ControlPlaneLiveRegistrySnapshot(1, 100, 0),
                    [new ControlPlaneLiveSubscriptionSnapshot(
                        new string('c', 64),
                        new string('a', 64),
                        new string('b', 64),
                        "<tenant>",
                        "policy:v1",
                        10,
                        true,
                        3,
                        2.5,
                        2,
                        5,
                        2,
                        2048,
                        2,
                        4,
                        4,
                        1,
                        0,
                        0,
                        0,
                        1,
                        "slow-client-reset",
                        10,
                        12,
                        2,
                        null,
                        3,
                        2,
                        5)]));
        }
    }

    private sealed class FakeContinuousGraphQueryService :
        IControlPlaneContinuousGraphQueryService
    {
        public ValueTask<ControlPlaneContinuousGraphOverview> GetContinuousGraphOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneContinuousGraphOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    [new ControlPlaneContinuousGraphQuerySnapshot(
                        "<graph>",
                        "risk-primary",
                        new string('d', 64),
                        "<transfers>",
                        "risk",
                        ["accounts", "<edges>"],
                        ["risk.accounts", "risk.<transfers>"],
                        100,
                        "TenantFilter, DeterministicOrdering, BoundedTake",
                        [new ControlPlaneContinuousGraphParameterSnapshot(
                            "minimumRisk", "Decimal", false, true, "0.72"),
                         new ControlPlaneContinuousGraphParameterSnapshot(
                             "tenantId", "String", false, false, null)],
                        true)]));
        }
    }

    private sealed class FakeContinuousGraphExecutionService :
        IControlPlaneContinuousGraphExecutionService
    {
        public ValueTask<ControlPlaneContinuousGraphRunResult> ExecuteAsync(
            string queryFingerprint,
            ControlPlaneActor actor,
            ControlPlaneContinuousGraphRunRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(new string('d', 64), queryFingerprint);
            Assert.Equal("operator@example.invalid", actor.ActorId);
            Assert.Equal("0.80", request.Parameters["minimumRisk"]);
            return ValueTask.FromResult(new ControlPlaneContinuousGraphRunResult(
                Guid.Parse("5fa80f3e-b759-4c89-bc70-77c70812ff68"),
                new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero),
                TimeSpan.FromMilliseconds(12.4),
                queryFingerprint,
                "risk-query",
                "risk-primary",
                "transfers",
                "risk",
                1,
                [new ControlPlaneContinuousGraphNode(
                    "account:1",
                    "Account <script>",
                    "Account",
                    [new ControlPlaneContinuousGraphProperty("risk", "0.91")])],
                [new ControlPlaneContinuousGraphEdge(
                    "transfer:1",
                    "account:1",
                    "account:1",
                    "TRANSFERRED_TO",
                    "Transfer",
                    true,
                    [new ControlPlaneContinuousGraphProperty("amount", "100")])],
                [new ControlPlaneContinuousGraphComposition("Account", 1)],
                [new ControlPlaneContinuousGraphComposition("Transfer", 1)]));
        }
    }

    private sealed class FakeFleetQueryService : IControlPlaneFleetQueryService
    {
        public ValueTask<ControlPlaneFleetOverview> GetFleetOverviewAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ControlPlaneFleetOverview(
                    new DateTimeOffset(2026, 8, 3, 16, 0, 0, TimeSpan.Zero),
                    [new ControlPlaneManagedDeploymentSnapshot(
                        "<deployment>",
                        "tenant-a",
                        "kubernetes",
                        "uk-south",
                        2,
                        2,
                        4,
                        ManagedDeploymentState.Ready,
                        false,
                        true,
                        2,
                        [ManagedWorkloadKind.Streams, ManagedWorkloadKind.Sync],
                        4,
                        2000,
                        4L * 1024 * 1024 * 1024,
                        20L * 1024 * 1024 * 1024,
                        null,
                        new DateTimeOffset(2026, 8, 3, 15, 59, 0, TimeSpan.Zero))]));
        }
    }

    private sealed class RecordingAuditStore : IControlPlaneAuditStore
    {
        public List<ControlPlaneAuditRecord> Records { get; } = [];

        public ValueTask AppendAsync(
            ControlPlaneAuditRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOperationHandler : IControlPlaneOperationHandler
    {
        public int ExecutionCount { get; private set; }

        public Exception? Failure { get; set; }

        public ValueTask ExecuteAsync(
            ControlPlaneOperationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.CompletedTask;
        }
    }
}
