# .NET, EF Core, and PostgreSQL compatibility

BlueTusk 1.2 runtime libraries target .NET 5 through .NET 11. Choose the
package assets that match your application’s target framework. Use the
[package catalog](https://bluetusk.io/packages) to install a matching release.

## Use the matching EF Core runtime and tools

| Application target | EF Core version used by the provider |
| --- | --- |
| .NET 5 | 5.0.17 |
| .NET 6 and .NET 7 | 7.0.20 |
| .NET 8 | 8.0.34 |
| .NET 9 | 9.0.23 |
| .NET 10 | 10.0.11 |
| .NET 11 | 11.0.0 |

Use the same EF Core major for design-time packages and the `dotnet-ef` tool.
APIs introduced in later EF Core versions, such as `ComplexProperty` in
EF Core 8, require that EF Core version or later. See the
[EF Core setup guide](../ef-core/README.md) for provider registration.

## Check integration prerequisites

- Streams, Sync, and Live Aspire integrations require .NET 8 or later.
- On .NET 5 and 6, use `BlueTuskDataSource` where the framework does not provide
  `DbDataSource`. BlueTusk supplies compatible batch base types on .NET 5.
- Use a runtime within [Microsoft’s support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
  for deployments that require runtime security updates.
- The production starter uses .NET 10. Use the SDK appropriate to your own
  application target and deployment environment.

## Match the PostgreSQL capabilities you use

For PostgreSQL 15–18, typed `Match` queries run over mapped relational tables.
The optional [BlueTusk server engine](../graph/engine.md) executes supported
bounded traversals when the installed extension and registration match.
Use [the Graph reference](../graph/reference.md) for the separate SQL/PGQ
compatibility option.

Logical replication requires the appropriate PostgreSQL configuration, a
publication, and a slot. Additional database features depend on the specific
[server extension and version](../extensions/reference.md) you install.

See [install packages](install.md) and [set up database access](quickstart.md)
to start an application.
