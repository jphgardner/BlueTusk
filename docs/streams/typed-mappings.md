# Typed change mappings

This guide shows you how to turn the rows in a change into instances of your
own classes, either by convention, with explicit column bindings, or from an
EF Core model.

A mapping is an optional layer over the dynamic `ChangeRow`. The original row,
with every [column state](concepts.md#what-a-column-value-can-be), is always
still available, and a mapping never invents a complete object from an
incomplete row.

## Map a table to a class

Start with a class that has a public parameterless constructor and public
settable properties:

```csharp
public sealed class Order
{
    public long Id { get; set; }

    public string Description { get; set; } = "";
}
```

Build the mapping from the table (`ChangeTable`) that Streams reports for the
change, for example `insert.NewRow.Table`. Map by convention: `Id` binds to
`id`, `Description` to `description`, and so on (PascalCase to snake_case):

```csharp
var mapping = new ChangeEntityMappingBuilder<Order>().Build(relation);
```

Or bind columns explicitly and check their PostgreSQL type OIDs:

```csharp
var mapping = new ChangeEntityMappingBuilder<Order>()
    .ToTable("app", "orders")
    .HasKey("id")
    .Property(order => order.Id, "id", expectedTypeOid: 20)
    .Property(order => order.Description, "description", expectedTypeOid: 25)
    .Build(relation);

if (mapping.Map(dynamicChange) is InsertChange<Order> { NewRow.HasValue: true } insert)
{
    Console.WriteLine($"Order {insert.NewRow.Value!.Id}: {insert.NewRow.Value.Description}");
}
```

`Map` returns `InsertChange<T>`, `UpdateChange<T>`, `DeleteChange<T>` or
`TruncateChange<T>` for the mapped table, and returns any other change
unchanged. Build the mapping once and reuse it; property setters and decoders
are prepared when you call `Build`.

The default decoders handle these .NET types:

| Value encoding | Decoded by default |
| --- | --- |
| `Text` (streamed changes, by default) | `string`, `byte[]`, `bool`, `short`, `int`, `long`, `float`, `double`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, enums |
| `Binary` (snapshot rows) | `byte[]`, `bool`, `short`, `int`, `long`, `float`, `double`, `Guid` |

For anything else, pass a `decoder` (`ChangeColumnDecoder<TProperty>`) to
`Property`. A failed decode throws `TypedChangeDecodingException`.

## Handle partial rows

`ChangeRow<T>.HasValue` is `true` only when every mapped column had a value.
It is `false` when any mapped column is not published, missing from an old row,
or an unchanged TOASTed value. In that case `Value` is not set; use
`ChangeRow<T>.Columns` to read the raw row instead.

A database `NULL` in a non-nullable property is a decoding failure, not a
default value. Use nullable property types for nullable columns.

For complete old rows on update and delete, set the table's replica identity to
`FULL`.

## Decide what happens when the schema changes

Each mapping has two fingerprints:

- `SchemaFingerprint` describes the table: schema, name, replica identity, and
  each column's name, type, modifier and key flag.
- `MappingFingerprint` adds your class and its column bindings.

Use `MappingFingerprint` as the `mappingFingerprint` of your checkpoint, so a
changed mapping cannot silently reuse an old checkpoint.

When a change arrives for a table whose shape differs from the one you built
the mapping with, the `ChangeMappingPolicy` decides what happens:

```csharp
var mapping = new ChangeEntityMappingBuilder<Order>().Build(
    relation,
    new ChangeMappingPolicy
    {
        SchemaChangeMode = SchemaChangeMode.Fail,
        DecodingFailureMode = TypedDecodingFailureMode.ContinueDynamically,
    });
```

| `SchemaChangeMode` | When the table shape changes |
| --- | --- |
| `PauseAndReload` (default) | Throws `ChangeSchemaReloadRequiredException` with both table definitions. Rebuild the mapping and retry. |
| `Fail` | Throws `ChangeSchemaMismatchException`. |
| `ContinueDynamically` | Returns the untyped change. |
| `ApplicationCallback` | Calls `SchemaChangeCallback`, which returns `Pause`, `Fail` or `ContinueDynamically`. |

| `TypedDecodingFailureMode` | When a value cannot be decoded |
| --- | --- |
| `Pause` (default) | Throws `TypedChangeDecodingException`. |
| `ContinueDynamically` | Returns the untyped change. |
| `ApplicationCallback` | Calls `DecodingFailureCallback`. |

Do not acknowledge a delivery after a mapping exception. Fix the cause, then
restart; the transaction is delivered again.

## Build mappings from an EF Core model

`BlueTusk.Streams.EntityFrameworkCore` reads table, schema, key and column names
from your EF Core model:

```csharp
var mapping = BlueTuskEfChangeMappingFactory.Create<Order>(dbContext.Model, relation);
```

with a context such as:

```csharp
public sealed class ShopContext : DbContext
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseBlueTusk("Host=localhost;Database=app");

    protected override void OnModelCreating(ModelBuilder model) =>
        model.Entity<Order>(order =>
        {
            order.ToTable("orders", "app");
            order.Property(o => o.Id).HasColumnName("id");
            order.Property(o => o.Description).HasColumnName("description");
        });
}
```

Building the model does not open a connection. `Create` checks the model
against the table at startup and throws `EfChangeMappingValidationException`
with one or more `BTSEF...` codes if, for example, the table differs, the entity
has no key, a mapped property or key column is not published, a property has
no public setter, or two properties bind the same column. It never fills
missing properties with defaults.

## Use mappings with NativeAOT and trimming

> **New in 1.1.0:** NativeAOT support for typed mappings is not in 1.0.0 or
> 1.1.0-rc.1.

Convention mapping keeps the public-property metadata it needs for trimming and
works under NativeAOT. If you wrap `ChangeEntityMappingBuilder<T>` in your own
generic code, put
`[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]`
on your type parameter too. EF Core model discovery and some optional packages
are not NativeAOT compatible; test the packages you use.

## Snapshot rows

Snapshot batches (see [snapshot and catch-up](snapshot-bootstrap.md)) contain
`ChangeSnapshotRow` values. Map them with `mapping.MapRow(row.Row)`. Copied
values use binary encoding, so text columns need a decoder:

```csharp
var mapping = new ChangeEntityMappingBuilder<Order>()
    .Property(
        order => order.Description,
        "description",
        decoder: (column, value) => Encoding.UTF8.GetString(value.Data.Span))
    .Build(relation);
```

This decoder works for both text and binary values, because PostgreSQL sends
`text` as UTF-8 in both forms. A snapshot row's identity is its
`SnapshotRowId` (epoch, table and key), not a `ChangeId`.

## Related pages

- [Concepts](concepts.md)
- [Configuration: mapping policy](configuration.md#typed-mappings)
