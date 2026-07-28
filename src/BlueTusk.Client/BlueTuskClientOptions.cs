using System.Data.Common;
using System.Globalization;
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

    /// <summary>Creates client options from a BlueTusk connection string.</summary>
    public static BlueTuskClientOptions FromConnectionString(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };

        return new BlueTuskClientOptions
        {
            Host = GetString(builder, "Host", "localhost"),
            Port = GetInt32(builder, "Port", 5432),
            Database = GetString(builder, "Database", string.Empty),
            Username = GetString(builder, "Username", string.Empty),
            Password = GetString(builder, "Password", string.Empty),
            ApplicationName = GetString(builder, "Application Name", "BlueTusk"),
            ConnectTimeout = TimeSpan.FromSeconds(GetInt32(builder, "Timeout", 15)),
            SslMode = GetEnum(builder, "SSL Mode", BlueTuskSslMode.VerifyFull),
            ChannelBinding = GetEnum(
                builder,
                "Channel Binding",
                BlueTuskChannelBindingMode.Prefer),
        };
    }

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

    private static string GetString(
        DbConnectionStringBuilder builder,
        string keyword,
        string defaultValue) =>
        builder.TryGetValue(keyword, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue
            : defaultValue;

    private static int GetInt32(
        DbConnectionStringBuilder builder,
        string keyword,
        int defaultValue) =>
        builder.TryGetValue(keyword, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : defaultValue;

    private static TEnum GetEnum<TEnum>(
        DbConnectionStringBuilder builder,
        string keyword,
        TEnum defaultValue)
        where TEnum : struct, Enum =>
        builder.TryGetValue(keyword, out var value)
            ? Enum.Parse<TEnum>(
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
                ignoreCase: true)
            : defaultValue;
}
