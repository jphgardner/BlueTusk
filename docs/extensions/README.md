# Use PostgreSQL extensions

PostgreSQL extensions can add functions, operators, index methods, and types.
BlueTusk can call ordinary extension SQL immediately; install a BlueTusk
extension package only when you want a strongly typed wire value, EF mapping,
or LINQ translation.

## Choose the smallest integration

| You need                                                                | What to install                                              |
| ----------------------------------------------------------------------- | ------------------------------------------------------------ |
| An extension function or operator in SQL                                | No BlueTusk extension package; use a parameterized command.  |
| A domain, enum, composite, array, range, or multirange over known types | Core provider catalogue discovery usually handles it.        |
| `citext` CLR values and EF mappings                                     | `BlueTusk.Extensions.Citext` plus the optional EF package.   |
| Vector similarity search                                                | `BlueTusk.Extensions.PgVector` plus the optional EF package. |
| Spatial values and LINQ                                                 | `BlueTusk.Extensions.PostGIS` plus the optional EF package.  |
| TimescaleDB helpers                                                     | `BlueTusk.Extensions.TimescaleDb` and its EF integration.    |
| A new extension-defined base type                                       | Install or build a codec package.                            |

Server installation is separate from client integration. Your database
operator must install the extension and approve its privileges, preload needs,
and upgrade policy.

## Example: use `citext`

Create the extension through a migration or controlled database operation:

```sql
CREATE EXTENSION IF NOT EXISTS citext;
```

Register its codec before building the long-lived data source:

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.Citext;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

await using var command = dataSource.CreateCommand(
    "SELECT @value::citext = 'bluetusk'::citext");
command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext>(new("BlueTusk"))
{
    ParameterName = "value",
});

var equal = await command.ExecuteScalarAsync<bool>(); // true
```

For EF Core, register both the data-source codec and the provider integration:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UseCitext()));
```

## Example: nearest neighbours with pgvector

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgVector()
    .Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UsePgVector()));

var nearest = await db.Items
    .OrderBy(item => EF.Functions.L2Distance(item.Embedding, probe))
    .Take(10)
    .ToListAsync();
```

Create the appropriate vector index in a migration and verify the query plan;
client-side typing does not guarantee index use.

## Build an integration for another extension

Install the template and generate a package outside BlueTusk core:

```powershell
dotnet new install BlueTusk.Templates
dotnet new bluetusk-extension `
  -n Contoso.BlueTusk.Extensions.MyType `
  --ExtensionName MyType `
  --PostgreSqlTypeName my_type
```

The generated project includes codec tests and the public extension
conformance checks.

## Production checklist

- Pin the server extension version alongside PostgreSQL and BlueTusk.
- Install extensions before application startup or migrations that use them.
- Register codecs before building the data source.
- Register EF mappings separately when the application uses EF Core.
- Test binary/text round trips and upgrade compatibility with real PostgreSQL.
- Keep unknown values opaque rather than guessing their meaning.

The [extension catalogue and SDK reference](reference.md) contains the complete
citext, pgvector, hstore, ltree, pg_trgm, pg_durable, PostGIS, and TimescaleDB
surface plus extension-author guidance.
