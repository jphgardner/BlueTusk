using Amazon.S3;

namespace BlueTusk.Sync.S3;

public sealed record S3SyncOptions
{
    public required IAmazonS3 Client { get; init; }

    public required string BucketName { get; init; }

    public required string Prefix { get; init; }

    public ServerSideEncryptionMethod ServerSideEncryption { get; init; } =
        ServerSideEncryptionMethod.AES256;

    public string? KmsKeyId { get; init; }

    public int MaxMutationCount { get; init; } = 100_000;

    public int MaxParquetBytes { get; init; } = 64 * 1024 * 1024;

    internal Func<S3SyncOptions, IS3SyncObjectStore>? ObjectStoreFactory { get; init; }

    internal string ObjectPrefix => Prefix.Trim('/');

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Client);
        ArgumentException.ThrowIfNullOrWhiteSpace(BucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(Prefix);
        if (BucketName.Length is < 3 or > 63 ||
            BucketName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new ArgumentException("The S3 bucket name is invalid.", nameof(BucketName));
        }

        var prefix = ObjectPrefix;
        if (prefix.Length > 700 ||
            prefix.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
            prefix.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The S3 prefix must contain non-empty path segments and cannot contain '.' or '..'.",
                nameof(Prefix));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMutationCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxMutationCount, 1_000_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxParquetBytes, 1024);
        if (!string.IsNullOrWhiteSpace(KmsKeyId) &&
            ServerSideEncryption != ServerSideEncryptionMethod.AWSKMS)
        {
            throw new ArgumentException(
                "KmsKeyId requires ServerSideEncryptionMethod.AWSKMS.",
                nameof(KmsKeyId));
        }
    }
}

public class S3SyncException : Exception
{
    public S3SyncException(string message)
        : base(message)
    {
    }

    public S3SyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class S3SyncConfigurationException : S3SyncException
{
    public S3SyncConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class S3SyncDeliveryException : S3SyncException
{
    public S3SyncDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class S3SyncObjectConflictException : S3SyncException
{
    public S3SyncObjectConflictException(string message)
        : base(message)
    {
    }
}
