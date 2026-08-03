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
        });

        var endpoints = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(15, endpoints.Length);
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
        Assert.Contains(">missing<", html, StringComparison.Ordinal);

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

        var operations = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText == "/operations/api/operations");
        Assert.Contains(
            operations.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == "ops-mutate");
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
                        "primary:fingerprint",
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
                        [],
                        [],
                        [])]));
        }
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
                        "TenantFilter, DeterministicOrdering, BoundedTake")]));
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
