# BlueTusk.Extensions.PgVector.EntityFrameworkCore

Preview Entity Framework Core integration for `BlueTusk.Extensions.PgVector`.
Register the ADO.NET codec on the data source and the EF mappings on the provider:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgVector()
    .Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource, provider => provider.UsePgVector())
    .Options;
```

`BlueTuskVector` and `BlueTuskVector[]` properties are mapped to `vector` and
`vector[]`; dimension-qualified store types such as `vector(768)` are preserved.
`EF.Functions.L2Distance`, `MaxInnerProduct`, `CosineDistance`, and `L1Distance`
translate to pgvector's index-compatible `<->`, `<#>`, `<=>`, and `<+>` operators.

Use `EnsureBlueTuskPgVector()` and `DropBlueTuskPgVector()` in migrations when
the application owns the extension lifecycle. These APIs and the BlueTusk EF
provider remain experimental `0.3.0-preview.1` previews.
