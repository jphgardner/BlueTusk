# Authentication

BlueTusk negotiates PostgreSQL SCRAM-SHA-256, SCRAM-SHA-256-PLUS channel
binding, PostgreSQL 18+ OAUTHBEARER, GSSAPI/Kerberos and SSPI, legacy MD5
challenges, and cleartext password challenges. TLS server
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

An access-token callback supplies a ready bearer/IAM token. When PostgreSQL 18+
advertises `OAUTHBEARER`, BlueTusk sends the token with the
[RFC 7628](https://www.rfc-editor.org/rfc/rfc7628) SASL mechanism. On servers requesting password, MD5, or SCRAM authentication, the
same callback supplies the token as the password, as required by database
services that use short-lived IAM tokens without PostgreSQL-native OAuth.

The callback is invoked once for each new physical connection, after TLS
negotiation and only when the server requests a credential. Pool checkout does
not refresh a token because an already-authenticated physical session does not
authenticate again. Clearing or expiring a pooled session causes the next
physical connection to request a new token.

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseAccessTokenProvider(async (request, cancellationToken) =>
        await tokenService.GetDatabaseTokenAsync(request, cancellationToken))
    .Build();
```

Native OAUTHBEARER always requires TLS and is incompatible with required channel
binding because the standardized mechanism has no channel-binding variant.
BlueTusk accepts an already-issued token; OAuth discovery, browser/device flows,
and refresh-token storage remain application concerns. Optional, separately
packaged [cloud identity adapters](cloud-identity.md) integrate AWS RDS/Aurora,
Azure Database for PostgreSQL, and Google Cloud SQL SDK credentials without
adding vendor dependencies to the core provider. PostgreSQL still requires a
correctly configured server-side OAuth validator; see PostgreSQL's
[OAuth authentication guide](https://www.postgresql.org/docs/current/auth-oauth.html).

## GSSAPI, Kerberos, and SSPI

When PostgreSQL requests GSSAPI, BlueTusk creates a platform
`NegotiateAuthentication` context with the Kerberos package. A PostgreSQL SSPI
request uses the platform Negotiate package and the same opaque-token wire
exchange. Both genuine synchronous and asynchronous opens support multistep
negotiation and require the server to be mutually authenticated before
`AuthenticationOk` is accepted.

The default service principal target is `postgres/<Host>`. Set
`Kerberos Service Name` when PostgreSQL was configured with a different
`krb_srvname` value:

```text
Host=db.example.test;Database=app;Username=worker;Kerberos Service Name=postgres
```

By default, the operating system supplies the process identity or credential
cache. An application that deliberately owns a separate credential can attach
it to the immutable data-source configuration; the credential is not placed in
the connection string:

```csharp
await using var dataSource = new BlueTuskDataSourceBuilder(connectionString)
    .UseGssCredential(await credentialVault.GetNetworkCredentialAsync())
    .Build();
```

GSSAPI does not invoke BlueTusk password or access-token callbacks. Returned
platform tokens and protocol write buffers are cleared after each transport
flush, and provider failures are reported without their original exception or
token content. Kerberos authenticates the peers but this PostgreSQL exchange
does not enable GSS-encrypted transport; configure TLS when database traffic
must be encrypted. Server principals, keytabs, role mapping, realms, and ticket
lifetime remain deployment responsibilities. See PostgreSQL's
[GSSAPI authentication guide](https://www.postgresql.org/docs/current/gssapi-auth.html).

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
temporary writable password/MD5/OAUTHBEARER/GSSAPI buffers are cleared. Immutable .NET strings
cannot be zeroed; keep connection strings and callback results short-lived and
never log them. Callback failures are reported without retaining the original
exception, preventing a callback exception message from leaking a credential.
