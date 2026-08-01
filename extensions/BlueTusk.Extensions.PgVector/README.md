# BlueTusk.Extensions.PgVector

Preview PostgreSQL `pgvector` support for BlueTusk. The package provides an
immutable dense single-precision `BlueTuskVector`, native text and binary
codecs, array composition, parameter inference, and data-source registration.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PgVector;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgVector()
    .Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::vector, $1 <-> '[1,2,4]'::vector");
command.Parameters.Add(
    new BlueTuskParameter<BlueTuskVector>(new(1f, 2f, 3f)));
```

PostgreSQL must have `CREATE EXTENSION vector` applied before the data source
is built. Pass the installation schema to `UsePgVector(schema)` when it is not
`public`. The package currently owns the dense `vector` wire type; `halfvec`,
`bit`, `sparsevec`, EF mappings, and LINQ distance translations remain future
separately tested additions.

This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1`
APIs, not stable or production-ready contracts.
