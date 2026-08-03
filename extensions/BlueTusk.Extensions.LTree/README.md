# BlueTusk.Extensions.LTree

Preview PostgreSQL `ltree` support for BlueTusk. The package registers distinct
CLR values and versioned text/binary codecs for `ltree`, `lquery`, and
`ltxtquery`, including their catalogue-composed array types.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.LTree;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseLTree()
    .Build();

await using var command = dataSource.CreateCommand(
    "SELECT $1::ltree ~ $2::lquery");
command.Parameters.Add(
    new BlueTuskParameter<BlueTuskLTree>(new("Top.Countries.Europe.Russia")));
command.Parameters.Add(
    new BlueTuskParameter<BlueTuskLQuery>(new("Top.*{,2}.Europe.Russ@*")));
```

PostgreSQL must have `CREATE EXTENSION ltree` applied before the data source is
built. Pass the installation schema to `UseLTree(schema)` when it is not
`public`. PostgreSQL remains the grammar authority because valid labels depend
on the database locale; BlueTusk preserves the server's canonical text and
rejects embedded null characters and unsupported binary versions.

This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1`
APIs, not stable or production-ready contracts.
