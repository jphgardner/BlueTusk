# BlueTusk.Extensions.Citext

Preview PostgreSQL `citext` support for BlueTusk. The package registers an extension-owned CLR value and runtime codec without adding citext-specific dependencies to the core provider.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.Citext;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

await using var command = dataSource.CreateCommand("SELECT $1::citext");
command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext>(new("BlueTusk")));
```

PostgreSQL must have `CREATE EXTENSION citext` applied. Pass the installation schema to `UseCitext(schema)` when it is not `public`.

Entity Framework Core integration is separately packaged as
`BlueTusk.Extensions.Citext.EntityFrameworkCore`. This keeps EF dependencies,
type mappings, and migration SQL out of the ADO.NET codec package.

This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1` APIs, not stable or production-ready contracts.
