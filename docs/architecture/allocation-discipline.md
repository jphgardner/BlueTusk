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

`BlueTuskProtocolConnection` retains one writer per physical session, clears it after every successful or failed write, rejects overlapping writes, and replaces storage that grows beyond 64 KiB so an exceptional command does not permanently inflate every pooled session. Runtime structured-codec encoding rents temporary sizing storage and copies only the exact payload into the caller-owned parameter value before returning the temporary buffer. Replication decodes one pulled frame at a time and retains its WAL body over the received memory; the 64-byte message object is measured and intentionally budgeted rather than described as allocation-free.

Machine-readable limits live in `benchmarks/allocation-budgets.json`. They intentionally allow modest short-run/runtime variance while keeping zero-allocation protocol writes strict. Refresh the named reports, review any ownership change, and then run:

```powershell
pwsh -File eng/verify-allocation-budgets.ps1
```

Raising a budget requires an explanation in the budget file and updated benchmark evidence. A release-grade performance claim still requires longer runs across supported environments; these short baselines are regression evidence, not universal throughput promises.
