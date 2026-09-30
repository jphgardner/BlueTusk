namespace BlueTusk.Sync.Webhooks;

public sealed record WebhookSyncOptions
{
    public required HttpClient Client { get; init; }

    public required Uri Endpoint { get; init; }

    public required string KeyId { get; init; }

    public required ReadOnlyMemory<byte> SigningKey { get; init; }

    public bool AllowInsecureHttp { get; init; }

    public int MaxEnvelopeBytes { get; init; } = 8 * 1024 * 1024;

    public int MaximumAttempts { get; init; } = 5;

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Client);
        ArgumentNullException.ThrowIfNull(Endpoint);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(KeyId);
        if (!Endpoint.IsAbsoluteUri ||
            (!string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(AllowInsecureHttp && string.Equals(
                 Endpoint.Scheme,
                 Uri.UriSchemeHttp,
                 StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException(
                "The webhook endpoint must be an absolute HTTPS URI unless insecure HTTP is explicitly enabled for local tests.",
                nameof(Endpoint));
        }

        if (!string.IsNullOrEmpty(Endpoint.UserInfo) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException(
                "The webhook endpoint cannot contain user information or a fragment.",
                nameof(Endpoint));
        }

        if (KeyId.Length > 128 || KeyId.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The signing key identifier must be at most 128 characters and cannot contain control characters.",
                nameof(KeyId));
        }

        if (SigningKey.Length < 32)
        {
            throw new ArgumentException(
                "The webhook HMAC signing key must contain at least 32 bytes.",
                nameof(SigningKey));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEnvelopeBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEnvelopeBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumAttempts, 10);
        if (InitialRetryDelay < TimeSpan.Zero || InitialRetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));
        }

        if (MaximumRetryDelay < InitialRetryDelay || MaximumRetryDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay));
        }
    }
}

public class WebhookSyncException : Exception
{
    public WebhookSyncException(string message)
        : base(message)
    {
    }

    public WebhookSyncException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WebhookSyncProtocolException : WebhookSyncException
{
    public WebhookSyncProtocolException(string message)
        : base(message)
    {
    }
}

public sealed class WebhookSyncDeliveryException : WebhookSyncException
{
    public WebhookSyncDeliveryException(string message)
        : base(message)
    {
    }

    public WebhookSyncDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
