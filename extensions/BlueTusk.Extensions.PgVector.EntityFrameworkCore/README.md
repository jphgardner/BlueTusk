# BlueTusk.Extensions.PgVector.EntityFrameworkCore

Stable Entity Framework Core integration for `BlueTusk.Extensions.PgVector`.
Register the ADO.NET codec on the data source and the EF mappings on the provider:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePgVector()
    .Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource, provider => provider.UsePgVector())
    .Options;
```

`BlueTuskVector`, `BlueTuskHalfVector`, and `BlueTuskSparseVector` scalar and
array properties are mapped to their matching pgvector types; dimension-qualified
store types such as `vector(768)` are preserved. `EF.Functions.L2Distance`,
`MaxInnerProduct`, `CosineDistance`, and `L1Distance` translate to pgvector's
index-compatible `<->`, `<#>`, `<=>`, and `<+>` operators. `HammingDistance` and
`JaccardDistance` translate `BlueTuskBitString` operands to `<~>` and `<%>`.

Use `EnsurePgVector()` and `DropPgVector()` in migrations when
the application owns the extension lifecycle. These APIs and the BlueTusk EF
provider use the stable 1.0.0 Provider-family contract.
