using System.Buffers.Binary;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

public sealed class BlueTuskBinaryExporter : IAsyncDisposable
{
    private const int MaximumHeaderExtensionLength = 64 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature =>
    [
        (byte)'P', (byte)'G', (byte)'C', (byte)'O', (byte)'P', (byte)'Y',
        (byte)'\n', 0xFF, (byte)'\r', (byte)'\n', 0,
    ];

    private readonly BlueTuskCopyPipe _pipe;
    private readonly Task<BlueTuskRawCopyResult> _copyTask;
    private readonly BlueTuskTypeRegistry _registry;
    private readonly short _expectedColumnCount;
    private short _fieldCount;
    private int _fieldIndex;
    private long _rowsRead;
    private bool _rowStarted;
    private bool _completed;
    private bool _disposed;

    internal BlueTuskBinaryExporter(
        BlueTuskCopyPipe pipe,
        Task<BlueTuskRawCopyResult> copyTask,
        BlueTuskTypeRegistry registry,
        int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, short.MaxValue);
        _pipe = pipe;
        _copyTask = copyTask;
        _registry = registry;
        _expectedColumnCount = checked((short)columnCount);
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var header = new byte[19];
        await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidOperationException("PostgreSQL binary COPY signature is invalid.");
        }

        var flags = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(11));
        if (flags != 0)
        {
            throw new NotSupportedException(
                $"PostgreSQL binary COPY flags 0x{flags:X8} are not supported.");
        }

        var extensionLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(15));
        if (extensionLength < 0)
        {
            throw new InvalidOperationException(
                "PostgreSQL binary COPY declared a negative header extension length.");
        }

        if (extensionLength > MaximumHeaderExtensionLength)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY header extension exceeds {MaximumHeaderExtensionLength} bytes.");
        }

        if (extensionLength > 0)
        {
            await ReadExactlyAsync(new byte[extensionLength], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<int> StartRowAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (_rowStarted && _fieldIndex != _fieldCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row has {_fieldCount - _fieldIndex} unread fields.");
        }

        var bytes = new byte[sizeof(short)];
        await ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        _fieldCount = BinaryPrimitives.ReadInt16BigEndian(bytes);
        if (_fieldCount == -1)
        {
            _completed = true;
            var result = await _copyTask.ConfigureAwait(false);
            if (result.Format != BlueTuskCopyDataFormat.Binary)
            {
                throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
            }

            if (result.RowsAffected != _rowsRead)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL reported {result.RowsAffected} copied rows; {_rowsRead} were read.");
            }

            return -1;
        }

        if (_fieldCount < 0 || _fieldCount != _expectedColumnCount)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY row contains {_fieldCount} fields; {_expectedColumnCount} were expected.");
        }

        _rowStarted = true;
        _fieldIndex = 0;
        _rowsRead = checked(_rowsRead + 1);
        return _fieldCount;
    }

    public ValueTask<T?> ReadAsync<T>(
        CancellationToken cancellationToken = default) =>
        ReadAsync<T>(postgreSqlTypeOid: null, cancellationToken);

    public async ValueTask<T?> ReadAsync<T>(
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (!_rowStarted || _fieldIndex >= _fieldCount)
        {
            throw new InvalidOperationException(
                "StartRowAsync must identify a row with an unread field before ReadAsync is called.");
        }

        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        _fieldIndex++;
        if (length == -1)
        {
            if (default(T) is not null)
            {
                throw new InvalidOperationException(
                    $"A null binary COPY field cannot be read as non-nullable {typeof(T).FullName}.");
            }

            return default;
        }

        if (length < -1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY field declared invalid length {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return BlueTuskBinaryCopyCodec.Decode<T>(
            payload,
            postgreSqlTypeOid,
            _registry);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_completed)
        {
            _pipe.CompleteWriting(
                new IOException("The binary COPY exporter was disposed before completion."));
            try
            {
                _ = await _copyTask.ConfigureAwait(false);
            }
            catch
            {
                // Disposal aborts and drains the COPY operation; its expected server error is suppressed.
            }
        }

        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await _pipe.ReadAsync(
                destination[offset..],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "PostgreSQL binary COPY ended in the middle of a value.");
            }

            offset += read;
        }
    }

    private void EnsureReadable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The binary COPY exporter is already complete.");
        }
    }
}
