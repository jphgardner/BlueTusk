# Cloud identity

BlueTusk keeps cloud SDK dependencies out of the core provider. Install only
the adapter for the database service in use:

```powershell
dotnet add package BlueTusk.Identity.Aws
dotnet add package BlueTusk.Identity.Azure
dotnet add package BlueTusk.Identity.GoogleCloud
```

Each adapter installs an access-token callback on the data-source builder and
requires TLS before the callback is invoked. A token is acquired for every new
physical connection. Checking an existing physical connection out of the pool
does not acquire another token because PostgreSQL does not authenticate that
session again. Connection lifetime and pool clearing therefore bound how long
an authenticated session can remain reusable independently of token expiry.

Provider exceptions and token values are not attached to BlueTusk's
authentication errors. Tokens are never placed in the connection string or
logged by the adapters. The returned .NET `string` is immutable and cannot be
overwritten, so applications should not retain or log it.

## AWS RDS and Aurora PostgreSQL

`BlueTusk.Identity.Aws` uses AWS SDK for .NET v4 to generate a SigV4 RDS IAM
authentication token from the host, port, and PostgreSQL username of the
physical connection being opened:

```csharp
using Amazon;
using BlueTusk.Data;
using BlueTusk.Identity.Aws;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAwsRdsIamAuthentication(RegionEndpoint.EUWest2)
    .Build();
```

The parameterless overload uses the AWS SDK's standard region and credential
resolution chains. Other overloads accept an explicit `AWSCredentials`,
`RegionEndpoint`, or both. Synchronous and asynchronous BlueTusk opens use the
corresponding AWS SDK token-generation path.

Enable IAM database authentication, grant the PostgreSQL role `rds_iam`, and
allow the AWS identity to perform `rds-db:connect`. Use the actual RDS or Aurora
endpoint rather than a custom DNS alias because the host is part of the
signature. AWS tokens are valid for 15 minutes; BlueTusk requests a fresh token
for each new physical connection rather than caching it. See AWS's
[IAM database authentication guide](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.IAMDBAuth.html)
and [.NET token-generator API](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/RDS/TRDSAuthTokenGenerator.html).

## Azure Database for PostgreSQL

`BlueTusk.Identity.Azure` accepts any `Azure.Core.TokenCredential`, including
managed identity, workload identity, a service principal, or a local developer
credential:

```csharp
using Azure.Identity;
using BlueTusk.Data;
using BlueTusk.Identity.Azure;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAzurePostgreSqlEntraAuthentication(new DefaultAzureCredential())
    .Build();
```

The adapter requests the public-cloud PostgreSQL scope
`https://ossrdbms-aad.database.windows.net/.default`. Pass the documented scope
explicitly for a sovereign cloud. Configure the connection username for the
Microsoft Entra principal created in PostgreSQL. Synchronous and asynchronous
BlueTusk opens call the matching `TokenCredential` API. The adapter package
depends on `Azure.Core`; the application chooses and owns its `Azure.Identity`
credential implementation. See Microsoft's
[managed identity connection guide](https://learn.microsoft.com/azure/postgresql/security/security-connect-with-managed-identity).

## Google Cloud SQL for PostgreSQL

`BlueTusk.Identity.GoogleCloud` scopes a `GoogleCredential` for Cloud SQL login
and supplies its access token as the PostgreSQL password:

```csharp
using BlueTusk.Data;
using BlueTusk.Identity.GoogleCloud;
using Google.Apis.Auth.OAuth2;

var credential = await GoogleCredential.GetApplicationDefaultAsync();
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseGoogleCloudSqlIamAuthentication(credential)
    .Build();
```

Google's official .NET token surface is asynchronous. Use
`OpenConnectionAsync`; a synchronous physical open fails with the standard
missing-synchronous-provider error instead of blocking asynchronous credential
I/O. The advanced `ITokenAccess` overload accepts an already scoped token
source. Configure Cloud SQL IAM database authentication and grant
`cloudsql.instances.login`. A service-account PostgreSQL username omits the
`.gserviceaccount.com` suffix.

This adapter handles identity only. Use an authorized public or private route,
or the Cloud SQL Auth Proxy, for network connectivity. See Google's
[IAM database login guide](https://docs.cloud.google.com/sql/docs/postgres/iam-logins).

## Live acceptance tests

Deterministic tests validate token generation or scope selection, TLS policy,
sync/async behavior, and secret non-disclosure without contacting a cloud.
Account-backed acceptance is opt-in and reads these complete connection
strings:

```powershell
$env:BLUETUSK_AWS_RDS_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
$env:BLUETUSK_AZURE_POSTGRESQL_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
$env:BLUETUSK_GOOGLE_CLOUD_SQL_TEST_CONNECTION_STRING = "Host=...;Database=...;Username=...;SSL Mode=VerifyFull"
dotnet test tests/BlueTusk.Identity.Tests
```

The AWS and Azure tests use their default SDK identity chains. The Google test
uses application default credentials. Missing variables skip only the matching
external-account test; no cloud credential belongs in source control.
