using System.Security.Claims;
using BlueTusk.Applications.Hosting;
using BlueTusk.ServiceTopology.Api;
using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddProductionHosting("service-topology", "topology");
var connectionString = builder.Configuration.RequiredConnectionString();
builder.Services.AddTopologyInfrastructure(connectionString);
builder.Services.AddApplicationLive(
    builder.Configuration,
    connectionString,
    new ApplicationLiveOptions(
        "topology-live",
        "service-topology",
        "topology",
        "services",
        ["Id", "TenantId", "Name", "Health", "Version", "UpdatedAt"]));
var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<TopologyDbContext>();
    await database.Database.MigrateAsync();
    return;
}

app.UseProductionHosting();
app.MapBffSessionEndpoints();
app.MapApplicationLive();
var topology = app.MapGroup("/api/v1/topology").RequireAuthorization("Viewer");
topology.MapGet("/services", async (
    ClaimsPrincipal principal,
    ITopologyRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListServicesAsync(RequireTenant(principal), cancellationToken)));
topology.MapPost("/services", async (
    RegisterServiceRequest request,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
{
    var created = await service.RegisterAsync(RequireTenant(principal), request.Name, cancellationToken);
    return Results.Created($"/api/v1/topology/services/{created.Id}", created);
}).RequireBffMutation();
topology.MapPost("/dependencies", async (
    ConnectServicesRequest request,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
{
    var dependency = await service.ConnectAsync(
        RequireTenant(principal),
        request.SourceId,
        request.DestinationId,
        cancellationToken);
    return Results.Created($"/api/v1/topology/dependencies/{dependency.Id}", dependency);
}).RequireBffMutation();
topology.MapGet("/dependencies", async (
    ClaimsPrincipal principal,
    ITopologyRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListDependenciesAsync(RequireTenant(principal), cancellationToken)));
topology.MapGet("/incidents", async (
    ClaimsPrincipal principal,
    ITopologyRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListIncidentsAsync(RequireTenant(principal), cancellationToken)));
topology.MapPost("/services/{serviceId:guid}/incidents", async (
    Guid serviceId,
    OpenIncidentRequest request,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
{
    var incident = await service.OpenIncidentAsync(
        RequireTenant(principal), serviceId, request.Summary, cancellationToken);
    return Results.Created($"/api/v1/topology/incidents/{incident.Id}", incident);
}).RequireBffMutation();
topology.MapGet("/services/{serviceId:guid}/blast-radius", async (
    Guid serviceId,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.BlastRadiusAsync(
        RequireTenant(principal), serviceId, cancellationToken)));
topology.MapGet("/paths", async (
    Guid sourceId,
    Guid destinationId,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.FindPathAsync(
        RequireTenant(principal), sourceId, destinationId, cancellationToken)));
topology.MapPost("/services/{serviceId:guid}/health", async (
    Guid serviceId,
    ReportHealthRequest request,
    ClaimsPrincipal principal,
    TopologyService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.ReportHealthAsync(
        RequireTenant(principal),
        serviceId,
        request.Health,
        request.ExpectedVersion,
        cancellationToken))).RequireBffMutation();

await app.RunAsync();

static string RequireTenant(ClaimsPrincipal principal) =>
    principal.FindFirstValue("tenant_id") ??
    throw new UnauthorizedAccessException("The authenticated session has no tenant scope.");

public partial class Program;
