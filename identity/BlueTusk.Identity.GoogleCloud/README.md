# BlueTusk.Identity.GoogleCloud

Optional Google authentication-library integration for manual IAM database
authentication to Cloud SQL for PostgreSQL. It scopes a `GoogleCredential` for
Cloud SQL login, refreshes the access token per new physical connection, and
requires TLS before requesting a token.

```csharp
using BlueTusk.Data;
using BlueTusk.Identity.GoogleCloud;
using Google.Apis.Auth.OAuth2;

var credential = await GoogleCredential.GetApplicationDefaultAsync();
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseGoogleCloudSqlIamAuthentication(credential)
    .Build();
```

Google's .NET token API is asynchronous. Use `OpenConnectionAsync`; a
synchronous open fails instead of blocking asynchronous credential I/O. The
advanced `ITokenAccess` overload accepts an already configured Cloud SQL login
token source.

Configure Cloud SQL IAM database authentication and `cloudsql.instances.login`.
For a service account, the PostgreSQL username omits the
`.gserviceaccount.com` suffix. The adapter handles tokens, not network tunnels;
use an authorized public/private route or the Cloud SQL Auth Proxy as required.
