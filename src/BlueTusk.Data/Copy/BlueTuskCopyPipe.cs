using System.Buffers;
using System.Threading.Channels;

namespace BlueTusk.Data.Copy;

internal sealed class BlueTuskCopyPipe : Stream
{
    private const int CoalescedWriteSize = 8 * 1024;
    private readonly Channel<PooledChunk> _channel;
    private readonly bool _coalesceWrites;
    private byte[]? _pendingWriteBuffer;
    private int _pendingWriteCount;
    private PooledChunk _current;
    private int _currentOffset;
    private bool _hasCurrent;

    public BlueTuskCopyPipe(bool coalesceWrites = false)
    {
        _coalesceWrites = coalesceWrites;
        _channel = Channel.CreateBounded<PooledChunk>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public ValueTask WriteChunkAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        if (_coalesceWrites)
        {
            return WriteCoalescedAsync(buffer, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.CopyTo(bytes);
        var chunk = new PooledChunk(bytes, buffer.Length);
        if (_channel.Writer.TryWrite(chunk))
        {
            return ValueTask.CompletedTask;
        }

        return EnqueueWriteAsync(chunk, cancellationToken);
    }

    private async ValueTask WriteCoalescedAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            _pendingWriteBuffer ??= ArrayPool<byte>.Shared.Rent(CoalescedWriteSize);
            var count = Math.Min(
                buffer.Length,
                _pendingWriteBuffer.Length - _pendingWriteCount);
            buffer[..count].CopyTo(_pendingWriteBuffer.AsMemory(_pendingWriteCount));
            _pendingWriteCount += count;
            buffer = buffer[count..];
            if (_pendingWriteCount == _pendingWriteBuffer.Length)
            {
                await EnqueuePendingWriteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void CompleteWriting(Exception? exception = null)
    {
        if (exception is not null)
        {
            ReturnPendingWriteBuffer();
        }
        else if (_pendingWriteCount != 0)
        {
            var bytes = _pendingWriteBuffer!;
            var length = _pendingWriteCount;
            _pendingWriteBuffer = null;
            _pendingWriteCount = 0;
            if (!_channel.Writer.TryWrite(new PooledChunk(bytes, length)))
            {
                ArrayPool<byte>.Shared.Return(bytes);
                exception = new IOException("The COPY pipe could not flush its final buffered chunk.");
            }
        }

        _channel.Writer.TryComplete(exception);
    }

    public async ValueTask CompleteWritingAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingWriteCount != 0)
        {
            await EnqueuePendingWriteAsync(cancellationToken).ConfigureAwait(false);
        }

        _channel.Writer.TryComplete();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (!_hasCurrent || _currentOffset == _current.Length)
        {
            ReleaseCurrent();
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_channel.Reader.TryRead(out _current))
            {
                _hasCurrent = true;
                _currentOffset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.Bytes.AsMemory(_currentOffset, count).CopyTo(buffer);
        _currentOffset += count;
        return count;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        WriteChunkAsync(buffer, cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous COPY pipe reads are not supported.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Synchronous COPY pipe writes are not supported.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CompleteWriting();
            ReturnPendingWriteBuffer();
            ReleaseCurrent();
            while (_channel.Reader.TryRead(out var chunk))
            {
                ArrayPool<byte>.Shared.Return(chunk.Bytes);
            }
        }

        base.Dispose(disposing);
    }

    private void ReleaseCurrent()
    {
        if (!_hasCurrent)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_current.Bytes);
        _current = default;
        _currentOffset = 0;
        _hasCurrent = false;
    }

    private async ValueTask EnqueuePendingWriteAsync(CancellationToken cancellationToken)
    {
        var bytes = _pendingWriteBuffer!;
        var length = _pendingWriteCount;
        _pendingWriteBuffer = null;
        _pendingWriteCount = 0;
        try
        {
            await _channel.Writer.WriteAsync(
                new PooledChunk(bytes, length),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(bytes);
            throw;
        }
    }

    private async ValueTask EnqueueWriteAsync(
        PooledChunk chunk,
        CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(chunk.Bytes);
            throw;
        }
    }

    private void ReturnPendingWriteBuffer()
    {
        if (_pendingWriteBuffer is null)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_pendingWriteBuffer);
        _pendingWriteBuffer = null;
        _pendingWriteCount = 0;
    }

    private readonly record struct PooledChunk(byte[] Bytes, int Length);
}
