using BlueTusk.FraudInvestigation.Application;
using BlueTusk.FraudInvestigation.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlueTusk.FraudInvestigation.Infrastructure;

public sealed class FraudDbContext(DbContextOptions<FraudDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Transfer> Transfers => Set<Transfer>();

    public DbSet<AlertRule> AlertRules => Set<AlertRule>();

    public DbSet<InvestigationCase> Cases => Set<InvestigationCase>();

    public DbSet<FraudEvidenceAudit> Evidence => Set<FraudEvidenceAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("fraud");
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("accounts");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.TenantId).HasMaxLength(80);
            entity.Property(account => account.DisplayName).HasMaxLength(200);
        });
        modelBuilder.Entity<Transfer>(entity =>
        {
            entity.ToTable("transfers");
            entity.HasKey(transfer => transfer.Id);
            entity.Property(transfer => transfer.TenantId).HasMaxLength(80);
            entity.Property(transfer => transfer.Currency).HasMaxLength(3);
            entity.Property(transfer => transfer.Amount).HasPrecision(20, 4);
            entity.HasIndex(transfer => new { transfer.TenantId, transfer.RecordedAt });
        });
        modelBuilder.Entity<InvestigationCase>(entity =>
        {
            entity.ToTable("investigation_cases");
            entity.HasKey(investigationCase => investigationCase.Id);
            entity.Property(investigationCase => investigationCase.TenantId).HasMaxLength(80);
            entity.Property(investigationCase => investigationCase.Reason).HasMaxLength(500);
            entity.Property(investigationCase => investigationCase.Assignee).HasMaxLength(200);
            entity.Property(investigationCase => investigationCase.DecisionNote).HasMaxLength(2000);
            entity.Property(investigationCase => investigationCase.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.HasKey(rule => rule.Id);
            entity.Property(rule => rule.TenantId).HasMaxLength(80);
            entity.Property(rule => rule.Name).HasMaxLength(200);
            entity.Property(rule => rule.MinimumAmount).HasPrecision(20, 4);
            entity.HasIndex(rule => new { rule.TenantId, rule.Name }).IsUnique();
        });
        modelBuilder.Entity<FraudEvidenceAudit>(entity =>
        {
            entity.ToTable("evidence_audit");
            entity.HasKey(entry => entry.Id);
            entity.Property(entry => entry.TenantId).HasMaxLength(80);
            entity.Property(entry => entry.Operation).HasMaxLength(100);
            entity.Property(entry => entry.Actor).HasMaxLength(200);
            entity.HasIndex(entry => new { entry.TenantId, entry.CaseId, entry.RecordedAt });
        });
        modelBuilder.HasPropertyGraph("fraud_graph", graph =>
        {
            graph.Vertex<Account>("accounts", vertex => vertex
                .HasLabel("account")
                .HasKey(account => account.Id)
                .Properties(account => new { account.Id, account.DisplayName }));
            graph.Edge<Transfer>("transfers", edge => edge
                .HasLabel("transfer")
                .HasKey(transfer => transfer.Id)
                .Properties(transfer => new { transfer.Id, transfer.Amount, transfer.Currency })
                .HasSource<Account>(transfer => transfer.SourceId, account => account.Id)
                .HasDestination<Account>(transfer => transfer.DestinationId, account => account.Id));
        });
    }
}

internal sealed class EfFraudRepository(FraudDbContext context) : IFraudRepository
{
    public ValueTask<Account?> FindAccountAsync(
        string tenantId,
        Guid id,
        CancellationToken cancellationToken) =>
        new(context.Accounts.SingleOrDefaultAsync(
            account => account.TenantId == tenantId && account.Id == id,
            cancellationToken));

    public async ValueTask AddAccountAsync(Account account, CancellationToken cancellationToken) =>
        _ = await context.Accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

    public async ValueTask AddTransferAsync(Transfer transfer, CancellationToken cancellationToken) =>
        _ = await context.Transfers.AddAsync(transfer, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<Account>> ListAccountsAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Accounts.AsNoTracking().Where(account => account.TenantId == tenantId)
            .OrderBy(account => account.DisplayName).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<Transfer>> ListTransfersAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Transfers.AsNoTracking().Where(transfer => transfer.TenantId == tenantId)
            .OrderByDescending(transfer => transfer.RecordedAt).Take(1000)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask AddAlertRuleAsync(AlertRule rule, CancellationToken cancellationToken) =>
        _ = await context.AlertRules.AddAsync(rule, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<AlertRule>> ListAlertRulesAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.AlertRules.AsNoTracking().Where(rule => rule.TenantId == tenantId)
            .OrderBy(rule => rule.Name).ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask AddCaseAsync(
        InvestigationCase investigationCase,
        CancellationToken cancellationToken) =>
        _ = await context.Cases.AddAsync(investigationCase, cancellationToken).ConfigureAwait(false);

    public ValueTask<InvestigationCase?> FindCaseAsync(
        string tenantId,
        Guid id,
        CancellationToken cancellationToken) =>
        new(context.Cases.SingleOrDefaultAsync(
            investigationCase =>
                investigationCase.TenantId == tenantId && investigationCase.Id == id,
            cancellationToken));

    public async ValueTask<IReadOnlyList<InvestigationCase>> ListCasesAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await context.Cases.AsNoTracking()
            .Where(investigationCase => investigationCase.TenantId == tenantId)
            .OrderByDescending(investigationCase => investigationCase.OpenedAt)
            .Take(200)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async ValueTask AppendEvidenceAsync(
        FraudEvidenceEntry entry,
        CancellationToken cancellationToken) =>
        _ = await context.Evidence.AddAsync(new FraudEvidenceAudit
        {
            TenantId = entry.TenantId,
            CaseId = entry.CaseId,
            Operation = entry.Operation,
            Actor = entry.Actor,
            Detail = entry.Detail,
            RecordedAt = entry.RecordedAt,
        }, cancellationToken).ConfigureAwait(false);

    public async ValueTask<IReadOnlyList<FraudEvidenceEntry>> ListEvidenceAsync(
        string tenantId,
        Guid caseId,
        CancellationToken cancellationToken) =>
        await context.Evidence.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId && entry.CaseId == caseId)
            .OrderBy(entry => entry.RecordedAt)
            .Select(entry => new FraudEvidenceEntry(
                entry.TenantId,
                entry.CaseId,
                entry.Operation,
                entry.Actor,
                entry.Detail,
                entry.RecordedAt))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public ValueTask SaveAsync(CancellationToken cancellationToken) =>
        new(context.SaveChangesAsync(cancellationToken));
}

public sealed class FraudEvidenceAudit
{
    public long Id { get; set; }
    public required string TenantId { get; set; }
    public Guid CaseId { get; set; }
    public required string Operation { get; set; }
    public required string Actor { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public static class FraudInfrastructure
{
    public static IServiceCollection AddFraudInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<FraudDbContext>(options => options.UseBlueTusk(connectionString));
        services.AddScoped<IFraudRepository, EfFraudRepository>();
        services.AddScoped<FraudService>();
        return services;
    }
}
