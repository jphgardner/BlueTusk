# Authentication

BlueTusk negotiates PostgreSQL SCRAM-SHA-256, SCRAM-SHA-256-PLUS channel
binding, legacy MD5 challenges, and cleartext password challenges. TLS server
certificate validation uses the platform policy by default. See PostgreSQL's
[password authentication](https://www.postgresql.org/docs/current/auth-password.html)
and [encryption options](https://www.postgresql.org/docs/current/encryption-options.html)
for server configuration guidance.

## Credential sources

Credentials are resolved lazily only when PostgreSQL asks for a password. A
certificate-authenticated or trusted connection therefore does not read a
password file or invoke a callback. The precedence for a password challenge is:

1. access-token callback;
2. password callback;
3. explicit `Password` connection setting; then
4. the first matching PostgreSQL password-file entry.

Password and access-token callbacks are mutually exclusive. A synchronous open
requires a synchronous callback; asynchronous opens prefer the asynchronous
callback and fall back to the synchronous callback when necessary. Configure
both callback forms when the same data source is used by synchronous and
asynchronous callers:

```csharp
var builder = new BlueTuskDataSourceBuilder(connectionString)
    .UsePasswordProvider(request => vault.GetPassword(request.Host, request.Username))
    .UsePasswordProvider(async (request, cancellationToken) =>
        await vault.GetPasswordAsync(request.Host, request.Username, cancellationToken));

await using var dataSource = builder.Build();
```

An access-token callback supplies a token in PostgreSQL's password field, as
used by database services with short-lived IAM tokens. It is invoked once for
each new physical connection, after TLS negotiation and only when the server
requests a credential. Pool checkout does not refresh a token because an
already-authenticated physical session does not authenticate again. Clearing or
expiring a pooled session causes the next physical connection to request a new
token.

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAccessTokenProvider(async (request, cancellationToken) =>
        await tokenService.GetDatabaseTokenAsync(request, cancellationToken))
    .Build();
```

This callback is not OAuth/OAUTHBEARER protocol support. OAuth SASL and cloud
SDK-specific integrations remain separate post-initial features.

## PostgreSQL password files

Set `Passfile=/path/to/file` to select a password file. When neither a callback
nor an explicit password is configured, BlueTusk checks `PGPASSFILE`, then the
platform default: `%APPDATA%\postgresql\pgpass.conf` on Windows or
`$HOME/.pgpass` on Unix. The file follows PostgreSQL's
[`hostname:port:database:username:password`](https://www.postgresql.org/docs/current/libpq-pgpass.html)
format, first-match ordering, `*` wildcards, and backslash escaping for colons
and backslashes.

Physical replication sessions match the special `replication` database field;
logical database replication continues to match its configured database name.

On Unix, group or world permissions cause the file to be ignored; use mode
`0600`. Windows relies on the security of the application-data directory, which
matches PostgreSQL behavior. An empty `BlueTuskClientOptions.Passfile` disables
default password-file lookup for low-level clients.

## TLS client certificates

Low-level clients can supply `BlueTuskClientOptions.ClientCertificates` and an
optional `LocalCertificateSelectionCallback`. A data source provides fluent
equivalents and preserves them for pooled, unpooled, notification, and dedicated
replication sessions:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseClientCertificate(clientCertificate)
    .UseClientCertificateSelectionCallback(
        (_, _, certificates, _, acceptableIssuers) => certificates[0])
    .Build();
```

Certificates remain caller-owned and must stay valid for the data source's
lifetime. The same runtime client identity and credential callbacks are retained
for EF physical-database maintenance connections. Platform server-certificate
and hostname validation remains enabled.
`UseRemoteCertificateValidationCallback` is available for private trust models,
but the callback becomes the security boundary and must validate the presented
certificate rather than accepting every certificate.

## Cleartext and legacy MD5 compatibility

PostgreSQL MD5 challenges are supported for legacy servers, but MD5 is
deprecated by PostgreSQL and SCRAM should be preferred. A cleartext password
challenge is accepted over TLS. On plaintext transport it fails closed unless
`Allow Unencrypted Password=true` is explicitly configured for a trusted
compatibility environment.

Authentication protocol buffers are overwritten after transport flushes and
temporary writable password/MD5 buffers are cleared. Immutable .NET strings
cannot be zeroed; keep connection strings and callback results short-lived and
never log them. Callback failures are reported without retaining the original
exception, preventing a callback exception message from leaking a credential.
