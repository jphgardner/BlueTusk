using System.Buffers;
using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;

namespace BlueTusk.Streams;

public static class TransactionSpoolFormat
{
    public const int CurrentVersion = 1;
}

public readonly record struct TransactionSpoolKey(string SourceFingerprint, uint TransactionId);

public interface ITransactionSpool
{
    ValueTask<ITransactionSpoolWriter> CreateAsync(
        TransactionSpoolKey key,
        CancellationToken cancellationToken = default);
}

public interface ITransactionSpoolWriter : IAsyncDisposable
{
    ValueTask AppendAsync(ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default);

    ValueTask<ITransactionSpoolReader> CompleteAsync(CancellationToken cancellationToken = default);

    ValueTask AbortAsync(CancellationToken cancellationToken = default);
}

internal interface ITransactionSpoolBufferWriter
{
    ValueTask AppendAsync<TState>(
        TState state,
        Action<IBufferWriter<byte>, TState> serializer,
        CancellationToken cancellationToken = default);
}

internal interface IBufferWriterSegmentSink : IBufferWriter<byte>
{
    void Write(ReadOnlyMemory<byte> source);
}

public interface ITransactionSpoolReader : IAsyncDisposable
{
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRecordsAsync(CancellationToken cancellationToken = default);
}

public interface ITransactionSpoolProtector
{
    string Id { get; }

    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}

public sealed record FileTransactionSpoolOptions
{
    public required string DirectoryPath { get; init; }

    public long MaxStorageBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public int MaxRecordBytes { get; init; } = 256 * 1024 * 1024;

    public ITransactionSpoolProtector? Protector { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStorageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecordBytes);
    }
}

public sealed class TransactionSpoolLimitExceededException : Exception
{
    public TransactionSpoolLimitExceededException(string message)
        : base(message)
    {
    }
}

public sealed class TransactionSpoolIntegrityException : Exception
{
    public TransactionSpoolIntegrityException(string message)
        : base(message)
    {
    }

    public TransactionSpoolIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class FileTransactionSpool : ITransactionSpool
{
    private readonly FileTransactionSpoolOptions _options;
    private readonly ITransactionSpoolProtector _protector;
    private long _reservedBytes;

    public FileTransactionSpool(FileTransactionSpoolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _protector = options.Protector ?? PassThroughTransactionSpoolProtector.Instance;
        Directory.CreateDirectory(_options.DirectoryPath);
        _reservedBytes = CalculateExistingReservation();
        if (_reservedBytes > _options.MaxStorageBytes)
        {
            throw new TransactionSpoolLimitExceededException(
                $"Existing transaction spool artifacts consume {_reservedBytes} bytes, " +
                $"which exceeds the {_options.MaxStorageBytes}-byte storage limit.");
        }
    }

    public long ReservedBytes => Interlocked.Read(ref _reservedBytes);

    public ValueTask<ITransactionSpoolWriter> CreateAsync(
        TransactionSpoolKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key.SourceFingerprint);

        var stem = $"{Sanitize(key.SourceFingerprint)}-{key.TransactionId}-{Guid.NewGuid():N}";
        var partialPath = Path.Combine(_options.DirectoryPath, stem + ".partial");
        var readyPath = Path.Combine(_options.DirectoryPath, stem + ".ready");
        ITransactionSpoolWriter writer = new FileTransactionSpoolWriter(
            this,
            key,
            partialPath,
            readyPath,
            _options.MaxRecordBytes,
            _protector);
        return ValueTask.FromResult(writer);
    }

    private static string Sanitize(string value)
    {
        var length = Math.Min(value.Length, 24);
        Span<char> buffer = stackalloc char[length];
        for (var index = 0; index < length; index++)
        {
            var character = value[index];
            buffer[index] = char.IsAsciiLetterOrDigit(character) ? character : '_';
        }

        return new string(buffer);
    }

    private long CalculateExistingReservation()
    {
        long total = 0;
        foreach (var pattern in new[] { "*.partial", "*.ready" })
        {
            foreach (var path in Directory.EnumerateFiles(_options.DirectoryPath, pattern))
            {
                total = checked(total + new FileInfo(path).Length);
            }
        }

        return total;
    }

