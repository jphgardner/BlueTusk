using System.Text;
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
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IControlPlaneQueryService>(new FakeQueryService());
        await using var application = builder.Build();
        application.MapBlueTuskDashboard(options =>
        {
            options.RoutePrefix = "/operations";
            options.ReadAuthorizationPolicy = "ops-read";
        });

        var endpoints = ((IEndpointRouteBuilder)application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.Equal(7, endpoints.Length);
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
        };

        await sources.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var html = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains(">missing<", html, StringComparison.Ordinal);
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
}
