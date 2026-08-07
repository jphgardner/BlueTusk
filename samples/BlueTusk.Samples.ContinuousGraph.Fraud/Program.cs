using BlueTusk.ContinuousGraph;
using BlueTusk.Data;
using BlueTusk.Live;
using BlueTusk.Live.Testing;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable(
    "BLUETUSK_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine(
        "Set BLUETUSK_CONNECTION_STRING to a PostgreSQL 19 database.");
    return 1;
}

await using var connection = new BlueTuskConnection(connectionString);
await connection.OpenAsync();
if (connection.SupportsSqlPgq is not true)
{
    Console.Error.WriteLine(
        $"Continuous Graph requires PostgreSQL 19 SQL/PGQ; " +
        $"the server is {connection.ServerVersion}.");
    return 2;
}

await ExecuteAsync(
    """
    CREATE TEMP TABLE fraud_accounts (
        id int8 PRIMARY KEY,
        display_name text NOT NULL);
    CREATE TEMP TABLE fraud_transfers (
        id int8 PRIMARY KEY,
        source_id int8 NOT NULL REFERENCES fraud_accounts (id),
        destination_id int8 NOT NULL REFERENCES fraud_accounts (id),
        amount numeric NOT NULL);
    INSERT INTO fraud_accounts VALUES
        (1, 'Contoso Treasury'), (2, 'New Vendor'), (3, 'Long-term Supplier');
    INSERT INTO fraud_transfers VALUES
        (100, 1, 2, 25000), (101, 1, 3, 500);
    CREATE TEMP PROPERTY GRAPH fraud_graph
        VERTEX TABLES (
            fraud_accounts AS accounts
            KEY (id)
            LABEL account PROPERTIES (
                id AS "Id",
                display_name AS "DisplayName"))
        EDGE TABLES (
            fraud_transfers AS transfers
            KEY (id)
            SOURCE KEY (source_id) REFERENCES accounts (id)
            DESTINATION KEY (destination_id) REFERENCES accounts (id)
            LABEL transfer PROPERTIES (
                id AS "Id",
                source_id AS "SourceId",
                destination_id AS "DestinationId",
                amount AS "Amount"));
    """);

try
{
    var contextFactory = new FraudContextFactory(connection);
    var definition = new ContinuousGraphQueryDefinition<FraudContext, FraudPath, long>(
        "high-value-outgoing-transfers",
        "fraud-demo",
        "1",
        "fraud_graph",
        graphSchema: null,
        ["accounts", "transfers"],
        [
            new LiveQueryParameter("accountId", typeof(long)),
            new LiveQueryParameter("minimumAmount", typeof(decimal)),
        ],
        new Dictionary<string, object?>
        {
            ["accountId"] = 1L,
            ["minimumAmount"] = 10_000m,
        },
        25,
        (context, arguments) =>
        {
            var accountId = arguments.Get<long>("accountId");
            var minimumAmount = arguments.Get<decimal>("minimumAmount");
            return context.PropertyGraph("fraud_graph")
                .Match(pattern => pattern
                    .Vertex<Account>("source", account => account.Id == accountId)
                    .Outgoing<Transfer>("transfer")
                    .Vertex<Account>("target"))
                .Select<FraudPath>(projection => projection
                    .Property<Transfer, long>(
                        "transfer", transfer => transfer.Id, result => result.TransferId)
                    .Property<Account, long>(
                        "source", account => account.Id, result => result.SourceId)
                    .Property<Account, long>(
                        "target", account => account.Id, result => result.TargetId)
                    .Property<Account, string>(
                        "target",
                        account => account.DisplayName,
                        result => result.TargetName)
                    .Property<Transfer, decimal>(
                        "transfer", transfer => transfer.Amount, result => result.Amount))
                .Where(result => result.Amount >= minimumAmount)
                .OrderByDescending(result => result.Amount)
                .ThenBy(result => result.TransferId)
                .Take(25);
        },
        result => result.TransferId,
        FraudPathComparer.Instance);
    var plan = await ContinuousGraphQueryCompiler.CompileAsync(
        contextFactory,
        definition);
    var arguments = plan.Bind(
        new Dictionary<string, object?>
        {
            ["accountId"] = 1L,
            ["minimumAmount"] = 10_000m,
        });
    var invalidations = new InMemoryLiveInvalidationLog();
    await using var session = plan.CreateSession(
        arguments,
        new LiveSecurityScope("tenant:fraud-demo", "policy-v1"),
        invalidations);
    var initial = await session.StartAsync();
    Print("Initial high-value paths", initial);

    await ExecuteAsync(
        "UPDATE fraud_transfers SET amount = 15000 WHERE id = 101");
    _ = invalidations.Append("fraud-demo", plan.Dependencies);
    var refresh = await session.RefreshToCurrentAsync();
    if (refresh is not null)
    {
        Print("After the transfer change", refresh);
    }
}
finally
{
    await ExecuteAsync("DROP PROPERTY GRAPH IF EXISTS fraud_graph");
}

