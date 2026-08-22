using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using BlueTusk.Client;
using BlueTusk.Data.Copy;
using BlueTusk.Data.Internal;
using BlueTusk.Data.LargeObjects;
using BlueTusk.Data.Notifications;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Represents a logical connection to PostgreSQL.</summary>
public sealed class BlueTuskConnection : DbConnection, IProviderConnection
{
    private const int NotificationBufferCapacity = 1_024;
    private readonly BlueTuskConnectionPoolBase? _pool;
    private readonly BlueTuskTypeMetadataCache _typeMetadata;
    private readonly BlueTuskClientConfiguration _clientConfiguration;
    private OptionalState? _optionalState;
    private BlueTuskConnectionStringBuilder _settings = null!;
    private object? _sessionLease;
    private bool _hideSensitiveConnectionString;
    private bool _sessionTouched;
    private bool _disposed;
    private ConnectionState _state = ConnectionState.Closed;

    public BlueTuskConnection()
    {
        _typeMetadata = new BlueTuskTypeMetadataCache();
        _clientConfiguration = BlueTuskClientConfiguration.Empty;
        _settings = new BlueTuskConnectionStringBuilder();
    }

    public BlueTuskConnection(string connectionString)
        : this(connectionString, pool: null, new BlueTuskTypeMetadataCache())
    {
    }

    internal BlueTuskConnection(string connectionString, BlueTuskConnectionPoolBase? pool)
        : this(connectionString, pool, new BlueTuskTypeMetadataCache())
    {
    }

    internal BlueTuskConnection(
        string connectionString,
        BlueTuskConnectionPoolBase? pool,
        BlueTuskTypeMetadataCache typeMetadata,
        BlueTuskClientConfiguration? clientConfiguration = null,
        bool hideSensitiveConnectionString = false,
        BlueTuskConnectionStringBuilder? sharedSettings = null)
    {
        _pool = pool;
        _typeMetadata = typeMetadata ?? throw new ArgumentNullException(nameof(typeMetadata));
        _clientConfiguration = clientConfiguration ?? BlueTuskClientConfiguration.Empty;
        if (sharedSettings is null)
        {
            ConnectionString = connectionString;
        }
        else
        {
            _settings = sharedSettings;
        }

        _hideSensitiveConnectionString = hideSensitiveConnectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _hideSensitiveConnectionString
            ? _settings.GetPublicConnectionString()
            : _settings.ConnectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("The connection string cannot change while the connection is open.");
            }

