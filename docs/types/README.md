# Use PostgreSQL types

BlueTusk maps common PostgreSQL values directly to normal .NET types. Start
here to read and write values safely; use the [complete type
reference](reference.md) only when you need a specialised or catalogue-defined
type.

## The normal path

Use typed parameters and typed reader methods. BlueTusk sends parameter values
separately from SQL and chooses the PostgreSQL wire format for you:

```csharp
await using var command = dataSource.CreateCommand(
    "SELECT @id::int8, @created::timestamptz, @tags::text[]");

command.Parameters.Add(new BlueTuskParameter<long>(42)
{
    ParameterName = "id",
});
command.Parameters.Add(new BlueTuskParameter<DateTimeOffset>(DateTimeOffset.UtcNow)
{
    ParameterName = "created",
});
command.Parameters.Add(new BlueTuskParameter<string[]>(["paid", "priority"])
{
    ParameterName = "tags",
    PostgreSqlTypeName = "pg_catalog.text[]",
});

await using var reader = await command.ExecuteReaderAsync();
await reader.ReadAsync();

var id = reader.GetInt64(0);
var created = reader.GetFieldValue<DateTimeOffset>(1);
var tags = reader.GetFieldValue<string[]>(2);
```

## Common mappings

| PostgreSQL             | Typical .NET type         | Notes                                          |
| ---------------------- | ------------------------- | ---------------------------------------------- |
| `int2`, `int4`, `int8` | `short`, `int`, `long`    | Use the matching width.                        |
| `numeric`              | `decimal`                 | Check precision and scale in the schema.       |
| `text`, `varchar`      | `string`                  | Length rules remain a schema concern.          |
| `uuid`                 | `Guid`                    | Native binary mapping.                         |
| `bytea`                | `byte[]` or streaming API | Stream very large values.                      |
| `date`                 | `DateOnly`                | Calendar date without a time zone.             |
| `timestamp`            | `DateTime`                | No time-zone conversion.                       |
| `timestamptz`          | `DateTimeOffset`          | Prefer UTC at application boundaries.          |
| `json`, `jsonb`        | JSON/string mapping       | Choose an explicit application representation. |
| `type[]`               | `T[]`                     | Element mapping must also be known.            |

`ExecuteScalarAsync<T>()` converts its result with the same rules as
`GetFieldValue<T>()`, so `ExecuteScalarAsync<decimal>()` reads a `numeric`
result. For a column that can be `NULL`, ask for a nullable type such as
`decimal?` or `int?`: a non-nullable `T` returns its default value instead.

## Null values need a type

PostgreSQL cannot always infer the intended type of a null parameter. State it
explicitly:

```csharp
command.Parameters.Add(new BlueTuskParameter(null)
{
    ParameterName = "status",
    PostgreSqlTypeName = "app.order_status",
});
```

Use `DbType`, `PostgreSqlTypeOid`, or `PostgreSqlTypeName`; do not rely on an
ambiguous server guess. `PostgreSqlTypeName` must include the schema, for
example `pg_catalog.jsonb` or `app.order_status`. The same applies to a
`string[]` parameter, as in the first example: give it
`PostgreSqlTypeName = "pg_catalog.text[]"`.

## Map application-defined types once

Register enums and composites before building the long-lived data source:

```csharp
var builder = new BlueTuskDataSourceBuilder(connectionString);
builder.MapEnum("app.order_status", new Dictionary<OrderStatus, string>
{
    [OrderStatus.Pending] = "pending",
    [OrderStatus.Paid] = "paid",
});
builder.MapComposite<Address>("app.address");
await using var dataSource = builder.Build();
```

Without a labels dictionary, `MapEnum` takes each member's label from
`[BlueTuskName("paid")]` (namespace `BlueTusk.TypeSystem`), then from
`[EnumMember(Value = "paid")]`, and otherwise uses the CLR member name exactly
(`Paid`, not `paid`). With EF Core, prefer the attributes: LINQ writes an enum
constant with its attribute label but cannot see a labels dictionary (see
[EF Core enums](../ef-core/concepts.md#postgresql-enums-need-three-pieces)).
`MapComposite` matches members to fields by snake_case name (`HouseNumber` to
`house_number`).

A mapped type does not have to exist when the data source first connects.
Until the catalogue contains it, the mapping stays unresolved and only a value
that uses it fails. EF Core migrations reload the catalogue after they run, so
an application can map `app.order_status` before the migration that creates it.
After creating or changing a type any other way while the application is
running, call `ReloadTypesAsync()` on the data source before using the new
catalogue shape.

## Production rules

- Keep the database schema and CLR mapping versioned together.
- Prefer typed parameters over string conversion.
- Use sequential readers for large binary or text values.
- Treat unknown extension values as opaque until a semantic codec is installed.
- Test boundary values: null, infinity, precision, time zones, and array bounds.

The [complete reference](reference.md) documents numeric edge cases, temporal
rules, ranges, multiranges, arrays, composites, domains, vectors, JSONPath,
catalogue vectors, and opaque values.
