# BlueTusk

**PostgreSQL for .NET.**

[Website](https://bluetusk.io/) · [Documentation](docs/README.md) · [Quickstart](docs/getting-started/quickstart.md)

BlueTusk provides ADO.NET and EF Core access to PostgreSQL. Use the same
database for application queries, committed change delivery, graph traversals,
and durable workloads such as jobs and projections.

## Start with your workload

| Need | Go to |
| --- | --- |
| Run SQL, manage connections, or load rows in bulk | [ADO.NET](docs/ado-net/README.md) |
| Map entities and manage migrations | [EF Core](docs/ef-core/README.md) |
| Consume commits, synchronize systems, or push authorized updates | [Real-time data](docs/realtime-platform/README.md) |
| Query relationships or maintain graph results | [Graphs](docs/graph/README.md) and [Continuous Graph](docs/continuous-graph/README.md) |
| Use PostgreSQL types, vector search, geospatial data, or time series | [Extensions](docs/extensions/README.md) |
| Store documents and events, run jobs, or build an offline app | [Data services](docs/ecosystem/README.md) |
| Secure, deploy, or troubleshoot an application | [Production checklist](docs/operations/production-checklist.md) |

## Run your first query

The [quickstart](docs/getting-started/quickstart.md) uses the .NET 10 SDK and
PostgreSQL 15–18. From an existing .NET project, add the provider:

```sh
dotnet add package BlueTusk.Data --version 1.2.0
```

Create a shared data source and pass input using parameters:

```csharp
using BlueTusk.Data;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString).Build();
await using var command = dataSource.CreateCommand("SELECT $1::int4 + $2::int4");
command.Parameters.Add(new BlueTuskParameter<int>(20));
command.Parameters.Add(new BlueTuskParameter<int>(22));
Console.WriteLine(await command.ExecuteScalarAsync<int>());
```

Use the [installation guide](docs/getting-started/install.md) to match
packages to your runtime and server. [Application examples](docs/getting-started/examples.md)
show complete database, worker, and browser flows.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup and
[website/README.md](website/README.md) for the documentation site.
Report a suspected vulnerability privately using [SECURITY.md](SECURITY.md).

BlueTusk is available under the [MIT License](LICENSE).
