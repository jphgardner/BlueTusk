using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BlueTusk.Client;

namespace BlueTusk.Data;

/// <summary>Builds and validates BlueTusk PostgreSQL connection strings.</summary>
[SuppressMessage(
    "Design",
    "CA1010:Generic interface should also be implemented",
    Justification = "DbConnectionStringBuilder defines the required non-generic collection contract.")]
public sealed class BlueTuskConnectionStringBuilder : DbConnectionStringBuilder
{
    public BlueTuskConnectionStringBuilder()
    {
    }

    public BlueTuskConnectionStringBuilder(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public string Host
    {
        get => GetString(nameof(Host), "localhost");
        set => this[nameof(Host)] = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A host is required.", nameof(value))
            : value;
    }

    public int Port
    {
        get
        {
            var ports = GetString(nameof(Port), "5432");
            if (ports.Contains(','))
            {
                throw new InvalidOperationException(
                    "Port contains a multi-host port list. Use HostEndpoints to inspect individual ports.");
            }

            var value = Convert.ToInt32(ports, CultureInfo.InvariantCulture);
            return value is > 0 and <= 65_535
                ? value
                : throw new ArgumentOutOfRangeException(nameof(Port));
        }

        set => this[nameof(Port)] = value is > 0 and <= 65_535
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Gets or sets the raw shared or comma-separated PostgreSQL port list.</summary>
    public string Ports
    {
        get => GetString(nameof(Port), "5432");
        set => this[nameof(Port)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the parsed, positionally paired host endpoints.</summary>
    public IReadOnlyList<BlueTuskHostEndpoint> HostEndpoints => ParseHostEndpoints();

    public string Database
    {
        get => GetString(nameof(Database), string.Empty);
        set => this[nameof(Database)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Username
    {
        get => GetString(nameof(Username), string.Empty);
        set => this[nameof(Username)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    [PasswordPropertyText(true)]
    [AllowNull]
    public string? Password
    {
        get => TryGetValue(nameof(Password), out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
        set
        {
            if (value is null)
            {
                Remove(nameof(Password));
            }
            else
            {
                this[nameof(Password)] = value;
            }
        }
    }

    /// <summary>
    /// Gets or sets an explicit PostgreSQL password-file path. Null uses the platform default;
    /// an empty value disables password-file lookup.
    /// </summary>
    [AllowNull]
    public string? Passfile
    {
        get => TryGetValue(nameof(Passfile), out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;
        set
        {
            if (value is null)
            {
                Remove(nameof(Passfile));
            }
            else
            {
                this[nameof(Passfile)] = value;
            }
        }
    }

    public TimeSpan Timeout
    {
        get => GetPositiveTimeSpan(nameof(Timeout), 15);
        set
        {
            if (value <= TimeSpan.Zero || value.TotalSeconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            this[nameof(Timeout)] = checked((int)Math.Ceiling(value.TotalSeconds));
        }
    }

    public bool Pooling
    {
        get => GetBoolean(nameof(Pooling), true);
        set => this[nameof(Pooling)] = value;
    }

    public string ApplicationName
    {
        get => GetString("Application Name", "BlueTusk");
        set => this["Application Name"] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public BlueTuskSslMode SslMode
    {
        get => GetEnum("SSL Mode", BlueTuskSslMode.VerifyFull);
        set => this["SSL Mode"] = value.ToString();
    }

    public BlueTuskChannelBindingMode ChannelBinding
    {
        get => GetEnum("Channel Binding", BlueTuskChannelBindingMode.Prefer);
        set => this["Channel Binding"] = value.ToString();
    }

    /// <summary>Gets or sets the Kerberos service name used for PostgreSQL GSSAPI authentication.</summary>
    public string KerberosServiceName
    {
        get => GetString("Kerberos Service Name", "postgres");
        set => this["Kerberos Service Name"] = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A Kerberos service name is required.", nameof(value))
            : value;
    }

    /// <summary>
    /// Gets or sets whether cleartext password authentication may be used without TLS.
    /// </summary>
    public bool AllowUnencryptedPassword
    {
        get => GetBoolean("Allow Unencrypted Password", false);
        set => this["Allow Unencrypted Password"] = value;
    }

    public BlueTuskTargetSessionAttributes TargetSessionAttributes
    {
        get => GetEnum("Target Session Attributes", BlueTuskTargetSessionAttributes.Any);
        set => this["Target Session Attributes"] = value.ToString();
    }

    public BlueTuskLoadBalanceHosts LoadBalanceHosts
    {
        get => GetEnum("Load Balance Hosts", BlueTuskLoadBalanceHosts.Disable);
        set => this["Load Balance Hosts"] = value.ToString();
    }

    public int MinimumPoolSize
    {
        get
        {
            var value = GetInt32("Minimum Pool Size", 0);
            return value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(MinimumPoolSize));
        }

        set => this["Minimum Pool Size"] = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public int MaximumPoolSize
    {
        get
        {
            var value = GetInt32("Maximum Pool Size", 100);
            return value > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(MaximumPoolSize));
        }

        set => this["Maximum Pool Size"] = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public TimeSpan ConnectionIdleLifetime
    {
        get => GetNonNegativeTimeSpan("Connection Idle Lifetime", 300);
        set => SetNonNegativeTimeSpan("Connection Idle Lifetime", value);
    }

    public TimeSpan ConnectionLifetime
    {
        get => GetNonNegativeTimeSpan("Connection Lifetime", 3_600);
        set => SetNonNegativeTimeSpan("Connection Lifetime", value);
    }

    /// <summary>Gets or sets the maximum number of automatically prepared statements per physical connection.</summary>
    public int MaxAutoPrepare
    {
        get
        {
            var value = GetInt32("Max Auto Prepare", 0);
            return value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(MaxAutoPrepare));
        }

        set => this["Max Auto Prepare"] = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Gets or sets the executions required before a statement is automatically prepared.</summary>
    public int AutoPrepareMinUsages
    {
        get
        {
            var value = GetInt32("Auto Prepare Min Usages", 5);
            return value > 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(AutoPrepareMinUsages));
        }

        set => this["Auto Prepare Min Usages"] = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    internal void Validate()
    {
        _ = Host;
        _ = HostEndpoints;
        _ = Timeout;
        _ = Passfile;
        _ = SslMode;
        _ = ChannelBinding;
        _ = KerberosServiceName;
        _ = AllowUnencryptedPassword;
        _ = TargetSessionAttributes;
        _ = LoadBalanceHosts;
        _ = ConnectionIdleLifetime;
        _ = ConnectionLifetime;
        _ = MaxAutoPrepare;
        _ = AutoPrepareMinUsages;

        if (KerberosServiceName.IndexOfAny(['/', '@', '\0']) >= 0)
        {
            throw new ArgumentException(
                "A Kerberos service name cannot contain '/', '@', or a null character.",
                nameof(KerberosServiceName));
        }

        if (MinimumPoolSize > MaximumPoolSize)
        {
            throw new ArgumentException("Minimum Pool Size cannot exceed Maximum Pool Size.");
        }
    }

    private string GetString(string keyword, string defaultValue) =>
        TryGetValue(keyword, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? defaultValue
            : defaultValue;

    private int GetInt32(string keyword, int defaultValue) =>
        TryGetValue(keyword, out var value)
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : defaultValue;

    private bool GetBoolean(string keyword, bool defaultValue) =>
        TryGetValue(keyword, out var value)
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : defaultValue;

    private TimeSpan GetPositiveTimeSpan(string keyword, int defaultSeconds)
    {
        var seconds = GetInt32(keyword, defaultSeconds);
        return seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new ArgumentOutOfRangeException(keyword);
    }

    private TimeSpan GetNonNegativeTimeSpan(string keyword, int defaultSeconds)
    {
        var seconds = GetInt32(keyword, defaultSeconds);
        return seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : throw new ArgumentOutOfRangeException(keyword);
    }

    private void SetNonNegativeTimeSpan(string keyword, TimeSpan value)
    {
        if (value < TimeSpan.Zero || value.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        this[keyword] = checked((int)Math.Ceiling(value.TotalSeconds));
    }

    private BlueTuskHostEndpoint[] ParseHostEndpoints()
    {
        var hosts = Host.Split(',', StringSplitOptions.TrimEntries);
        if (hosts.Length == 0 || hosts.Any(static host => host.Length == 0))
        {
            throw new ArgumentException("Host must contain one or more non-empty host names.");
        }

        var portItems = Ports.Split(',', StringSplitOptions.TrimEntries);
        if (portItems.Length != 1 && portItems.Length != hosts.Length)
        {
            throw new ArgumentException(
                "Port must contain one shared value or one value for every host.");
        }

        var endpoints = new BlueTuskHostEndpoint[hosts.Length];
        for (var index = 0; index < hosts.Length; index++)
        {
            var portText = portItems.Length == 1 ? portItems[0] : portItems[index];
            var port = portText.Length == 0
                ? 5432
                : Convert.ToInt32(portText, CultureInfo.InvariantCulture);
            if (port is < 1 or > 65_535)
            {
                throw new ArgumentOutOfRangeException(nameof(Port));
            }

            endpoints[index] = new BlueTuskHostEndpoint(hosts[index], port);
        }

        return endpoints;
    }

    private TEnum GetEnum<TEnum>(string keyword, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (!TryGetValue(keyword, out var value))
        {
            return defaultValue;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        var normalized = text
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed)
                ? parsed
                : throw new ArgumentException($"'{text}' is not a valid value for {keyword}.");
    }
}
