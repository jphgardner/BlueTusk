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
in-memory ownership budgets above. Its final 2026-08-02 MediumRun records:

| Workload | BlueTusk | Npgsql | Current result |
| --- | ---: | ---: | --- |
| Parameterized scalar | 1,663 B | 2,094 B | BlueTusk 20.6% lower |
| Explicitly prepared scalar | 796 B | 1,099 B | BlueTusk 27.6% lower |
| Untouched warm checkout | 168 B | 184 B | BlueTusk 8.7% lower |
| Sequential 1,000-row read | 1,400 B | 1,529 B | BlueTusk 8.4% lower |
| Sequential 1 MiB `bytea` | 3,900 B | 8,938 B | BlueTusk 56.4% lower |

The same run measures BlueTusk/Npgsql at 446/487 us for parameterized scalar
execution, 288/326 ns for warm checkout, 436/445 us for prepared scalar execution,
672/743 us for the 1,000-row reader, and 4.390/4.482 ms for the isolated 1 MiB
stream. Parameterized execution, warm checkout, and the row reader have
non-overlapping latency intervals at this sample size; prepared and stream
intervals overlap and are treated as parity despite lower BlueTusk means. These
longer paired results are regression evidence, not a
provider-wide latency guarantee.

The V1 multiplexing MediumRun uses four physical lanes and 64-command bursts.
It records BlueTusk/Npgsql fresh-command allocation at 1,733/1,738 B and
reused-command allocation at 1,143/794 B. Mean latency is 19.83/20.57 µs for
fresh commands and 17.41/20.01 µs for reused commands; BlueTusk also records
lower P95 and P99 in both pairs. Against ordinary pooled BlueTusk, multiplexing
uses less than 30% of the latency and 60% of the allocation allowed by the
checked-in budgets. The full result measurements and environment manifest are
stored under `benchmarks/baselines/windows-ryzen7-5800x-dotnet10`.

An exact-candidate run keeps those BenchmarkDotNet rows as the absolute-latency
and allocation authority, then records five alternating-provider trials for
the cross-provider latency gate. This isolates provider order drift without
changing the allocation ceilings or BlueTusk's absolute P95 budgets; the
candidate manifest hashes both raw reports.

`BlueTuskProtocolConnection` retains one writer per physical session, clears it after every successful or failed write, rejects overlapping writes, and replaces writer storage that grows beyond 64 KiB so an exceptional command does not permanently inflate every pooled session. Its receive side rents one 64 KiB protocol buffer per physical session. Incremental field reads of at least 8 KiB pass the caller's buffer directly to the transport after consuming buffered bytes, avoiding both an intermediate copy and a transient large rental; smaller reads use adaptive bounded read-ahead. The socket receive window defaults to 256 KiB and caller-visible streams still do not materialize the field. Runtime structured-codec encoding rents temporary sizing storage and copies only the exact payload into the caller-owned parameter value before returning the temporary buffer. Replication decodes one pulled frame at a time and retains its WAL body over the received memory; the 64-byte message object is measured and intentionally budgeted rather than described as allocation-free.

Warm command instances cache the structural named-parameter plan, but parameter
values are encoded on every execution and prepared-statement type identity is
revalidated. Asynchronous scalar execution drains the complete protocol group
for connection safety while retaining only the first value needed by ADO.NET.
Prepared scalar commands reuse statement metadata captured by `Prepare`, and
fixed-width prepared values reuse command-owned wire buffers while still being
re-encoded after every value mutation. Timeout cancellation shares the command's
CancelRequest timer instead of allocating linked cancellation sources per
operation. Prepared commands amortize native timer scheduling across adjacent
executions: successful operations only refresh the protected deadline until the
outstanding wake-up fires. Other commands rent warmed timeout registrations
instead of creating a native timer for every command object. Untouched logical connections avoid allocating rare transaction,
notification, and large-object state. Large streamed payloads either read
directly into sufficiently large caller buffers or rent an adaptive read-ahead
buffer for small reads. Both paths return legal partial reads without wrapping
each transport wait at every abstraction layer and complete protocol/row/stream
accounting in one pending-read continuation; rented read-ahead returns to the
64 KiB steady-state window at the next frame.
Sequential portal metadata is parsed directly from the shared protocol buffer;
only DataRow payloads enter the incremental field-streaming path. Unlimited
sequential commands avoid an intermediate metadata flush, use the unnamed portal,
reuse the server's unnamed statement for repeated exact SQL and parameter type
OIDs, share parameterless rewrite/encoding state, create their parameter collection only when
it is requested, and return their row/header storage to the physical session at
disposal. Single-segment
backend frames decode in place. Portal startup and prepared scalar continuations
use pooled `ValueTask` state, while the row reader reuses one per-session
completion source. Small streamed control payloads and repeated command tags are
also retained by the physical session rather than copied for every reader.
Fully buffered DataRows remain a read-only view over the protocol window for the
current reader iteration instead of being copied into row-local storage. Repeated
portal metadata is reused only after the newly received `RowDescription` matches
the cached wire payload byte for byte, so schema or format changes still replace
the cache immediately. The portal pins that protocol window for its complete
lifetime, eliminating per-row buffer-lifetime atomics, and contiguous backend
frames use a direct array decoder. The reader caches the concrete field array and
field count for typed sequential access, avoiding repeated interface dispatch and
duplicate field validation without weakening public ordinal checks.
Portal frontend messages use struct-backed parameter views instead of copied
type/value arrays and capturing writer delegates.
The streaming reader holds its command and timeout directly instead of allocating
capturing completion and exception-translation delegates for each reader.

Machine-readable limits live in `benchmarks/allocation-budgets.json`. They intentionally allow modest short-run/runtime variance while keeping zero-allocation protocol writes strict. Refresh the named reports, review any ownership change, and then run:

```powershell
pwsh -File eng/verify-allocation-budgets.ps1
pwsh -File eng/verify-multiplexing-performance.ps1
```

Raising a budget requires an explanation in the budget file and updated benchmark evidence. A release-grade performance claim still requires longer runs across supported environments; the short baselines and paired MediumRun are regression evidence, not universal throughput promises.
