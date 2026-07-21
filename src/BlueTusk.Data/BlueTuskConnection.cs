using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

/// <summary>Represents one physical connection to PostgreSQL.</summary>
public sealed class BlueTuskConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private BlueTuskConnectionStringBuilder _settings = new();
    private BlueTuskSession? _session;
    private BlueTuskTransaction? _currentTransaction;
    private bool _startingTransaction;
    private readonly object _transactionSync = new();
    private ConnectionState _state = ConnectionState.Closed;

    public BlueTuskConnection()
    {
    }

    public BlueTuskConnection(string connectionString)
    {
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

            _settings = new BlueTuskConnectionStringBuilder(value ?? string.Empty);
            _connectionString = value ?? string.Empty;
        }
    }

    public override string Database => _settings.Database;

    public override string DataSource => _settings.Host;

    public override string ServerVersion =>
        _session?.Parameters.TryGetValue("server_version", out var version) == true ? version : string.Empty;

    public override ConnectionState State => _state;

    public override int ConnectionTimeout => checked((int)_settings.Timeout.TotalSeconds);

    internal BlueTuskSession Session =>
        _session ?? throw new InvalidOperationException("The connection is not open.");

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
            _session = await BlueTuskSession.OpenAsync(
                new BlueTuskClientOptions
                {
                    Host = _settings.Host,
                    Port = _settings.Port,
                    Database = _settings.Database,
                    Username = _settings.Username,
                    Password = _settings.Password,
                    ApplicationName = _settings.ApplicationName,
                    ConnectTimeout = _settings.Timeout,
                    SslMode = _settings.SslMode,
                    ChannelBinding = _settings.ChannelBinding,
                },
                cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Open);
        }
        catch
        {
            _session = null;
            SetState(ConnectionState.Closed);
            throw;
        }
    }

    public override void Close()
    {
        DetachTransaction()?.ConnectionClosed();
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        SetState(ConnectionState.Closed);
    }

    public override async Task CloseAsync()
    {
        DetachTransaction()?.ConnectionClosed();
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
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
