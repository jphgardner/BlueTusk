using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BlueTusk.Client;
using BlueTusk.Extensions;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Collects immutable provider configuration before a data source is created.</summary>
public sealed class BlueTuskDataSourceBuilder : IBlueTuskPluginContext
{
    private BlueTuskClientConfiguration _clientConfiguration = BlueTuskClientConfiguration.Empty;

    public BlueTuskDataSourceBuilder(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _ = new BlueTuskConnectionStringBuilder(connectionString);
    }

    internal string ConnectionString { get; }

    public BlueTuskTypeRegistryBuilder Types { get; } = new();

    public BlueTuskFeatureRegistryBuilder Features { get; } = new();

    public BlueTuskDataSourceBuilder UsePlugin(IBlueTuskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        plugin.Configure(this);
        return this;
    }

    public BlueTuskDataSourceBuilder MapEnum<TEnum>(
        string postgresTypeName,
        IReadOnlyDictionary<TEnum, string>? labels = null)
        where TEnum : struct, Enum
    {
        var typeName = BlueTuskTypeName.Parse(postgresTypeName);
        Types.Register(typeName.Schema, typeName.Name, new BlueTuskEnumCodec<TEnum>(labels));
        return this;
    }

    public BlueTuskDataSourceBuilder MapComposite<T>(string postgresTypeName)
    {
        var typeName = BlueTuskTypeName.Parse(postgresTypeName);
        Types.Register(typeName.Schema, typeName.Name, new BlueTuskCompositeCodec<T>());
        return this;
    }

    /// <summary>Uses a synchronous password callback for each new physical connection.</summary>
    public BlueTuskDataSourceBuilder UsePasswordProvider(BlueTuskPasswordProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _clientConfiguration = _clientConfiguration with { PasswordProvider = provider };
        return this;
    }

    /// <summary>Uses an asynchronous password callback for each new physical connection.</summary>
    public BlueTuskDataSourceBuilder UsePasswordProvider(BlueTuskPasswordProviderAsync provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _clientConfiguration = _clientConfiguration with { PasswordProviderAsync = provider };
        return this;
    }

    /// <summary>Uses a synchronous access-token callback for each new physical connection.</summary>
    public BlueTuskDataSourceBuilder UseAccessTokenProvider(BlueTuskAccessTokenProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _clientConfiguration = _clientConfiguration with { AccessTokenProvider = provider };
        return this;
    }

    /// <summary>Uses an asynchronous access-token callback for each new physical connection.</summary>
    public BlueTuskDataSourceBuilder UseAccessTokenProvider(BlueTuskAccessTokenProviderAsync provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _clientConfiguration = _clientConfiguration with { AccessTokenProviderAsync = provider };
        return this;
    }

    /// <summary>
    /// Requires TLS before an access-token callback may be invoked or its value sent to PostgreSQL.
    /// </summary>
    public BlueTuskDataSourceBuilder RequireTlsForAccessTokens()
    {
        _clientConfiguration = _clientConfiguration with { AccessTokenRequiresTls = true };
        return this;
    }

    /// <summary>
    /// Uses an explicit credential for PostgreSQL GSSAPI/Kerberos or SSPI authentication.
    /// Omit this configuration to use the process identity or platform credential cache.
    /// </summary>
    public BlueTuskDataSourceBuilder UseGssCredential(NetworkCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        _clientConfiguration = _clientConfiguration with { GssCredential = credential };
        return this;
    }

    /// <summary>Adds a caller-owned TLS client certificate offered to PostgreSQL.</summary>
    public BlueTuskDataSourceBuilder UseClientCertificate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _clientConfiguration = _clientConfiguration with
        {
            ClientCertificates = [.. _clientConfiguration.ClientCertificates, certificate],
        };
        return this;
    }

    /// <summary>Configures selection from the TLS client-certificate collection.</summary>
    public BlueTuskDataSourceBuilder UseClientCertificateSelectionCallback(
        LocalCertificateSelectionCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _clientConfiguration = _clientConfiguration with { LocalCertificateSelectionCallback = callback };
        return this;
    }

    /// <summary>Overrides platform server-certificate validation.</summary>
    public BlueTuskDataSourceBuilder UseRemoteCertificateValidationCallback(
        RemoteCertificateValidationCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _clientConfiguration = _clientConfiguration with { RemoteCertificateValidationCallback = callback };
        return this;
    }

    public BlueTuskDataSource Build()
    {
        _clientConfiguration.Validate();
        return new BlueTuskDataSource(
            ConnectionString,
            Types.Build(),
            Features.Build(),
            _clientConfiguration);
    }
}
