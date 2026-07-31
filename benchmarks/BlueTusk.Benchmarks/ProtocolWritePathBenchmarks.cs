using System.Net;
using BenchmarkDotNet.Attributes;
using BlueTusk.Protocol;
using BlueTusk.Transport;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class ProtocolWritePathBenchmarks : IDisposable
{
    private static readonly uint[] ParameterTypeOids = [23, 25];
    private static readonly short[] BinaryResultFormat = [1];
    private static readonly BlueTuskBindParameter[] BindParameters =
    [
        new(1, new byte[sizeof(int)]),
        new(0, "allocation-baseline"u8.ToArray()),
    ];

    private readonly BlueTuskProtocolConnection _connection = new(new NullTransport());

    [Benchmark(Baseline = true)]
    public void WriteSimpleQuery() =>
        _connection.Write(static output =>
            BlueTuskFrontendMessageWriter.WriteSimpleQuery(output, "SELECT 42::int4"));

    [Benchmark]
    public void WriteExtendedQuery() =>
        _connection.Write(static output =>
        {
            BlueTuskFrontendMessageWriter.WriteParse(
                output,
                string.Empty,
                "SELECT $1::int4, $2::text",
                ParameterTypeOids);
            BlueTuskFrontendMessageWriter.WriteBind(
                output,
                string.Empty,
                string.Empty,
                BindParameters,
                BinaryResultFormat);
            BlueTuskFrontendMessageWriter.WriteDescribePortal(output, string.Empty);
            BlueTuskFrontendMessageWriter.WriteExecute(output, string.Empty);
            BlueTuskFrontendMessageWriter.WriteSync(output);
        });

    [GlobalCleanup]
    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class NullTransport : IBlueTuskTransport
    {
        public EndPoint? RemoteEndPoint => null;

        public void Connect(BlueTuskEndpoint endpoint, BlueTuskTransportOptions options)
        {
        }

        public ValueTask ConnectAsync(
            BlueTuskEndpoint endpoint,
            BlueTuskTransportOptions options,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public int Read(Span<byte> buffer) => throw new NotSupportedException();

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Flush()
        {
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
