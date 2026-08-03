# BlueTusk.Extensions.PostGIS.EntityFrameworkCore

Preview NetTopologySuite integration for BlueTusk's separately packaged
PostGIS transport. Register the EWKB codecs on the long-lived data source and
the spatial mappings on the EF provider:

```csharp
using BlueTusk.Data;
using BlueTusk.Extensions.PostGIS;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UsePostGis()
    .Build();

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseBlueTusk(dataSource, provider => provider.UsePostGis())
    .Options;
```

`Geometry`, `Point`, `LineString`, `Polygon`, `MultiPoint`,
`MultiLineString`, `MultiPolygon`, and `GeometryCollection` scalar and array
properties map to PostGIS `geometry` by default. Exact typmods and geography
intent are retained through ordinary EF configuration:

```csharp
entity.Property(value => value.Location)
    .HasColumnType("geometry(Point,4326)");
entity.Property(value => value.ServiceArea)
    .HasColumnType("geography(Point,4326)");
```

NetTopologySuite predicates, measurements, set operations, buffers, and common
members translate to schema-qualified PostGIS functions. The package also adds
`EF.Functions.IsWithinDistance`, `BoundingBoxIntersects`, `Transform`,
`MakeValid`, `Force2D`, and `AsGeoJson`. Geography translation is deliberately
limited to operations PostGIS defines for geography: distance, within-distance,
intersection, covers/covered-by, area, length, and centroid. A geometry-only
operation on geography fails translation with a focused diagnostic.

The public conversion helpers preserve SRID and XY/Z/M ordinates while bridging
between NetTopologySuite objects and BlueTusk's immutable EWKB transport values.
EF change tracking snapshots mutable geometry instances and geometry arrays.

Use `EnsurePostGis()` and `DropPostGis()` in migrations when the
application owns the extension lifecycle. The live PostgreSQL 18/PostGIS 3.6
gate covers geometry, geography, typmods, arrays, parameters, spatial filters,
projections, and compiled queries. These APIs remain experimental
`0.3.0-preview.1` contracts.
