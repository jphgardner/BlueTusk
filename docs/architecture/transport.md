# Transport contract

`BlueTusk.Transport` owns byte movement and connection establishment. It has no PostgreSQL
authentication, SQL, type-system, pooling, or ADO.NET knowledge.

## Endpoints and address attempts

TCP endpoints accept a DNS name or numeric address. Sync and async connections resolve the
name once, preserve the platform resolver's IPv4/IPv6 preference, and try every returned address
in order. The configured connect timeout is one deadline shared by all address attempts rather
than a fresh timeout for every address. Asynchronous DNS resolution is included in that
deadline and observes caller cancellation. The dedicated synchronous path uses the platform's
synchronous DNS resolver and never blocks on an asynchronous operation.

Unix-domain endpoints bypass DNS. TCP-only `NoDelay` and keepalive options are not applied to
Unix sockets; their bounded send and receive buffers still are. Both endpoint families have
real synchronous and asynchronous connection tests.

## Socket and stream behavior

TCP keepalive and `NoDelay` are enabled by default. Send and receive socket buffers are bounded
and configurable through `BlueTuskTransportOptions`. The defaults use a 32 KiB send window and
a 256 KiB receive window: commands are normally small writes, while measured sequential reads
benefit from enough kernel-side capacity to keep the protocol consumer supplied. Applications
with very large pools can reduce the receive value when native per-socket memory is more
important than bulk-read throughput. Protocol-level framing uses pooled, bounded buffers above
the socket. Reads and writes operate directly on caller-provided `Span<T>` or `Memory<T>`.
Awaited writes provide network backpressure instead of creating an unbounded transport queue.

Async connect, read, write, flush, and TLS operations pass cancellation to the underlying .NET
network operation. A caller cancellation remains an `OperationCanceledException`. Expiration
of the connect deadline is instead reported as a classified transport failure.

## Connection failure classification

`BlueTuskTransportException` reports connection-establishment failures without asking higher
layers to interpret platform-specific messages. `FailureKind` distinguishes name resolution,
timeout, connection refusal, network/host reachability, address availability, and other socket
failures. `Endpoint` identifies the logical target. For TCP address attempts,
`AddressFailures` preserves each attempted address and its `SocketError`, in order.

This is intentionally connection-level classification. PostgreSQL authentication and server
errors remain Client/Protocol concerns, and TLS certificate validation retains the standard
.NET authentication exception semantics.

## TLS and buffering decision

The protocol layer requests PostgreSQL TLS negotiation and then asks the transport to upgrade
the connected stream. The transport applies the configured server-validation callback, client
certificate collection, certificate-selection callback, protocol versions, and revocation
policy. Safe platform server-certificate validation remains the default.

The production implementation deliberately remains on `Socket`, `NetworkStream`, `SslStream`,
`ArrayPool<T>`, spans, and memory. The separate [transport-pipeline ADR](decisions/0005-postgresql-pipeline-mode-and-transport-pipelines.md)
records why benchmark results did not justify adopting `System.IO.Pipelines`.
