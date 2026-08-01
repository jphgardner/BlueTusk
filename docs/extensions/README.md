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

The immutable descriptor is available for integration code that must inspect configured optional behavior:

```csharp
var feature = dataSource.Features.GetRequired<BlueTuskCitextFeature>(
    BlueTuskCitextFeature.RegistryName);
```

No citext SQL, CLR type, or package reference is present in `BlueTusk.Data`, `BlueTusk.Client`, or lower layers. EF-specific extension integration remains separate from this ADO.NET codec package. The authoring template and compatibility harness establish an executable preview contract, but stability still requires ecosystem feedback and an explicit versioning commitment.