    private void Reserve(long byteCount)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _reservedBytes);
            var updated = checked(current + byteCount);
            if (updated > _options.MaxStorageBytes)
            {
                throw new TransactionSpoolLimitExceededException(
                    $"The transaction spool limit of {_options.MaxStorageBytes} bytes would be exceeded.");
            }

            if (Interlocked.CompareExchange(ref _reservedBytes, updated, current) == current)
            {
                return;
            }
        }
    }

    private void Release(long byteCount) => Interlocked.Add(ref _reservedBytes, -byteCount);

    private sealed class FileTransactionSpoolWriter :
        ITransactionSpoolWriter,
        ITransactionSpoolBufferWriter
    {
        private const uint Magic = 0x50535442;
        private readonly FileTransactionSpool _owner;
        private readonly string _partialPath;
        private readonly string _readyPath;
        private readonly int _maxRecordBytes;
        private readonly ITransactionSpoolProtector _protector;
        private FileStream? _stream;
        private long _reservedBytes;
        private int _recordCount;
        private bool _completed;
        private readonly byte[] _recordHeader = new byte[8];

        public FileTransactionSpoolWriter(
            FileTransactionSpool owner,
            TransactionSpoolKey key,
            string partialPath,
            string readyPath,
            int maxRecordBytes,
            ITransactionSpoolProtector protector)
        {
            _owner = owner;
            _partialPath = partialPath;
            _readyPath = readyPath;
            _maxRecordBytes = maxRecordBytes;
            _protector = protector;
            _stream = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            var source = Encoding.UTF8.GetBytes(key.SourceFingerprint);
            var protectorId = Encoding.UTF8.GetBytes(protector.Id);
            var header = new byte[20 + source.Length + protectorId.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
            BinaryPrimitives.WriteInt32LittleEndian(
                header.AsSpan(4),
                TransactionSpoolFormat.CurrentVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), key.TransactionId);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), source.Length);
            source.CopyTo(header.AsSpan(16));
            var protectorOffset = 16 + source.Length;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(protectorOffset), protectorId.Length);
            protectorId.CopyTo(header.AsSpan(protectorOffset + 4));
            try
            {
                Reserve(header.Length);
                _stream.Write(header);
            }
            catch
            {
                _stream.Dispose();
                _stream = null;
                File.Delete(_partialPath);
                Release(_reservedBytes);
                throw;
            }
        }

        public async ValueTask AppendAsync(
            ReadOnlyMemory<byte> record,
            CancellationToken cancellationToken = default)
        {
            var stream = RequireActive();
            if (record.Length > _maxRecordBytes)
            {
                throw new TransactionSpoolLimitExceededException(
                    $"A spool record of {record.Length} bytes exceeds the {_maxRecordBytes}-byte record limit.");
            }

            ReadOnlyMemory<byte> protectedData = ReferenceEquals(
                _protector,
                PassThroughTransactionSpoolProtector.Instance)
                ? record
                : _protector.Protect(record.Span);
            if (protectedData.Length > _maxRecordBytes)
            {
                throw new TransactionSpoolLimitExceededException(
                    $"A protected spool record of {protectedData.Length} bytes exceeds the {_maxRecordBytes}-byte record limit.");
            }

            BinaryPrimitives.WriteInt32LittleEndian(_recordHeader, protectedData.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(
                _recordHeader.AsSpan(4),
                Crc32.Compute(protectedData.Span));
            Reserve(_recordHeader.Length + protectedData.Length);
            try
            {
                await stream.WriteAsync(_recordHeader, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(protectedData, cancellationToken).ConfigureAwait(false);
                _recordCount++;
            }
            catch
            {
                Release(_recordHeader.Length + protectedData.Length);
                throw;
            }
        }

        public async ValueTask AppendAsync<TState>(
            TState state,
            Action<IBufferWriter<byte>, TState> serializer,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(serializer);
            if (!ReferenceEquals(_protector, PassThroughTransactionSpoolProtector.Instance))
            {
                var contiguous = new ArrayBufferWriter<byte>();
                serializer(contiguous, state);
                await AppendAsync(contiguous.WrittenMemory, cancellationToken).ConfigureAwait(false);
                return;
            }

            var stream = RequireActive();
            using var record = new PooledSegmentBufferWriter(_maxRecordBytes);
            serializer(record, state);
            record.WriteRecordHeader();
            Reserve(_recordHeader.Length + record.WrittenCount);
            try
            {
                await record.WriteToAsync(stream, cancellationToken).ConfigureAwait(false);
                _recordCount++;
            }
            catch
            {
                Release(_recordHeader.Length + record.WrittenCount);
                throw;
            }
        }

        public async ValueTask<ITransactionSpoolReader> CompleteAsync(
            CancellationToken cancellationToken = default)
        {
            var stream = RequireActive();
            BinaryPrimitives.WriteInt32LittleEndian(_recordHeader, -1);
            BinaryPrimitives.WriteInt32LittleEndian(_recordHeader.AsSpan(4), _recordCount);
            Reserve(_recordHeader.Length);
            try
            {
                await stream.WriteAsync(_recordHeader, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                await stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
                File.Move(_partialPath, _readyPath);
                _completed = true;
                return new FileTransactionSpoolReader(
                    _owner,
                    _readyPath,
                    _reservedBytes,
                    _maxRecordBytes,
                    _protector);
            }
            catch
            {
                Release(_recordHeader.Length);
                throw;
            }
        }

        public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed)
            {
                throw new InvalidOperationException("A completed transaction spool cannot be aborted by its writer.");
            }

            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
                _stream = null;
            }

            File.Delete(_partialPath);
            Release(_reservedBytes);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed && (_stream is not null || File.Exists(_partialPath)))
            {
                await AbortAsync().ConfigureAwait(false);
            }
        }

        private FileStream RequireActive()
        {
            if (_completed || _stream is null)
            {
                throw new InvalidOperationException("The transaction spool writer is no longer active.");
            }

            return _stream;
        }

        private void Reserve(long byteCount)
        {
            _owner.Reserve(byteCount);
            _reservedBytes = checked(_reservedBytes + byteCount);
        }

        private void Release(long byteCount)
        {
            _owner.Release(byteCount);
            _reservedBytes -= byteCount;
        }
    }

    private sealed class PooledSegmentBufferWriter : IBufferWriterSegmentSink, IDisposable
    {
        private const int SegmentSize = 64 * 1024;
        private readonly int _maximumLength;
        private readonly List<Segment> _segments = [];
        private byte[]? _current;
        private int _currentWritten;
        private int _writtenCount;
        private uint _checksumState = uint.MaxValue;

        internal PooledSegmentBufferWriter(int maximumLength)
        {
            _maximumLength = maximumLength;
            _current = ArrayPool<byte>.Shared.Rent(SegmentSize);
            _currentWritten = 8;
            _segments.Add(new Segment(_current.AsMemory(0, _currentWritten), _current));
        }

        internal int WrittenCount => _writtenCount;

        internal uint Checksum => ~_checksumState;

        internal void WriteRecordHeader()
        {
            var header = _segments[0].Owner!.AsSpan(0, 8);
            BinaryPrimitives.WriteInt32LittleEndian(header, WrittenCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Checksum);
        }

        public void Advance(int count)
        {
            if (_current is null ||
                count < 0 ||
                count > _current.Length - _currentWritten ||
                count > _maximumLength - _writtenCount)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _checksumState = Crc32.Append(
                _checksumState,
                _current.AsSpan(_currentWritten, count));
            _currentWritten += count;
            _writtenCount += count;
            _segments[^1] = new Segment(
                _current.AsMemory(0, _currentWritten),
                _current);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _current.AsMemory(_currentWritten);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureBuffer(sizeHint);
            return _current.AsSpan(_currentWritten);
        }

        public void Write(ReadOnlyMemory<byte> source)
        {
            if (source.IsEmpty)
            {
                return;
            }

            if (source.Length > _maximumLength - _writtenCount)
            {
                throw new TransactionSpoolLimitExceededException(
                    $"A spool record exceeds the {_maximumLength}-byte record limit.");
            }

            _checksumState = Crc32.Append(_checksumState, source.Span);
            _writtenCount += source.Length;
            _current = null;
            _currentWritten = 0;
            _segments.Add(new Segment(source, Owner: null));
        }

        internal async ValueTask WriteToAsync(
            Stream destination,
            CancellationToken cancellationToken)
        {
            foreach (var segment in _segments)
            {
                await destination.WriteAsync(
                    segment.Memory,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            foreach (var segment in _segments)
            {
                if (segment.Owner is not null)
                {
                    ArrayPool<byte>.Shared.Return(segment.Owner);
                }
            }

            _segments.Clear();
            _current = null;
            _currentWritten = 0;
        }

        private void EnsureBuffer(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            if (sizeHint > _maximumLength - _writtenCount)
            {
                throw new TransactionSpoolLimitExceededException(
                    $"A spool record exceeds the {_maximumLength}-byte record limit.");
            }

            if (_current is not null && sizeHint <= _current.Length - _currentWritten)
            {
                return;
            }

            _current = ArrayPool<byte>.Shared.Rent(Math.Max(SegmentSize, sizeHint));
            _currentWritten = 0;
            _segments.Add(new Segment(ReadOnlyMemory<byte>.Empty, _current));
        }

        private readonly record struct Segment(
            ReadOnlyMemory<byte> Memory,
            byte[]? Owner);
    }

    private sealed class FileTransactionSpoolReader : ITransactionSpoolReader
    {
        private const uint Magic = 0x50535442;
        private const int MemoryMappedRecordThreshold = 85_000;
        private readonly FileTransactionSpool _owner;
        private readonly string _path;
        private readonly long _reservedBytes;
        private readonly int _maxRecordBytes;
        private readonly ITransactionSpoolProtector _protector;
        private int _disposed;

        public FileTransactionSpoolReader(
            FileTransactionSpool owner,
            string path,
            long reservedBytes,
            int maxRecordBytes,
            ITransactionSpoolProtector protector)
        {
            _owner = owner;
            _path = path;
            _reservedBytes = reservedBytes;
            _maxRecordBytes = maxRecordBytes;
            _protector = protector;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadRecordsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var fixedHeader = new byte[16];
            await ReadExactlyAsync(stream, fixedHeader, cancellationToken).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(4)) !=
                TransactionSpoolFormat.CurrentVersion)
            {
                throw new TransactionSpoolIntegrityException("The transaction spool header is invalid or unsupported.");
            }

            var sourceLength = BinaryPrimitives.ReadInt32LittleEndian(fixedHeader.AsSpan(12));
            _ = await ReadBoundedBytesAsync(stream, sourceLength, cancellationToken).ConfigureAwait(false);
            var lengthBuffer = new byte[4];
            await ReadExactlyAsync(stream, lengthBuffer, cancellationToken).ConfigureAwait(false);
            var protectorLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
            var protectorBytes = await ReadBoundedBytesAsync(stream, protectorLength, cancellationToken).ConfigureAwait(false);
            var protectorId = Encoding.UTF8.GetString(protectorBytes);
            if (!string.Equals(protectorId, _protector.Id, StringComparison.Ordinal))
            {
                throw new TransactionSpoolIntegrityException(
                    $"The transaction spool requires protector '{protectorId}', but '{_protector.Id}' is configured.");
            }

            var recordsRead = 0;
            var recordHeader = new byte[8];
            while (true)
            {
                await ReadExactlyAsync(stream, recordHeader, cancellationToken).ConfigureAwait(false);
                var recordLength = BinaryPrimitives.ReadInt32LittleEndian(recordHeader);
                if (recordLength == -1)
                {
                    var declaredCount = BinaryPrimitives.ReadInt32LittleEndian(recordHeader.AsSpan(4));
                    if (declaredCount != recordsRead || stream.Position != stream.Length)
                    {
                        throw new TransactionSpoolIntegrityException("The transaction spool completion footer is invalid.");
                    }

                    yield break;
                }

                var protectedData = ReferenceEquals(
                    _protector,
                    PassThroughTransactionSpoolProtector.Instance)
                    ? await ReadBoundedRecordAsync(
                        stream,
                        recordLength,
                        cancellationToken).ConfigureAwait(false)
                    : await ReadBoundedBytesAsync(
                        stream,
                        recordLength,
                        cancellationToken).ConfigureAwait(false);
                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader.AsSpan(4));
                if (Crc32.Compute(protectedData.Span) != expectedChecksum)
                {
                    throw new TransactionSpoolIntegrityException("A transaction spool record failed its integrity check.");
                }

                recordsRead++;
                yield return ReferenceEquals(
                    _protector,
                    PassThroughTransactionSpoolProtector.Instance)
                    ? protectedData
                    : _protector.Unprotect(protectedData.Span);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                File.Delete(_path);
                _owner.Release(_reservedBytes);
            }

            return ValueTask.CompletedTask;
        }

        private async ValueTask<byte[]> ReadBoundedBytesAsync(
            Stream stream,
            int length,
            CancellationToken cancellationToken)
        {
            if (length < 0 || length > _maxRecordBytes)
            {
                throw new TransactionSpoolIntegrityException(
                    $"A transaction spool length of {length} is outside the configured bounds.");
            }

            var buffer = new byte[length];
            await ReadExactlyAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            return buffer;
        }

        private async ValueTask<ReadOnlyMemory<byte>> ReadBoundedRecordAsync(
            FileStream stream,
            int length,
            CancellationToken cancellationToken)
        {
            ValidateRecordLength(length);
            if (length < MemoryMappedRecordThreshold)
            {
                return await ReadBoundedBytesAsync(
                    stream,
                    length,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (length > stream.Length - stream.Position)
            {
                throw new TransactionSpoolIntegrityException(
                    "The transaction spool ended before its declared data was complete.");
            }

            var memory = MemoryMappedRecordMemory.Create(stream, length);
            stream.Position += length;
            return memory;
        }

        private void ValidateRecordLength(int length)
        {
            if (length < 0 || length > _maxRecordBytes)
            {
                throw new TransactionSpoolIntegrityException(
                    $"A transaction spool length of {length} is outside the configured bounds.");
            }
        }

        private static async ValueTask ReadExactlyAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            try
            {
                await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                throw new TransactionSpoolIntegrityException(
                    "The transaction spool ended before its declared data was complete.",
                    exception);
            }
        }
    }

    private sealed unsafe class MemoryMappedRecordMemory : MemoryManager<byte>
    {
        private readonly MemoryMappedRecordOwner _owner;
        private readonly int _length;
        private int _disposed;

        private MemoryMappedRecordMemory(MemoryMappedRecordOwner owner, int length)
        {
            _owner = owner;
            _length = length;
        }

        internal static ReadOnlyMemory<byte> Create(FileStream stream, int length)
        {
            var mapping = MemoryMappedFile.CreateFromFile(
                stream,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: true);
            MemoryMappedViewAccessor? accessor = null;
            try
            {
                accessor = mapping.CreateViewAccessor(
                    stream.Position,
                    length,
                    MemoryMappedFileAccess.Read);
                var owner = new MemoryMappedRecordOwner(mapping, accessor);
                mapping = null!;
                accessor = null;
                return new MemoryMappedRecordMemory(owner, length).Memory;
            }
            finally
            {
                accessor?.Dispose();
                mapping?.Dispose();
            }
        }

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new Span<byte>(_owner.Pointer, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)_length);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return new MemoryHandle(_owner.Pointer + elementIndex, pinnable: this);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Dispose();
            }
        }
    }

    private sealed unsafe class MemoryMappedRecordOwner : IDisposable
    {
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _accessor;
        private byte* _pointer;
        private int _disposed;

        internal MemoryMappedRecordOwner(
            MemoryMappedFile mapping,
            MemoryMappedViewAccessor accessor)
        {
            _mapping = mapping;
            _accessor = accessor;
            byte* basePointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePointer);
            _pointer = basePointer + accessor.PointerOffset;
        }

        ~MemoryMappedRecordOwner()
        {
            Dispose();
        }

        internal byte* Pointer
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _pointer;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
            _accessor.Dispose();
            _accessor = null;
            _mapping!.Dispose();
            _mapping = null;
            GC.SuppressFinalize(this);
        }
    }

    private sealed class PassThroughTransactionSpoolProtector : ITransactionSpoolProtector
    {
        public static PassThroughTransactionSpoolProtector Instance { get; } = new();

        public string Id => "none";

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => protectedData.ToArray();
    }

    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var value = Append(uint.MaxValue, data);
            return ~value;
        }

        public static uint Append(uint value, ReadOnlySpan<byte> data)
        {
            foreach (var item in data)
            {
                value = (value >> 8) ^ Table[(value ^ item) & 0xFF];
            }

            return value;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value >> 1) ^ (0xEDB88320U & (uint)-(int)(value & 1));
                }

                table[index] = value;
            }

            return table;
        }
    }
}
