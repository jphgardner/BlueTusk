using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using BlueTusk.Client;
using BlueTusk.Data.Copy;
using BlueTusk.Data.LargeObjects;
using BlueTusk.Data.Notifications;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Represents a logical connection to PostgreSQL.</summary>
public sealed class BlueTuskConnection : DbConnection
{
    private const int NotificationBufferCapacity = 1_024;
    private readonly BlueTuskConnectionPool? _pool;
    private readonly BlueTuskTypeMetadataCache _typeMetadata;
    private readonly SemaphoreSlim _largeObjectGate = new(1, 1);
    private readonly SemaphoreSlim _notificationGate = new(1, 1);
    private readonly object _notificationStateSync = new();
    private readonly Dictionary<string, NotificationSubscription> _notificationSubscriptions =
        new(StringComparer.Ordinal);
    private string _connectionString = string.Empty;
    private BlueTuskConnectionStringBuilder _settings = new();
    private IBlueTuskPhysicalSession? _session;
    private BlueTuskPooledSession? _pooledSession;
    private BlueTuskTransaction? _currentTransaction;
    private BlueTuskTransaction? _implicitLargeObjectTransaction;
    private bool _startingTransaction;
    private readonly object _transactionSync = new();
    private Channel<BlueTuskNotification> _notificationChannel = CreateNotificationChannel();
    private bool _notificationChannelCompleted;
    private bool _acceptingNotificationSubscriptions;
    private bool _disposed;
    private ConnectionState _state = ConnectionState.Closed;

    public BlueTuskConnection()
    {
        _typeMetadata = new BlueTuskTypeMetadataCache();
    }

    public BlueTuskConnection(string connectionString)
        : this(connectionString, pool: null, new BlueTuskTypeMetadataCache())
    {
    }

    internal BlueTuskConnection(string connectionString, BlueTuskConnectionPool? pool)
        : this(connectionString, pool, new BlueTuskTypeMetadataCache())
    {
    }

    internal BlueTuskConnection(
        string connectionString,
        BlueTuskConnectionPool? pool,
        BlueTuskTypeMetadataCache typeMetadata)
    {
        _pool = pool;
        _typeMetadata = typeMetadata ?? throw new ArgumentNullException(nameof(typeMetadata));
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
            {
                throw new InvalidOperationException("The connection string cannot change while the connection is open.");
            }

            if (_pool is not null &&
                _connectionString.Length > 0 &&
                !string.Equals(_connectionString, value, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A connection created by a data source cannot use a different connection string.");
            }

            _settings = new BlueTuskConnectionStringBuilder(value ?? string.Empty);
            _settings.Validate();
            _connectionString = value ?? string.Empty;
        }
    }

    public override string Database => _settings.Database;

    public override string DataSource => _settings.Host;

    public override string ServerVersion =>
        _session?.Parameters.TryGetValue("server_version", out var version) == true ? version : string.Empty;

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => checked((int)_settings.Timeout.TotalSeconds);

    /// <summary>Gets the asynchronous stream of notifications received by this connection.</summary>
    public IAsyncEnumerable<BlueTuskNotification> Notifications
    {
        get
        {
            lock (_notificationStateSync)
            {
                return _notificationChannel.Reader.ReadAllAsync();
            }
        }
    }

    internal IBlueTuskPhysicalSession Session =>
        _session ?? throw new InvalidOperationException("The connection is not open.");

    internal bool HasOpenSession => _session is { IsOpen: true };

    internal BlueTuskTypeRegistry TypeRegistry => _typeMetadata.Registry;

    internal BlueTuskTransaction? CurrentTransaction
    {
        get
        {
            lock (_transactionSync)
            {
                return _currentTransaction;
            }
        }
    }

