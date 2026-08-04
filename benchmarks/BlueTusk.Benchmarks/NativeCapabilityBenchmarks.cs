using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using BlueTusk.Data.Copy;
using BlueTusk.Data.LargeObjects;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.Brief]
public class NativeCapabilityBenchmarks : IAsyncDisposable
{
    private readonly BlueTuskTypeRegistry _registry = BlueTuskBuiltInTypes.CreateRegistry();
    private readonly byte[] _largeObjectBuffer = new byte[8192];
    private readonly BlueTuskLargeObjectStream _largeObjectStream;
    private readonly BlueTuskBackendMessage _notification;

    public NativeCapabilityBenchmarks()
    {
        var notification = new ArrayBufferWriter<byte>();
        WriteInt32(notification, 1234);
        WriteCString(notification, "orders");
        WriteCString(notification, "order-42-created");
        _notification = new BlueTuskBackendMessage(
            (byte)'A',
            new ReadOnlySequence<byte>(notification.WrittenMemory));
        _largeObjectStream = new BlueTuskLargeObjectStream(
            42,
            FileAccess.Read,
            length: long.MaxValue,
            position: 0,
            new BenchmarkLargeObjectOperations());
    }

    [Benchmark]
    public byte[] EncodeBinaryCopyInt32() =>
        BlueTuskBinaryCopyCodec.Encode(42, postgreSqlTypeOid: null, _registry);

    [Benchmark]
    public BlueTuskNotificationResponse DecodeNotification() =>
        BlueTuskBackendMessageDecoder.DecodeNotificationResponse(_notification);

    [Benchmark]
    public ValueTask<int> ReadLargeObjectChunkAsync() =>
        _largeObjectStream.ReadAsync(_largeObjectBuffer);

    [GlobalCleanup]
    public Task CleanupAsync() =>
        DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        await _largeObjectStream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void WriteCString(ArrayBufferWriter<byte> output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.CopyTo(output.GetSpan(bytes.Length));
        output.Advance(bytes.Length);
        output.GetSpan(1)[0] = 0;
        output.Advance(1);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.GetSpan(sizeof(int)), value);
        output.Advance(sizeof(int));
    }

    private sealed class BenchmarkLargeObjectOperations : IBlueTuskLargeObjectOperations
    {
        private static readonly byte[] Payload = new byte[8192];

        public ValueTask<byte[]> ReadAsync(int count, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Payload);

        public ValueTask<int> WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<long> SeekAsync(
            long offset,
            SeekOrigin origin,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask SetLengthAsync(long value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask CloseAsync(bool commit, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Abandon()
        {
        }
    }
}
