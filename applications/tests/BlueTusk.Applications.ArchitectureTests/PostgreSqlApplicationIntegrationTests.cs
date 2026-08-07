using BlueTusk.FraudInvestigation.Application;
using BlueTusk.FraudInvestigation.Infrastructure;
using BlueTusk.OrderOperations.Application;
using BlueTusk.OrderOperations.Infrastructure;
using BlueTusk.ServiceTopology.Application;
using BlueTusk.ServiceTopology.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTusk.Applications.ArchitectureTests;

public sealed class PostgreSqlApplicationIntegrationTests
{
    [Fact]
    public async Task Migrations_tenant_isolation_idempotency_and_graph_models_work()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "BLUETUSK_APPLICATION_TEST_CONNECTION");
        var allowReset = Environment.GetEnvironmentVariable(
            "BLUETUSK_APPLICATION_TEST_ALLOW_RESET");
        if (string.IsNullOrWhiteSpace(connectionString) || allowReset != "1")
        {
            Assert.Skip(
                "Set the dedicated application test connection and explicit reset marker to run PostgreSQL integration tests.");
        }

        await VerifyOrdersAsync(connectionString);
        await VerifyTopologyAsync(connectionString);
        await VerifyFraudAsync(connectionString);
    }

    private static async Task VerifyOrdersAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddOrderInfrastructure(connectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderOperationsDbContext>();
        _ = await database.Database.EnsureDeletedAsync();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<OrderService>();
        _ = await service.CreateAsync("tenant-a", "A-1", "create-a", TestContext.Current.CancellationToken);
        _ = await service.CreateAsync("tenant-b", "B-1", "create-b", TestContext.Current.CancellationToken);
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var tenantA = await repository.SearchAsync("tenant-a", null, TestContext.Current.CancellationToken);
        Assert.Single(tenantA);
        Assert.Equal("A-1", tenantA[0].CustomerReference);
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            _ = await service.CreateAsync(
                "tenant-a",
                "A-2",
                "create-a",
                TestContext.Current.CancellationToken));
    }

    private static async Task VerifyTopologyAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddTopologyInfrastructure(connectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TopologyDbContext>();
        _ = await database.Database.EnsureDeletedAsync();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<TopologyService>();
        var billing = await service.RegisterAsync("tenant-a", "billing", TestContext.Current.CancellationToken);
        var identity = await service.RegisterAsync("tenant-a", "identity", TestContext.Current.CancellationToken);
        _ = await service.RegisterAsync("tenant-b", "identity", TestContext.Current.CancellationToken);
        _ = await service.ConnectAsync(
            "tenant-a", billing.Id, identity.Id, TestContext.Current.CancellationToken);
        _ = await service.OpenIncidentAsync(
            "tenant-a", identity.Id, "Identity is unavailable", TestContext.Current.CancellationToken);
        var repository = scope.ServiceProvider.GetRequiredService<ITopologyRepository>();
        var tenantA = await repository.ListServicesAsync("tenant-a", TestContext.Current.CancellationToken);
        Assert.Equal(2, tenantA.Count);
        Assert.Contains(tenantA, item => item.Name == "billing");
        Assert.Equal(
            [billing.Id, identity.Id],
            await service.FindPathAsync(
                "tenant-a", billing.Id, identity.Id, TestContext.Current.CancellationToken));
        Assert.Equal(
            [billing.Id],
            await service.BlastRadiusAsync(
                "tenant-a", identity.Id, TestContext.Current.CancellationToken));
        Assert.Single(await repository.ListIncidentsAsync(
            "tenant-a", TestContext.Current.CancellationToken));
    }

    private static async Task VerifyFraudAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddFraudInfrastructure(connectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<FraudDbContext>();
        _ = await database.Database.EnsureDeletedAsync();
        await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
        var service = scope.ServiceProvider.GetRequiredService<FraudService>();
        var source = await service.RegisterAccountAsync(
            "tenant-a", "Treasury", TestContext.Current.CancellationToken);
        var destination = await service.RegisterAccountAsync(
            "tenant-a", "New vendor", TestContext.Current.CancellationToken);
        var mule = await service.RegisterAccountAsync(
            "tenant-a", "Settlement intermediary", TestContext.Current.CancellationToken);
        var transfer = await service.RecordTransferAsync(
            "tenant-a",
            source.Id,
            destination.Id,
            25_000m,
            "GBP",
            TestContext.Current.CancellationToken);
        Assert.Equal(25_000m, transfer.Amount);
        _ = await service.RecordTransferAsync(
            "tenant-a",
            destination.Id,
            mule.Id,
            15_000m,
            "GBP",
            TestContext.Current.CancellationToken);
        var rule = await service.CreateAlertRuleAsync(
            "tenant-a", "High value multi-hop", 10_000m, TestContext.Current.CancellationToken);
        Assert.True(rule.Enabled);
        var paths = await service.FindSuspiciousPathsAsync(
            "tenant-a", source.Id, 4, 10_000m, TestContext.Current.CancellationToken);
        Assert.Contains(paths, path => path.AccountIds.SequenceEqual([source.Id, destination.Id, mule.Id]));
        var investigationCase = await service.OpenCaseAsync(
            "tenant-b", "velocity", "integration-test", TestContext.Current.CancellationToken);
        investigationCase = await service.AssignCaseAsync(
            "tenant-b",
            investigationCase.Id,
            "analyst@example.test",
            "integration-test",
            investigationCase.Version,
            TestContext.Current.CancellationToken);
        investigationCase = await service.DecideCaseAsync(
            "tenant-b",
            investigationCase.Id,
            BlueTusk.FraudInvestigation.Domain.CaseDecision.Suspicious,
            "Confirmed graph path",
            "integration-test",
            investigationCase.Version,
            TestContext.Current.CancellationToken);
        var repository = scope.ServiceProvider.GetRequiredService<IFraudRepository>();
        Assert.Empty(await repository.ListCasesAsync("tenant-a", TestContext.Current.CancellationToken));
        Assert.Single(await repository.ListCasesAsync("tenant-b", TestContext.Current.CancellationToken));
        Assert.Equal(3, (await repository.ListEvidenceAsync(
            "tenant-b", investigationCase.Id, TestContext.Current.CancellationToken)).Count);
        Assert.Single(await repository.ListAlertRulesAsync(
            "tenant-a", TestContext.Current.CancellationToken));
    }
}