    public override void Open() =>
        throw new NotSupportedException("Synchronous connection opening is not implemented yet. Use OpenAsync.");

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != ConnectionState.Closed)
        {
            throw new InvalidOperationException("The connection is already open or opening.");
        }

        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        SetState(ConnectionState.Connecting);
        try
        {
            if (_pool is null)
            {
                _session = await BlueTuskPhysicalSession.OpenAsync(_settings, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _pooledSession = await _pool.RentAsync(cancellationToken).ConfigureAwait(false);
                _session = _pooledSession.Session;
            }

            await _typeMetadata.EnsureLoadedAsync(_session, cancellationToken).ConfigureAwait(false);

            BeginNotificationLifetime();
            SetState(ConnectionState.Open);
        }
        catch
        {
            var pooledSession = Interlocked.Exchange(ref _pooledSession, null);
            if (pooledSession is not null)
            {
                _pool!.Return(pooledSession);
            }

            _session = null;
            SetState(ConnectionState.Closed);
            throw;
        }
    }

    public override void Close()
    {
        var transaction = DetachTransaction();
        Interlocked.Exchange(ref _implicitLargeObjectTransaction, null);
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
                var session = Interlocked.Exchange(ref _session, null);
                var pooledSession = Interlocked.Exchange(ref _pooledSession, null);
                if (pooledSession is not null)
                {
                    _pool!.Return(pooledSession);
                }
                else
                {
                    session?.Dispose();
                }
            }
            finally
            {
                CompleteNotificationLifetime();
                SetState(ConnectionState.Closed);
            }
        }
    }

    public override async Task CloseAsync()
    {
        var transaction = DetachTransaction();
        Interlocked.Exchange(ref _implicitLargeObjectTransaction, null);
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
                var session = Interlocked.Exchange(ref _session, null);
                var pooledSession = Interlocked.Exchange(ref _pooledSession, null);
                if (pooledSession is not null)
                {
                    _pool!.Return(pooledSession);
                }
                else if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                CompleteNotificationLifetime();
                SetState(ConnectionState.Closed);
            }
        }
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Changing databases requires opening a new PostgreSQL connection.");

    /// <summary>Starts receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public async ValueTask ListenAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        var quotedChannel = BlueTuskSqlIdentifier.Quote(channel, nameof(channel));
        EnsureNotificationsAvailable();

        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IBlueTuskPhysicalSession? listenerSession = null;
        try
        {
            EnsureNotificationsAvailable();
            if (!_acceptingNotificationSubscriptions)
            {
                throw new InvalidOperationException(
                    "The connection is closing and cannot accept notification subscriptions.");
            }

            if (_notificationSubscriptions.ContainsKey(channel))
            {
                return;
            }

            listenerSession = await BlueTuskPhysicalSession.OpenAsync(
                _settings,
                cancellationToken).ConfigureAwait(false);
            _ = await listenerSession.ExecuteSimpleQueryAsync(
                $"LISTEN {quotedChannel}",
                cancellationToken).ConfigureAwait(false);

            var subscription = new NotificationSubscription(
                channel,
                listenerSession,
                GetNotificationWriter());
            _notificationSubscriptions.Add(channel, subscription);
            subscription.Start(PumpNotificationsAsync(subscription));
            listenerSession = null;
        }
        finally
        {
            _notificationGate.Release();
            if (listenerSession is not null)
            {
                await listenerSession.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops receiving asynchronous notifications for a PostgreSQL channel.</summary>
    public async ValueTask UnlistenAsync(
        string channel,
        CancellationToken cancellationToken = default)
    {
        _ = BlueTuskSqlIdentifier.Quote(channel, nameof(channel));

        NotificationSubscription? subscription;
        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _notificationSubscriptions.Remove(channel, out subscription);
        }
        finally
        {
            _notificationGate.Release();
        }

        if (subscription is not null)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops receiving notifications for every channel on this connection.</summary>
    public async ValueTask UnlistenAllAsync(CancellationToken cancellationToken = default)
    {
        NotificationSubscription[] subscriptions;
        await _notificationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            subscriptions = _notificationSubscriptions.Values.ToArray();
            _notificationSubscriptions.Clear();
        }
        finally
        {
            _notificationGate.Release();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
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

        await _largeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (Volatile.Read(ref _implicitLargeObjectTransaction) is not null)
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
                Volatile.Write(ref _implicitLargeObjectTransaction, transaction);
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
            _largeObjectGate.Release();
        }
    }

    public new async ValueTask<BlueTuskTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default) =>
        await BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken).ConfigureAwait(false);

    public new async ValueTask<BlueTuskTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default) =>
        (BlueTuskTransaction)await BeginDbTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

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

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("Synchronous transaction start is not implemented yet. Use BeginTransactionAsync.");

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to begin a transaction.");
        }

        lock (_transactionSync)
        {
            if (_currentTransaction is not null || _startingTransaction)
            {
                throw new InvalidOperationException("The connection already has an active transaction.");
            }

            _startingTransaction = true;
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
            lock (_transactionSync)
            {
                _currentTransaction = transaction;
            }

            return transaction;
        }
        finally
        {
            lock (_transactionSync)
            {
                _startingTransaction = false;
            }
        }
    }

    protected override DbCommand CreateDbCommand() => new BlueTuskCommand { Connection = this };

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

    public override async ValueTask DisposeAsync()
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
        GC.SuppressFinalize(this);
    }

    internal void CompleteTransaction(BlueTuskTransaction transaction)
    {
        lock (_transactionSync)
        {
            if (ReferenceEquals(_currentTransaction, transaction))
            {
                _currentTransaction = null;
            }
        }
    }

    internal void ValidateCommandTransaction(BlueTuskTransaction? transaction)
    {
        lock (_transactionSync)
        {
            if (_currentTransaction is null)
            {
                if (transaction is not null)
                {
                    throw new InvalidOperationException("The command transaction is not active on this connection.");
                }

                return;
            }

            if (!ReferenceEquals(_currentTransaction, transaction))
            {
                throw new InvalidOperationException(
                    "A command executed while the connection has an active transaction must enlist in that transaction.");
            }
        }
    }

    private BlueTuskTransaction? DetachTransaction()
    {
        lock (_transactionSync)
        {
            var transaction = _currentTransaction;
            _currentTransaction = null;
            _startingTransaction = false;
            return transaction;
        }
    }

    internal async ValueTask CompleteImplicitLargeObjectTransactionAsync(
        BlueTuskTransaction transaction,
        bool commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await _largeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(
                    Volatile.Read(ref _implicitLargeObjectTransaction),
                    transaction))
            {
                await CompleteImplicitLargeObjectTransactionCoreAsync(
                    transaction,
                    commit).ConfigureAwait(false);
            }
        }
        finally
        {
            _largeObjectGate.Release();
        }
    }

    private async ValueTask<T> ExecuteLargeObjectTransactionAsync<T>(
        Func<BlueTuskTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _largeObjectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        BlueTuskTransaction? transaction = null;
        var ownsTransaction = false;
        try
        {
            EnsureLargeObjectsAvailable();
            if (Volatile.Read(ref _implicitLargeObjectTransaction) is not null)
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
                Volatile.Write(ref _implicitLargeObjectTransaction, transaction);
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
            _largeObjectGate.Release();
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
            _ = Interlocked.CompareExchange(
                ref _implicitLargeObjectTransaction,
                null,
                transaction);
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
        _notificationGate.Wait();
        try
        {
            lock (_notificationStateSync)
            {
                if (_notificationChannelCompleted)
                {
                    _notificationChannel = CreateNotificationChannel();
                    _notificationChannelCompleted = false;
                }
            }

            _acceptingNotificationSubscriptions = true;
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    private void CompleteNotificationLifetime(Exception? exception = null)
    {
        lock (_notificationStateSync)
        {
            if (_notificationChannelCompleted)
            {
                return;
            }

            _notificationChannelCompleted = true;
            _notificationChannel.Writer.TryComplete(exception);
        }
    }

    private ChannelWriter<BlueTuskNotification> GetNotificationWriter()
    {
        lock (_notificationStateSync)
        {
            if (_notificationChannelCompleted)
            {
                throw new InvalidOperationException(
                    "The notification stream has completed. Reopen the connection before listening again.");
            }

            return _notificationChannel.Writer;
        }
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
        _notificationGate.Wait();
        try
        {
            _acceptingNotificationSubscriptions = false;
            var subscriptions = _notificationSubscriptions.Values.ToArray();
            _notificationSubscriptions.Clear();
            return subscriptions;
        }
        finally
        {
            _notificationGate.Release();
        }
    }

    private async ValueTask<NotificationSubscription[]> DetachNotificationSubscriptionsAsync()
    {
        await _notificationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _acceptingNotificationSubscriptions = false;
            var subscriptions = _notificationSubscriptions.Values.ToArray();
            _notificationSubscriptions.Clear();
            return subscriptions;
        }
        finally
        {
            _notificationGate.Release();
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

    private void EnsureCopyAvailable()
    {
        if (_state != ConnectionState.Open)
        {
            throw new InvalidOperationException("The connection must be open to start COPY.");
        }

        lock (_transactionSync)
        {
            if (_startingTransaction)
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
        if (_session is not { IsOpen: true } session ||
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

    private sealed class NotificationSubscription : IDisposable, IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellationSource = new();
        private Task _pump = Task.CompletedTask;
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

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationSource.Cancel();
            _pump.GetAwaiter().GetResult();
            Session.Dispose();
            _cancellationSource.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellationSource.Cancel();
            await _pump.ConfigureAwait(false);
            await Session.DisposeAsync().ConfigureAwait(false);
            _cancellationSource.Dispose();
        }
    }
}
