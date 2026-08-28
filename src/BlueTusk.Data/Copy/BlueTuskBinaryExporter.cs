using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

public sealed class BlueTuskBinaryExporter : IDisposable, IAsyncDisposable
{
    private const int MaximumHeaderExtensionLength = 64 * 1024 * 1024;
    private static ReadOnlySpan<byte> Signature =>
    [
        (byte)'P', (byte)'G', (byte)'C', (byte)'O', (byte)'P', (byte)'Y',
        (byte)'\n', 0xFF, (byte)'\r', (byte)'\n', 0,
    ];

    private readonly BlueTuskCopyPipe _pipe;
    private readonly Task<BlueTuskRawCopyResult> _copyTask;
    private readonly BlueTuskCopyOutOperation? _synchronousOperation;
    private readonly BlueTuskCopyOutOperation? _asynchronousOperation;
    private readonly BlueTuskTypeRegistry _registry;
    private readonly short _expectedColumnCount;
    private readonly byte[] _scratch = new byte[19];
    private readonly BlueTuskBinaryCopyFieldState[] _fieldStates;
    private short _fieldCount;
    private int _fieldIndex;
    private long _rowsRead;
    private bool _rowStarted;
    private bool _completed;
    private bool _disposed;
    private bool _fieldStatesReturned;

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
        _fieldStates = ArrayPool<BlueTuskBinaryCopyFieldState>.Shared.Rent(columnCount);
        Array.Clear(_fieldStates);
    }

    internal BlueTuskBinaryExporter(
        BlueTuskCopyOutOperation operation,
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
        _expectedColumnCount = checked((short)columnCount);
        _fieldStates = ArrayPool<BlueTuskBinaryCopyFieldState>.Shared.Rent(columnCount);
        Array.Clear(_fieldStates);
    }

    internal void Initialize()
    {
        try
        {
            ReadExactly(_scratch);
            ValidateHeader(_scratch);
        }
        catch
        {
            ReturnFieldStates();
            throw;
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReadExactlyAsync(_scratch, cancellationToken).ConfigureAwait(false);
            if (!_scratch.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            {
                throw new InvalidOperationException("PostgreSQL binary COPY signature is invalid.");
            }

            var flags = BinaryPrimitives.ReadInt32BigEndian(_scratch.AsSpan(11));
            if (flags != 0)
            {
                throw new NotSupportedException(
                    $"PostgreSQL binary COPY flags 0x{flags:X8} are not supported.");
            }

            var extensionLength = BinaryPrimitives.ReadInt32BigEndian(_scratch.AsSpan(15));
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
        catch
        {
            ReturnFieldStates();
            throw;
        }
    }

    public int StartRow()
    {
        EnsureSynchronousMode();
        EnsurePreviousRowComplete();
        var bytes = new byte[sizeof(short)];
        ReadExactly(bytes);
        _fieldCount = BinaryPrimitives.ReadInt16BigEndian(bytes);
        if (_fieldCount == -1)
        {
            _completed = true;
            var scratch = new byte[1];
            while (_synchronousOperation!.Read(scratch) != 0)
            {
            }

            var result = _synchronousOperation.Result ?? throw new InvalidOperationException(
                "PostgreSQL did not complete binary COPY after its trailer.");
            if (result.Response.Format != BlueTuskCopyFormat.Binary)
            {
                throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
            }

            if (!BlueTuskCommandTagParser.TryGetRowsAffected(result.CommandTag, out var rowsAffected) ||
                rowsAffected != _rowsRead)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL reported an invalid binary COPY row count for {_rowsRead} rows read.");
            }

            return -1;
        }

        ValidateFieldCount();
        _rowStarted = true;
        _fieldIndex = 0;
        _rowsRead = checked(_rowsRead + 1);
        return _fieldCount;
    }

    public ValueTask<int> StartRowAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (_rowStarted && _fieldIndex != _fieldCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row has {_fieldCount - _fieldIndex} unread fields.");
        }

        if (_asynchronousOperation?.TryReadExactly(_scratch.AsMemory(0, sizeof(short))) == true)
        {
            return ProcessRowHeaderAsync(cancellationToken);
        }

        return StartRowSlowAsync(cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> StartRowSlowAsync(CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(
            _scratch.AsMemory(0, sizeof(short)),
            cancellationToken).ConfigureAwait(false);
        return await ProcessRowHeaderAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<int> ProcessRowHeaderAsync(CancellationToken cancellationToken)
    {
        _fieldCount = BinaryPrimitives.ReadInt16BigEndian(_scratch);
        if (_fieldCount == -1)
        {
            _completed = true;
            return CompleteExportAsync(cancellationToken);
        }

        if (_fieldCount < 0 || _fieldCount != _expectedColumnCount)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY row contains {_fieldCount} fields; {_expectedColumnCount} were expected.");
        }

        _rowStarted = true;
        _fieldIndex = 0;
        _rowsRead = checked(_rowsRead + 1);
        return ValueTask.FromResult((int)_fieldCount);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> CompleteExportAsync(CancellationToken cancellationToken)
    {
        if (_asynchronousOperation is not null)
        {
            while (await _asynchronousOperation.ReadAsync(
                _scratch.AsMemory(0, 1),
                cancellationToken).ConfigureAwait(false) != 0)
            {
            }

            var result = _asynchronousOperation.Result ?? throw new InvalidOperationException(
                "PostgreSQL did not complete binary COPY after its trailer.");
            if (result.Response.Format != BlueTuskCopyFormat.Binary)
            {
                throw new InvalidOperationException("PostgreSQL did not execute binary COPY.");
            }

            if (!BlueTuskCommandTagParser.TryGetRowsAffected(
                    result.CommandTag,
                    out var rowsAffected) ||
                rowsAffected != _rowsRead)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL reported an invalid binary COPY row count for {_rowsRead} rows read.");
            }
        }
        else
        {
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
        }

        return -1;
    }

    public ValueTask<T?> ReadAsync<T>(
        CancellationToken cancellationToken = default) =>
        ReadAsync<T>(postgreSqlTypeOid: null, cancellationToken);

    public T? Read<T>(uint? postgreSqlTypeOid = null)
    {
        EnsureSynchronousMode();
        EnsureFieldAvailable();
        var lengthBytes = new byte[sizeof(int)];
        ReadExactly(lengthBytes);
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
        ReadExactly(payload);
        return BlueTuskBinaryCopyCodec.Decode<T>(
            payload,
            postgreSqlTypeOid,
            _registry,
            ref _fieldStates[_fieldIndex - 1].Decoder);
    }

    /// <summary>Reads the current field without decoding its PostgreSQL binary payload.</summary>
    public ReadOnlyMemory<byte>? ReadRaw()
    {
        EnsureSynchronousMode();
        EnsureFieldAvailable();
        var lengthBytes = new byte[sizeof(int)];
        ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        _fieldIndex++;
        if (length == -1)
        {
            return null;
        }

        if (length < -1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY field declared invalid length {length}.");
        }

        var payload = new byte[length];
        ReadExactly(payload);
        return payload;
    }

    public ValueTask<T?> ReadAsync<T>(
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (!_rowStarted || _fieldIndex >= _fieldCount)
        {
            throw new InvalidOperationException(
                "StartRowAsync must identify a row with an unread field before ReadAsync is called.");
        }

        if (_asynchronousOperation?.TryReadExactly(_scratch.AsMemory(0, sizeof(int))) != true)
        {
            return ReadFieldSlowAsync<T>(postgreSqlTypeOid, cancellationToken);
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(_scratch);
        _fieldIndex++;
        return DecodeFieldAsync<T>(length, postgreSqlTypeOid, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<T?> ReadFieldSlowAsync<T>(
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken)
    {
        await ReadExactlyAsync(
            _scratch.AsMemory(0, sizeof(int)),
            cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(_scratch);
        _fieldIndex++;
        return await DecodeFieldAsync<T>(
            length,
            postgreSqlTypeOid,
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<T?> DecodeFieldAsync<T>(
        int length,
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken)
    {
        if (length == -1)
        {
            if (default(T) is not null)
            {
                throw new InvalidOperationException(
                    $"A null binary COPY field cannot be read as non-nullable {typeof(T).FullName}.");
            }

            return ValueTask.FromResult<T?>(default);
        }

        if (length < -1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY field declared invalid length {length}.");
        }

        if (_asynchronousOperation?.TryReadMemory(length, out var value) == true)
        {
            return ValueTask.FromResult<T?>(
                BlueTuskBinaryCopyCodec.Decode<T>(
                    value,
                    postgreSqlTypeOid,
                    _registry,
                    ref _fieldStates[_fieldIndex - 1].Decoder));
        }

        return ReadAndDecodeFieldAsync<T>(length, postgreSqlTypeOid, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<T?> ReadAndDecodeFieldAsync<T>(
        int length,
        uint? postgreSqlTypeOid,
        CancellationToken cancellationToken)
    {
        var fieldIndex = _fieldIndex - 1;
        var payload = _fieldStates[fieldIndex].Buffer;
        if (payload is null || payload.Length != length)
        {
            payload = new byte[length];
            _fieldStates[fieldIndex].Buffer = payload;
        }

        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return BlueTuskBinaryCopyCodec.Decode<T>(
            payload,
            postgreSqlTypeOid,
            _registry,
            ref _fieldStates[fieldIndex].Decoder);
    }

    /// <summary>Reads the current field without decoding its PostgreSQL binary payload.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<ReadOnlyMemory<byte>?> ReadRawAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReadable();
        if (!_rowStarted || _fieldIndex >= _fieldCount)
        {
            throw new InvalidOperationException(
                "StartRowAsync must identify a row with an unread field before ReadRawAsync is called.");
        }

        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        _fieldIndex++;
        if (length == -1)
        {
            return null;
        }

        if (length < -1)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY field declared invalid length {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_synchronousOperation is not null)
            {
                _synchronousOperation.Dispose();
                return;
            }

            if (_asynchronousOperation is not null)
            {
                await _asynchronousOperation.DisposeAsync().ConfigureAwait(false);
                return;
            }

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
        finally
        {
            ReturnFieldStates();
        }
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
                "An asynchronously created binary exporter must be disposed asynchronously.");
        }

        _disposed = true;
        try
        {
            _synchronousOperation.Dispose();
        }
        finally
        {
            ReturnFieldStates();
        }
    }

    private void ReadExactly(Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = _synchronousOperation!.Read(destination[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "PostgreSQL binary COPY ended in the middle of a value.");
            }

            offset += read;
        }
    }

    private ValueTask ReadExactlyAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        if (_asynchronousOperation?.TryReadExactly(destination) == true)
        {
            return ValueTask.CompletedTask;
        }

        return ReadExactlySlowAsync(destination, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadExactlySlowAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = _asynchronousOperation is null
                ? await _pipe.ReadAsync(
                    destination[offset..],
                    cancellationToken).ConfigureAwait(false)
                : await _asynchronousOperation.ReadAsync(
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

    private void EnsurePreviousRowComplete()
    {
        EnsureReadable();
        if (_rowStarted && _fieldIndex != _fieldCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row has {_fieldCount - _fieldIndex} unread fields.");
        }
    }

    private void EnsureFieldAvailable()
    {
        EnsureReadable();
        if (!_rowStarted || _fieldIndex >= _fieldCount)
        {
            throw new InvalidOperationException(
                "StartRow or StartRowAsync must identify a row with an unread field before Read is called.");
        }
    }

    private void EnsureSynchronousMode()
    {
        if (_synchronousOperation is null)
        {
            throw new InvalidOperationException(
                "This binary exporter was created for asynchronous operation.");
        }
    }

    private void ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (!header[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidOperationException("PostgreSQL binary COPY signature is invalid.");
        }

        var flags = BinaryPrimitives.ReadInt32BigEndian(header[11..]);
        if (flags != 0)
        {
            throw new NotSupportedException(
                $"PostgreSQL binary COPY flags 0x{flags:X8} are not supported.");
        }

        var extensionLength = BinaryPrimitives.ReadInt32BigEndian(header[15..]);
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
            ReadExactly(new byte[extensionLength]);
        }
    }

    private void ValidateFieldCount()
    {
        if (_fieldCount < 0 || _fieldCount != _expectedColumnCount)
        {
            throw new InvalidOperationException(
                $"PostgreSQL binary COPY row contains {_fieldCount} fields; {_expectedColumnCount} were expected.");
        }
    }

    private void ReturnFieldStates()
    {
        if (_fieldStatesReturned)
        {
            return;
        }

        _fieldStatesReturned = true;
        ArrayPool<BlueTuskBinaryCopyFieldState>.Shared.Return(
            _fieldStates,
            clearArray: true);
    }
}
