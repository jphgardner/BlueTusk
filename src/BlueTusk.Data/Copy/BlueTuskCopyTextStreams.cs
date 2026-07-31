using System.Text;

namespace BlueTusk.Data.Copy;

internal sealed class BlueTuskCopyTextReaderStream : Stream
{
    private const int CharacterBufferSize = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TextReader _reader;
    private readonly Encoder _encoder = StrictUtf8.GetEncoder();
    private readonly char[] _characters = new char[CharacterBufferSize];
    private readonly byte[] _bytes = new byte[StrictUtf8.GetMaxByteCount(CharacterBufferSize)];
    private int _byteOffset;
    private int _byteCount;
    private bool _endOfText;
    private bool _encoderFlushed;

    public BlueTuskCopyTextReaderStream(TextReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_byteCount == 0 && !_encoderFlushed)
        {
            await FillBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = Math.Min(buffer.Length, _byteCount);
        _bytes.AsMemory(_byteOffset, count).CopyTo(buffer);
        _byteOffset += count;
        _byteCount -= count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0)
        {
            return 0;
        }

        while (_byteCount == 0 && !_encoderFlushed)
        {
            FillBuffer();
        }

        var copied = Math.Min(count, _byteCount);
        _bytes.AsSpan(_byteOffset, copied).CopyTo(buffer.AsSpan(offset, copied));
        _byteOffset += copied;
        _byteCount -= copied;
        return copied;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    private async ValueTask FillBufferAsync(CancellationToken cancellationToken)
    {
        _byteOffset = 0;
        var characterCount = 0;
        if (!_endOfText)
        {
            characterCount = await _reader.ReadAsync(
                _characters.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            _endOfText = characterCount == 0;
        }

        _encoder.Convert(
            _characters.AsSpan(0, characterCount),
            _bytes,
            _endOfText,
            out var charactersUsed,
            out _byteCount,
            out var completed);
        if (charactersUsed != characterCount)
        {
            throw new InvalidOperationException(
                "The UTF-8 COPY buffer could not consume the available characters.");
        }

        _encoderFlushed = _endOfText && completed;
    }

    private void FillBuffer()
    {
        _byteOffset = 0;
        var characterCount = 0;
        if (!_endOfText)
        {
            characterCount = _reader.Read(_characters, 0, _characters.Length);
            _endOfText = characterCount == 0;
        }

        _encoder.Convert(
            _characters.AsSpan(0, characterCount),
            _bytes,
            _endOfText,
            out var charactersUsed,
            out _byteCount,
            out var completed);
        if (charactersUsed != characterCount)
        {
            throw new InvalidOperationException(
                "The UTF-8 COPY buffer could not consume the available characters.");
        }

        _encoderFlushed = _endOfText && completed;
    }
}

internal sealed class BlueTuskCopyTextWriterStream : Stream
{
    private const int CharacterBufferSize = 4_096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TextWriter _writer;
    private readonly Decoder _decoder = StrictUtf8.GetDecoder();
    private readonly char[] _characters = new char[CharacterBufferSize];
    private bool _completed;

    public BlueTuskCopyTextWriterStream(TextWriter writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => !_completed;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        var offset = 0;
        while (offset < buffer.Length)
        {
            _decoder.Convert(
                buffer.Span[offset..],
                _characters,
                flush: false,
                out var bytesUsed,
                out var charactersUsed,
                out _);
            offset += bytesUsed;
            if (charactersUsed > 0)
            {
                await _writer.WriteAsync(
                    _characters.AsMemory(0, charactersUsed),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _decoder.Convert(
            ReadOnlySpan<byte>.Empty,
            _characters,
            flush: true,
            out _,
            out var charactersUsed,
            out _);
        if (charactersUsed > 0)
        {
            await _writer.WriteAsync(
                _characters.AsMemory(0, charactersUsed),
                cancellationToken).ConfigureAwait(false);
        }

        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _decoder.Convert(
            ReadOnlySpan<byte>.Empty,
            _characters,
            flush: true,
            out _,
            out var charactersUsed,
            out _);
        if (charactersUsed > 0)
        {
            _writer.Write(_characters, 0, charactersUsed);
        }

        _writer.Flush();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        ObjectDisposedException.ThrowIf(_completed, this);
        var remaining = buffer.AsSpan(offset, count);
        while (!remaining.IsEmpty)
        {
            _decoder.Convert(
                remaining,
                _characters,
                flush: false,
                out var bytesUsed,
                out var charactersUsed,
                out _);
            remaining = remaining[bytesUsed..];
            if (charactersUsed > 0)
            {
                _writer.Write(_characters, 0, charactersUsed);
            }
        }
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _writer.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();
}
