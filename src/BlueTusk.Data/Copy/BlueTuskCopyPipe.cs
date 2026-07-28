using System.Threading.Channels;

namespace BlueTusk.Data.Copy;

internal sealed class BlueTuskCopyPipe : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
    private byte[]? _current;
    private int _currentOffset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public async ValueTask WriteChunkAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        await _channel.Writer.WriteAsync(
            buffer.ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    public void CompleteWriting(Exception? exception = null) =>
        _channel.Writer.TryComplete(exception);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_current is null || _currentOffset == _current.Length)
        {
            if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_channel.Reader.TryRead(out _current))
            {
                _currentOffset = 0;
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, count).CopyTo(buffer);
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
}
