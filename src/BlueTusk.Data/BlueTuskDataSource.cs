using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;
using BlueTusk.Data.Internal;
using BlueTusk.Diagnostics;
using BlueTusk.Extensions;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Creates BlueTusk connections and owns their physical connection pool.</summary>
public sealed class BlueTuskDataSource : DbDataSource, IProviderDataSource
{
    private readonly BlueTuskConnectionStringBuilder _settings;
    private readonly BlueTuskConnectionPoolBase? _pool;
    private readonly BlueTuskTypeMetadataCache _typeMetadata;
    private readonly BlueTuskClientConfiguration _clientConfiguration;
    private readonly string _connectionString;
    private readonly BlueTuskCommandMultiplexer? _multiplexer;

    internal BlueTuskDataSource(
        string connectionString,
        BlueTuskTypeRegistry? configuredTypes = null,
        BlueTuskFeatureRegistry? features = null,
        BlueTuskClientConfiguration? clientConfiguration = null,
        BlueTuskMultiplexingOptions? multiplexing = null)
    {
        _settings = new BlueTuskConnectionStringBuilder(connectionString);
        _settings.Validate();
        _connectionString = connectionString;
        ConnectionString = _settings.GetPublicConnectionString();
        Features = features ?? BlueTuskFeatureRegistry.Empty;
        _clientConfiguration = clientConfiguration ?? BlueTuskClientConfiguration.Empty;
        _clientConfiguration.Validate();
        _typeMetadata = new BlueTuskTypeMetadataCache(configuredTypes);
        if (_settings.Pooling)
        {
            _pool = _settings.HostEndpoints.Count > 1
                ? new BlueTuskMultiHostConnectionPool(_settings, _clientConfiguration)
                : new BlueTuskConnectionPool(_settings, clientConfiguration: _clientConfiguration);
        }

        if (_settings.Multiplexing || multiplexing is not null)
        {
            if (_pool is null)
            {
                throw new ArgumentException("Multiplexing requires connection pooling.", nameof(connectionString));
            }

            var resolved = (multiplexing ?? new BlueTuskMultiplexingOptions())
                .Resolve(_settings.MaximumPoolSize);
            _multiplexer = new BlueTuskCommandMultiplexer(this, resolved);
        }
    }

    public override string ConnectionString { get; }

    public static BlueTuskDataSource Create(string connectionString) => new(connectionString);

    public BlueTuskTypeRegistry TypeRegistry => _typeMetadata.Registry;

    internal BlueTuskDiagnosticsOptions DiagnosticsOptions => _clientConfiguration.Diagnostics;

    internal BlueTuskCommandMultiplexer? Multiplexer => _multiplexer;

    internal bool HasPoolWaiters => _pool?.Statistics.Waiting > 0;

    /// <summary>Gets whether bounded statement multiplexing is enabled for this data source.</summary>
    public bool IsMultiplexingEnabled => _multiplexer is not null;

    /// <summary>Gets the immutable optional-feature snapshot configured for this data source.</summary>
    public BlueTuskFeatureRegistry Features { get; }

    /// <summary>
    /// Creates options for a dedicated, unpooled session when exactly one host is configured.
    /// </summary>
    /// <remarks>
    /// The returned snapshot includes credentials and transport security settings. It does not
    /// lease a connection from this data source's pool and is suitable for APIs such as
    /// <c>BlueTusk.Replication</c> that own a physical session for their full lifetime.
    /// </remarks>
    public BlueTuskClientOptions CreateDedicatedSessionOptions()
    {
        var endpoints = _settings.HostEndpoints;
        if (endpoints.Count != 1)
        {
            throw new InvalidOperationException(
                "A multi-host data source requires an explicit endpoint for a dedicated session.");
        }

        return CreateDedicatedSessionOptions(endpoints[0]);
    }

