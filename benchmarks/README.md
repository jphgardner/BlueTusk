# BlueTusk benchmarks

The BenchmarkDotNet suite covers complete synchronous/asynchronous command parameter and result paths, protocol-connection writes, protocol parsing and incremental payload streaming, typed buffered readers and large field access, integer/numeric/temporal/JSONB codecs, catalogue-composed array/enum/range/composite encoding and decoding, binary COPY field encoding, notification decoding, large-object stream transfer overhead, and warm-session pool checkout today. Replication, EF materialisation, transport-prototype comparison, and graph workloads are added alongside those implementations. Pool, command, reader, protocol-streaming, and large-object workloads isolate provider bookkeeping with in-memory sessions; live PostgreSQL behavior is covered by integration, stress, and compatibility tests.

Run the complete suite in Release mode:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release
```

For a shorter development run:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short
```

Markdown and brief JSON reports are written below `artifacts/benchmarks` by default. Set `BLUETUSK_BENCHMARK_ARTIFACTS` to redirect them. Checked-in results below `benchmarks/baselines` document one named reference environment; they are evidence and comparison inputs, not universal performance promises.

Allocation ownership, the current end-to-end numbers, and the machine-checked regression budgets are documented in [Allocation discipline](../docs/architecture/allocation-discipline.md).
