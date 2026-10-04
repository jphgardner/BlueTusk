# BlueTusk Streams

BlueTusk Streams lets your .NET code react to every committed change in
PostgreSQL. It reads the write-ahead log through logical replication and gives
you whole transactions, in commit order, that you acknowledge when your work is
done. After a restart it carries on from the last acknowledged transaction.

## When to use Streams

Use Streams when you need to run your own code for every committed insert,
update, delete or truncate. For example:

- keep a read model, cache or search index in step with the database;
- publish integration events after a transaction commits;
- write an audit trail.

Use something else when:

| You want to | Use |
| --- | --- |
| Copy changes into PostgreSQL, Redis, NATS, Kafka, OpenSearch, S3 or a webhook without writing the apply code | [Sync](../sync/README.md) |
| Push live query results to browsers | [Live](../live/README.md) |
| Read raw replication protocol messages | The [replication API](../replication/README.md) |
| Run a query now | The [ADO.NET provider](../ado-net/README.md) |

Streams delivers each transaction **at least once**. Make your work safe to
repeat, or store your progress in the same transaction as your work. See
[delivery guarantees](../realtime-platform/contracts.md).

## Packages

| Package | What it adds |
| --- | --- |
| `BlueTusk.Streams` | Transactions, changes, acknowledgement, checkpoints, snapshot bootstrap and large-transaction spooling. Start here. |
| `BlueTusk.Streams.Storage.PostgreSql` | Durable checkpoint and lease store, and the durable relay, in PostgreSQL. |
| `BlueTusk.Streams.DependencyInjection` | Hosted snapshot-then-stream consumers and a health check. |
| `BlueTusk.Streams.Storage.File` | Checkpoint store on one host's local disk. |
| `BlueTusk.Streams.Storage.Redis` | Checkpoint store in Redis. |
| `BlueTusk.Streams.EntityFrameworkCore` | Typed change mappings built from an EF Core model. |
| `BlueTusk.Streams.CloudEvents` | One CloudEvents JSON event per transaction. |
| `BlueTusk.Streams.Aspire` | Wires Streams workers into a .NET Aspire AppHost. |
| `BlueTusk.Streams.Testing` | Test deliveries and a conformance suite for custom state stores. |
| `BlueTusk.Streams.Tool` | The `bluetusk-streams` command for validating and provisioning PostgreSQL. |

```powershell
dotnet add package BlueTusk.Streams
dotnet add package BlueTusk.Streams.Storage.PostgreSql
```

See [Install BlueTusk](../getting-started/install.md) to choose a channel and
pin a version.

**Status:** Core. Streams ships on the shared Core version line. `1.0.0` (stable)
and `1.1.0-rc.1` are published; `1.1.0` is not released yet. It supports
.NET 10 and PostgreSQL 15, 16, 17 and 18 (19 is preview).
[Prepared-transaction delivery](prepared-transactions.md) is an opt-in preview.

## Choose how to run a consumer

Most applications run Streams inside a .NET hosted service. Pick the shape that
matches your job:

| You want | Use | Start with |
| --- | --- | --- |
| A worker that processes new changes and resumes where it stopped | A hosted worker with a PostgreSQL checkpoint store | [Quick start](quickstart.md) |
| To copy the existing rows first, then stream new changes | `AddBlueTuskStreams().AddHostedConsumer<T>()` | [Snapshot and catch-up](snapshot-bootstrap.md), [hosting](hosting-observability.md) |
| Several consumers that each read the same changes at their own pace | The durable relay | [Durable relay](durable-relay.md) |
| To build the pipeline from its parts | `PgOutputChangeStream` and delivery observers | [Concepts](concepts.md) |

## What the code looks like

Register a hosted worker:

```csharp
builder.Services.AddSingleton(BlueTuskDataSource.Create(connectionString));
builder.Services.AddHostedService<OrdersStreamWorker>();
```

Inside the worker, read committed transactions and acknowledge each one after
your work is done:

```csharp
await foreach (var delivery in changes.ReadTransactionsAsync(stoppingToken))
{
    // Do your work for the whole transaction...
    await foreach (var change in delivery.Transaction.Changes.WithCancellation(stoppingToken))
    {
        Console.WriteLine($"{change.Kind} in transaction {delivery.Transaction.TransactionId}");
    }

    // ...then acknowledge it. Streams saves the checkpoint and tells PostgreSQL.
    await delivery.AcknowledgeAsync(stoppingToken);
}
```

The [quick start](quickstart.md) builds this worker end to end, including the
replication connection and the checkpoint store.

## Next steps

- [Quick start](quickstart.md): stream changes from a table and resume after a restart, in about 10 minutes.
- [Concepts](concepts.md): transactions, column states, acknowledgement, checkpoints, leases, spooling, snapshots and the relay.
- Guides:
  - [Snapshot and catch-up](snapshot-bootstrap.md): copy existing rows, then stream without a gap.
  - [Checkpoint and lease stores](state-stores.md): PostgreSQL, file, Redis and custom stores.
  - [Durable relay](durable-relay.md): feed many consumer groups from one slot.
  - [Hosting and observability](hosting-observability.md): hosted consumers, health checks, metrics and traces.
  - [Typed mappings](typed-mappings.md): map rows to your own classes or an EF Core model.
  - [CloudEvents](cloudevents.md): publish one event per transaction.
  - [Aspire](aspire.md): wire Streams workers in an Aspire AppHost.
  - [The `bluetusk-streams` tool](cli.md): validate and provision PostgreSQL.
  - [Prepared transactions](prepared-transactions.md): stage two-phase transactions (preview).
  - [Snapshot-then-stream sample](sample.md): run the sample worker in this repository.
- [Configuration](configuration.md): every option, default and configuration key.
- [Troubleshooting](troubleshooting.md): common errors and how to fix them.

## Reference records

- [Public API compatibility policy](api-compatibility.md)
- [Format compatibility registry](format-compatibility.md)
- [Release endurance record](release-endurance.md)
- Release notes: [1.0.0](release-notes-1.0.0.md),
  [0.1.0-preview.1](release-notes-0.1.0-preview.1.md),
  [1.1.0-rc.1](../releases/1.1.0-rc.1.md)
- [Transaction benchmark baseline](../../benchmarks/baselines/windows-ryzen7-5800x-dotnet10/results/BlueTusk.Benchmarks.StreamsTransactionBenchmarks-report-github.md)
