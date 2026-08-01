# BlueTusk benchmarks

The BenchmarkDotNet suite covers complete synchronous/asynchronous command parameter and result paths, protocol-connection writes, protocol parsing and incremental payload streaming, typed buffered readers and large field access, integer/numeric/temporal/JSONB codecs, catalogue-composed array/enum/range/composite encoding and decoding, binary COPY field encoding, notification decoding, replication WAL-frame decoding and bounded pull consumption, large-object stream transfer overhead, warm-session pool checkout, and the transport-pipeline decision. EF materialisation and graph workloads are added alongside those implementations. Pool, command, reader, protocol-streaming, replication, and large-object workloads isolate provider bookkeeping with in-memory sessions; live PostgreSQL behavior is covered by integration, stress, and compatibility tests.

`TransportPipelineBenchmarks` compares the production ArrayPool/Span/Memory reader with a benchmark-only, bounded `System.IO.Pipelines` prototype across fragmented rows, a 1 MiB field, COPY frames, and cancellation recovery, using genuine sync and async entry points. `TransportPipelineSocketBenchmarks` repeats the comparison over raw TCP and authenticated loopback TLS. The production packages do not reference `System.IO.Pipelines`; the resulting decision and limitations are in [ADR 0005](../docs/architecture/decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md).

`PullOneThousandBoundedXLogFrames` consumes one already-owned frame at a time
without a prefetch queue. This mirrors the replication async iterator's
backpressure boundary: the connection reads the next `CopyData` payload only
when the consumer requests the next message. The benchmark normalizes time and
allocation per frame; it does not claim to measure server or network latency.

Run the complete suite in Release mode:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release
```

For a shorter development run:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short
```

Run only the transport comparison with:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-restore -- --job short --filter '*TransportPipelineBenchmarks*' '*TransportPipelineSocketBenchmarks*'
```

On Windows, the TLS benchmark needs access to the current user's certificate key store for its transient self-signed server certificate. Validate the loopback harness with `--transport-tls-smoke` if needed.

Markdown and brief JSON reports are written below `artifacts/benchmarks` by default. Set `BLUETUSK_BENCHMARK_ARTIFACTS` to redirect them. Checked-in results below `benchmarks/baselines` document one named reference environment; they are evidence and comparison inputs, not universal performance promises.

Allocation ownership, the current end-to-end numbers, and the machine-checked regression budgets are documented in [Allocation discipline](../docs/architecture/allocation-discipline.md).
