using System.Security.Claims;
using BlueTusk.Applications.Hosting;
using BlueTusk.OrderOperations.Api;
using BlueTusk.OrderOperations.Application;
using BlueTusk.OrderOperations.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddProductionHosting("order-operations", "orders");
var connectionString = builder.Configuration.RequiredConnectionString();
builder.Services.AddOrderInfrastructure(connectionString);
builder.Services.AddApplicationLive(
    builder.Configuration,
    connectionString,
    new ApplicationLiveOptions(
        "orders-live",
        "order-operations",
        "orders",
        "fulfilment_orders",
        ["Id", "TenantId", "CustomerReference", "State", "Version", "UpdatedAt"]));

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
    await database.Database.MigrateAsync();
    return;
}

app.UseProductionHosting();
app.MapBffSessionEndpoints();
app.MapApplicationLive();

var orders = app.MapGroup("/api/v1/orders").RequireAuthorization("Viewer");
orders.MapGet("/", async (
    ClaimsPrincipal principal,
    string? query,
    IOrderRepository repository,
    CancellationToken cancellationToken) =>
{
    var tenantId = RequireTenant(principal);
    return Results.Ok(await repository.SearchAsync(tenantId, query, cancellationToken));
});
orders.MapGet("/{orderId:guid}", async (
    Guid orderId,
    ClaimsPrincipal principal,
    IOrderRepository repository,
    CancellationToken cancellationToken) =>
{
    var order = await repository.FindAsync(
        RequireTenant(principal),
        orderId,
        cancellationToken);
    return order is null ? Results.NotFound() : Results.Ok(order);
});
orders.MapGet("/{orderId:guid}/timeline", async (
    Guid orderId,
    ClaimsPrincipal principal,
    IOrderRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.TimelineAsync(
        RequireTenant(principal),
        orderId,
        cancellationToken)));
orders.MapPost("/", async (
    CreateOrderRequest request,
    HttpContext context,
    OrderService service,
    CancellationToken cancellationToken) =>
{
    var created = await service.CreateAsync(
        RequireTenant(context.User),
        request.CustomerReference,
        RequireIdempotencyKey(context.Request),
        cancellationToken);
    return Results.Created($"/api/v1/orders/{created.Id}", created);
}).RequireBffMutation();
orders.MapPost("/{orderId:guid}/{transition}", async (
    Guid orderId,
    string transition,
    TransitionOrderRequest request,
    HttpContext context,
    OrderService service,
    CancellationToken cancellationToken) =>
{
    var changed = await service.TransitionAsync(
        RequireTenant(context.User),
        orderId,
        transition,
        request.ExpectedVersion,
        request.AllocationReference,
        RequireIdempotencyKey(context.Request),
        cancellationToken);
    return Results.Ok(changed);
}).RequireBffMutation();

await app.RunAsync();

static string RequireTenant(ClaimsPrincipal principal) =>
    principal.FindFirstValue("tenant_id") ??
    throw new UnauthorizedAccessException("The authenticated session has no tenant scope.");

static string RequireIdempotencyKey(HttpRequest request)
{
    var value = request.Headers["Idempotency-Key"].ToString();
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    return value;
}

public partial class Program;
