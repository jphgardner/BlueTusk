using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
        var settings = ParseConnectionString(connectionString);

        return new BlueTuskClientOptions
        {
            Host = GetString(settings, "Host", "localhost"),
            Port = GetInt32(settings, "Port", 5432),
            Database = GetString(settings, "Database", string.Empty),
            Username = GetString(settings, "Username", string.Empty),
            Password = GetString(settings, "Password", string.Empty),
            ApplicationName = GetString(settings, "Application Name", "BlueTusk"),
            ConnectTimeout = TimeSpan.FromSeconds(GetInt32(settings, "Timeout", 15)),
            SslMode = GetEnum(settings, "SSL Mode", BlueTuskSslMode.VerifyFull),
            ChannelBinding = GetEnum(
                settings,
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
        Dictionary<string, string> settings,
        string keyword,
        string defaultValue) =>
        settings.TryGetValue(keyword, out var value)
            ? value
            : defaultValue;

    private static int GetInt32(
        Dictionary<string, string> settings,
        string keyword,
        int defaultValue) =>
        settings.TryGetValue(keyword, out var value)
            ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : defaultValue;

    private static TEnum GetEnum<TEnum>(
        Dictionary<string, string> settings,
        string keyword,
        TEnum defaultValue)
        where TEnum : struct, Enum =>
        settings.TryGetValue(keyword, out var value)
            ? Enum.Parse<TEnum>(value, ignoreCase: true)
            : defaultValue;

    private static Dictionary<string, string> ParseConnectionString(string connectionString)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < connectionString.Length)
        {
            SkipSeparatorsAndWhitespace(connectionString, ref index);
            if (index == connectionString.Length)
            {
                break;
            }

            var keyStart = index;
            while (index < connectionString.Length && connectionString[index] is not ('=' or ';'))
            {
                index++;
            }

            if (index == connectionString.Length || connectionString[index] != '=')
            {
                throw new ArgumentException("The connection string contains a keyword without a value.", nameof(connectionString));
            }

            var key = connectionString[keyStart..index].Trim();
            if (key.Length == 0)
            {
                throw new ArgumentException("The connection string contains an empty keyword.", nameof(connectionString));
            }

            index++;
            while (index < connectionString.Length && char.IsWhiteSpace(connectionString[index]))
            {
                index++;
            }

            settings[key] = index < connectionString.Length && connectionString[index] is ('\'' or '"')
                ? ReadQuotedValue(connectionString, ref index)
                : ReadUnquotedValue(connectionString, ref index);
        }

        return settings;
    }

    private static string ReadQuotedValue(string connectionString, ref int index)
    {
        var quote = connectionString[index++];
        var value = new StringBuilder();
        var closed = false;
        while (index < connectionString.Length)
        {
            var character = connectionString[index++];
            if (character != quote)
            {
                value.Append(character);
                continue;
            }

            if (index < connectionString.Length && connectionString[index] == quote)
            {
                value.Append(quote);
                index++;
                continue;
            }

            closed = true;
            break;
        }

        if (!closed)
        {
            throw new ArgumentException("The connection string contains an unterminated quoted value.", nameof(connectionString));
        }

        while (index < connectionString.Length && char.IsWhiteSpace(connectionString[index]))
        {
            index++;
        }

        if (index < connectionString.Length && connectionString[index] != ';')
        {
            throw new ArgumentException("The connection string contains characters after a quoted value.", nameof(connectionString));
        }

        if (index < connectionString.Length)
        {
            index++;
        }

        return value.ToString();
    }

    private static string ReadUnquotedValue(string connectionString, ref int index)
    {
        var valueStart = index;
        while (index < connectionString.Length && connectionString[index] != ';')
        {
            index++;
        }

        var value = connectionString[valueStart..index].Trim();
        if (index < connectionString.Length)
        {
            index++;
        }

        return value;
    }

    private static void SkipSeparatorsAndWhitespace(string connectionString, ref int index)
    {
        while (index < connectionString.Length
               && (connectionString[index] == ';' || char.IsWhiteSpace(connectionString[index])))
        {
            index++;
        }
    }
}
