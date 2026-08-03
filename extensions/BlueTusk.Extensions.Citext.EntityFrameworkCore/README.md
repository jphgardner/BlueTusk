# BlueTusk.Extensions.Citext.EntityFrameworkCore

Optional EF Core mapping and migration integration for
`BlueTusk.Extensions.Citext`. Keep the ADO.NET data-source registration and EF
registration explicit and separate:

```csharp
var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseCitext()
    .Build();

options.UseBlueTusk(dataSource, provider => provider.UseCitext());
```

`BlueTuskCitext` properties then map to `"public"."citext"`, including arrays,
and normal equality operators retain PostgreSQL's server-side case-insensitive
semantics. Use `migrationBuilder.EnsureBlueTuskCitext()` in `Up` before creating
objects that use the type, and `migrationBuilder.DropBlueTuskCitext()` in `Down`
when the application owns the extension installation.
