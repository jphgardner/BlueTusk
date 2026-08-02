# Allocation discipline

BlueTusk treats low allocation as a measured engineering constraint, not as a blanket “allocation-free” claim. Returned strings, arrays, records, and buffered large fields own managed memory by design. The provider instead targets bounded temporary storage, typed access without incidental boxing, and reusable per-session protocol buffers where ownership is unambiguous.

The `CommandPathBenchmarks` fixture measures named-parameter rewriting, parameter encoding, physical-session dispatch, and scalar or reader materialization together. Its physical session is in-memory so network scheduling does not hide provider bookkeeping. `ProtocolWritePathBenchmarks` separately measures complete simple and extended frontend writes through `BlueTuskProtocolConnection`.

The Windows/Ryzen 7 5800X/.NET 10 short baseline currently records:

| Workload | Allocated per operation | Ownership represented |
| --- | ---: | --- |
| Synchronous binary `int4` parameter and scalar | 1,048 B | command plan, parameter vector/payload, timeout, boxed `DbCommand` scalar |
| Text parameter and scalar | 1,424 B | command path plus UTF-8 parameter and returned CLR string |
| Buffered reader over 100 binary `int4` values | 2,560 B | reader/command objects; typed values do not allocate individually |
| Asynchronous binary `int4` parameter and scalar | 1,352 B | command path plus async cancellation/timeout infrastructure |
| Warm simple or extended protocol write | 0 B | reusable session writer after warm-up |
| Structured `int4[]` / composite parameter encoding | 384 B / 56 B | exact caller-owned wire payloads; temporary composite sizing storage is pooled |
| Incremental one-megabyte backend payload drain | 176 B | bounded streaming state, not field materialization |
| One-kilobyte WAL decode / bounded WAL pull | 64 B | decoded envelope; WAL payload remains a zero-copy slice |
| Buffered one-megabyte `bytea` | 1,049,117 B | the caller-owned byte array is inherent |
| Buffered one-megabyte text | 2,097,447 B | the caller-owned UTF-16 string is inherent |
| EF compile plus first execution of a parameterized query | 132,048 B | fresh relational compilation, context/query state, and one scalar result |
| EF materialization of 100 orders | 164,679 B | context/query state plus caller-owned entities and strings |
| EF insert / load-and-update | 27,462 B / 37,665 B | normalized tracked write and `SaveChanges` paths |
| Prepared raw / typed EF traversal of 999 edges | 187,936 B / 685,864 B | readers plus caller-owned typed graph results |

The live PostgreSQL 19 comparison separates provider efficiency from the
in-memory ownership budgets above. Its final 2026-08-02 ShortRun records:

| Workload | BlueTusk | Npgsql | Current result |
| --- | ---: | ---: | --- |
| Parameterized scalar | 2,064 B | 2,113 B | BlueTusk 2.3% lower |
| Explicitly prepared scalar | 992 B | 1,132 B | BlueTusk 12.4% lower |
| Untouched warm checkout | 168 B | 184 B | BlueTusk 8.7% lower |
| Sequential 1,000-row read | 5,519 B | 1,600 B | BlueTusk 3.45x higher; open gap |
| Sequential 1 MiB `bytea` | 12,610 B | 8,983 B | BlueTusk 1.40x higher; open gap |

The same run measures warm checkout at 199 ns for BlueTusk and 210 ns for
Npgsql. The other four BlueTusk latency results remain 1.28x to 1.50x Npgsql;
allocation wins are not presented as provider-wide latency wins.

`BlueTuskProtocolConnection` retains one writer per physical session, clears it after every successful or failed write, rejects overlapping writes, and replaces writer storage that grows beyond 64 KiB so an exceptional command does not permanently inflate every pooled session. Its receive side rents one 64 KiB buffer per physical session and uses that same storage as bounded read-ahead for incremental large fields; caller-visible streams still do not materialize the field. Runtime structured-codec encoding rents temporary sizing storage and copies only the exact payload into the caller-owned parameter value before returning the temporary buffer. Replication decodes one pulled frame at a time and retains its WAL body over the received memory; the 64-byte message object is measured and intentionally budgeted rather than described as allocation-free.

Warm command instances cache the structural named-parameter plan, but parameter
values are encoded on every execution and prepared-statement type identity is
revalidated. Asynchronous scalar execution drains the complete protocol group
for connection safety while retaining only the first value needed by ADO.NET.
Prepared scalar commands reuse statement metadata captured by `Prepare`, and
fixed-width prepared values reuse command-owned wire buffers while still being
re-encoded after every value mutation. Timeout cancellation shares the command's
CancelRequest timer instead of allocating linked cancellation sources per
operation. Untouched logical connections avoid allocating rare transaction,
notification, and large-object state. Large streamed payloads rent an adaptive
read-ahead buffer, return legal partial reads without wrapping each transport
wait at every abstraction layer, and return to the 64 KiB steady-state window
at the next frame.

Machine-readable limits live in `benchmarks/allocation-budgets.json`. They intentionally allow modest short-run/runtime variance while keeping zero-allocation protocol writes strict. Refresh the named reports, review any ownership change, and then run:

```powershell
pwsh -File eng/verify-allocation-budgets.ps1
```

Raising a budget requires an explanation in the budget file and updated benchmark evidence. A release-grade performance claim still requires longer runs across supported environments; these short baselines are regression evidence, not universal throughput promises.
