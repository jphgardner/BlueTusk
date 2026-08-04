# Schema discovery

BlueTusk implements provider-neutral ADO.NET schema discovery for applications,
libraries and diagnostic tools that inspect database metadata at runtime.

## Connection collections

Call `GetSchema()` to list supported collections:

```csharp
await using var connection = await dataSource.OpenConnectionAsync();
var collections = await connection.GetSchemaAsync();
```

V1 supports:

| Collection | Requires open connection | Purpose |
| --- | --- | --- |
| `MetaDataCollections` | No | Lists the available schema collections |
| `DataSourceInformation` | No | Provider and identifier behavior |
| `DataTypes` | No | Provider type metadata |
| `Restrictions` | No | Restriction positions for each collection |
| `ReservedWords` | No | PostgreSQL reserved words |
| `Databases` | Yes | Visible databases |
| `Schemas` | Yes | Visible schemas |
| `Tables` | Yes | Tables and views filtered by restrictions |
| `Columns` | Yes | Column metadata filtered by restrictions |

Catalogue-backed collections require an open connection because visibility is
defined by the authenticated PostgreSQL session.

## Restrictions

Restrictions are positional and collection-specific. For example:

```csharp
var tables = await connection.GetSchemaAsync(
    "Tables",
    [
        connection.Database,
        "public",
        "orders",
        "BASE TABLE",
    ]);
```

Use `null` for a restriction you do not want to constrain. Treat returned
metadata as the current authenticated view of the catalogue, not a durable
schema snapshot.

## Reader column schema

After executing a command, use `GetColumnSchema` or `GetSchemaTable`:

```csharp
await using var command = connection.CreateCommand();
command.CommandText = """
    SELECT id, created_at, payload
    FROM events
    WHERE tenant_id = @tenant_id
    """;
command.Parameters.Add(
    new BlueTuskParameter<Guid>("tenant_id", tenantId));

await using var reader = await command.ExecuteReaderAsync(cancellationToken);
var columns = await reader.GetColumnSchemaAsync(cancellationToken);
var table = await reader.GetSchemaTableAsync(cancellationToken);
```

Column metadata exposes the information available from PostgreSQL row
descriptions and provider type resolution, including:

- column name and ordinal;
- CLR data type;
- provider type identity and name;
- nullability when known;
- size/precision/scale when known; and
- source relation/column metadata when PostgreSQL supplies it.

Unavailable metadata remains unavailable; BlueTusk does not invent key or
origin information.

## Intentional exclusions

`CommandBehavior.SchemaOnly` and `CommandBehavior.KeyInfo` are excluded in V1
and throw `NotSupportedException`. Silently treating either as
`CommandBehavior.Default` would execute behavior the caller did not request.

Use a bounded query that returns no rows when you need PostgreSQL to describe a
result shape:

```sql
SELECT id, created_at, payload
FROM events
WHERE false
```

This still parses, plans and describes an ordinary query, so apply the same SQL
trust and timeout policy as any other command.

## Tooling guidance

Schema discovery is useful for:

- Dapper-style mapping diagnostics;
- admin and inspection tools;
- code-generation inputs;
- compatibility probes; and
- generic data export.

It is not a replacement for EF Core reverse engineering, which preserves a
larger PostgreSQL-specific model including indexes, constraints, policies,
partitions, routines, extensions and other schema objects.

For database-first EF workflows, use the
[EF Core scaffolding guidance](../ef-core/README.md). For catalogue-driven
provider types, read the [type system guide](../types/README.md).
