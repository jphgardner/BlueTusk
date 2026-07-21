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
        get => GetInt32(nameof(Port), 5432);
        set => this[nameof(Port)] = value is > 0 and <= 65_535
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

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
    public string Password
    {
        get => GetString(nameof(Password), string.Empty);
        set => this[nameof(Password)] = value ?? throw new ArgumentNullException(nameof(value));
    }

    public TimeSpan Timeout
    {
        get => TimeSpan.FromSeconds(GetInt32(nameof(Timeout), 15));
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

    public int MinimumPoolSize
    {
        get => GetInt32("Minimum Pool Size", 0);
        set => this["Minimum Pool Size"] = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public int MaximumPoolSize
    {
        get => GetInt32("Maximum Pool Size", 100);
        set => this["Maximum Pool Size"] = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
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

    private TEnum GetEnum<TEnum>(string keyword, TEnum defaultValue)
        where TEnum : struct, Enum =>
        TryGetValue(keyword, out var value)
            ? Enum.Parse<TEnum>(Convert.ToString(value, CultureInfo.InvariantCulture)!, ignoreCase: true)
            : defaultValue;
}
