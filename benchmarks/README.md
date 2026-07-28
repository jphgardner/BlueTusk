# BlueTusk benchmarks

The BenchmarkDotNet suite covers protocol parsing/writing, integer/numeric/temporal/JSONB codecs, catalogue-composed array/enum/range/composite decoding, and warm-session pool checkout today. Reader, COPY, replication, EF materialisation, and graph workloads are added alongside those implementations. The pool workload isolates provider bookkeeping and reset dispatch with an in-memory physical session; live PostgreSQL behavior is covered by integration tests.

Run the complete suite in Release mode:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release
```

For a shorter development run:

```powershell
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release -- --job short
```

Markdown and brief JSON reports are written below `artifacts/benchmarks` by default. Set `BLUETUSK_BENCHMARK_ARTIFACTS` to redirect them. Checked-in results below `benchmarks/baselines` document one named reference environment; they are evidence and comparison inputs, not universal performance promises.
