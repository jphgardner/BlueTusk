# Typed change mappings

Streams always retains the dynamic `ChangeRow` and its explicit per-column states. Typed mapping is an optional projection over that lossless row; it does not replace it and never manufactures a complete CLR object from an incomplete PostgreSQL tuple.

## Convention and explicit mapping

`ChangeEntityMappingBuilder<T>` maps public writable CLR properties by convention. Pascal-case property names map to snake-case PostgreSQL columns. Table, key, column, expected type OID, and decoder overrides are explicit and contribute to the stable mapping fingerprint.

```csharp
var mapping = new ChangeEntityMappingBuilder<Order>()
    .ToTable("sales", "orders")
    .HasKey("id")
    .Property(order => order.Id, "id", expectedTypeOid: 23)
    .Property(order => order.DisplayName, "display_name", expectedTypeOid: 25)
    .Build(relation);

Change mapped = mapping.Map(dynamicChange);
```

Property setters and default decoders are compiled once while the mapping is built. The default decoder handles the common pgoutput text forms and fixed-width binary scalar forms without reflection per row. A custom decoder can be supplied for application types. The EF adapter will build the same core mapping contract from EF metadata; it does not create a second mapping system.

`SchemaFingerprint` describes the complete source relation shape: schema, table, replica identity, ordered columns, PostgreSQL type identity, modifiers, and key flags. It deliberately excludes the transient relation OID. `MappingFingerprint` additionally describes the CLR type, property/column bindings, expected OIDs, and configured keys. Both are SHA-256 fingerprints over canonical data and are suitable for checkpoint compatibility checks.

## Partial rows remain partial

`ChangeRow<T>.HasValue` is true only when every mapped member was materialised. The value is absent when any mapped column is:

- not published;
- unavailable in an old-row image; or
- an unchanged TOAST value.

The original `ChangeRow` remains available through `ChangeRow<T>.Columns`, including database null and decoding-failure state. A database null assigned to a non-nullable CLR property is a typed decoding failure, not a default CLR value.

## Drift and failure policy

The default schema mode is `PauseAndReload`. A changed relation raises `ChangeSchemaReloadRequiredException` with both fingerprints and relation definitions. The other deliberate modes are `Fail`, `ContinueDynamically`, and `ApplicationCallback`.

Typed decoding failures also pause by default. Dynamic continuation and application callback are opt-in. Dynamic continuation returns the original untyped change so an operator policy can preserve information without claiming successful typed decoding.

## Snapshot and transaction consumer lifecycle

`IChangeStreamConsumer` keeps bootstrap delivery separate from normal transaction delivery:

1. `ResetSnapshotAsync` establishes a new epoch and identifies an abandoned epoch when restarting.
2. `StartSnapshotAsync` declares the bounded table set.
3. `ConsumeSnapshotBatchAsync` delivers immutable, keyed snapshot rows.
4. `CompleteSnapshotAsync` closes that epoch.
5. `ConsumeTransactionAsync` receives normal acknowledgement-bearing transaction deliveries.

Snapshot row identity is the snapshot epoch plus table identity and a length-delimited hash of the key states and values. It is intentionally distinct from `ChangeId`, which is derived from WAL transaction identity.
