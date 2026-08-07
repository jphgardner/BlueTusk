# BlueTusk.Extensions.PgVector

Stable PostgreSQL `pgvector` support for BlueTusk. The package provides
immutable `BlueTuskVector`, `BlueTuskHalfVector`, and `BlueTuskSparseVector`
values, native text and binary codecs, array composition, parameter inference,
and data-source registration.

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
`public`. Add the independently packaged
`BlueTusk.Extensions.PgVector.EntityFrameworkCore` integration for EF scalar
and array mappings, dimension-qualified store types, migration helpers, and
typed LINQ distance translations. Sparse-vector indices are zero-based in CLR
APIs and converted to pgvector's one-based text representation. The EF package
also maps Hamming and Jaccard distance over the core provider's
`BlueTuskBitString`, so PostgreSQL's general `bit` type remains outside the
optional extension codec package.

This package and the BlueTusk extension SDK use the stable 1.0.0
Provider-family contract.
