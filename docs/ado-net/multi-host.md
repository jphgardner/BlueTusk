# Multi-host connections

BlueTusk accepts PostgreSQL's keyword/value multi-host form. `Host` is a comma-separated ordered list; `Port` can contain one shared port or a positionally matching list.

```text
Host=db-a,db-b,db-c;Port=5432;Target Session Attributes=read-write
Host=db-a,db-b;Port=5432,6432;Target Session Attributes=prefer-standby
```

Hosts are attempted in configuration order by default. Set `Load Balance Hosts=random` to shuffle the host order for each new physical connection. DNS expansion within one host remains the transport layer's responsibility.

`Target Session Attributes` accepts:

- `any`
- `primary`
- `standby`
- `prefer-primary`
- `prefer-standby`
- `read-write`
- `read-only`

BlueTusk probes `pg_is_in_recovery()` and `transaction_read_only` after authentication when role selection is required. A strict target rejects incompatible servers; a preferred target retains the first healthy fallback while it searches the remaining hosts. Network and server-availability failures advance to the next host, while authentication rejection stops the sequence. This follows PostgreSQL's [multiple-host and target-session connection behavior](https://www.postgresql.org/docs/current/libpq-connect.html#LIBPQ-MULTIPLE-HOSTS).

`BlueTuskConnection.ConnectedEndpoint` reports the selected host and port. Failure messages identify attempted endpoints but never include passwords or authentication payloads.

Per-host pool partitioning is still in progress for 0.1.0. Until that lands, pooled multi-host sessions share the data source's aggregate pool.
