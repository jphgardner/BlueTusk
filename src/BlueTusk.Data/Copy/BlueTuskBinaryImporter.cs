using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

public sealed class BlueTuskBinaryImporter : IDisposable, IAsyncDisposable
{
    private const int WriteBufferSize = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Header =
    [
        (byte)'P', (byte)'G', (byte)'C', (byte)'O', (byte)'P', (byte)'Y',
        (byte)'\n', 0xFF, (byte)'\r', (byte)'\n', 0,
        0, 0, 0, 0,
        0, 0, 0, 0,
    ];

    private readonly BlueTuskCopyPipe _pipe;
    private readonly Task<BlueTuskRawCopyResult> _copyTask;
    private readonly BlueTuskCopyInOperation? _synchronousOperation;
    private readonly BlueTuskCopyInOperation? _asynchronousOperation;
    private readonly BlueTuskTypeRegistry _registry;
    private readonly BlueTuskParameter _reusableParameter = new();
    private readonly short _columnCount;
    private readonly byte[]?[] _fieldBuffers;
    private byte[]? _writeBuffer;
    private int _writeBufferCount;
    private int _fieldIndex;
    private long _rowsStarted;
    private bool _rowStarted;
    private bool _completed;
    private bool _disposed;

    internal BlueTuskBinaryImporter(
        BlueTuskCopyPipe pipe,
        Task<BlueTuskRawCopyResult> copyTask,
        BlueTuskTypeRegistry registry,
        int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, short.MaxValue);
        _pipe = pipe;
        _copyTask = copyTask;
        _registry = registry;
        _columnCount = checked((short)columnCount);
        _fieldBuffers = new byte[]?[columnCount];
    }

    internal BlueTuskBinaryImporter(
        BlueTuskCopyInOperation operation,
        BlueTuskTypeRegistry registry,
        int columnCount,
        bool asynchronous = false)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, short.MaxValue);
        _pipe = null!;
        _copyTask = null!;
        if (asynchronous)
        {
            _asynchronousOperation = operation ?? throw new ArgumentNullException(nameof(operation));
        }
        else
        {
            _synchronousOperation = operation ?? throw new ArgumentNullException(nameof(operation));
        }
        _registry = registry;
        _columnCount = checked((short)columnCount);
        _fieldBuffers = new byte[]?[columnCount];
    }

    internal void Initialize() => _synchronousOperation!.Write(Header);

    internal ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        var capacity = EnsureWriteCapacityAsync(Header.Length, cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitInitializeAsync(capacity);
        }

        BufferHeader();
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitInitializeAsync(ValueTask capacity)
    {
        await capacity.ConfigureAwait(false);
        BufferHeader();
    }

    private void BufferHeader()
    {
        Header.CopyTo(_writeBuffer!, _writeBufferCount);
        _writeBufferCount += Header.Length;
    }

    public void StartRow()
    {
        EnsureSynchronousMode();
        EnsureRowComplete();
        var header = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(header, _columnCount);
        _synchronousOperation!.Write(header);
        _rowStarted = true;
        _fieldIndex = 0;
        _rowsStarted = checked(_rowsStarted + 1);
    }

    public ValueTask StartRowAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_rowStarted && _fieldIndex != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row contains {_fieldIndex} of {_columnCount} fields.");
        }

        var capacity = EnsureWriteCapacityAsync(sizeof(short), cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitStartRowAsync(capacity);
        }

        StartRowBuffered();
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitStartRowAsync(ValueTask capacity)
    {
        await capacity.ConfigureAwait(false);
        StartRowBuffered();
    }

    private void StartRowBuffered()
    {
        BinaryPrimitives.WriteInt16BigEndian(
            _writeBuffer.AsSpan(_writeBufferCount, sizeof(short)),
            _columnCount);
        _writeBufferCount += sizeof(short);
        _rowStarted = true;
        _fieldIndex = 0;
        _rowsStarted = checked(_rowsStarted + 1);
    }

    public ValueTask WriteAsync<T>(
        T? value,
        CancellationToken cancellationToken = default)
    {
        if (value is int intValue)
        {
            return WriteInt32Async(intValue, cancellationToken);
        }

        if (value is bool boolValue)
        {
            return WriteBooleanAsync(boolValue, cancellationToken);
        }

        if (value is Guid guidValue)
        {
            return WriteGuidAsync(guidValue, cancellationToken);
        }

        if (value is string stringValue)
        {
            return WriteStringAsync(stringValue, cancellationToken);
        }

        return WriteAsync(value, postgreSqlTypeOid: null, cancellationToken);
    }

    private ValueTask WriteInt32Async(
        int value,
        CancellationToken cancellationToken)
    {
        EnsureFieldWritable();
        var capacity = EnsureWriteCapacityAsync(sizeof(int) * 2, cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitWriteInt32Async(capacity, value);
        }

        WriteInt32Buffered(value);
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitWriteInt32Async(ValueTask capacity, int value)
    {
        await capacity.ConfigureAwait(false);
        WriteInt32Buffered(value);
    }

    private void WriteInt32Buffered(int value)
    {
        var destination = _writeBuffer.AsSpan(_writeBufferCount, sizeof(int) * 2);
        BinaryPrimitives.WriteInt32BigEndian(destination, sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination[sizeof(int)..], value);
        _writeBufferCount += sizeof(int) * 2;
        _fieldIndex++;
    }

    private ValueTask WriteBooleanAsync(
        bool value,
        CancellationToken cancellationToken)
    {
        EnsureFieldWritable();
        var capacity = EnsureWriteCapacityAsync(sizeof(int) + sizeof(byte), cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitWriteBooleanAsync(capacity, value);
        }

        WriteBooleanBuffered(value);
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitWriteBooleanAsync(ValueTask capacity, bool value)
    {
        await capacity.ConfigureAwait(false);
        WriteBooleanBuffered(value);
    }

    private void WriteBooleanBuffered(bool value)
    {
        var destination = _writeBuffer.AsSpan(_writeBufferCount, sizeof(int) + sizeof(byte));
        BinaryPrimitives.WriteInt32BigEndian(destination, sizeof(byte));
        destination[sizeof(int)] = value ? (byte)1 : (byte)0;
        _writeBufferCount += sizeof(int) + sizeof(byte);
        _fieldIndex++;
    }

    private ValueTask WriteGuidAsync(
        Guid value,
        CancellationToken cancellationToken)
    {
        EnsureFieldWritable();
        const int payloadLength = 16;
        var capacity = EnsureWriteCapacityAsync(sizeof(int) + payloadLength, cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitWriteGuidAsync(capacity, value);
        }

        WriteGuidBuffered(value);
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitWriteGuidAsync(ValueTask capacity, Guid value)
    {
        await capacity.ConfigureAwait(false);
        WriteGuidBuffered(value);
    }

    private void WriteGuidBuffered(Guid value)
    {
        const int payloadLength = 16;
        var destination = _writeBuffer.AsSpan(_writeBufferCount, sizeof(int) + payloadLength);
        BinaryPrimitives.WriteInt32BigEndian(destination, payloadLength);
        if (!value.TryWriteBytes(
                destination[sizeof(int)..],
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != payloadLength)
        {
            throw new InvalidOperationException("Could not encode a UUID COPY value.");
        }

        _writeBufferCount += sizeof(int) + payloadLength;
        _fieldIndex++;
    }

    private ValueTask WriteStringAsync(
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureFieldWritable();
        var payloadLength = StrictUtf8.GetByteCount(value);
        var capacity = EnsureWriteCapacityAsync(
            checked(sizeof(int) + payloadLength),
            cancellationToken);
        if (!capacity.IsCompletedSuccessfully)
        {
            return AwaitWriteStringAsync(capacity, value, payloadLength);
        }

        WriteStringBuffered(value, payloadLength);
        return ValueTask.CompletedTask;
    }

    private async ValueTask AwaitWriteStringAsync(
        ValueTask capacity,
        string value,
        int payloadLength)
    {
        await capacity.ConfigureAwait(false);
        WriteStringBuffered(value, payloadLength);
    }

    private static void WriteStringBuffered(
        string value,
        int payloadLength,
        byte[]? writeBuffer,
        ref int writeBufferCount)
    {
        var destination = writeBuffer.AsSpan(
            writeBufferCount,
            sizeof(int) + payloadLength);
        BinaryPrimitives.WriteInt32BigEndian(destination, payloadLength);
        var bytesWritten = StrictUtf8.GetBytes(value, destination[sizeof(int)..]);
        if (bytesWritten != payloadLength)
        {
            throw new InvalidOperationException(
                $"The UTF-8 encoder wrote {bytesWritten} bytes; {payloadLength} were expected.");
        }

        writeBufferCount += sizeof(int) + payloadLength;
    }

    private void WriteStringBuffered(string value, int payloadLength)
    {
        WriteStringBuffered(value, payloadLength, _writeBuffer, ref _writeBufferCount);
        _fieldIndex++;
    }

    public void Write<T>(T? value, uint? postgreSqlTypeOid = null)
    {
        EnsureSynchronousMode();
        var field = EncodeField(value, postgreSqlTypeOid);
        _synchronousOperation!.Write(field);
        _fieldIndex++;
    }

    public async ValueTask WriteAsync<T>(
        T? value,
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken = default)
    {
        EnsureFieldWritable();
        if (value is null)
        {
            await EnsureWriteCapacityAsync(sizeof(int), cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteInt32BigEndian(
                _writeBuffer.AsSpan(_writeBufferCount, sizeof(int)),
                -1);
            _writeBufferCount += sizeof(int);
        }
        else
        {
            var payload = BlueTuskBinaryCopyCodec.Encode(
                value,
                postgreSqlTypeOid,
                _registry,
                _reusableParameter,
                ref _fieldBuffers[_fieldIndex]);
            var fieldLength = checked(sizeof(int) + payload.Length);
            await EnsureWriteCapacityAsync(fieldLength, cancellationToken).ConfigureAwait(false);
            BinaryPrimitives.WriteInt32BigEndian(
                _writeBuffer.AsSpan(_writeBufferCount, sizeof(int)),
                payload.Length);
            _writeBufferCount += sizeof(int);
            payload.CopyTo(_writeBuffer.AsMemory(_writeBufferCount));
            _writeBufferCount += payload.Length;
        }

        _fieldIndex++;
    }

    public long Complete()
    {
        EnsureSynchronousMode();
        EnsureRowComplete();
        var trailer = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(trailer, -1);
        _synchronousOperation!.Write(trailer);
        _completed = true;
        var result = _synchronousOperation.Complete();
        if (result.Response.Format != BlueTuskCopyFormat.Binary)
        {
            throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
        }

        if (!BlueTuskCommandTagParser.TryGetRowsAffected(result.CommandTag, out var rowsAffected))
        {
            throw new InvalidOperationException(
                $"PostgreSQL returned invalid binary COPY command tag '{result.CommandTag}'.");
        }

        if (rowsAffected != _rowsStarted)
        {
            throw new InvalidOperationException(
                $"PostgreSQL reported {rowsAffected} copied rows; {_rowsStarted} were written.");
        }

        return rowsAffected;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<long> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_rowStarted && _fieldIndex != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row contains {_fieldIndex} of {_columnCount} fields.");
        }

        await EnsureWriteCapacityAsync(sizeof(short), cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteInt16BigEndian(
            _writeBuffer.AsSpan(_writeBufferCount, sizeof(short)),
            -1);
        _writeBufferCount += sizeof(short);
        await FlushWriteBufferAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
        if (_asynchronousOperation is not null)
        {
            var operationResult = await _asynchronousOperation.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            if (operationResult.Response.Format != BlueTuskCopyFormat.Binary)
            {
                throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
            }

            if (!BlueTuskCommandTagParser.TryGetRowsAffected(
                    operationResult.CommandTag,
                    out var operationRowsAffected))
            {
                throw new InvalidOperationException(
                    $"PostgreSQL returned invalid binary COPY command tag '{operationResult.CommandTag}'.");
            }

            if (operationRowsAffected != _rowsStarted)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL reported {operationRowsAffected} copied rows; {_rowsStarted} were written.");
            }

            return operationRowsAffected;
        }

        _pipe.CompleteWriting();
        var result = await _copyTask.ConfigureAwait(false);
        if (result.Format != BlueTuskCopyDataFormat.Binary)
        {
            throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
        }

        if (result.RowsAffected != _rowsStarted)
        {
            throw new InvalidOperationException(
                $"PostgreSQL reported {result.RowsAffected} copied rows; {_rowsStarted} were written.");
        }

        return result.RowsAffected;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_synchronousOperation is not null)
        {
            _synchronousOperation.Dispose();
            return;
        }

        if (_asynchronousOperation is not null)
        {
            if (!_completed)
            {
                await _asynchronousOperation.DisposeAsync().ConfigureAwait(false);
            }

            ReturnWriteBuffer();
            return;
        }

        if (!_completed)
        {
            _pipe.CompleteWriting(
                new IOException("The binary COPY importer was disposed before completion."));
            try
            {
                _ = await _copyTask.ConfigureAwait(false);
            }
            catch
            {
                // Disposal aborts and drains the COPY operation; its expected server error is suppressed.
            }
        }

        ReturnWriteBuffer();
        await _pipe.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_synchronousOperation is null)
        {
            throw new InvalidOperationException(
                "An asynchronously created binary importer must be disposed asynchronously.");
        }

        _disposed = true;
        _synchronousOperation.Dispose();
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The binary COPY importer is already complete.");
        }
    }

    private byte[] EncodeField<T>(T? value, uint? postgreSqlTypeOid)
    {
        EnsureFieldWritable();

        if (value is null)
        {
            var nullField = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(nullField, -1);
            return nullField;
        }

        var payload = BlueTuskBinaryCopyCodec.Encode(value, postgreSqlTypeOid, _registry);
        var field = new byte[checked(sizeof(int) + payload.Length)];
        BinaryPrimitives.WriteInt32BigEndian(field, payload.Length);
        payload.CopyTo(field, sizeof(int));
        return field;
    }

    private void EnsureFieldWritable()
    {
        EnsureWritable();
        if (!_rowStarted)
        {
            throw new InvalidOperationException(
                "StartRow or StartRowAsync must be called before writing binary COPY fields.");
        }

        if (_fieldIndex >= _columnCount)
        {
            throw new InvalidOperationException(
                $"The binary COPY row already contains its {_columnCount} fields.");
        }
    }

    private void EnsureRowComplete()
    {
        EnsureWritable();
        if (_rowStarted && _fieldIndex != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row contains {_fieldIndex} of {_columnCount} fields.");
        }
    }

    private void EnsureSynchronousMode()
    {
        if (_synchronousOperation is null)
        {
            throw new InvalidOperationException(
                "This binary importer was created for asynchronous operation.");
        }
    }

    private ValueTask WriteChunkAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (_asynchronousOperation is not null)
        {
            return _asynchronousOperation.WriteAsync(data, cancellationToken);
        }

        var write = _pipe.WriteChunkAsync(data, cancellationToken);
        return write.IsCompletedSuccessfully
            ? ValueTask.CompletedTask
            : AwaitWriteChunkAsync(write, cancellationToken);
    }

    private async ValueTask AwaitWriteChunkAsync(
        ValueTask write,
        CancellationToken cancellationToken)
    {
        var writeTask = write.AsTask();
        var completed = await Task.WhenAny(writeTask, _copyTask)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(completed, _copyTask))
        {
            _ = await _copyTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                "PostgreSQL completed binary COPY before receiving its trailer.");
        }

        await writeTask.ConfigureAwait(false);
    }

    private ValueTask EnsureWriteCapacityAsync(
        int required,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWritable();
        if (_asynchronousOperation is null && _copyTask.IsCompleted)
        {
            return ObserveCompletedCopyAsync();
        }

        _writeBuffer ??= ArrayPool<byte>.Shared.Rent(Math.Max(WriteBufferSize, required));
        if (required <= _writeBuffer.Length - _writeBufferCount)
        {
            return ValueTask.CompletedTask;
        }

        return EnsureWriteCapacitySlowAsync(required, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask EnsureWriteCapacitySlowAsync(
        int required,
        CancellationToken cancellationToken)
    {
        await FlushWriteBufferAsync(cancellationToken).ConfigureAwait(false);
        var writeBuffer = _writeBuffer!;
        if (required > writeBuffer.Length)
        {
            ArrayPool<byte>.Shared.Return(writeBuffer);
            _writeBuffer = ArrayPool<byte>.Shared.Rent(required);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ObserveCompletedCopyAsync()
    {
        _ = await _copyTask.ConfigureAwait(false);
        throw new InvalidOperationException(
            "PostgreSQL completed binary COPY before receiving its trailer.");
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask FlushWriteBufferAsync(CancellationToken cancellationToken)
    {
        if (_writeBufferCount == 0)
        {
            return;
        }

        await WriteChunkAsync(
            _writeBuffer.AsMemory(0, _writeBufferCount),
            cancellationToken).ConfigureAwait(false);
        _writeBufferCount = 0;
    }

    private void ReturnWriteBuffer()
    {
        if (_writeBuffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_writeBuffer);
        _writeBuffer = null;
        _writeBufferCount = 0;
    }
}
