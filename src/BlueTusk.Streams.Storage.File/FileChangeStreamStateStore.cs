using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.TypeSystem;

namespace BlueTusk.Streams.Storage.File;

public static class FileChangeStreamStateFormat
{
    public const int CurrentVersion = 1;
}

public sealed record FileChangeStreamStateStoreOptions
{
    public required string DirectoryPath { get; init; }

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(LockRetryDelay, TimeSpan.Zero);
        if (LockRetryDelay > LockTimeout)
        {
            throw new ArgumentException(
                "The lock retry delay cannot exceed the lock timeout.",
                nameof(LockRetryDelay));
        }
    }
}

public sealed class FileChangeStreamStateStore : IChangeStreamStateStore
{
    private static ReadOnlySpan<byte> Magic => "BTSTATE1"u8;
    private const int HeaderSize = 12;
    private const int HashSize = 32;
    private const int MaximumPayloadSize = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly FileChangeStreamStateStoreOptions _options;
    private readonly TimeProvider _timeProvider;

    public FileChangeStreamStateStore(
        FileChangeStreamStateStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetFullPath(options.DirectoryPath));
    }

    public async ValueTask<ChangeStreamCheckpoint?> ReadAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken = default)
    {
        await using var stateLock = await AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        var document = await ReadDocumentAsync(GetStatePath(key), cancellationToken).ConfigureAwait(false);
        return document.Checkpoint?.ToCheckpoint();
    }

    public async ValueTask<ChangeCheckpointWriteResult> CompareExchangeAsync(
        ChangeStreamStateKey key,
        long expectedGeneration,
        ChangeStreamCheckpoint replacement,
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(lease);
        await using var stateLock = await AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        var statePath = GetStatePath(key);
        var document = await ReadDocumentAsync(statePath, cancellationToken).ConfigureAwait(false);
        var current = document.Checkpoint?.ToCheckpoint();
        var now = _timeProvider.GetUtcNow();

        if (!IsLeaseCurrent(document.Lease, key, lease, now, requireUnexpired: true))
        {
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Fenced, current);
        }

        if (!string.Equals(key.SourceFingerprint, replacement.Source.Fingerprint, StringComparison.Ordinal))
        {
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current);
        }

        if ((current?.StoreGeneration ?? -1) != expectedGeneration ||
            replacement.StoreGeneration != checked(expectedGeneration + 1))
        {
            return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Conflict, current);
        }

        if (current is not null)
        {
            try
            {
                replacement.EnsureCompatibleWith(current);
            }
            catch (ChangeStreamCheckpointMismatchException)
            {
                return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Incompatible, current);
            }

            if (replacement.AcknowledgedCommitPosition < current.AcknowledgedCommitPosition)
            {
                return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.BackwardMovement, current);
            }
        }

        document.Checkpoint = CheckpointDocument.FromCheckpoint(replacement);
        await WriteDocumentAsync(statePath, document, cancellationToken).ConfigureAwait(false);
        return new ChangeCheckpointWriteResult(ChangeCheckpointWriteStatus.Stored, replacement);
    }

    public async ValueTask<ChangeLeaseAcquireResult> AcquireAsync(
        ChangeStreamStateKey key,
        string ownerId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseArguments(ownerId, duration);
        await using var stateLock = await AcquireLockAsync(key, cancellationToken).ConfigureAwait(false);
        var statePath = GetStatePath(key);
        var document = await ReadDocumentAsync(statePath, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var current = document.Lease?.ToLease(key);

        if (current is not null &&
            current.ExpiresAt > now &&
            !string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
        {
            return new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.HeldByAnotherOwner, current);
        }

        ChangeStreamLease acquired;
        if (current is not null &&
            current.ExpiresAt > now &&
            string.Equals(current.OwnerId, ownerId, StringComparison.Ordinal))
        {
            acquired = current with { ExpiresAt = now + duration };
        }
        else
        {
            var token = checked(document.LastFencingToken + 1);
            document.LastFencingToken = token;
            acquired = new ChangeStreamLease(key, ownerId, token, now + duration);
        }

        document.Lease = LeaseDocument.FromLease(acquired);
        await WriteDocumentAsync(statePath, document, cancellationToken).ConfigureAwait(false);
        return new ChangeLeaseAcquireResult(ChangeLeaseAcquireStatus.Acquired, acquired);
    }

    public async ValueTask<ChangeStreamLease?> RenewAsync(
        ChangeStreamLease lease,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseArguments(lease.OwnerId, duration);
        await using var stateLock = await AcquireLockAsync(lease.Key, cancellationToken).ConfigureAwait(false);
        var statePath = GetStatePath(lease.Key);
        var document = await ReadDocumentAsync(statePath, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        if (!IsLeaseCurrent(document.Lease, lease.Key, lease, now, requireUnexpired: true))
        {
            return null;
        }

        var renewed = lease with { ExpiresAt = now + duration };
        document.Lease = LeaseDocument.FromLease(renewed);
        await WriteDocumentAsync(statePath, document, cancellationToken).ConfigureAwait(false);
        return renewed;
    }

    public async ValueTask<bool> ReleaseAsync(
        ChangeStreamLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        await using var stateLock = await AcquireLockAsync(lease.Key, cancellationToken).ConfigureAwait(false);
        var statePath = GetStatePath(lease.Key);
        var document = await ReadDocumentAsync(statePath, cancellationToken).ConfigureAwait(false);
        if (!IsLeaseCurrent(
                document.Lease,
                lease.Key,
                lease,
                _timeProvider.GetUtcNow(),
                requireUnexpired: false))
        {
            return false;
        }

        document.Lease = null;
        await WriteDocumentAsync(statePath, document, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<FileStream> AcquireLockAsync(
        ChangeStreamStateKey key,
        CancellationToken cancellationToken)
    {
        var lockPath = GetStatePath(key) + ".lock";
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (stopwatch.Elapsed < _options.LockTimeout)
            {
                await Task.Delay(_options.LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<StateDocument> ReadDocumentAsync(
        string statePath,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(statePath))
        {
            return new StateDocument();
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < HeaderSize + HashSize || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new FileChangeStreamStateStoreException("The state file header is invalid.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Magic.Length, sizeof(int)));
        if (payloadLength < 0 || payloadLength > MaximumPayloadSize ||
            bytes.Length != HeaderSize + payloadLength + HashSize)
        {
            throw new FileChangeStreamStateStoreException("The state file length is invalid.");
        }

        var payload = bytes.AsSpan(HeaderSize, payloadLength);
        var expectedHash = bytes.AsSpan(HeaderSize + payloadLength, HashSize);
        Span<byte> actualHash = stackalloc byte[HashSize];
        _ = SHA256.HashData(payload, actualHash);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new FileChangeStreamStateStoreException("The state file checksum is invalid.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<StateDocument>(payload, SerializerOptions) ??
                throw new JsonException("The state payload is empty.");
            if (document.FormatVersion != FileChangeStreamStateFormat.CurrentVersion)
            {
                throw new FileChangeStreamStateStoreException(
                    $"State format {document.FormatVersion} is not supported.");
            }

            return document;
        }
        catch (JsonException exception)
        {
            throw new FileChangeStreamStateStoreException(
                "The state file payload is invalid.",
                exception);
        }
    }

    private static async ValueTask WriteDocumentAsync(
        string statePath,
        StateDocument document,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        if (payload.Length > MaximumPayloadSize)
        {
            throw new FileChangeStreamStateStoreException("The state payload exceeds the storage limit.");
        }

        var output = new byte[HeaderSize + payload.Length + HashSize];
        Magic.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(Magic.Length, sizeof(int)), payload.Length);
        payload.CopyTo(output.AsSpan(HeaderSize));
        _ = SHA256.HashData(payload, output.AsSpan(HeaderSize + payload.Length, HashSize));

        var temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            System.IO.File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            System.IO.File.Delete(temporaryPath);
        }
    }

    private string GetStatePath(ChangeStreamStateKey key)
    {
        var identity = Encoding.UTF8.GetBytes(key.SourceFingerprint + "\n" + key.ConsumerGroup);
        var fileName = Convert.ToHexStringLower(SHA256.HashData(identity)) + ".state";
        return Path.Combine(Path.GetFullPath(_options.DirectoryPath), fileName);
    }

    private static bool IsLeaseCurrent(
        LeaseDocument? currentDocument,
        ChangeStreamStateKey key,
        ChangeStreamLease candidate,
        DateTimeOffset now,
        bool requireUnexpired)
    {
        var current = currentDocument?.ToLease(key);
        return candidate.Key == key &&
               current is not null &&
               current.FencingToken == candidate.FencingToken &&
               string.Equals(current.OwnerId, candidate.OwnerId, StringComparison.Ordinal) &&
               (!requireUnexpired || current.ExpiresAt > now);
    }

    private static void ValidateLeaseArguments(string ownerId, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
    }

    private sealed class StateDocument
    {
        public int FormatVersion { get; init; } = FileChangeStreamStateFormat.CurrentVersion;

        public CheckpointDocument? Checkpoint { get; set; }

        public LeaseDocument? Lease { get; set; }

        public long LastFencingToken { get; set; }
    }

    private sealed record CheckpointDocument(
        int FormatVersion,
        string SystemIdentifier,
        string DatabaseName,
        string SlotName,
        string PublicationFingerprint,
        string DatabaseIdentity,
        string OutputPlugin,
        string MappingFingerprint,
        ulong AcknowledgedCommitPosition,
        long StoreGeneration)
    {
        public static CheckpointDocument FromCheckpoint(ChangeStreamCheckpoint checkpoint) =>
            new(
                checkpoint.FormatVersion,
                checkpoint.Source.SystemIdentifier,
                checkpoint.Source.DatabaseName,
                checkpoint.Source.SlotName,
                checkpoint.Source.PublicationFingerprint,
                checkpoint.DatabaseIdentity,
                checkpoint.OutputPlugin,
                checkpoint.MappingFingerprint,
                checkpoint.AcknowledgedCommitPosition.Value,
                checkpoint.StoreGeneration);

        public ChangeStreamCheckpoint ToCheckpoint() =>
            new(
                FormatVersion,
                new ChangeSourceIdentity(
                    SystemIdentifier,
                    DatabaseName,
                    SlotName,
                    PublicationFingerprint),
                DatabaseIdentity,
                OutputPlugin,
                MappingFingerprint,
                new BlueTuskLogSequenceNumber(AcknowledgedCommitPosition),
                StoreGeneration);
    }

    private sealed record LeaseDocument(
        string OwnerId,
        long FencingToken,
        DateTimeOffset ExpiresAt)
    {
        public static LeaseDocument FromLease(ChangeStreamLease lease) =>
            new(lease.OwnerId, lease.FencingToken, lease.ExpiresAt);

        public ChangeStreamLease ToLease(ChangeStreamStateKey key) =>
            new(key, OwnerId, FencingToken, ExpiresAt);
    }
}

public sealed class FileChangeStreamStateStoreException : Exception
{
    public FileChangeStreamStateStoreException(string message)
        : base(message)
    {
    }

    public FileChangeStreamStateStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
