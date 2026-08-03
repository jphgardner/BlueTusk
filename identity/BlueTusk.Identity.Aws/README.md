# BlueTusk.Identity.Aws

Optional AWS SDK v4 integration for RDS and Aurora PostgreSQL IAM database
authentication. It signs a fresh token for every new physical connection,
uses the selected host, port, and PostgreSQL username, and requires TLS before
the SDK credential chain is invoked.

```csharp
using Amazon;
using BlueTusk.Data;
using BlueTusk.Identity.Aws;

await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAwsRdsIamAuthentication(RegionEndpoint.EUWest2)
    .Build();
```

The parameterless overload uses the AWS SDK region and credential resolution
chains. Other overloads accept an explicit `AWSCredentials`, `RegionEndpoint`,
or both. RDS IAM must be enabled, the PostgreSQL role must have `rds_iam`, and
the AWS identity must have `rds-db:connect`. Use the actual RDS endpoint rather
than a custom DNS alias because the host is part of the signature.

The package never caches or logs signed tokens. Pool checkout does not request
a token; opening the next physical connection does.
