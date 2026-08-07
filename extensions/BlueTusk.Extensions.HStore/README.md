# BlueTusk.Extensions.HStore

Stable PostgreSQL `hstore` support for BlueTusk. The package provides an
immutable, structurally comparable `BlueTuskHStore` value with nullable text
values, native text and binary codecs, array composition, parameter inference,
and data-source registration.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.HStore;

var attributes = new BlueTuskHStore(
    new("owner", "BlueTusk"),
    new("reviewed", null));

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseHStore()
    .Build();
await using var command = dataSource.CreateCommand(
    "SELECT $1::hstore, $1 ? 'owner'");
command.Parameters.Add(new BlueTuskParameter<BlueTuskHStore>(attributes));
```

PostgreSQL must have `CREATE EXTENSION hstore` applied before the data source is
built. Pass the installation schema to `UseHStore(schema)` when it is not
`public`.

This package and the BlueTusk extension SDK use the stable 1.0.0
Provider-family contract.
