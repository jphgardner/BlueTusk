using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;
using BlueTusk.Data.Copy;
using BlueTusk.Protocol;
using BlueTusk.TypeSystem;

namespace BlueTusk.Data;

/// <summary>Represents a logical connection to PostgreSQL.</summary>
public sealed class BlueTuskConnection : DbConnection
{
    private readonly BlueTuskConnectionPool? _pool;
    private readonly BlueTuskTypeMetadataCache _typeMetadata;
    private string _connectionString = string.Empty;
    private BlueTuskConnectionStringBuilder _settings = new();
    private IBlueTuskPhysicalSession? _session;
    private BlueTuskPooledSession? _pooledSession;
    private BlueTuskTransaction? _currentTransaction;
    private bool _startingTransaction;
    private readonly object _transactionSync = new();
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
        DetachTransaction()?.ConnectionClosed();
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

        SetState(ConnectionState.Closed);
    }

    public override async Task CloseAsync()
    {
        DetachTransaction()?.ConnectionClosed();
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

        SetState(ConnectionState.Closed);
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("Changing databases requires opening a new PostgreSQL connection.");

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
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
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
}
