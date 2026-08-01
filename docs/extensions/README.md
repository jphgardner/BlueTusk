# Extension SDK

Extensions register types and immutable feature descriptors through `BlueTusk.Extensions.Abstractions`. `BlueTuskDataSourceBuilder.Build()` snapshots both registries into the resulting data source. Later builder changes do not mutate an existing data source, and optional packages remain independently deployable without extension-specific dependencies in BlueTusk core packages.

The API is still preview. `BlueTusk.Extensions.Citext` is the first executable compatibility slice; it does not make the broader extension SDK stable.

## Start an extension package

`BlueTusk.Templates` provides a complete source-and-test skeleton:

```powershell
dotnet new install BlueTusk.Templates
dotnet new bluetusk-extension `
  -n Contoso.BlueTusk.Extensions.MyType `
  --ExtensionName MyType `
  --PostgreSqlTypeName my_type
```

The generated package keeps extension-specific SQL and CLR types outside the
core provider. It includes binary/text codec tests and a live contract test
using `BlueTusk.Extensions.Testing`.

The framework-neutral compatibility verifier checks four integration
boundaries through a built data source: immutable feature retention, live
catalogue type discovery, resolved CLR identity, and resolved codec identity.
It briefly checks out a normal pooled connection; the caller continues to own
and dispose the data source. Extension authors must also add representative
value round trips, PostgreSQL behavioural tests, package-content inspection,
and any separate EF translation/migration plug-in tests.

## citext preview

Install `citext` in PostgreSQL, configure one long-lived data source, and use the extension-owned CLR value so runtime type inference remains unambiguous from ordinary PostgreSQL `text`:

```sql
CREATE EXTENSION IF NOT EXISTS citext;
```

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.Citext;

var builder = new BlueTuskDataSourceBuilder(connectionString).UseCitext();
await using var dataSource = builder.Build();
await using var command = dataSource.CreateCommand(
    "SELECT $1::citext = 'bluetusk'::citext, $1::citext");
command.Parameters.Add(new BlueTuskParameter<BlueTuskCitext>(new("BlueTusk")));

await using var reader = await command.ExecuteReaderAsync();
await reader.ReadAsync();
var equal = reader.GetBoolean(0); // true; comparison is performed by PostgreSQL
var value = reader.GetFieldValue<BlueTuskCitext>(1);
```

`UseCitext("extensions")` supports an extension installed into a non-default schema. Scalar and array values use PostgreSQL's text/binary send and receive functions and the same runtime catalogue used by the rest of the data source.

EF integration is deliberately a second package,
`BlueTusk.Extensions.Citext.EntityFrameworkCore`. Register the codec on the data
source and the EF mapping on the provider independently:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

services.AddDbContext<AppDbContext>(options =>
    options.UseBlueTusk(dataSource, provider => provider.UseCitext()));
```

This maps `BlueTuskCitext` and `BlueTuskCitext[]` to schema-qualified PostgreSQL
types. Normal EF equality queries remain parameterized and use PostgreSQL's
case-insensitive `citext` operator semantics. The plug-in participates in EF's
service-provider cache identity, so different installation schemas do not share
an incompatible singleton mapping.

Migrations that own installation of the PostgreSQL extension can use helpers
from the companion package. Their extension-specific SQL stays outside the core
provider:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.EnsureBlueTuskCitext();
    // Create citext-backed tables after this operation.
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    // Drop dependent tables before removing the extension.
    migrationBuilder.DropBlueTuskCitext();
}
```

The immutable descriptor is available for integration code that must inspect configured optional behavior:

```csharp
var feature = dataSource.Features.GetRequired<BlueTuskCitextFeature>(
    BlueTuskCitextFeature.RegistryName);
```

No citext SQL, CLR type, or package reference is present in `BlueTusk.Data`,
`BlueTusk.Client`, `BlueTusk.EntityFrameworkCore`, or lower layers. The EF
mapping and migration SQL live only in the companion package. The authoring
template and compatibility harness establish an executable preview contract,
but stability still requires ecosystem feedback and an explicit versioning
commitment.