    /// <summary>Creates options for a dedicated, unpooled session on one configured host.</summary>
    /// <param name="endpoint">A host endpoint present in this data source's configuration.</param>
    /// <remarks>
    /// Host selection is explicit for multi-host sources because replication resume and failover
    /// policy belong to the caller. Creating the snapshot does not open or borrow a pooled session.
    /// </remarks>
    public BlueTuskClientOptions CreateDedicatedSessionOptions(BlueTuskHostEndpoint endpoint)
    {
        if (!_settings.HostEndpoints.Contains(endpoint))
        {
            throw new ArgumentException(
                "The dedicated-session endpoint must be configured on this data source.",
                nameof(endpoint));
        }

        return _clientConfiguration.Apply(new BlueTuskClientOptions
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            Database = _settings.Database,
            Username = _settings.Username,
            Password = _settings.Password,
            Passfile = _settings.Passfile,
            KerberosServiceName = _settings.KerberosServiceName,
            ApplicationName = _settings.ApplicationName,
            ConnectTimeout = _settings.Timeout,
            SslMode = _settings.SslMode,
            ChannelBinding = _settings.ChannelBinding,
            AllowUnencryptedPassword = _settings.AllowUnencryptedPassword,
        });
    }

    public new BlueTuskConnection CreateConnection() => CreateBlueTuskConnection();

    internal BlueTuskConnection CreateUnpooledConnection(string connectionString) =>
        new(
            connectionString ?? throw new ArgumentNullException(nameof(connectionString)),
            pool: null,
            new BlueTuskTypeMetadataCache(),
            _clientConfiguration,
            hideSensitiveConnectionString: true);

    public new BlueTuskConnection OpenConnection() => (BlueTuskConnection)base.OpenConnection();

    public new ValueTask<BlueTuskConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            var opening = connection.OpenAsync(cancellationToken);
            return opening.IsCompletedSuccessfully
                ? new ValueTask<BlueTuskConnection>(connection)
                : CompleteOpenConnectionAsync(connection, opening);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal ValueTask<BlueTuskConnection> OpenCommandConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = CreateConnection();
        try
        {
            var opening = connection.OpenForCommandAsync(
                allowPendingReset: _typeMetadata.IsLoaded,
                cancellationToken);
            return opening.IsCompletedSuccessfully
                ? new ValueTask<BlueTuskConnection>(connection)
                : CompleteOpenConnectionAsync(connection, opening);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public new BlueTuskCommand CreateCommand(string commandText) => (BlueTuskCommand)base.CreateCommand(commandText);

    public new BlueTuskBatch CreateBatch() => (BlueTuskBatch)base.CreateBatch();

    public BlueTuskPoolStatistics GetPoolStatistics() =>
        _pool?.Statistics ?? new BlueTuskPoolStatistics(
            PoolingEnabled: false,
            MinimumSize: 0,
            MaximumSize: 0,
            Total: 0,
            Idle: 0,
            Busy: 0,
            Waiting: 0,
            Opened: 0,
            Reused: 0,
            Discarded: 0);

    /// <summary>Gets pool statistics partitioned by configured host endpoint.</summary>
    public IReadOnlyDictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics> GetHostPoolStatistics() =>
        _pool?.HostStatistics ?? new Dictionary<BlueTuskHostEndpoint, BlueTuskPoolStatistics>();

    /// <summary>Gets a point-in-time snapshot of the statement multiplexing scheduler.</summary>
    public BlueTuskMultiplexingStatistics GetMultiplexingStatistics() =>
        _multiplexer?.Statistics ?? new BlueTuskMultiplexingStatistics(
            Enabled: false,
            Workers: 0,
            Queued: 0,
            Executing: 0,
            Accepted: 0,
            Completed: 0,
            Canceled: 0,
            Faulted: 0,
            PipelineFlushes: 0,
            PipelinedCommands: 0);

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        _pool?.WarmUpAsync(cancellationToken) ?? ValueTask.CompletedTask;

    public void WarmUp() => _pool?.WarmUp();

    public void ReloadTypes()
    {
        using var connection = OpenConnection();
        _typeMetadata.Reload(connection.Session);
    }

    public async ValueTask ReloadTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await _typeMetadata.ReloadAsync(connection.Session, cancellationToken).ConfigureAwait(false);
    }

    public void ClearPool() => _pool?.Clear();

    public ValueTask ClearPoolAsync() => _pool?.ClearAsync() ?? ValueTask.CompletedTask;

    DbDataSource IProviderDataSource.Instance => this;

    string IProviderDataSource.UnredactedConnectionString => _connectionString;

    BlueTuskTypeRegistry IProviderDataSource.TypeRegistry => TypeRegistry;

    BlueTuskDiagnosticsOptions IProviderDataSource.Diagnostics => DiagnosticsOptions;

    DbConnection IProviderDataSource.CreateConnection() => CreateConnection();

    DbConnection IProviderDataSource.CreateAdminConnection(string connectionString) =>
        CreateUnpooledConnection(connectionString);

    void IProviderDataSource.ClearPool() => ClearPool();

    ValueTask IProviderDataSource.ClearPoolAsync() => ClearPoolAsync();

    protected override DbConnection CreateDbConnection() => CreateBlueTuskConnection();

    private BlueTuskConnection CreateBlueTuskConnection() =>
        new BlueTuskConnection(
            _connectionString,
            _pool,
            _typeMetadata,
            _clientConfiguration,
            hideSensitiveConnectionString: true,
            sharedSettings: _settings);

    internal ValueTask<BlueTuskConnection> OpenMultiplexingConnectionAsync(
        CancellationToken cancellationToken) =>
        OpenConnectionAsync(cancellationToken);

    protected override DbConnection OpenDbConnection()
    {
        var connection = CreateConnection();
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    protected override async ValueTask<DbConnection> OpenDbConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<BlueTuskConnection> CompleteOpenConnectionAsync(
        BlueTuskConnection connection,
        Task opening)
    {
        try
        {
            await opening.ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override DbCommand CreateDbCommand([AllowNull] string commandText) =>
        new BlueTuskCommand(commandText ?? string.Empty, this);

    protected override DbBatch CreateDbBatch() => new BlueTuskBatch(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _multiplexer?.Dispose();
            _pool?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync().ConfigureAwait(false);
        }

        if (_pool is not null)
        {
            await _pool.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsyncCore().ConfigureAwait(false);
    }
}
