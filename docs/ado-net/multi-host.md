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

`BlueTuskDataSource` partitions physical pools by host endpoint. Each endpoint independently enforces minimum/maximum size, idle and maximum lifetime, reset, warm-up, and draining. A checkout tries immediate capacity across the selected host order before waiting, so saturation of one endpoint can route work to another acceptable endpoint. Targeted checkouts refresh the server role before acceptance, and each returned lease routes back to its owning endpoint pool.

`GetPoolStatistics()` aggregates all endpoint pools. `GetHostPoolStatistics()` returns the same counters keyed by `BlueTuskHostEndpoint`. Pool-size settings apply per endpoint, so a three-host data source with `Maximum Pool Size=20` has an aggregate maximum of 60 physical sessions.
