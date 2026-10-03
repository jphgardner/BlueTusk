# Publish transactions as CloudEvents

This guide shows you how to turn each committed transaction into one
[CloudEvents 1.0](https://cloudevents.io/) structured JSON event, for example
to send it to a message broker or an HTTP endpoint.

Install the package:

```powershell
dotnet add package BlueTusk.Streams.CloudEvents
```

## Write one event per transaction

Inside your read loop, format the transaction, send it, and acknowledge only
after the destination has accepted it:

```csharp
var formatter = new ChangeTransactionCloudEventFormatter();
await formatter.WriteStructuredAsync(delivery.Transaction, destination, cancellationToken);
await destination.FlushAsync(cancellationToken);

// Acknowledge only after the destination has durably accepted the event.
await delivery.AcknowledgeAsync(cancellationToken);
```

`destination` is any writable `Stream`. To get the bytes instead, call
`ToStructuredJsonAsync(transaction)`, which returns a `ReadOnlyMemory<byte>`.

The formatter never acknowledges for you. If the process stops before
`AcknowledgeAsync`, the same transaction is formatted and sent again with the
same event ID.

## What the event contains

A whole transaction becomes one event, never one event per row:

| Attribute | Value |
| --- | --- |
| `specversion` | `1.0` |
| `id` | `<source fingerprint>:<commit-end LSN as 16 hex digits>:<transaction ID>`. The same on every redelivery. |
| `source` | `urn:bluetusk:postgresql:<source fingerprint>` |
| `type` | `io.bluetusk.streams.transaction.v1` |
| `subject` | `slot/<slot>/transaction/<transaction ID>` |
| `time` | The PostgreSQL commit timestamp. |
| `datacontenttype` | `application/vnd.bluetusk.change-transaction+binary;version=1` |
| `bluetusklsn`, `bluetuskxid`, `bluetuskchanges`, `bluetuskformat` | Commit-end position, transaction ID, change count and envelope format version. |
| `data_base64` | The transaction in BlueTusk's binary envelope format. |

For example (shortened):

```text
{"specversion":"1.0","id":"0d034f72...a3ef49:0000000000001000:42","source":"urn:bluetusk:postgresql:0d034f72...a3ef49","type":"io.bluetusk.streams.transaction.v1","subject":"slot/orders_slot/transaction/42", ...}
```

The envelope is the same versioned, checksummed format the
[durable relay](durable-relay.md) stores. It keeps table and column metadata,
change order, every [column state](concepts.md#what-a-column-value-can-be) and
logical messages. Use the event `id` for de-duplication in brokers that support
it; delivery is still at least once.

To read the attributes without writing the event:

```csharp
var metadata = formatter.Describe(delivery.Transaction);
Console.WriteLine($"{metadata.Id} {metadata.Type} {metadata.Subject}");
```

## Change the event type or size limits

```csharp
var custom = new ChangeTransactionCloudEventFormatter(new ChangeTransactionCloudEventOptions
{
    EventType = "com.example.orders.transaction.v1",
    MaximumEventBytes = 16 * 1024 * 1024,
});
```

| Option | Default |
| --- | --- |
| `EventType` | `io.bluetusk.streams.transaction.v1` |
| `DataContentType` | `application/vnd.bluetusk.change-transaction+binary;version=1` |
| `MaximumEventBytes` | 384 MiB |
| `Envelope` | `ChangeTransactionEnvelopeOptions` (256 MiB per envelope, 1,000,000 changes) |

An event larger than `MaximumEventBytes` throws `InvalidOperationException`
("The structured CloudEvent requires approximately ... bytes") before any JSON
is written, so a partial event never reaches the destination. Most brokers accept
far smaller messages than the default, so set the limit to what your broker
allows. See [configuration](configuration.md#cloudevents) for every envelope
limit.

## Related pages

- [Concepts: acknowledge after your work is durable](concepts.md#acknowledge-after-your-work-is-durable)
- [Sync](../sync/README.md) has ready-made Kafka, NATS and webhook destinations.
