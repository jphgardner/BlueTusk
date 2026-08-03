# ADR 0005: Separate PostgreSQL pipeline mode from transport pipelines

- Status: Accepted; retain the ArrayPool/Span/Memory transport
- Date: 2026-08-01

## Context

PostgreSQL pipeline mode batches extended-query operations between explicit `Sync` boundaries and returns ordered result groups without waiting after every operation. `System.IO.Pipelines` is a .NET buffering and asynchronous I/O abstraction. A similarly named API does not implement PostgreSQL's protocol semantics, and adopting it would affect BlueTusk's genuine synchronous transport promise.

## Decision

Implement PostgreSQL pipeline mode in the Client layer over the existing protocol and transport abstractions. Its public contract must define operation ordering, explicit synchronization boundaries, aborted-group errors, cancellation, early disposal, and recovery to `ReadyForQuery` before a session can be reused. Capability flags alone do not constitute support.

Keep the current ArrayPool/Span/Memory transport. The checked-in bounded `System.IO.Pipelines` prototype does not clear the adoption gate across representative synchronous and asynchronous workloads. `System.IO.Pipelines` remains confined to the benchmark project and is not a production dependency.

## Measurement gate

The comparison must use checked-in, reproducible benchmarks for:

- backend frames fragmented at representative and adversarial boundaries;
- many small rows and large fields;
- COPY streaming and cancellation recovery;
- plain TCP and TLS;
- synchronous and asynchronous commands; and
- throughput, tail latency, allocated bytes, retained buffers, and implementation complexity.

Adoption requires a meaningful measured benefit without regressing synchronous behavior, bounded-memory guarantees, cancellation safety, or protocol-test clarity. Otherwise the current transport remains the accepted implementation.

## Measurements

`TransportPipelineBenchmarks` compares warm, reusable readers over identical PostgreSQL frames. Its prototype uses a 2 MiB pause threshold and 64 KiB pump windows. The workloads are 256 rows fragmented one byte at a time, one 1 MiB field, 128 COPY-sized 8 KiB frames, and an error/`ReadyForQuery` cancellation-drain boundary. `TransportPipelineSocketBenchmarks` repeats the comparison over raw TCP and authenticated `SslStream` loopback peers. The reports include P95 latency and managed allocation.

The Windows/Ryzen 7 5800X/.NET 10 short-run baseline found:

| Workload | Current | Pipelines prototype | Result |
| --- | ---: | ---: | --- |
| byte-fragmented rows, sync | 187.7 us | 204.7 us | prototype 9% slower |
| byte-fragmented rows, async | 260.9 us | 158.5 us | prototype 39% faster |
| 1 MiB field, sync | 30.2 us | 61.6 us and 96 B | prototype 2.04x slower |
| 1 MiB field, async | 30.9 us | 59.2 us and 96 B | prototype 1.92x slower |
| COPY stream, sync | 42.5 us | 60.2 us | prototype 42% slower |
| COPY stream, async | 56.1 us | 56.7 us | effectively tied |
| cancellation drain, sync | 801 ns | 618 ns | prototype 23% faster |
| cancellation drain, async | 976 ns | 812 ns | prototype 17% faster |
| raw TCP, sync, per frame | 796 ns | 774 ns | effectively tied |
| raw TCP, async, per frame | 748 ns | 1.320 us | prototype 76% slower |
| TLS, sync, per frame | 1.073 us | 1.235 us | prototype 15% slower |
| TLS, async, per frame | 1.149 us | 1.117 us | effectively tied |

P95 values track the same broad result; see the checked-in Markdown and JSON reports for the full statistics. A short run has only three measured iterations, so small differences—especially the loopback TLS figures—are directional rather than universal claims.

The prototype is 163 lines even though it covers only read pumping, frame parsing, bounded buffering, and sync bridging. A production adoption would still need connection establishment, writes, TLS upgrade, Unix sockets, timeouts, unframed startup reads, incremental payload streaming, and cancellation/failure classification. It would also introduce `PipeReader`/`PipeWriter` completion, `AdvanceTo`, scheduler, and backpressure lifetimes while the genuine synchronous API still requires a separate path or blocking bridge.

Both designs must retain enough data for a complete 1 MiB frame when using the buffered-message API. The current protocol connection additionally exposes incremental header/payload reads for large values, and its COPY path reuses one frame buffer. The prototype's bounded windows therefore do not provide a decisive memory advantage for the implemented streaming paths.

The comparison is reproducible with:

```powershell
$env:BLUETUSK_BENCHMARK_ARTIFACTS = "benchmarks/baselines/windows-ryzen7-5800x-dotnet10"
dotnet run --project benchmarks/BlueTusk.Benchmarks -c Release --no-restore -- --job short --filter '*TransportPipelineBenchmarks*' '*TransportPipelineSocketBenchmarks*'
```

On Windows, the TLS benchmark creates a transient self-signed server certificate and requires access to the current user's certificate key store. `--transport-tls-smoke` validates all four TLS paths before a measurement run.

Revisit the decision if production traces show fragmented asynchronous reads are a dominant bottleneck, or if a future prototype can preserve the synchronous path and large-field/COPY behavior while demonstrating a material end-to-end win.

## Consequences

PostgreSQL pipeline mode ships independently of the transport decision. Its Client-layer implementation has fake-server, conformance, stress, and live PostgreSQL coverage for explicit synchronization boundaries, ordered group errors, cancellation draining, and safe session reuse. Documentation and capability detection describe pipeline semantics separately from transport buffering. The measured transport gate is complete, and the production dependency graph remains unchanged.
