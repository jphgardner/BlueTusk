# Quick start: stream changes from a table

In this quick start you build a .NET worker that prints every committed change
to a PostgreSQL table, then stop and restart it and watch it carry on from where
it stopped. It takes about 10 minutes.

## Before you start

You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a PostgreSQL 15, 16, 17 or 18 test server with `wal_level = logical`.

The `bluetusk-postgres` Docker container from
[step 1 of the 5-minute first app](../getting-started/quickstart.md#1-start-postgresql)
already has `wal_level=logical`. On another server, check with
`SHOW wal_level;`; changing it needs `ALTER SYSTEM SET wal_level = logical;` and
a restart.

## 1. Prepare PostgreSQL

Open `psql` as the `postgres` superuser:

```powershell
docker exec -it bluetusk-postgres psql -U postgres
```

Run this SQL:

```sql
-- A login that may open replication connections.
CREATE ROLE streams_quickstart WITH LOGIN REPLICATION PASSWORD 'local-dev-only';
-- Lets the worker create its checkpoint schema in this database.
GRANT CREATE ON DATABASE postgres TO streams_quickstart;

-- The table to watch.
CREATE SCHEMA app;
CREATE TABLE app.orders (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    description text NOT NULL
);
GRANT USAGE ON SCHEMA app TO streams_quickstart;
GRANT SELECT ON app.orders TO streams_quickstart;

-- Which tables to capture, and a slot that remembers how far you have read.
CREATE PUBLICATION orders_publication FOR TABLE app.orders;
SELECT pg_create_logical_replication_slot('orders_quickstart', 'pgoutput');
```

Type `\q` to leave `psql`. The [concepts page](../getting-started/concepts.md#two-kinds-of-transaction)
explains publications and slots.

## 2. Create the project

```powershell
dotnet new console --framework net10.0 --name OrdersStream
cd OrdersStream
dotnet add package BlueTusk.Streams
dotnet add package BlueTusk.Streams.Storage.PostgreSql
dotnet add package Microsoft.Extensions.Hosting
```

## 3. Set the connection string

```powershell
$env:BLUETUSK_STREAMS_SOURCE = "Host=localhost;Port=5432;Username=streams_quickstart;Password=local-dev-only;Database=postgres;SSL Mode=Disable;Channel Binding=Disable"
```

On Linux or macOS, use `export BLUETUSK_STREAMS_SOURCE="..."`.

> **Warning:** `SSL Mode=Disable` is only for a local test container. Keep the
> default, `SSL Mode=VerifyFull`, everywhere else.

## 4. Write the worker

Replace the contents of `Program.cs`:

```csharp
using System.Text;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Replication.PgOutput;
using BlueTusk.Streams;
using BlueTusk.Streams.Storage.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration["BLUETUSK_STREAMS_SOURCE"]
    ?? throw new InvalidOperationException("Set BLUETUSK_STREAMS_SOURCE first.");

builder.Services.AddSingleton(BlueTuskDataSource.Create(connectionString));
builder.Services.AddHostedService<OrdersStreamWorker>();

await builder.Build().RunAsync();

sealed class OrdersStreamWorker(BlueTuskDataSource dataSource, IConfiguration configuration)
    : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = configuration.GetSection("BlueTusk:Streams");
        var slot = settings["Slot"] ?? "orders_quickstart";
        var publication = settings["Publications:0"] ?? "orders_publication";
        var consumerGroup = settings["ConsumerGroup"] ?? "console";

        // 1. A durable checkpoint and lease store in PostgreSQL.
        var store = new PostgreSqlChangeStreamStateStore(
            new PostgreSqlStreamsStorageOptions { ControlDataSource = dataSource });
        await store.InitializeAsync(stoppingToken);

        // 2. A dedicated logical replication connection.
        await using var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(
            dataSource.CreateDedicatedSessionOptions(), stoppingToken);
        var server = await replication.IdentifySystemAsync(stoppingToken);
        var source = new ChangeSourceIdentity(
            server.SystemIdentifier, server.DatabaseName!, slot, publication);

        // 3. Take the consumer-group lease and load the last checkpoint.
        await using var checkpoints = await CheckpointingChangeDeliveryObserver.AcquireAsync(
            store,
            ChangeStreamStateKey.Create(source, consumerGroup),
            ownerId: $"{Environment.MachineName}:{Environment.ProcessId}",
            LeaseDuration,
            ChangeStreamCheckpoint.CreateInitial(
                source, server.SystemIdentifier, "pgoutput", "console-v1"),
            new LogicalReplicationFeedbackSender(replication),
            stoppingToken);
        var resumeFrom = checkpoints.Checkpoint?.AcknowledgedCommitPosition ?? default;
        Console.WriteLine($"Consumer group '{consumerGroup}' starts after {resumeFrom}");

        // 4. Read committed transactions from the slot, after the checkpoint.
        var changes = new PgOutputChangeStream(
            replication
                .StartReplicationAsync(
                    new BlueTuskPgOutputReplicationOptions
                    {
                        SlotName = slot,
                        PublicationNames = [publication],
                        StartPosition = resumeFrom,
                        ProtocolVersion = 2,
                        StreamingMode = BlueTuskLogicalStreamingMode.On,
                    },
                    stoppingToken)
                .DecodePgOutputAsync(
                    new BlueTuskPgOutputDecoderOptions
                    {
                        ProtocolVersion = 2,
                        StreamingMode = BlueTuskPgOutputStreamingMode.On,
                    },
                    stoppingToken),
            source,
            observer: checkpoints);

        await foreach (var delivery in changes.ReadTransactionsAsync(stoppingToken))
        {
            var transaction = delivery.Transaction;
            Console.WriteLine(
                $"Transaction {transaction.TransactionId} committed at " +
                $"{transaction.CommitEndPosition} ({transaction.Changes.Count} changes)");

            await foreach (var change in transaction.Changes.WithCancellation(stoppingToken))
            {
                Console.WriteLine($"  {change.Kind,-6} {Describe(change)}");
            }

            // 5. Acknowledge only after your work is done. This stores the
            //    checkpoint, then confirms the position to PostgreSQL.
            await delivery.AcknowledgeAsync(stoppingToken);
        }
    }

    private static string Describe(Change change)
    {
        var row = change switch
        {
            InsertChange insert => insert.NewRow,
            UpdateChange update => update.NewRow,
            DeleteChange delete => delete.OldRow,
            _ => null,
        };
        if (row is null)
        {
            return string.Empty;
        }

        var columns = row.Table.Columns.Select(column =>
        {
            var value = row[column.Ordinal];
            var text = value.State == ChangeColumnState.Value
                ? Encoding.UTF8.GetString(value.Data.Span)
                : $"<{value.State}>";
            return $"{column.Name}={text}";
        });
        return $"{row.Table} {string.Join(", ", columns)}";
    }
}
```

The numbered comments: (1) the store keeps one checkpoint row per consumer
group in the `bluetusk_streams` schema; (2) the **source identity** names the
server, database, slot and publication; (3) the **lease** stops a second copy
from taking over, and the observer renews it in the background until it is
disposed; (4) Streams assembles complete transactions; (5)
`AcknowledgeAsync` saves the checkpoint, then lets the slot release that WAL.

## 5. Run it

```powershell
dotnet run
```

```text
Consumer group 'console' starts after 0/0
```

`0/0` means there is no checkpoint yet. Leave the worker running.

## 6. Make some changes

In a second terminal:

```powershell
docker exec bluetusk-postgres psql -U postgres `
  -c "INSERT INTO app.orders (description) VALUES ('first order'), ('second order');" `
  -c "UPDATE app.orders SET description = 'first order (paid)' WHERE id = 1;" `
  -c "DELETE FROM app.orders WHERE id = 2;"
```

The worker prints one block per transaction. Your transaction IDs and
positions will differ:

```text
Transaction 1466 committed at 0/577A780 (2 changes)
  Insert app.orders id=1, description=first order
  Insert app.orders id=2, description=second order
Transaction 1467 committed at 0/577A810 (1 changes)
  Update app.orders id=1, description=first order (paid)
Transaction 1468 committed at 0/577A888 (1 changes)
  Delete app.orders id=2, description=<OldValueUnavailable>
```

The two inserts arrive together because they were one transaction. The delete
shows `<OldValueUnavailable>` because PostgreSQL only logs the key of a deleted
row by default. See [column states](concepts.md#what-a-column-value-can-be).

## 7. Stop, change and restart

Press Ctrl+C in the first terminal. While the worker is stopped, add two rows:

```powershell
docker exec bluetusk-postgres psql -U postgres `
  -c "INSERT INTO app.orders (description) VALUES ('while stopped 1'), ('while stopped 2');"
```

Start the worker again with `dotnet run`:

```text
Consumer group 'console' starts after 0/577A888
Transaction 1477 committed at 0/57B0570 (2 changes)
  Insert app.orders id=3, description=while stopped 1
  Insert app.orders id=4, description=while stopped 2
```

The worker resumed after its checkpoint: nothing was lost and nothing was
repeated. Press Ctrl+C when you are done.

> **Note:** After a crash, the old lease is held for up to 30 seconds and a
> restart fails with `ChangeStreamLeaseUnavailableException`. See
> [troubleshooting](troubleshooting.md#another-worker-owns-the-consumer-group).

## 8. Clean up

A slot keeps WAL until it is dropped, so drop slots you no longer use. In
`psql` as `postgres`:

```sql
SELECT pg_drop_replication_slot('orders_quickstart');
DROP PUBLICATION orders_publication;
DROP SCHEMA bluetusk_streams CASCADE;
DROP SCHEMA app CASCADE;
REVOKE CREATE ON DATABASE postgres FROM streams_quickstart;
DROP ROLE streams_quickstart;
```

## Next steps

- [Concepts](concepts.md)
- [Snapshot and catch-up](snapshot-bootstrap.md): copy existing rows first.
- [Checkpoint and lease stores](state-stores.md)
- [Configuration](configuration.md)
- [Troubleshooting](troubleshooting.md)
