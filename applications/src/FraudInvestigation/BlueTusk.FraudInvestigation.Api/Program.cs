using System.Security.Claims;
using BlueTusk.Applications.Hosting;
using BlueTusk.FraudInvestigation.Api;
using BlueTusk.FraudInvestigation.Application;
using BlueTusk.FraudInvestigation.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddProductionHosting("fraud-investigation", "fraud");
var connectionString = builder.Configuration.RequiredConnectionString();
builder.Services.AddFraudInfrastructure(connectionString);
builder.Services.AddApplicationLive(
    builder.Configuration,
    connectionString,
    new ApplicationLiveOptions(
        "fraud-live",
        "fraud-investigation",
        "fraud",
        "investigation_cases",
        ["Id", "TenantId", "Reason", "Assignee", "Decision", "Version", "OpenedAt"]));
var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
    await database.Database.MigrateAsync();
    return;
}

app.UseProductionHosting();
app.MapBffSessionEndpoints();
app.MapApplicationLive();
var fraud = app.MapGroup("/api/v1/fraud").RequireAuthorization("Viewer");
fraud.MapGet("/accounts", async (
    ClaimsPrincipal principal,
    IFraudRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAccountsAsync(RequireTenant(principal), cancellationToken)));
fraud.MapPost("/accounts", async (
    RegisterAccountRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
{
    var account = await service.RegisterAccountAsync(
        RequireTenant(principal), request.DisplayName, cancellationToken);
    return Results.Created($"/api/v1/fraud/accounts/{account.Id}", account);
}).RequireBffMutation();
fraud.MapPost("/transfers", async (
    RecordTransferRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
{
    var transfer = await service.RecordTransferAsync(
        RequireTenant(principal),
        request.SourceId,
        request.DestinationId,
        request.Amount,
        request.Currency,
        cancellationToken);
    return Results.Created($"/api/v1/fraud/transfers/{transfer.Id}", transfer);
}).RequireBffMutation();
fraud.MapGet("/transfers", async (
    ClaimsPrincipal principal,
    IFraudRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListTransfersAsync(RequireTenant(principal), cancellationToken)));
fraud.MapGet("/alert-rules", async (
    ClaimsPrincipal principal,
    IFraudRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListAlertRulesAsync(RequireTenant(principal), cancellationToken)));
fraud.MapPost("/alert-rules", async (
    CreateAlertRuleRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
{
    var rule = await service.CreateAlertRuleAsync(
        RequireTenant(principal), request.Name, request.MinimumAmount, cancellationToken);
    return Results.Created($"/api/v1/fraud/alert-rules/{rule.Id}", rule);
}).RequireBffMutation();
fraud.MapGet("/accounts/{accountId:guid}/suspicious-paths", async (
    Guid accountId,
    int? maximumHops,
    decimal? minimumTotal,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.FindSuspiciousPathsAsync(
        RequireTenant(principal),
        accountId,
        maximumHops ?? 4,
        minimumTotal ?? 10000m,
        cancellationToken)));
fraud.MapGet("/cases", async (
    ClaimsPrincipal principal,
    IFraudRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListCasesAsync(RequireTenant(principal), cancellationToken)));
fraud.MapPost("/cases", async (
    OpenCaseRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
{
    var investigationCase = await service.OpenCaseAsync(
        RequireTenant(principal), request.Reason, RequireActor(principal), cancellationToken);
    return Results.Created($"/api/v1/fraud/cases/{investigationCase.Id}", investigationCase);
}).RequireBffMutation();
fraud.MapPost("/cases/{caseId:guid}/assignment", async (
    Guid caseId,
    AssignCaseRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.AssignCaseAsync(
        RequireTenant(principal),
        caseId,
        request.Assignee,
        RequireActor(principal),
        request.ExpectedVersion,
        cancellationToken))).RequireBffMutation();
fraud.MapGet("/cases/{caseId:guid}/evidence", async (
    Guid caseId,
    ClaimsPrincipal principal,
    IFraudRepository repository,
    CancellationToken cancellationToken) =>
    Results.Ok(await repository.ListEvidenceAsync(
        RequireTenant(principal), caseId, cancellationToken)));
fraud.MapPost("/cases/{caseId:guid}/decision", async (
    Guid caseId,
    DecideCaseRequest request,
    ClaimsPrincipal principal,
    FraudService service,
    CancellationToken cancellationToken) =>
    Results.Ok(await service.DecideCaseAsync(
        RequireTenant(principal),
        caseId,
        request.Decision,
        request.Note,
        RequireActor(principal),
        request.ExpectedVersion,
        cancellationToken))).RequireBffMutation();

await app.RunAsync();

static string RequireTenant(ClaimsPrincipal principal) =>
    principal.FindFirstValue("tenant_id") ??
    throw new UnauthorizedAccessException("The authenticated session has no tenant scope.");

static string RequireActor(ClaimsPrincipal principal) =>
    principal.Identity?.Name ?? throw new UnauthorizedAccessException("The session has no actor identity.");

public partial class Program;