return 0;

async Task ExecuteAsync(string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    _ = await command.ExecuteNonQueryAsync();
}

static void Print(string heading, LiveDiffBatch<FraudPath, long> batch)
{
    Console.WriteLine(heading);
    if (batch.Events.Any(graphEvent =>
            graphEvent.Kind is LiveEventKind.InitialResult or LiveEventKind.ResultReset))
    {
        foreach (var row in batch.Snapshot.Rows)
        {
            Console.WriteLine(
                $"  snapshot: transfer={row.TransferId}, " +
                $"target={row.TargetName}, amount={row.Amount}");
        }

        return;
    }

    foreach (var graphEvent in batch.Events)
    {
        Console.WriteLine(
            $"  {graphEvent.Kind}: transfer={graphEvent.Key}, " +
            $"target={graphEvent.Row?.TargetName}, amount={graphEvent.Row?.Amount}");
    }
}

internal sealed class FraudContextFactory(BlueTuskConnection connection) :
    IDbContextFactory<FraudContext>
{
    public FraudContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FraudContext>()
            .UseBlueTusk(connection)
            .Options;
        return new FraudContext(options);
    }
}

internal sealed class FraudContext(DbContextOptions<FraudContext> options) :
    DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.ToTable("fraud_accounts");
            entity.HasKey(account => account.Id);
            entity.Property(account => account.Id).HasColumnName("id");
            entity.Property(account => account.DisplayName).HasColumnName("display_name");
        });
        modelBuilder.Entity<Transfer>(entity =>
        {
            entity.ToTable("fraud_transfers");
            entity.HasKey(transfer => transfer.Id);
            entity.Property(transfer => transfer.Id).HasColumnName("id");
            entity.Property(transfer => transfer.SourceId).HasColumnName("source_id");
            entity.Property(transfer => transfer.DestinationId).HasColumnName("destination_id");
            entity.Property(transfer => transfer.Amount).HasColumnName("amount");
        });
        modelBuilder.HasPropertyGraph(
            "fraud_graph",
            graph =>
            {
                graph.Vertex<Account>("accounts", vertex => vertex
                    .HasLabel("account")
                    .HasKey(account => account.Id)
                    .Properties(account => new { account.Id, account.DisplayName }));
                graph.Edge<Transfer>("transfers", edge => edge
                    .HasLabel("transfer")
                    .HasKey(transfer => transfer.Id)
                    .Properties(transfer => new
                    {
                        transfer.Id,
                        transfer.SourceId,
                        transfer.DestinationId,
                        transfer.Amount,
                    })
                    .HasSource<Account>(
                        transfer => transfer.SourceId,
                        account => account.Id)
                    .HasDestination<Account>(
                        transfer => transfer.DestinationId,
                        account => account.Id));
            });
    }
}

internal sealed class Account
{
    public long Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;
}

internal sealed class Transfer
{
    public long Id { get; set; }

    public long SourceId { get; set; }

    public long DestinationId { get; set; }

    public decimal Amount { get; set; }
}

internal sealed class FraudPath
{
    public long TransferId { get; set; }

    public long SourceId { get; set; }

    public long TargetId { get; set; }

    public string TargetName { get; set; } = string.Empty;

    public decimal Amount { get; set; }
}

internal sealed class FraudPathComparer : IEqualityComparer<FraudPath>
{
    public static FraudPathComparer Instance { get; } = new();

    public bool Equals(FraudPath? x, FraudPath? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null &&
         x.TransferId == y.TransferId &&
         x.SourceId == y.SourceId &&
         x.TargetId == y.TargetId &&
         string.Equals(x.TargetName, y.TargetName, StringComparison.Ordinal) &&
         x.Amount == y.Amount);

    public int GetHashCode(FraudPath obj) =>
        HashCode.Combine(
            obj.TransferId,
            obj.SourceId,
            obj.TargetId,
            obj.TargetName,
            obj.Amount);
}
