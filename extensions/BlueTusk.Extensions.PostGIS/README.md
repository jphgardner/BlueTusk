# BlueTusk.Extensions.PostGIS

Preview PostGIS transport support for BlueTusk. The package registers distinct
`BlueTuskGeometry` and `BlueTuskGeography` CLR values, native EWKB binary
transport, WKT/EWKT text input, hexadecimal EWKB text fallback, array
composition, and parameter inference.

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PostGIS;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePostGis()
    .Build();
var point = BlueTuskGeometry.FromText("SRID=4326;POINT(-0.1276 51.5072)");
await using var command = dataSource.CreateCommand(
    "SELECT ST_AsText($1::geometry)");
command.Parameters.Add(new BlueTuskParameter<BlueTuskGeometry>(point));
```

PostgreSQL must have `CREATE EXTENSION postgis` applied before the data source
is built. Pass the installation schema to `UsePostGis(schema)` when needed.
PostGIS remains responsible for parsing, coordinate-system validation,
topology, and spatial algorithms. The BlueTusk values intentionally preserve
opaque EWKB or server-parseable text rather than defining a competing geometry
object model.

This package and the BlueTusk extension SDK are experimental `0.3.0-preview.1`
APIs, not stable or production-ready contracts.