            if (_pool is not null &&
                _settings is not null &&
                _settings.ConnectionString.Length > 0 &&
                !string.Equals(
                    _settings.ConnectionString,
                    new BlueTuskConnectionStringBuilder(value ?? string.Empty).ConnectionString,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A connection created by a data source cannot use a different connection string.");
            }

            _settings = new BlueTuskConnectionStringBuilder(value ?? string.Empty);
            _settings.Validate();
            _hideSensitiveConnectionString = false;
        }
    }

    public override string Database => _settings.Database;

    public override string DataSource => ConnectedEndpoint?.Host ?? _settings.Host;

    /// <summary>Gets the host and port selected for the current physical connection.</summary>
    public BlueTuskHostEndpoint? ConnectedEndpoint => PhysicalSession?.Endpoint;

    public override string ServerVersion =>
        PhysicalSession?.Parameters.TryGetValue("server_version", out var version) == true
            ? version
            : string.Empty;

    /// <summary>Gets the capabilities detected for the currently open physical session.</summary>
    public BlueTuskServerCapabilities? ServerCapabilities => PhysicalSession?.Capabilities;

    /// <summary>
    /// Gets whether the current physical session supports SQL/PGQ, or <see langword="null"/>
    /// while no physical session is open.
    /// </summary>
    public bool? SupportsSqlPgq => PhysicalSession?.Capabilities.SupportsSqlPgq;

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => checked((int)_settings.Timeout.TotalSeconds);

    public override bool CanCreateBatch => true;

    protected override DbProviderFactory DbProviderFactory => BlueTuskProviderFactory.Instance;

    public override DataTable GetSchema() =>
        BlueTuskSchemaCollections.Get(this, "MetaDataCollections", restrictions: null);

    public override DataTable GetSchema(string collectionName) =>
        BlueTuskSchemaCollections.Get(this, collectionName, restrictions: null);

    public override DataTable GetSchema(string collectionName, string?[]? restrictionValues) =>
        BlueTuskSchemaCollections.Get(this, collectionName, restrictionValues);

    [SuppressMessage(
        "ApiDesign",
        "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The optional cancellation token is inherited from DbConnection.")]
    public override Task<DataTable> GetSchemaAsync(
        CancellationToken cancellationToken = default) =>
        BlueTuskSchemaCollections.GetAsync(
            this,
            "MetaDataCollections",
            restrictions: null,
            cancellationToken);

    [SuppressMessage(
        "ApiDesign",
        "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The optional cancellation token is inherited from DbConnection.")]
    public override Task<DataTable> GetSchemaAsync(
        string collectionName,
        CancellationToken cancellationToken = default) =>
        BlueTuskSchemaCollections.GetAsync(
            this,
            collectionName,
            restrictions: null,
            cancellationToken);

    [SuppressMessage(
        "ApiDesign",
        "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The optional cancellation token is inherited from DbConnection.")]
    public override Task<DataTable> GetSchemaAsync(
        string collectionName,
        string?[]? restrictionValues,
        CancellationToken cancellationToken = default) =>
        BlueTuskSchemaCollections.GetAsync(
            this,
            collectionName,
            restrictionValues,
            cancellationToken);

    /// <summary>Gets the asynchronous stream of notifications received by this connection.</summary>
    public IAsyncEnumerable<BlueTuskNotification> Notifications
    {
        get
        {
            var notifications = GetNotificationState();
            lock (notifications.Sync)
            {
                return notifications.GetOrCreateChannel().Reader.ReadAllAsync();
            }
        }
    }

    /// <summary>Blocks until the next subscribed PostgreSQL notification is available.</summary>
    public BlueTuskNotification WaitForNotification()
    {
        EnsureNotificationsAvailable();
        var notifications = GetNotificationState();
        var channel = notifications.GetOrCreateChannel();
        while (true)
        {
            lock (notifications.Sync)
            {
                if (channel.Reader.TryRead(out var notification))
                {
                    return notification;
                }

                if (notifications.ChannelCompleted)
                {
                    throw new EndOfStreamException("The PostgreSQL notification stream has completed.");
                }
            }

            notifications.Available.WaitOne();
        }
    }

    internal IBlueTuskPhysicalSession Session
    {
        get
        {
            _sessionTouched = true;
            return PhysicalSession ?? throw new InvalidOperationException("The connection is not open.");
        }
    }

    internal bool HasOpenSession => PhysicalSession is { IsOpen: true };

    internal bool HasPendingPoolReset =>
        _sessionLease is BlueTuskPooledSession { RequiresReset: true };

    internal ValueTask<BlueTuskScalarQueryResult> ExecuteResetAndExtendedScalarAsync(
        string sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> parameters,
        bool useBinaryResults,
        CancellationToken cancellationToken)
    {
        if (_sessionLease is not BlueTuskPooledSession { RequiresReset: true } pooledSession)
        {
            return Session.ExecuteExtendedScalarAsync(
                sql,
                parameters,
                useBinaryResults,
                cancellationToken);
        }

        _sessionTouched = true;
        return pooledSession.Session.ExecuteResetAndExtendedScalarAsync(
            sql,
            parameters,
            useBinaryResults,
            pooledSession.ResetCompletedCallback,
            cancellationToken);
    }

    internal void AbortPhysicalSession()
    {
        var lease = Interlocked.Exchange(ref _sessionLease, null);
        try
        {
            if (lease is BlueTuskPooledSession pooledSession)
            {
                try
                {
                    pooledSession.Session.Dispose();
                }
                finally
                {
                    _pool!.Return(pooledSession);
                }
            }
            else if (lease is IBlueTuskPhysicalSession session)
            {
                session.Dispose();
            }
        }
        finally
        {
            _sessionTouched = false;
            SetState(ConnectionState.Closed);
        }
    }

    private IBlueTuskPhysicalSession? PhysicalSession => _sessionLease switch
    {
        BlueTuskPooledSession pooledSession => pooledSession.Session,
        IBlueTuskPhysicalSession session => session,
        _ => null,
    };

    internal BlueTuskResolvedField[] ResolveRowFields(
        IReadOnlyList<BlueTuskFieldDescription> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var types = TypeRegistry;
        var optional = Volatile.Read(ref _optionalState) ?? Optional;
        if (ReferenceEquals(optional.ResolvedRowDescription, fields) &&
            ReferenceEquals(optional.ResolvedRowRegistry, types) &&
            optional.ResolvedRowFields is { } cached)
        {
            return cached;
        }

        var resolved = new BlueTuskResolvedField[fields.Count];
        for (var index = 0; index < resolved.Length; index++)
        {
            resolved[index] = BlueTuskValueDecoder.Resolve(types, fields[index]);
        }

        optional.ResolvedRowDescription = fields;
        optional.ResolvedRowRegistry = types;
        optional.ResolvedRowFields = resolved;
        return resolved;
    }

    private SemaphoreSlim LargeObjectGate =>
        LazyInitializer.EnsureInitialized(
            ref Optional.LargeObjectGate,
            static () => new SemaphoreSlim(1, 1));

    private OptionalState Optional =>
        LazyInitializer.EnsureInitialized(
            ref _optionalState,
            static () => new OptionalState());

    private OptionalState Transactions => Optional;

    private NotificationState? ReadNotificationState()
    {
        var optional = Volatile.Read(ref _optionalState);
        return optional is null
            ? null
            : Volatile.Read(ref optional.Notifications);
    }

    private BlueTuskTransaction? ReadImplicitLargeObjectTransaction()
    {
        var transactions = Volatile.Read(ref _optionalState);
        return transactions is null
            ? null
            : Volatile.Read(ref transactions.ImplicitLargeObject);
    }

    private void SetImplicitLargeObjectTransaction(BlueTuskTransaction transaction) =>
        Volatile.Write(ref Transactions.ImplicitLargeObject, transaction);

    private void ClearImplicitLargeObjectTransaction()
    {
        var transactions = Volatile.Read(ref _optionalState);
        if (transactions is not null)
        {
            Interlocked.Exchange(ref transactions.ImplicitLargeObject, null);
        }
    }

    private void ClearImplicitLargeObjectTransaction(BlueTuskTransaction transaction)
    {
        var transactions = Volatile.Read(ref _optionalState);
        if (transactions is not null)
        {
            _ = Interlocked.CompareExchange(
                ref transactions.ImplicitLargeObject,
                null,
                transaction);
        }
    }

    internal BlueTuskDiagnosticsOptions DiagnosticsOptions => _clientConfiguration.Diagnostics;

    internal string UnredactedConnectionString => _settings.ConnectionString;

    internal BlueTuskHostEndpoint DiagnosticEndpoint =>
        ConnectedEndpoint ?? _settings.HostEndpoints[0];

    internal BlueTuskConnection CreateUnpooledConnection(string connectionString) =>
        new(
            connectionString ?? throw new ArgumentNullException(nameof(connectionString)),
            pool: null,
            new BlueTuskTypeMetadataCache(),
            _clientConfiguration,
            hideSensitiveConnectionString: true);

    /// <summary>Gets the current immutable PostgreSQL type-registry snapshot.</summary>
    public BlueTuskTypeRegistry TypeRegistry => _typeMetadata.Registry;

    /// <summary>Reloads PostgreSQL type metadata for this open connection.</summary>
    public void ReloadTypes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _typeMetadata.Reload(Session);
    }

    /// <summary>Reloads PostgreSQL type metadata for this open connection.</summary>
    public ValueTask ReloadTypesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _typeMetadata.ReloadAsync(Session, cancellationToken);
    }

    DbConnection IProviderConnection.Instance => this;

    string IProviderConnection.UnredactedConnectionString => UnredactedConnectionString;

    BlueTuskTypeRegistry IProviderConnection.TypeRegistry => TypeRegistry;

    ProviderCapabilities? IProviderConnection.Capabilities =>
        ServerCapabilities is { } capabilities
            ? new ProviderCapabilities(capabilities.SupportsSqlPgq)
            : null;

    BlueTuskDiagnosticsOptions IProviderConnection.Diagnostics => DiagnosticsOptions;

    DbConnection IProviderConnection.CreateAdminConnection(string connectionString) =>
        CreateUnpooledConnection(connectionString);

    void IProviderConnection.ReloadTypes() => ReloadTypes();

    ValueTask IProviderConnection.ReloadTypesAsync(CancellationToken cancellationToken) =>
        ReloadTypesAsync(cancellationToken);

    internal BlueTuskTransaction? CurrentTransaction
    {
        get
        {
            var transactions = Volatile.Read(ref _optionalState);
            if (transactions is null)
            {
                return null;
            }

            lock (transactions.Sync)
            {
                return transactions.Current;
            }
        }
    }

    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        SetState(ConnectionState.Connecting);
        try
        {
            if (_pool is null)
            {
                _sessionLease = BlueTuskPhysicalSession.Open(_settings, _clientConfiguration);
            }
            else
            {
                _sessionLease = _pool.Rent();
            }

            _typeMetadata.EnsureLoaded(PhysicalSession!);
            _sessionTouched = false;
            BeginNotificationLifetime();
            _hideSensitiveConnectionString = true;
            SetState(ConnectionState.Open);
        }
        catch
        {
            var lease = Interlocked.Exchange(ref _sessionLease, null);
            if (lease is BlueTuskPooledSession pooledSession)
            {
                _pool!.Return(pooledSession);
            }
            else if (lease is IBlueTuskPhysicalSession session)
            {
                session.Dispose();
            }
            SetState(ConnectionState.Closed);
            throw;
        }
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        SetState(ConnectionState.Connecting);
        try
        {
            if (_pool is null)
            {
                return OpenUnpooledAsync(cancellationToken);
            }

            var renting = _pool.RentAsync(cancellationToken);
            if (!renting.IsCompletedSuccessfully)
            {
                return CompletePooledOpenAsync(renting, cancellationToken);
            }

            _sessionLease = renting.Result;
            var loadingTypes = _typeMetadata.EnsureLoadedAsync(PhysicalSession!, cancellationToken);
            if (!loadingTypes.IsCompletedSuccessfully)
            {
                return CompleteOpenAsync(loadingTypes);
            }

            CompleteOpen();
            return Task.CompletedTask;
        }
        catch
        {
            CleanupFailedOpen();
            throw;
        }
    }

    internal Task OpenForCommandAsync(
        bool allowPendingReset,
        CancellationToken cancellationToken)
    {
        if (!allowPendingReset || _pool is not BlueTuskConnectionPool singleHostPool)
        {
            return OpenAsync(cancellationToken);
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ConnectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        SetState(ConnectionState.Connecting);
        try
        {
            var renting = singleHostPool.RentForCommandAsync(cancellationToken);
            if (!renting.IsCompletedSuccessfully)
            {
                return CompletePooledOpenAsync(renting, cancellationToken);
            }

            _sessionLease = renting.Result;
            var loadingTypes = _typeMetadata.EnsureLoadedAsync(PhysicalSession!, cancellationToken);
            if (!loadingTypes.IsCompletedSuccessfully)
            {
                return CompleteOpenAsync(loadingTypes);
            }

            CompleteOpen();
            return Task.CompletedTask;
        }
        catch
        {
            CleanupFailedOpen();
            throw;
        }
    }

    private async Task OpenUnpooledAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessionLease = await BlueTuskPhysicalSession.OpenAsync(
                _settings,
                _clientConfiguration,
                cancellationToken).ConfigureAwait(false);
            await _typeMetadata.EnsureLoadedAsync(
                PhysicalSession!,
                cancellationToken).ConfigureAwait(false);
            CompleteOpen();
        }
        catch
        {
            await CleanupFailedOpenAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompletePooledOpenAsync(
        ValueTask<BlueTuskPooledSession> renting,
        CancellationToken cancellationToken)
    {
        try
        {
            _sessionLease = await renting.ConfigureAwait(false);
            await _typeMetadata.EnsureLoadedAsync(
                PhysicalSession!,
                cancellationToken).ConfigureAwait(false);
            CompleteOpen();
        }
        catch
        {
            await CleanupFailedOpenAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CompleteOpenAsync(ValueTask loadingTypes)
    {
        try
        {
            await loadingTypes.ConfigureAwait(false);
            CompleteOpen();
        }
        catch
        {
            await CleanupFailedOpenAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void CompleteOpen()
    {
        _sessionTouched = false;
        BeginNotificationLifetime();
        _hideSensitiveConnectionString = true;
        SetState(ConnectionState.Open);
    }

    private void CleanupFailedOpen()
    {
        var lease = Interlocked.Exchange(ref _sessionLease, null);
        if (lease is BlueTuskPooledSession pooledSession)
        {
            _pool!.Return(pooledSession);
        }
        else if (lease is IBlueTuskPhysicalSession session)
        {
            session.Dispose();
        }

        SetState(ConnectionState.Closed);
    }

    private async ValueTask CleanupFailedOpenAsync()
    {
        var lease = Interlocked.Exchange(ref _sessionLease, null);
        if (lease is BlueTuskPooledSession pooledSession)
        {
            _pool!.Return(pooledSession);
        }
        else if (lease is IBlueTuskPhysicalSession session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        SetState(ConnectionState.Closed);
    }

    public override void Close()
    {
        var transaction = DetachTransaction();
        ClearImplicitLargeObjectTransaction();
        transaction?.ConnectionClosed();
        var subscriptions = DetachNotificationSubscriptions();
        try
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
        finally
        {
            try
            {
                var lease = Interlocked.Exchange(ref _sessionLease, null);
                if (lease is BlueTuskPooledSession pooledSession)
                {
                    if (_sessionTouched)
                    {
                        pooledSession.MarkDirty();
                    }

                    _pool!.Return(pooledSession);
                }
                else if (lease is IBlueTuskPhysicalSession session)
                {
                    session.Dispose();
                }
            }
            finally
            {
                CompleteNotificationLifetime();
                _sessionTouched = false;
                SetState(ConnectionState.Closed);
            }
        }
    }

    public override async Task CloseAsync()
    {
        var transaction = DetachTransaction();
        ClearImplicitLargeObjectTransaction();
        transaction?.ConnectionClosed();
        var subscriptions = await DetachNotificationSubscriptionsAsync().ConfigureAwait(false);
        try
        {
            foreach (var subscription in subscriptions)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                var lease = Interlocked.Exchange(ref _sessionLease, null);
                if (lease is BlueTuskPooledSession pooledSession)
                {
                    if (_sessionTouched)
                    {
                        pooledSession.MarkDirty();
                    }

                    _pool!.Return(pooledSession);
                }
                else if (lease is IBlueTuskPhysicalSession session)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                CompleteNotificationLifetime();
                _sessionTouched = false;
                SetState(ConnectionState.Closed);
            }
        }
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Changing databases requires opening a new PostgreSQL connection.");

    /// <summary>Starts receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public void Listen(string channel)
    {
        var quotedChannel = BlueTuskSql.QuoteIdentifier(channel);
        EnsureNotificationsAvailable();
        var notifications = GetNotificationState();

        notifications.Gate.Wait();
        IBlueTuskPhysicalSession? listenerSession = null;
        try
        {
            EnsureNotificationsAvailable();
            if (!notifications.AcceptingSubscriptions)
            {
                throw new InvalidOperationException(
                    "The connection is closing and cannot accept notification subscriptions.");
            }

            if (notifications.Subscriptions.ContainsKey(channel))
            {
                return;
            }

            listenerSession = BlueTuskPhysicalSession.Open(_settings, _clientConfiguration);
            _ = listenerSession.ExecuteSimpleQuery($"LISTEN {quotedChannel}");

            var subscription = new NotificationSubscription(
                channel,
                listenerSession,
                GetNotificationWriter(notifications));
            notifications.Subscriptions.Add(channel, subscription);
            subscription.StartSynchronous(
                Task.Factory.StartNew(
                    () => PumpNotifications(subscription),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));
            listenerSession = null;
        }
        finally
        {
            notifications.Gate.Release();
            listenerSession?.Dispose();
        }
    }

    /// <summary>Starts receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public async ValueTask ListenAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        var quotedChannel = BlueTuskSql.QuoteIdentifier(channel);
        EnsureNotificationsAvailable();
        var notifications = GetNotificationState();

        await notifications.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IBlueTuskPhysicalSession? listenerSession = null;
        try
        {
            EnsureNotificationsAvailable();
            if (!notifications.AcceptingSubscriptions)
            {
                throw new InvalidOperationException(
                    "The connection is closing and cannot accept notification subscriptions.");
            }

            if (notifications.Subscriptions.ContainsKey(channel))
            {
                return;
            }

            listenerSession = await BlueTuskPhysicalSession.OpenAsync(
                _settings,
                _clientConfiguration,
                cancellationToken).ConfigureAwait(false);
            _ = await listenerSession.ExecuteSimpleQueryAsync(
                $"LISTEN {quotedChannel}",
                cancellationToken).ConfigureAwait(false);

            var subscription = new NotificationSubscription(
                channel,
                listenerSession,
                GetNotificationWriter(notifications));
            notifications.Subscriptions.Add(channel, subscription);
            subscription.Start(PumpNotificationsAsync(subscription));
            listenerSession = null;
        }
        finally
        {
            notifications.Gate.Release();
            if (listenerSession is not null)
            {
                await listenerSession.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public void Unlisten(string channel)
    {
        _ = BlueTuskSql.QuoteIdentifier(channel);
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        NotificationSubscription? subscription;
        notifications.Gate.Wait();
        try
        {
            notifications.Subscriptions.Remove(channel, out subscription);
        }
        finally
        {
            notifications.Gate.Release();
        }

        subscription?.Dispose();
    }

    /// <summary>Stops receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public async ValueTask UnlistenAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        _ = BlueTuskSql.QuoteIdentifier(channel);
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        NotificationSubscription? subscription;
        await notifications.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            notifications.Subscriptions.Remove(channel, out subscription);
        }
        finally
        {
            notifications.Gate.Release();
        }

        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops receiving notifications for every channel on this connection.</summary>
    public void UnlistenAll()
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        NotificationSubscription[] subscriptions;
        notifications.Gate.Wait();
        try
        {
            subscriptions = notifications.Subscriptions.Values.ToArray();
            notifications.Subscriptions.Clear();
        }
        finally
        {
            notifications.Gate.Release();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    /// <summary>Stops receiving notifications for every channel on this connection.</summary>
    public async ValueTask UnlistenAllAsync(CancellationToken cancellationToken = default)
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        NotificationSubscription[] subscriptions;
        await notifications.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            subscriptions = notifications.Subscriptions.Values.ToArray();
            notifications.Subscriptions.Clear();
        }
        finally
        {
            notifications.Gate.Release();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a PostgreSQL large object with a server-assigned object identifier.</summary>
    public uint CreateLargeObject() => CreateLargeObject(preferredObjectId: 0);

    /// <summary>Creates a PostgreSQL large object, optionally requesting a specific object identifier.</summary>
    public uint CreateLargeObject(uint preferredObjectId) =>
        ExecuteLargeObjectTransaction(
            transaction => BlueTuskLargeObjectOperations.ExecuteScalar<uint>(
                this,
                transaction,
                "SELECT pg_catalog.lo_create($1)",
                [new BlueTuskParameter<uint>(preferredObjectId)]));

    /// <summary>Deletes a PostgreSQL large object.</summary>
    public void DeleteLargeObject(uint objectId)
    {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        var deleted = ExecuteLargeObjectTransaction(
            transaction => BlueTuskLargeObjectOperations.ExecuteScalar<int>(
                this,
                transaction,
                "SELECT pg_catalog.lo_unlink($1)",
                [new BlueTuskParameter<uint>(objectId)]));
        if (deleted != 1)
        {
            throw new BlueTuskException(
                $"PostgreSQL returned the unexpected lo_unlink result {deleted}.");
        }
    }

    /// <summary>Opens a transactional stream over a PostgreSQL large object.</summary>
    public BlueTuskLargeObjectStream OpenLargeObject(uint objectId, FileAccess access)
    {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        if (access is < FileAccess.Read or > FileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        LargeObjectGate.Wait();
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (ReadImplicitLargeObjectTransaction() is not null)
            {
                throw new InvalidOperationException(
                    "Only one implicitly transactional large-object stream can be open at a time. " +
                    "Begin an explicit transaction to open multiple streams.");
            }

            transaction = CurrentTransaction;
            if (transaction is null)
            {
                transaction = BeginTransaction(IsolationLevel.ReadCommitted);
                ownsTransaction = true;
                SetImplicitLargeObjectTransaction(transaction);
            }

            try
            {
                var mode = access switch
                {
                    FileAccess.Read => 0x0004_0000,
                    FileAccess.Write => 0x0002_0000,
                    FileAccess.ReadWrite => 0x0006_0000,
                    _ => throw new UnreachableException(),
                };
                var descriptor = BlueTuskLargeObjectOperations.ExecuteScalar<int>(
                    this,
                    transaction,
                    "SELECT pg_catalog.lo_open($1, $2)",
                    [
                        new BlueTuskParameter<uint>(objectId),
                        new BlueTuskParameter<int>(mode),
                    ]);
                var operations = new BlueTuskLargeObjectOperations(
                    this,
                    transaction,
                    descriptor,
                    ownsTransaction);
                var length = operations.Seek(0, SeekOrigin.End);
                var position = operations.Seek(0, SeekOrigin.Begin);
                return new BlueTuskLargeObjectStream(
                    objectId,
                    access,
                    length,
                    position,
                    operations);
            }
            catch
            {
                if (ownsTransaction)
                {
                    CompleteImplicitLargeObjectTransactionCore(transaction, commit: false);
                }

                throw;
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    /// <summary>Creates a PostgreSQL large object with a server-assigned object identifier.</summary>
    public ValueTask<uint> CreateLargeObjectAsync(CancellationToken cancellationToken = default) =>
        CreateLargeObjectAsync(preferredObjectId: 0, cancellationToken);

    /// <summary>Creates a PostgreSQL large object, optionally requesting a specific object identifier.</summary>
    public ValueTask<uint> CreateLargeObjectAsync(
        uint preferredObjectId,
        CancellationToken cancellationToken = default) =>
        ExecuteLargeObjectTransactionAsync(
            (transaction, token) => BlueTuskLargeObjectOperations.ExecuteScalarAsync<uint>(
                this,
                transaction,
                "SELECT pg_catalog.lo_create($1)",
                [new BlueTuskParameter<uint>(preferredObjectId)],
                token),
            cancellationToken);

    /// <summary>Deletes a PostgreSQL large object.</summary>
    public async ValueTask DeleteLargeObjectAsync(
        uint objectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        var deleted = await ExecuteLargeObjectTransactionAsync(
            (transaction, token) => BlueTuskLargeObjectOperations.ExecuteScalarAsync<int>(
                this,
                transaction,
                "SELECT pg_catalog.lo_unlink($1)",
                [new BlueTuskParameter<uint>(objectId)],
                token),
            cancellationToken).ConfigureAwait(false);
        if (deleted != 1)
        {
            throw new BlueTuskException(
                $"PostgreSQL returned the unexpected lo_unlink result {deleted}.");
        }
    }

    /// <summary>Opens a transactional asynchronous stream over a PostgreSQL large object.</summary>
    public async ValueTask<BlueTuskLargeObjectStream> OpenLargeObjectAsync(
        uint objectId,
        FileAccess access,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(objectId);
        if (access is < FileAccess.Read or > FileAccess.ReadWrite)
        {
            throw new ArgumentOutOfRangeException(nameof(access));
        }

        await LargeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (ReadImplicitLargeObjectTransaction() is not null)
            {
                throw new InvalidOperationException(
                    "Only one implicitly transactional large-object stream can be open at a time. " +
                    "Begin an explicit transaction to open multiple streams.");
            }

            transaction = CurrentTransaction;
            if (transaction is null)
            {
                transaction = await BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken).ConfigureAwait(false);
                ownsTransaction = true;
                SetImplicitLargeObjectTransaction(transaction);
            }

            try
            {
                var mode = access switch
                {
                    FileAccess.Read => 0x0004_0000,
                    FileAccess.Write => 0x0002_0000,
                    FileAccess.ReadWrite => 0x0006_0000,
                    _ => throw new UnreachableException(),
                };
                var descriptor = await BlueTuskLargeObjectOperations.ExecuteScalarAsync<int>(
                    this,
                    transaction,
                    "SELECT pg_catalog.lo_open($1, $2)",
                    [
                        new BlueTuskParameter<uint>(objectId),
                        new BlueTuskParameter<int>(mode),
                    ],
                    cancellationToken).ConfigureAwait(false);
                var operations = new BlueTuskLargeObjectOperations(
                    this,
                    transaction,
                    descriptor,
                    ownsTransaction);
                var length = await operations.SeekAsync(
                    0,
                    SeekOrigin.End,
                    cancellationToken).ConfigureAwait(false);
                var position = await operations.SeekAsync(
                    0,
                    SeekOrigin.Begin,
                    cancellationToken).ConfigureAwait(false);
                return new BlueTuskLargeObjectStream(
                    objectId,
                    access,
                    length,
                    position,
                    operations);
            }
            catch
            {
                if (ownsTransaction)
                {
                    await CompleteImplicitLargeObjectTransactionCoreAsync(
                        transaction,
                        commit: false).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    public new BlueTuskTransaction BeginTransaction() =>
        (BlueTuskTransaction)base.BeginTransaction();

    public new BlueTuskTransaction BeginTransaction(IsolationLevel isolationLevel) =>
        (BlueTuskTransaction)base.BeginTransaction(isolationLevel);

    public new async ValueTask<BlueTuskTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default) =>
        await BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken).ConfigureAwait(false);

    public new async ValueTask<BlueTuskTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default) =>
        (BlueTuskTransaction)await BeginDbTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    public BlueTuskRawCopyResult CopyFrom(string copyCommand, Stream source)
    {
        EnsureCopyAvailable();
        return CopyFromCore(copyCommand, source, copyStarted: null);
    }

    public BlueTuskRawCopyResult CopyTo(string copyCommand, Stream destination)
    {
        EnsureCopyAvailable();
        return CopyToCore(copyCommand, destination, copyStarted: null);
    }

    public async ValueTask<BlueTuskRawCopyResult> CopyFromAsync(
        string copyCommand,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        EnsureCopyAvailable();
        return await CopyFromCoreAsync(
            copyCommand,
            source,
            copyStarted: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BlueTuskRawCopyResult> CopyToAsync(
        string copyCommand,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        EnsureCopyAvailable();
        return await CopyToCoreAsync(
            copyCommand,
            destination,
            copyStarted: null,
            cancellationToken).ConfigureAwait(false);
    }

    public BlueTuskBinaryImporter BeginBinaryImport(string copyCommand)
    {
        EnsureCopyAvailable();
        BlueTuskCopyInOperation? operation = null;
        try
        {
            operation = Session.BeginCopyIn(copyCommand);
            ValidateBinaryCopyResponse(operation.Response);
            var importer = new BlueTuskBinaryImporter(
                operation,
                TypeRegistry,
                operation.Response.ColumnFormats.Count);
            importer.Initialize();
            operation = null;
            return importer;
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    public BlueTuskBinaryExporter BeginBinaryExport(string copyCommand)
    {
        EnsureCopyAvailable();
        BlueTuskCopyOutOperation? operation = null;
        try
        {
            operation = Session.BeginCopyOut(copyCommand);
            ValidateBinaryCopyResponse(operation.Response);
            var exporter = new BlueTuskBinaryExporter(
                operation,
                TypeRegistry,
                operation.Response.ColumnFormats.Count);
            exporter.Initialize();
            operation = null;
            return exporter;
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        finally
        {
            operation?.Dispose();
        }
    }

    public async ValueTask<BlueTuskBinaryImporter> BeginBinaryImportAsync(
        string copyCommand,
        CancellationToken cancellationToken = default)
    {
        EnsureCopyAvailable();
        var pipe = new BlueTuskCopyPipe();
        var started = new TaskCompletionSource<BlueTuskCopyResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var copyTask = CopyFromCoreAsync(
            copyCommand,
            pipe,
            response => started.TrySetResult(response),
            CancellationToken.None).AsTask();
        var response = await AwaitCopyStartAsync(
            started.Task,
            copyTask,
            pipe,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateBinaryCopyResponse(response);
            var importer = new BlueTuskBinaryImporter(
                pipe,
                copyTask,
                TypeRegistry,
                response.ColumnFormats.Count);
            await importer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return importer;
        }
        catch
        {
            pipe.CompleteWriting(
                new IOException("Binary COPY import could not be initialized."));
            try
            {
                _ = await copyTask.ConfigureAwait(false);
            }
            catch
            {
                // The initialization exception remains authoritative.
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<BlueTuskBinaryExporter> BeginBinaryExportAsync(
        string copyCommand,
        CancellationToken cancellationToken = default)
    {
        EnsureCopyAvailable();
        var pipe = new BlueTuskCopyPipe();
        var started = new TaskCompletionSource<BlueTuskCopyResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var copyTask = CopyToPipeAsync(
            copyCommand,
            pipe,
            response => started.TrySetResult(response)).AsTask();
        var response = await AwaitCopyStartAsync(
            started.Task,
            copyTask,
            pipe,
            cancellationToken).ConfigureAwait(false);
        try
        {
            ValidateBinaryCopyResponse(response);
            var exporter = new BlueTuskBinaryExporter(
                pipe,
                copyTask,
                TypeRegistry,
                response.ColumnFormats.Count);
            await exporter.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return exporter;
        }
        catch
        {
            pipe.CompleteWriting(
                new IOException("Binary COPY export could not be initialized."));
            try
            {
                _ = await copyTask.ConfigureAwait(false);
            }
            catch
            {
                // The initialization exception remains authoritative.
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private BlueTuskRawCopyResult CopyFromCore(
        string copyCommand,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        try
        {
            return CreateRawCopyResult(Session.CopyIn(copyCommand, source, copyStarted));
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        catch (Exception) when (!HasOpenSession)
        {
            Close();
            throw;
        }
    }

    private BlueTuskRawCopyResult CopyToCore(
        string copyCommand,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted)
    {
        try
        {
            return CreateRawCopyResult(Session.CopyOut(copyCommand, destination, copyStarted));
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        catch (Exception) when (!HasOpenSession)
        {
            Close();
            throw;
        }
    }

    private async ValueTask<BlueTuskRawCopyResult> CopyFromCoreAsync(
        string copyCommand,
        Stream source,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Session.CopyInAsync(
                copyCommand,
                source,
                copyStarted,
                cancellationToken).ConfigureAwait(false);
            return CreateRawCopyResult(result);
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        catch (Exception) when (!HasOpenSession)
        {
            Close();
            throw;
        }
    }

    private async ValueTask<BlueTuskRawCopyResult> CopyToCoreAsync(
        string copyCommand,
        Stream destination,
        Action<BlueTuskCopyResponse>? copyStarted,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await Session.CopyOutAsync(
                copyCommand,
                destination,
                copyStarted,
                cancellationToken).ConfigureAwait(false);
            return CreateRawCopyResult(result);
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        catch (Exception) when (!HasOpenSession)
        {
            Close();
            throw;
        }
    }

    private async ValueTask<BlueTuskRawCopyResult> CopyToPipeAsync(
        string copyCommand,
        BlueTuskCopyPipe pipe,
        Action<BlueTuskCopyResponse> copyStarted)
    {
        try
        {
            var result = await CopyToCoreAsync(
                copyCommand,
                pipe,
                copyStarted,
                CancellationToken.None).ConfigureAwait(false);
            pipe.CompleteWriting();
            return result;
        }
        catch (Exception exception)
        {
            pipe.CompleteWriting(exception);
            throw;
        }
    }

    public BlueTuskRawCopyResult CopyTextFrom(string copyCommand, TextReader source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var stream = new BlueTuskCopyTextReaderStream(source);
        return CopyFrom(copyCommand, stream);
    }

    public BlueTuskRawCopyResult CopyTextTo(string copyCommand, TextWriter destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var stream = new BlueTuskCopyTextWriterStream(destination);
        var result = CopyTo(copyCommand, stream);
        stream.Complete();
        return result;
    }

    public async ValueTask<BlueTuskRawCopyResult> CopyTextFromAsync(
        string copyCommand,
        TextReader source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var stream = new BlueTuskCopyTextReaderStream(source);
        return await CopyFromAsync(
            copyCommand,
            stream,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BlueTuskRawCopyResult> CopyTextToAsync(
        string copyCommand,
        TextWriter destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        await using var stream = new BlueTuskCopyTextWriterStream(destination);
        var result = await CopyToAsync(
            copyCommand,
            stream,
            cancellationToken).ConfigureAwait(false);
        await stream.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to begin a transaction.");
        }

        var transactions = Transactions;
        lock (transactions.Sync)
        {
            if (transactions.Current is not null || transactions.Starting)
            {
                throw new InvalidOperationException("The connection already has an active transaction.");
            }

            transactions.Starting = true;
        }

        try
        {
            try
            {
                _ = Session.ExecuteSimpleQuery(BlueTuskTransaction.GetBeginStatement(isolationLevel));
            }
            catch (BlueTuskServerException exception)
            {
                throw new BlueTuskException(exception);
            }

            if (Session.TransactionStatus != BlueTuskTransactionStatus.InTransaction)
            {
                throw new BlueTuskException("PostgreSQL did not enter a transaction after BEGIN.");
            }

            var transaction = new BlueTuskTransaction(this, isolationLevel);
            lock (transactions.Sync)
            {
                transactions.Current = transaction;
            }

            return transaction;
        }
        finally
        {
            lock (transactions.Sync)
            {
                transactions.Starting = false;
            }
        }
    }

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to begin a transaction.");
        }

        var transactions = Transactions;
        lock (transactions.Sync)
        {
            if (transactions.Current is not null || transactions.Starting)
            {
                throw new InvalidOperationException("The connection already has an active transaction.");
            }

            transactions.Starting = true;
        }

        try
        {
            try
            {
                _ = await Session.ExecuteSimpleQueryAsync(
                    BlueTuskTransaction.GetBeginStatement(isolationLevel),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception)
            {
                throw new BlueTuskException(exception);
            }
            catch (OperationCanceledException)
            {
                await RecoverCancelledBeginAsync().ConfigureAwait(false);
                throw;
            }

            if (Session.TransactionStatus != BlueTuskTransactionStatus.InTransaction)
            {
                throw new BlueTuskException("PostgreSQL did not enter a transaction after BEGIN.");
            }

            var transaction = new BlueTuskTransaction(this, isolationLevel);
            lock (transactions.Sync)
            {
                transactions.Current = transaction;
            }

            return transaction;
        }
        finally
        {
            lock (transactions.Sync)
            {
                transactions.Starting = false;
            }
        }
    }

    protected override DbCommand CreateDbCommand() => new BlueTuskCommand { Connection = this };

    protected override DbBatch CreateDbBatch() => new BlueTuskBatch(this);

    public new BlueTuskBatch CreateBatch() => (BlueTuskBatch)base.CreateBatch();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            try
            {
                Close();
            }
            finally
            {
                _disposed = true;
                CompleteNotificationLifetime();
            }
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        if (!_disposed &&
            _sessionLease is BlueTuskPooledSession &&
            ReadNotificationState() is null)
        {
            try
            {
                Close();
            }
            finally
            {
                _disposed = true;
            }

            GC.SuppressFinalize(this);
            return base.DisposeAsync();
        }

        GC.SuppressFinalize(this);
        return DisposeAsyncSlow();
    }

    private async ValueTask DisposeAsyncSlow()
    {
        if (!_disposed)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                _disposed = true;
                CompleteNotificationLifetime();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal void CompleteTransaction(BlueTuskTransaction transaction)
    {
        var transactions = Transactions;
        lock (transactions.Sync)
        {
            if (ReferenceEquals(transactions.Current, transaction))
            {
                transactions.Current = null;
            }
        }
    }

    internal void ValidateCommandTransaction(BlueTuskTransaction? transaction)
    {
        var transactions = Volatile.Read(ref _optionalState);
        if (transactions is null)
        {
            if (transaction is not null)
            {
                throw new InvalidOperationException("The command transaction is not active on this connection.");
            }

            return;
        }

        lock (transactions.Sync)
        {
            if (transactions.Current is null)
            {
                if (transaction is not null)
                {
                    throw new InvalidOperationException("The command transaction is not active on this connection.");
                }

                return;
            }

            if (!ReferenceEquals(transactions.Current, transaction))
            {
                throw new InvalidOperationException(
                    "A command executed while the connection has an active transaction must enlist in that transaction.");
            }
        }
    }

    private BlueTuskTransaction? DetachTransaction()
    {
        var transactions = Volatile.Read(ref _optionalState);
        if (transactions is null)
        {
            return null;
        }

        lock (transactions.Sync)
        {
            var transaction = transactions.Current;
            transactions.Current = null;
            transactions.Starting = false;
            return transaction;
        }
    }

    internal void CompleteImplicitLargeObjectTransaction(
        BlueTuskTransaction transaction,
        bool commit)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        LargeObjectGate.Wait();
        try
        {
            if (ReferenceEquals(ReadImplicitLargeObjectTransaction(), transaction))
            {
                CompleteImplicitLargeObjectTransactionCore(transaction, commit);
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    internal async ValueTask CompleteImplicitLargeObjectTransactionAsync(
        BlueTuskTransaction transaction,
        bool commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await LargeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(ReadImplicitLargeObjectTransaction(), transaction))
            {
                await CompleteImplicitLargeObjectTransactionCoreAsync(
                    transaction,
                    commit).ConfigureAwait(false);
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    private async ValueTask<T> ExecuteLargeObjectTransactionAsync<T>(
        Func<BlueTuskTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await LargeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (ReadImplicitLargeObjectTransaction() is not null)
            {
                throw new InvalidOperationException(
                    "A large-object management operation cannot run while an implicitly " +
                    "transactional large-object stream is open.");
            }

            transaction = CurrentTransaction;
            if (transaction is null)
            {
                transaction = await BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken).ConfigureAwait(false);
                ownsTransaction = true;
                SetImplicitLargeObjectTransaction(transaction);
            }

            try
            {
                var result = await operation(transaction, cancellationToken).ConfigureAwait(false);
                if (ownsTransaction)
                {
                    await CompleteImplicitLargeObjectTransactionCoreAsync(
                        transaction,
                        commit: true).ConfigureAwait(false);
                }

                return result;
            }
            catch
            {
                if (ownsTransaction)
                {
                    await CompleteImplicitLargeObjectTransactionCoreAsync(
                        transaction,
                        commit: false).ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    private T ExecuteLargeObjectTransaction<T>(Func<BlueTuskTransaction, T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        LargeObjectGate.Wait();
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (ReadImplicitLargeObjectTransaction() is not null)
            {
                throw new InvalidOperationException(
                    "A large-object management operation cannot run while an implicitly " +
                    "transactional large-object stream is open.");
            }

            transaction = CurrentTransaction;
            if (transaction is null)
            {
                transaction = BeginTransaction(IsolationLevel.ReadCommitted);
                ownsTransaction = true;
                SetImplicitLargeObjectTransaction(transaction);
            }

            try
            {
                var result = operation(transaction);
                if (ownsTransaction)
                {
                    CompleteImplicitLargeObjectTransactionCore(transaction, commit: true);
                }

                return result;
            }
            catch
            {
                if (ownsTransaction)
                {
                    CompleteImplicitLargeObjectTransactionCore(transaction, commit: false);
                }

                throw;
            }
        }
        finally
        {
            LargeObjectGate.Release();
        }
    }

    private void CompleteImplicitLargeObjectTransactionCore(
        BlueTuskTransaction transaction,
        bool commit)
    {
        try
        {
            if (!transaction.IsCompleted && _state == ConnectionState.Open)
            {
                if (commit)
                {
                    transaction.Commit();
                }
                else
                {
                    transaction.Rollback();
                }
            }
        }
        finally
        {
            ClearImplicitLargeObjectTransaction(transaction);
        }
    }

    private async ValueTask CompleteImplicitLargeObjectTransactionCoreAsync(
        BlueTuskTransaction transaction,
        bool commit)
    {
        try
        {
            if (!transaction.IsCompleted && _state == ConnectionState.Open)
            {
                if (commit)
                {
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ClearImplicitLargeObjectTransaction(transaction);
        }
    }

    private static Channel<BlueTuskNotification> CreateNotificationChannel() =>
        Channel.CreateBounded<BlueTuskNotification>(
            new BoundedChannelOptions(NotificationBufferCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

    private void BeginNotificationLifetime()
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        notifications.Gate.Wait();
        try
        {
            lock (notifications.Sync)
            {
                if (notifications.ChannelCompleted && notifications.Channel is not null)
                {
                    notifications.Channel = CreateNotificationChannel();
                }

                notifications.ChannelCompleted = false;
            }

            notifications.AcceptingSubscriptions = true;
        }
        finally
        {
            notifications.Gate.Release();
        }
    }

    private void CompleteNotificationLifetime(Exception? exception = null)
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return;
        }

        lock (notifications.Sync)
        {
            if (notifications.ChannelCompleted)
            {
                return;
            }

            notifications.ChannelCompleted = true;
            notifications.Channel?.Writer.TryComplete(exception);
            notifications.Available.Set();
        }
    }

    private static ChannelWriter<BlueTuskNotification> GetNotificationWriter(
        NotificationState notifications)
    {
        lock (notifications.Sync)
        {
            if (notifications.ChannelCompleted)
            {
                throw new InvalidOperationException(
                    "The notification stream has completed. Reopen the connection before listening again.");
            }

            return notifications.GetOrCreateChannel().Writer;
        }
    }

    private NotificationState GetNotificationState()
    {
        var notifications = ReadNotificationState();
        if (notifications is not null)
        {
            return notifications;
        }

        var created = new NotificationState
        {
            AcceptingSubscriptions = _state == ConnectionState.Open,
        };
        return Interlocked.CompareExchange(ref Optional.Notifications, created, null) ?? created;
    }

    private void EnsureNotificationsAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The connection must be open to listen for PostgreSQL notifications.");
        }
    }

    private NotificationSubscription[] DetachNotificationSubscriptions()
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return [];
        }

        notifications.Gate.Wait();
        try
        {
            notifications.AcceptingSubscriptions = false;
            var subscriptions = notifications.Subscriptions.Values.ToArray();
            notifications.Subscriptions.Clear();
            return subscriptions;
        }
        finally
        {
            notifications.Gate.Release();
        }
    }

    private async ValueTask<NotificationSubscription[]> DetachNotificationSubscriptionsAsync()
    {
        var notifications = ReadNotificationState();
        if (notifications is null)
        {
            return [];
        }

        await notifications.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            notifications.AcceptingSubscriptions = false;
            var subscriptions = notifications.Subscriptions.Values.ToArray();
            notifications.Subscriptions.Clear();
            return subscriptions;
        }
        finally
        {
            notifications.Gate.Release();
        }
    }

    private async Task PumpNotificationsAsync(NotificationSubscription subscription)
    {
        try
        {
            while (true)
            {
                var response = await subscription.Session.WaitForNotificationAsync(
                    subscription.CancellationToken).ConfigureAwait(false);
                await subscription.Writer.WriteAsync(
                    new BlueTuskNotification(
                        response.ProcessId,
                        response.Channel,
                        response.Payload),
                    subscription.CancellationToken).ConfigureAwait(false);
                ReadNotificationState()?.Available.Set();
            }
        }
        catch (OperationCanceledException) when (subscription.IsCancellationRequested)
        {
            // Unlisten and connection shutdown interrupt the otherwise indefinite receive.
        }
        catch (Exception exception)
        {
            CompleteNotificationLifetime(exception);
        }
    }

    private void PumpNotifications(NotificationSubscription subscription)
    {
        try
        {
            while (true)
            {
                var response = subscription.Session.WaitForNotification();
                var notification = new BlueTuskNotification(
                    response.ProcessId,
                    response.Channel,
                    response.Payload);
                var spinWait = new SpinWait();
                while (!subscription.Writer.TryWrite(notification))
                {
                    if (subscription.IsCancellationRequested)
                    {
                        return;
                    }

                    spinWait.SpinOnce();
                }

                ReadNotificationState()?.Available.Set();
            }
        }
        catch (Exception) when (subscription.IsCancellationRequested)
        {
            // Unlisten and connection shutdown dispose the blocking listener session.
        }
        catch (Exception exception)
        {
            CompleteNotificationLifetime(exception);
        }
    }

    private void EnsureCopyAvailable()
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to start COPY.");
        }

        var transactions = Volatile.Read(ref _optionalState);
        if (transactions is null)
        {
            return;
        }

        lock (transactions.Sync)
        {
            if (transactions.Starting)
            {
                throw new InvalidOperationException(
                    "COPY cannot start while a transaction is being opened.");
            }
        }
    }

    private void EnsureLargeObjectsAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The connection must be open to access PostgreSQL large objects.");
        }
    }

    private static BlueTuskRawCopyResult CreateRawCopyResult(BlueTuskCopyResult result)
    {
        if (!BlueTuskCommandTagParser.TryGetRowsAffected(result.CommandTag, out var rowsAffected))
        {
            throw new BlueTuskException(
                $"PostgreSQL completed COPY with an invalid command tag '{result.CommandTag}'.");
        }

        return new BlueTuskRawCopyResult(
            result.Response.Format == BlueTuskCopyFormat.Binary
                ? BlueTuskCopyDataFormat.Binary
                : BlueTuskCopyDataFormat.Text,
            result.Response.ColumnFormats
                .Select(
                    static format => format == BlueTuskCopyFormat.Binary
                        ? BlueTuskCopyDataFormat.Binary
                        : BlueTuskCopyDataFormat.Text)
                .ToArray(),
            rowsAffected,
            result.BytesTransferred);
    }

    private async ValueTask<BlueTuskCopyResponse> AwaitCopyStartAsync(
        Task<BlueTuskCopyResponse> started,
        Task<BlueTuskRawCopyResult> copyTask,
        BlueTuskCopyPipe pipe,
        CancellationToken cancellationToken)
    {
        try
        {
            var completed = await Task.WhenAny(started, copyTask)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(completed, copyTask))
            {
                _ = await copyTask.ConfigureAwait(false);
                if (started.IsCompletedSuccessfully)
                {
                    return await started.ConfigureAwait(false);
                }

                throw new BlueTuskException(
                    "PostgreSQL completed COPY without entering COPY mode.");
            }

            return await started.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pipe.CompleteWriting(
                new OperationCanceledException(
                    "Binary COPY initialization was cancelled.",
                    cancellationToken));
            await Session.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = await copyTask.ConfigureAwait(false);
            }
            catch
            {
                // The caller's cancellation remains authoritative.
            }

            throw;
        }
    }

    private static void ValidateBinaryCopyResponse(BlueTuskCopyResponse response)
    {
        if (response.Format != BlueTuskCopyFormat.Binary ||
            response.ColumnFormats.Any(
                static format => format != BlueTuskCopyFormat.Binary))
        {
            throw new InvalidOperationException(
                "The COPY command must specify PostgreSQL binary format.");
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A failed cleanup closes the physical connection; the original cancellation remains authoritative.")]
    private async ValueTask RecoverCancelledBeginAsync()
    {
        if (PhysicalSession is not { IsOpen: true } session ||
            session.TransactionStatus == BlueTuskTransactionStatus.Idle)
        {
            return;
        }

        try
        {
            _ = await session.ExecuteSimpleQueryAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private void SetState(ConnectionState state)
    {
        var previous = _state;
        _state = state;
        if (previous != state)
        {
            OnStateChange(new StateChangeEventArgs(previous, state));
        }
    }

    private sealed class OptionalState
    {
        internal object Sync { get; } = new();

        internal SemaphoreSlim? LargeObjectGate;

        internal NotificationState? Notifications;

        internal BlueTuskTransaction? Current;

        internal BlueTuskTransaction? ImplicitLargeObject;

        internal IReadOnlyList<BlueTuskFieldDescription>? ResolvedRowDescription;

        internal BlueTuskTypeRegistry? ResolvedRowRegistry;

        internal BlueTuskResolvedField[]? ResolvedRowFields;

        internal bool Starting;
    }

    private sealed class NotificationState
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);

        internal object Sync { get; } = new();

        internal AutoResetEvent Available { get; } = new(initialState: false);

        internal Dictionary<string, NotificationSubscription> Subscriptions { get; } =
            new(StringComparer.Ordinal);

        internal Channel<BlueTuskNotification>? Channel { get; set; }

        internal bool ChannelCompleted { get; set; }

        internal bool AcceptingSubscriptions { get; set; }

        internal Channel<BlueTuskNotification> GetOrCreateChannel() =>
            Channel ??= CreateNotificationChannel();
    }

    private sealed class NotificationSubscription : IDisposable, IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellationSource = new();
        private Task _pump = Task.CompletedTask;
        private bool _synchronous;
        private int _disposed;

        public NotificationSubscription(
            string channel,
            IBlueTuskPhysicalSession session,
            ChannelWriter<BlueTuskNotification> writer)
        {
            Channel = channel;
            Session = session;
            Writer = writer;
        }

        public string Channel { get; }

        public IBlueTuskPhysicalSession Session { get; }

        public ChannelWriter<BlueTuskNotification> Writer { get; }

        public CancellationToken CancellationToken => _cancellationSource.Token;

        public bool IsCancellationRequested => _cancellationSource.IsCancellationRequested;

        public void Start(Task pump)
        {
            ArgumentNullException.ThrowIfNull(pump);
            if (!ReferenceEquals(_pump, Task.CompletedTask))
            {
                throw new InvalidOperationException("The notification subscription has already started.");
            }

            _pump = pump;
        }

        public void StartSynchronous(Task pump)
        {
            _synchronous = true;
            Start(pump);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationSource.Cancel();
            if (_synchronous)
            {
                Session.Dispose();
            }

            _pump.GetAwaiter().GetResult();
            if (!_synchronous)
            {
                Session.Dispose();
            }

            _cancellationSource.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationSource.Cancel();
            if (_synchronous)
            {
                Session.Dispose();
            }

            await _pump.ConfigureAwait(false);
            if (!_synchronous)
            {
                await Session.DisposeAsync().ConfigureAwait(false);
            }

            _cancellationSource.Dispose();
        }
    }
}
