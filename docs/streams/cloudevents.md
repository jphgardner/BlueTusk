# CloudEvents

`BlueTusk.Streams.CloudEvents` writes CloudEvents 1.0 structured JSON without changing the source transaction delivery unit. One committed PostgreSQL transaction becomes one event with one versioned BlueTusk transaction envelope in `data_base64`.

```csharp
var formatter = new ChangeTransactionCloudEventFormatter();
await formatter.WriteStructuredAsync(delivery.Transaction, destination, cancellationToken);
await destination.FlushAsync(cancellationToken);
await delivery.AcknowledgeAsync(cancellationToken);
```

The default event uses:

- type `io.bluetusk.streams.transaction.v1`;
- source `urn:bluetusk:postgresql:<source fingerprint>`;
- a deterministic ID composed from source fingerprint, commit-end LSN, and transaction ID;
- subject `slot/<slot>/transaction/<xid>`;
- the PostgreSQL commit timestamp;
- content type `application/vnd.bluetusk.change-transaction+binary;version=1`; and
- `bluetusklsn`, `bluetuskxid`, `bluetuskchanges`, and `bluetuskformat` extension attributes.

The binary data is the same bounded, versioned, SHA-256 integrity-checked envelope used by the durable relay. It retains table metadata, ordering, every explicit row state, and logical messages. Stable event IDs support broker deduplication, but delivery remains advertised as at least once.

Formatting has independent event and envelope limits. The formatter rejects an oversized event before writing JSON. It does not acknowledge a delivery; application or connector code acknowledges only after the event destination confirms durable handling.
