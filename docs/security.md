# Secure a BlueTusk application

Use PostgreSQL roles, TLS, and application authorization together. Give each
process only the privileges it needs and verify the settings with the identity
that will serve traffic.

## Verify the database server

Store the connection string in a secret store. Configure certificate chain
and hostname verification for deployed environments:

```text
SSL Mode=VerifyFull;Channel Binding=Prefer;Persist Security Info=false
```

Use a trusted CA and a server name present in the certificate. Require channel
binding where your server and authentication provider support the policy.
Prefer SCRAM or a supported managed identity. See
[authentication](ado-net/authentication.md) and
[cloud identity](ado-net/cloud-identity.md) for configuration details.

## Separate database responsibilities

Use distinct logins or controlled role grants for application queries,
migrations, logical replication, and administration. Restrict the runtime
identity to its schemas, tables, sequences, and functions. Run migrations
with a deployment identity. Limit replication to the intended publication,
database, slot, and trusted consumer.

Pass user input through parameters. Validate any identifier or SQL fragment
against a server-owned allowlist before constructing a command. Treat user
supplied SQL, custom operators, and migration bodies as trusted code boundaries.

## Preserve tenant isolation

Apply PostgreSQL row-level security to the tables that hold tenant data.
Test with the actual application role. Superusers and roles with
`BYPASSRLS` bypass RLS, and table owners normally bypass it unless the table
forces RLS. Verify that the runtime login has no grant path to bypass the
policy. Set request-specific database state within the scope of the
transaction, and clear state before returning a session to the pool.

For Live and Continuous Graph, construct the server-side security scope
from authenticated caller and policy data. Use database roles and policies
that enforce that scope during initial queries, scoped updates, and repair.
Include every vertex and edge table in the policy. Verify that resume tokens,
query sharing, and authorized connections cannot cross tenant boundaries.

## Protect credentials and telemetry

Never include credentials, bearer or resume tokens, SQL parameter values,
result payloads, or tenant identifiers in metrics. Restrict detailed logs,
database dumps, backups, and support reproductions to the people and systems
that need them. Rehearse credential rotation, revocation, and expiry on an
isolated deployment with the same authentication mode.

See [observability](operations/observability.md) for safe dimensions and
the [security policy](../SECURITY.md) to report a suspected vulnerability.
