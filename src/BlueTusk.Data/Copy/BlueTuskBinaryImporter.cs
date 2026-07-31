using System.Buffers.Binary;
using BlueTusk.Client;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data.Copy;

public sealed class BlueTuskBinaryImporter : IDisposable, IAsyncDisposable
{
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
    private readonly BlueTuskTypeRegistry _registry;
    private readonly short _columnCount;
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
    }

    internal BlueTuskBinaryImporter(
        BlueTuskCopyInOperation operation,
        BlueTuskTypeRegistry registry,
        int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, short.MaxValue);
        _pipe = null!;
        _copyTask = null!;
        _synchronousOperation = operation ?? throw new ArgumentNullException(nameof(operation));
        _registry = registry;
        _columnCount = checked((short)columnCount);
    }

    internal void Initialize() => _synchronousOperation!.Write(Header);

    internal ValueTask InitializeAsync(CancellationToken cancellationToken) =>
        WriteChunkAsync(Header, cancellationToken);

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

    public async ValueTask StartRowAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_rowStarted && _fieldIndex != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row contains {_fieldIndex} of {_columnCount} fields.");
        }

        var header = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(header, _columnCount);
        await WriteChunkAsync(header, cancellationToken).ConfigureAwait(false);
        _rowStarted = true;
        _fieldIndex = 0;
        _rowsStarted = checked(_rowsStarted + 1);
    }

    public ValueTask WriteAsync<T>(
        T? value,
        CancellationToken cancellationToken = default) =>
        WriteAsync(value, postgreSqlTypeOid: null, cancellationToken);

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
        var field = EncodeField(value, postgreSqlTypeOid);
        await WriteChunkAsync(field, cancellationToken).ConfigureAwait(false);
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

    public async ValueTask<long> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (_rowStarted && _fieldIndex != _columnCount)
        {
            throw new InvalidOperationException(
                $"The current binary COPY row contains {_fieldIndex} of {_columnCount} fields.");
        }

        var trailer = new byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(trailer, -1);
        await WriteChunkAsync(trailer, cancellationToken).ConfigureAwait(false);
        _pipe.CompleteWriting();
        _completed = true;
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

    private async ValueTask WriteChunkAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var writeTask = _pipe.WriteChunkAsync(data, cancellationToken).AsTask();
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
}
