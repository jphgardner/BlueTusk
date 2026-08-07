using NATS.Client.JetStream;

namespace BlueTusk.Sync.Nats;

public sealed record NatsSyncOptions
{
    public required INatsJSContext JetStream { get; init; }

    public required string StreamName { get; init; }

    public required string SubjectPrefix { get; init; }

    public bool CreateStream { get; init; } = true;

    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    public long MaxBytes { get; init; } = 10L * 1024 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = 8 * 1024 * 1024;

    public TimeSpan DuplicateWindow { get; init; } = TimeSpan.FromHours(24);

    public int Replicas { get; init; } = 1;

    public int PublishRetryAttempts { get; init; } = 3;

    public TimeSpan PublishRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    internal string StreamSubject => SubjectPrefix + ".>";

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(JetStream);
        ValidateStreamName(StreamName);
        ValidateSubjectPrefix(SubjectPrefix);
        if (MaxAge < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAge));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxMessageBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((long)MaxMessageBytes, MaxBytes);
        if (DuplicateWindow < TimeSpan.FromSeconds(1) || DuplicateWindow > MaxAge)
        {
            throw new ArgumentOutOfRangeException(nameof(DuplicateWindow));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(Replicas, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Replicas, 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(PublishRetryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PublishRetryAttempts, 20);
        if (PublishRetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PublishRetryDelay));
        }
    }

    private static void ValidateStreamName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 255 || value.Any(character =>
                char.IsWhiteSpace(character) || character is '.' or '*' or '>'))
        {
            throw new ArgumentException(
                "The stream name must be at most 255 characters and cannot contain whitespace, '.', '*', or '>'.",
                nameof(value));
        }
    }

    private static void ValidateSubjectPrefix(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 240 ||
            value[0] == '.' ||
            value[^1] == '.' ||
            value.Contains("..", StringComparison.Ordinal) ||
            value.Any(character => char.IsWhiteSpace(character) || character is '*' or '>'))
        {
            throw new ArgumentException(
                "The subject prefix must contain non-empty literal tokens, be at most 240 characters, and cannot contain whitespace, '*' or '>'.",
                nameof(value));
        }
    }
}

public class NatsSyncException : Exception
{
    public NatsSyncException(string message)
        : base(message)
    {
    }

    public NatsSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class NatsSyncStreamConfigurationException : NatsSyncException
{
    public NatsSyncStreamConfigurationException(string message)
        : base(message)
    {
    }
}

public sealed class NatsSyncEnvelopeException : NatsSyncException
{
    public NatsSyncEnvelopeException(string message)
        : base(message)
    {
    }

    public NatsSyncEnvelopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
