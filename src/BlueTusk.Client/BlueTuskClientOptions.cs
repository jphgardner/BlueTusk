using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace BlueTusk.Client;

public enum BlueTuskSslMode
{
    Disable,
    Prefer,
    Require,
    VerifyFull,
}

public enum BlueTuskChannelBindingMode
{
    Disable,
    Prefer,
    Require,
}

public enum BlueTuskReplicationMode
{
    None,
    Physical,
    Database,
}

/// <summary>Connection settings for one physical PostgreSQL session.</summary>
public sealed record BlueTuskClientOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 5432;

    public required string Database { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }

    public string ApplicationName { get; init; } = "BlueTusk";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public BlueTuskSslMode SslMode { get; init; } = BlueTuskSslMode.VerifyFull;

    public BlueTuskChannelBindingMode ChannelBinding { get; init; } = BlueTuskChannelBindingMode.Prefer;

    public BlueTuskReplicationMode ReplicationMode { get; init; }

    public X509RevocationMode CertificateRevocationCheckMode { get; init; } = X509RevocationMode.Online;

    public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Port, 65_535);
        ArgumentException.ThrowIfNullOrWhiteSpace(Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(Username);
        ArgumentNullException.ThrowIfNull(Password);
        ArgumentNullException.ThrowIfNull(ApplicationName);

        if (ConnectTimeout <= TimeSpan.Zero && ConnectTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }

        if (ChannelBinding == BlueTuskChannelBindingMode.Require && SslMode == BlueTuskSslMode.Disable)
        {
            throw new ArgumentException("Required channel binding cannot be used when TLS is disabled.");
        }

        if (!Enum.IsDefined(ReplicationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(ReplicationMode));
        }
    }
}
