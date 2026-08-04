# Dependency injection and health checks

`BlueTusk.Data.DependencyInjection` provides the V1 host-registration surface
for applications that use `Microsoft.Extensions.DependencyInjection` and
`Microsoft.Extensions.Diagnostics.HealthChecks`.

## Registration

```csharp
services.AddDataSource(
    configuration.GetConnectionString("PostgreSQL")!,
    builder =>
    {
        builder.ConfigureDiagnostics(diagnostics);
        builder.ConfigureTypes(types);
    },
    healthCheckName: "postgresql");
```

The registration creates one long-lived `BlueTuskDataSource`. The same
instance is resolvable as:

- `BlueTuskDataSource` for provider-specific APIs; and
- `DbDataSource` for provider-neutral application and library code.

Do not register a data source as transient or create one per request. The data
source owns the physical pool and immutable provider configuration.

## Consuming the data source

Provider-neutral service:

```csharp
public sealed class AccountReader(DbDataSource dataSource)
{
    public async Task<Account?> FindAsync(
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name FROM accounts WHERE id = @id");
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.DbType = DbType.Int64;
        parameter.Value = id;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Account(reader.GetInt64(0), reader.GetString(1))
            : null;
    }
}
```

Provider-specific code can resolve `BlueTuskDataSource` when it needs a
dedicated replication session, PostgreSQL type configuration or another
BlueTusk-only surface.

## Readiness health check

When `healthCheckName` is supplied, registration adds a check tagged:

- `bluetusk`
- `ready`

The check opens a logical connection and executes `SELECT 1`. It verifies the
path an application needs for ordinary database work: configuration, DNS,
network reachability, authentication, pool acquisition and a server
round-trip.

It does not prove:

- that every migration has been applied;
- that a replication slot is healthy;
- that a standby has caught up;
- that a Sync destination is reachable; or
- that the application has domain-level read/write permission.

Add separate checks for those responsibilities.

## Liveness versus readiness

Database connectivity normally belongs in readiness, not liveness. Restarting
a healthy process because PostgreSQL is briefly unavailable can amplify an
incident.

Recommended policy:

| Probe | Includes BlueTusk check? | Purpose |
| --- | --- | --- |
| Liveness | No | Process is running and not deadlocked |
| Readiness | Yes | Instance can accept work that requires PostgreSQL |
| Startup | Optional | Slow initialization or migration coordination |

Configure the host’s health-check timeout below its request deadline and above
normal pool-acquisition plus network latency. Avoid sub-second probe intervals
that create load during a database incident.

## Ownership and disposal

The dependency-injection container disposes the registered data source when the
host stops. Application services should dispose only the logical connections,
commands, readers and transactions they create.

If an application owns multiple PostgreSQL configurations, register named
wrapper services or explicit application abstractions. Avoid service-locator
selection by raw connection string.

## Testing registrations

A unit test can resolve both service types and assert reference equality. A
live integration test should execute the readiness check against the selected
PostgreSQL test container.

The repository coverage is in
`tests/BlueTusk.Data.DependencyInjection.Tests`. Live cases read
`BLUETUSK_TEST_CONNECTION_STRING` and skip when it is absent; the dedicated CI
matrix supplies it.

## Migration from Npgsql DI

Provider-neutral services that consume `DbDataSource`, `DbConnection`,
`DbCommand`, Dapper and explicit `DbTransaction` can usually retain those
abstractions. Replace the registration and review provider-specific:

- type names and OIDs;
- connection-string options;
- notification and replication APIs;
- routine invocation conventions; and
- ambient transaction assumptions.

See the [ADO.NET compatibility matrix](compatibility.md) for intentional V1
exclusions.
