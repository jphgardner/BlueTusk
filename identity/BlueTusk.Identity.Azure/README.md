# BlueTusk.Identity.Azure

Optional Azure SDK integration for Microsoft Entra authentication to Azure
Database for PostgreSQL. It accepts any `Azure.Core.TokenCredential`, requests
the official PostgreSQL scope, refreshes per new physical connection, and
requires TLS before invoking the credential.

```csharp
using Azure.Identity;
using BlueTusk.Data;
using BlueTusk.Identity.Azure;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAzurePostgreSqlEntraAuthentication(new DefaultAzureCredential())
    .Build();
```

The default scope is
`https://ossrdbms-aad.database.windows.net/.default`; sovereign-cloud
deployments can pass their documented scope explicitly. Configure the
PostgreSQL username for the Entra principal created on the server.

The package depends only on `Azure.Core`, so applications select their desired
credential implementation through `Azure.Identity` or another Azure SDK
package. Tokens are never placed in the connection string, cached, or logged.
