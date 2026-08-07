using System.Security.Cryptography;
using System.Text;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.Streams.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var sourceConnection = Environment.GetEnvironmentVariable("BLUETUSK_STREAMS_SOURCE");
var slot = builder.Configuration["BlueTusk:Streams:Slot"];
var publication = builder.Configuration["BlueTusk:Streams:Publications:0"];
if (string.IsNullOrWhiteSpace(sourceConnection) ||
    string.IsNullOrWhiteSpace(slot) ||
    string.IsNullOrWhiteSpace(publication))
{
    Console.Error.WriteLine(
        "Set BLUETUSK_STREAMS_SOURCE, BlueTusk__Streams__Slot, and " +
        "BlueTusk__Streams__Publications__0. See docs/streams/sample.md.");
    return 2;
}

var schema = builder.Configuration["BlueTusk:Streams:Sample:Schema"] ?? "app";
var tableName = builder.Configuration["BlueTusk:Streams:Sample:Table"] ?? "orders";
var dataSource = new BlueTuskDataSourceBuilder(sourceConnection).Build();
await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
                 dataSource.CreateDedicatedSessionOptions()))
{
    var server = await replication.IdentifySystemAsync();
    var table = new ChangeTable(
        relationId: 0,
        schema,
        tableName,
        replicaIdentity: 'd',
        [
            new ChangeColumn(0, "id", 20, -1, IsKey: true),
            new ChangeColumn(1, "description", 25, -1, IsKey: false),
            new ChangeColumn(2, "updated_at", 1184, -1, IsKey: false),
        ]);
    var sourceIdentity = new ChangeSourceIdentity(
        server.SystemIdentifier,
        server.DatabaseName ?? throw new InvalidOperationException(
            "PostgreSQL did not identify the logical-replication database."),
        slot,
        PublicationFingerprint(publication, schema, tableName));
    var snapshotOptions = new PostgreSqlConsistentSnapshotOptions
    {
        Source = sourceIdentity,
        PublicationNames = [publication],
        Tables = [new PostgreSqlSnapshotTable(table, [0])],
        ExistingSlotMode = PostgreSqlExistingSnapshotSlotMode.RestartSnapshot,
    };

    builder.Services.AddSingleton(dataSource);
    builder.Services.AddSingleton<ConsoleChangeConsumer>();
    builder.Services.AddBlueTuskStreams()
        .AddHostedConsumer<ConsoleChangeConsumer>(
            "console-projector",
            services => new PostgreSqlConsistentSnapshotSource(
                services.GetRequiredService<BlueTuskDataSource>(),
                snapshotOptions));
}

await builder.Build().RunAsync();
return 0;

static string PublicationFingerprint(string publication, string schema, string table)
{
    var canonical = $"P:{publication}\nT:{schema}.{table}";
    return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}

internal sealed class ConsoleChangeConsumer : IChangeStreamConsumer
{
    public ValueTask ResetSnapshotAsync(
        SnapshotReset reset,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"SNAPSHOT RESET {reset.Epoch.Value} {reset.Reason}");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartSnapshotAsync(
        SnapshotStart start,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"SNAPSHOT START {start.Epoch.Value} tables={start.TableCount}");
        return ValueTask.CompletedTask;
    }

    public ValueTask ConsumeSnapshotBatchAsync(
        ChangeSnapshotBatch batch,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(
            $"SNAPSHOT BATCH {batch.Table} sequence={batch.Sequence} rows={batch.Rows.Count}");
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteSnapshotAsync(
        SnapshotComplete complete,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"SNAPSHOT COMPLETE {complete.Epoch.Value} rows={complete.RowCount}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConsumeTransactionAsync(
        ChangeTransactionDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        var changes = 0;
        await foreach (var change in delivery.Transaction.Changes.WithCancellation(cancellationToken))
        {
            Console.WriteLine(
                $"CHANGE {change.Id} {change.GetType().Name}");
            changes++;
        }

        Console.WriteLine(
            $"TRANSACTION xid={delivery.Transaction.TransactionId} " +
            $"commit={delivery.Transaction.CommitEndPosition} changes={changes}");
        await delivery.AcknowledgeAsync(cancellationToken);
    }
}
