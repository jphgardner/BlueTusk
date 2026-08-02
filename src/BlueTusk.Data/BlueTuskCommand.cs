using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

public sealed class BlueTuskCommand : DbCommand
{
    private static long s_preparedStatementSequence;
    private readonly BlueTuskParameterCollection _parameters = new();
    private BlueTuskConnection? _connection;
    private readonly BlueTuskDataSource? _dataSource;
    private BlueTuskTransaction? _transaction;
    private BlueTuskConnection? _executingConnection;
    private int _commandTimeout = 30;
    private int _sequentialFetchSize;
    private BlueTuskCommandExecutionMode _executionMode;
    private int _executing;
    private int _cancellationRequested;
    private int _timeoutRequested;
    private bool _prepareRequested;
    private PreparedStatementState? _preparedStatement;
    private BlueTuskCommandPlan? _commandPlan;
    private string? _commandPlanText;
    private int _commandPlanParameterVersion = -1;
    private BlueTuskExtendedQueryParameter[]? _preparedEncodedParameters;
    private byte[]?[]? _preparedParameterBuffers;
    private ReusableCommandTimeout? _preparedCommandTimeout;

    public BlueTuskCommand()
    {
    }

    public BlueTuskCommand(string commandText, BlueTuskConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    internal BlueTuskCommand(string commandText, BlueTuskDataSource dataSource)
    {
        CommandText = commandText;
        _dataSource = dataSource;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    /// <summary>Gets or sets whether BlueTusk selects, requires, or bypasses the extended query protocol.</summary>
    public BlueTuskCommandExecutionMode ExecutionMode
    {
        get => _executionMode;
        set => _executionMode = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Gets or sets the maximum rows requested per portal fetch for sequential readers.
    /// Zero streams the complete response without suspending the portal.
    /// </summary>
    public int SequentialFetchSize
    {
        get => _sequentialFetchSize;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _sequentialFetchSize = value;
        }
    }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("BlueTusk currently supports text commands only.");
            }
        }
    }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => Connection = value switch
        {
            null => null,
            BlueTuskConnection connection => connection,
            _ => throw new ArgumentException("A BlueTuskCommand requires a BlueTuskConnection.", nameof(value)),
        };
    }

    public new BlueTuskConnection? Connection
    {
        get => _connection;
        set
        {
            if (!ReferenceEquals(_connection, value))
            {
                _preparedStatement = null;
            }

            _connection = value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new BlueTuskParameterCollection Parameters => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            BlueTuskTransaction transaction => transaction,
            _ => throw new ArgumentException("A BlueTuskCommand requires a BlueTuskTransaction.", nameof(value)),
        };
    }

    public new BlueTuskTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    public override void Cancel()
    {
        var connection = Volatile.Read(ref _executingConnection);
        if (connection is not { HasOpenSession: true })
        {
            return;
        }

        Interlocked.Exchange(ref _cancellationRequested, 1);
        connection.Session.Cancel();
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        var connection = Volatile.Read(ref _executingConnection);
        if (connection is not { HasOpenSession: true })
        {
            return;
        }

        Interlocked.Exchange(ref _cancellationRequested, 1);
        await connection.Session.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    public override int ExecuteNonQuery() => GetRecordsAffected(ExecuteCore());

    public override object? ExecuteScalar()
    {
        var result = ExecuteCore();
        var resultSet = result.FirstOrDefault;
        return resultSet is { Fields.Count: > 0, Rows.Count: > 0 }
            ? BlueTuskValueDecoder.Decode(GetTypeRegistry(), resultSet.Fields[0], resultSet.Rows[0].Values[0])
            : null;
    }

    public override void Prepare()
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        try
        {
            _prepareRequested = true;
            _ = EnsurePrepared();
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        try
        {
            _prepareRequested = true;
            _ = await EnsurePreparedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    protected override DbParameter CreateDbParameter() => new BlueTuskParameter();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _preparedCommandTimeout?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        behavior.HasFlag(CommandBehavior.SequentialAccess)
            ? ExecuteStreamingDataReader(behavior)
            : new BlueTuskDataReader(
                ExecuteCore(),
                behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
                GetTypeRegistry());

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        if (behavior.HasFlag(CommandBehavior.SequentialAccess))
        {
            return await ExecuteStreamingDataReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        }

        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return new BlueTuskDataReader(
            result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
            GetTypeRegistry());
    }

    private BlueTuskDataReader ExecuteStreamingDataReader(CommandBehavior behavior)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        BlueTuskConnection? ownedConnection = null;
        Timer? timeoutTimer = null;
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        try
        {
            var connection = GetStreamingConnection(ref ownedConnection);
            connection.ValidateCommandTransaction(_transaction);
            ValidateStreamingCommand();
            telemetry = StartTelemetry(connection);
            Volatile.Write(ref _executingConnection, connection);
            timeoutTimer = CreateTimeoutTimer();
            var portal = BeginStreamingPortal(connection);
            return CreateStreamingReader(
                portal,
                connection,
                ownedConnection,
                behavior,
                timeoutTimer);
        }
        catch (BlueTuskServerException exception)
        {
            timeoutTimer?.Dispose();
            ownedConnection?.Dispose();
            CompleteStreamingExecution();
            var translated = TranslateReaderServerException(exception);
            failure = translated;
            throw translated;
        }
        catch (Exception exception)
        {
            failure = exception;
            timeoutTimer?.Dispose();
            ownedConnection?.Dispose();
            CompleteStreamingExecution();
            throw;
        }
        finally
        {
            telemetry.Complete(failure);
        }
    }

    private async ValueTask<BlueTuskDataReader> ExecuteStreamingDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        BlueTuskConnection? ownedConnection = null;
        Timer? timeoutTimer = null;
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        try
        {
            var connection = await GetStreamingConnectionAsync(
                opened => ownedConnection = opened,
                cancellationToken).ConfigureAwait(false);
            connection.ValidateCommandTransaction(_transaction);
            ValidateStreamingCommand();
            telemetry = StartTelemetry(connection);
            Volatile.Write(ref _executingConnection, connection);
            timeoutTimer = CreateTimeoutTimer();
            var portal = await BeginStreamingPortalAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            return CreateStreamingReader(
                portal,
                connection,
                ownedConnection,
                behavior,
                timeoutTimer);
        }
        catch (BlueTuskServerException exception)
        {
            timeoutTimer?.Dispose();
            if (ownedConnection is not null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }

            CompleteStreamingExecution();
            var translated = TranslateReaderServerException(exception);
            failure = translated;
            throw translated;
        }
        catch (Exception exception)
        {
            failure = exception;
            timeoutTimer?.Dispose();
            if (ownedConnection is not null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }

            CompleteStreamingExecution();
            throw;
        }
        finally
        {
            telemetry.Complete(failure);
        }
    }

    private BlueTuskConnection GetStreamingConnection(ref BlueTuskConnection? ownedConnection)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The command has no connection or data source.");
        }

        if (_connection is not null)
        {
            return _connection.State == ConnectionState.Open
                ? _connection
                : throw new InvalidOperationException("The command connection is not open.");
        }

        ownedConnection = _dataSource!.OpenConnection();
        return ownedConnection;
    }

    private async ValueTask<BlueTuskConnection> GetStreamingConnectionAsync(
        Action<BlueTuskConnection> setOwnedConnection,
        CancellationToken cancellationToken)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The command has no connection or data source.");
        }

        if (_connection is not null)
        {
            return _connection.State == ConnectionState.Open
                ? _connection
                : throw new InvalidOperationException("The command connection is not open.");
        }

        var ownedConnection = await _dataSource!.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        setOwnedConnection(ownedConnection);
        return ownedConnection;
    }

    private void ValidateStreamingCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
        {
            throw new InvalidOperationException(
                "Sequential readers require the extended query protocol and cannot use simple execution mode.");
        }
    }

    private BlueTuskPortal BeginStreamingPortal(BlueTuskConnection connection)
    {
        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        var useBinaryResults = connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
        try
        {
            if (_prepareRequested)
            {
                var prepared = EnsurePrepared(plan, parameters);
                return connection.Session.BeginPreparedPortal(
                    prepared.Name,
                    parameters,
                    useBinaryResults,
                    SequentialFetchSize);
            }

            return connection.Session.BeginPortal(
                plan.Sql,
                parameters,
                useBinaryResults,
                SequentialFetchSize);
        }
        catch (BlueTuskServerException exception) when (
            useBinaryResults && IsMissingBinaryOutputFunction(exception))
        {
            if (_prepareRequested)
            {
                var prepared = EnsurePrepared(plan, parameters);
                return connection.Session.BeginPreparedPortal(
                    prepared.Name,
                    parameters,
                    useBinaryResults: false,
                    SequentialFetchSize);
            }

            return connection.Session.BeginPortal(
                plan.Sql,
                parameters,
                useBinaryResults: false,
                SequentialFetchSize);
        }
    }

    private async ValueTask<BlueTuskPortal> BeginStreamingPortalAsync(
        BlueTuskConnection connection,
        CancellationToken cancellationToken)
    {
        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        var useBinaryResults = connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
        try
        {
            if (_prepareRequested)
            {
                var prepared = await EnsurePreparedAsync(
                    cancellationToken,
                    plan,
                    parameters).ConfigureAwait(false);
                return await connection.Session.BeginPreparedPortalAsync(
                    prepared.Name,
                    parameters,
                    useBinaryResults,
                    SequentialFetchSize,
                    cancellationToken).ConfigureAwait(false);
            }

            return await connection.Session.BeginPortalAsync(
                plan.Sql,
                parameters,
                useBinaryResults,
                SequentialFetchSize,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BlueTuskServerException exception) when (
            useBinaryResults && IsMissingBinaryOutputFunction(exception))
        {
            if (_prepareRequested)
            {
                var prepared = await EnsurePreparedAsync(
                    cancellationToken,
                    plan,
                    parameters).ConfigureAwait(false);
                return await connection.Session.BeginPreparedPortalAsync(
                    prepared.Name,
                    parameters,
                    useBinaryResults: false,
                    SequentialFetchSize,
                    cancellationToken).ConfigureAwait(false);
            }

            return await connection.Session.BeginPortalAsync(
                plan.Sql,
                parameters,
                useBinaryResults: false,
                SequentialFetchSize,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private BlueTuskDataReader CreateStreamingReader(
        BlueTuskPortal portal,
        BlueTuskConnection connection,
        BlueTuskConnection? ownedConnection,
        CommandBehavior behavior,
        Timer? timeoutTimer)
    {
        var connectionToClose = ownedConnection ??
            (behavior.HasFlag(CommandBehavior.CloseConnection) ? connection : null);
        return new BlueTuskDataReader(
            portal,
            connection,
            connectionToClose,
            connection.TypeRegistry,
            behavior.HasFlag(CommandBehavior.SingleRow),
            () =>
            {
                timeoutTimer?.Dispose();
                CompleteStreamingExecution();
            },
            () =>
            {
                timeoutTimer?.Dispose();
                CompleteStreamingExecution();
                return ValueTask.CompletedTask;
            },
            TranslateReaderServerException);
    }

    private Timer? CreateTimeoutTimer() => CommandTimeout > 0
        ? new Timer(
            static state => ((BlueTuskCommand)state!).CancelForTimeout(),
            this,
            TimeSpan.FromSeconds(CommandTimeout),
            Timeout.InfiniteTimeSpan)
        : null;

    private PreparedCommandTimeoutLease CreatePreparedCommandTimeout() => CommandTimeout > 0
        ? new PreparedCommandTimeoutLease(
            _preparedCommandTimeout ??= new ReusableCommandTimeout(this),
            TimeSpan.FromSeconds(CommandTimeout))
        : default;

    private void CompleteStreamingExecution()
    {
        Volatile.Write(ref _executingConnection, null);
        Volatile.Write(ref _executing, 0);
    }

    private Exception TranslateReaderServerException(BlueTuskServerException exception)
    {
        if (exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
        {
            return new TimeoutException(
                $"The command exceeded its {CommandTimeout}-second timeout.",
                exception);
        }

        if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
        {
            return new OperationCanceledException("The PostgreSQL command was cancelled.", exception);
        }

        return new BlueTuskException(exception);
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return GetRecordsAffected(result);
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        ExecuteScalarCoreAsync<object?>(cancellationToken).AsTask();

    public Task<T?> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default) =>
        ExecuteScalarCoreAsync<T>(cancellationToken).AsTask();

    private async ValueTask<BlueTuskQueryResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        try
        {
            return await ExecuteCoreOnceAsync(
                connection => telemetry = StartTelemetry(connection),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            telemetry.Complete(failure);
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    private BlueTuskQueryResult ExecuteCore()
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        try
        {
            return ExecuteCoreOnce(connection => telemetry = StartTelemetry(connection));
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            telemetry.Complete(failure);
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    private BlueTuskQueryResult ExecuteCoreOnce(Action<BlueTuskConnection> startTelemetry)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The command has no connection or data source.");
        }

        BlueTuskConnection? ownedConnection = null;
        var connection = _connection;
        if (connection is null)
        {
            ownedConnection = _dataSource!.OpenConnection();
            connection = ownedConnection;
        }
        else if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The command connection is not open.");
        }

        connection.ValidateCommandTransaction(_transaction);
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        startTelemetry(connection);

        Volatile.Write(ref _executingConnection, connection);
        using var timeoutTimer = CommandTimeout > 0
            ? new Timer(
                static state => ((BlueTuskCommand)state!).CancelForTimeout(),
                this,
                TimeSpan.FromSeconds(CommandTimeout),
                Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            var plan = GetCommandPlan();
            if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
            {
                if (plan.Parameters.Count != 0 || _prepareRequested)
                {
                    throw new InvalidOperationException(
                        "Simple execution mode cannot be used with parameters or a prepared command.");
                }

                return connection.Session.ExecuteSimpleQuery(plan.Sql);
            }

            if (ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
                plan.Parameters.Count == 0 &&
                !_prepareRequested)
            {
                return connection.Session.ExecuteSimpleQuery(plan.Sql);
            }

            var parameters = EncodeParameters(plan, connection.TypeRegistry);
            var useBinaryResults = connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
            try
            {
                if (_prepareRequested)
                {
                    var prepared = EnsurePrepared(plan, parameters);
                    return connection.Session.ExecutePreparedStatement(
                        prepared.Name,
                        parameters,
                        useBinaryResults);
                }

                return connection.Session.ExecuteExtendedQuery(
                    plan.Sql,
                    parameters,
                    useBinaryResults);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                if (_prepareRequested)
                {
                    var prepared = EnsurePrepared(plan, parameters);
                    return connection.Session.ExecutePreparedStatement(
                        prepared.Name,
                        parameters,
                        useBinaryResults: false);
                }

                return connection.Session.ExecuteExtendedQuery(
                    plan.Sql,
                    parameters,
                    useBinaryResults: false);
            }
        }
        catch (BlueTuskServerException exception) when (
            exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
        {
            throw new TimeoutException($"The command exceeded its {CommandTimeout}-second timeout.", exception);
        }
        catch (BlueTuskServerException exception)
        {
            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL command was cancelled.", exception);
            }

            throw new BlueTuskException(exception);
        }
        catch (Exception) when (!connection.HasOpenSession)
        {
            connection.Close();
            throw;
        }
        finally
        {
            ownedConnection?.Dispose();
        }
    }

    private async ValueTask<BlueTuskQueryResult> ExecuteCoreOnceAsync(
        Action<BlueTuskConnection> startTelemetry,
        CancellationToken cancellationToken)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The command has no connection or data source.");
        }

        BlueTuskConnection? ownedConnection = null;
        var connection = _connection;
        if (connection is null)
        {
            ownedConnection = await _dataSource!.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            connection = ownedConnection;
        }
        else if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The command connection is not open.");
        }

        connection.ValidateCommandTransaction(_transaction);

        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        startTelemetry(connection);

        using var timeoutTimer = CreateTimeoutTimer();
        Volatile.Write(ref _executingConnection, connection);
        try
        {
            var effectiveToken = cancellationToken;
            var plan = GetCommandPlan();
            if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
            {
                if (plan.Parameters.Count != 0 || _prepareRequested)
                {
                    throw new InvalidOperationException(
                        "Simple execution mode cannot be used with parameters or a prepared command.");
                }

                return await connection.Session.ExecuteSimpleQueryAsync(plan.Sql, effectiveToken)
                    .ConfigureAwait(false);
            }

            if (ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
                plan.Parameters.Count == 0 &&
                !_prepareRequested)
            {
                return await connection.Session.ExecuteSimpleQueryAsync(plan.Sql, effectiveToken)
                    .ConfigureAwait(false);
            }

            var parameters = EncodeParameters(plan, connection.TypeRegistry);
            var useBinaryResults = connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
            try
            {
                if (_prepareRequested)
                {
                    var prepared = await EnsurePreparedAsync(
                        effectiveToken,
                        plan,
                        parameters).ConfigureAwait(false);
                    return await connection.Session.ExecutePreparedStatementAsync(
                        prepared.Name,
                        parameters,
                        useBinaryResults,
                        effectiveToken).ConfigureAwait(false);
                }

                return await connection.Session.ExecuteExtendedQueryAsync(
                    plan.Sql,
                    parameters,
                    useBinaryResults,
                    effectiveToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                if (_prepareRequested)
                {
                    var prepared = await EnsurePreparedAsync(
                        effectiveToken,
                        plan,
                        parameters).ConfigureAwait(false);
                    return await connection.Session.ExecutePreparedStatementAsync(
                        prepared.Name,
                        parameters,
                        useBinaryResults: false,
                        effectiveToken).ConfigureAwait(false);
                }

                return await connection.Session.ExecuteExtendedQueryAsync(
                    plan.Sql,
                    parameters,
                    useBinaryResults: false,
                    effectiveToken).ConfigureAwait(false);
            }
        }
        catch (BlueTuskServerException exception)
        {
            if (exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
            {
                throw new TimeoutException(
                    $"The command exceeded its {CommandTimeout}-second timeout.",
                    exception);
            }

            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL command was cancelled.", exception);
            }

            throw new BlueTuskException(exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (!connection.HasOpenSession)
        {
            connection.Close();
            throw;
        }
        finally
        {
            if (ownedConnection is not null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<T?> ExecuteScalarCoreAsync<T>(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        try
        {
            if (_connection is null && _dataSource is null)
            {
                throw new InvalidOperationException("The command has no connection or data source.");
            }

            BlueTuskConnection? ownedConnection = null;
            var connection = _connection;
            if (connection is null)
            {
                ownedConnection = await _dataSource!.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                connection = ownedConnection;
            }
            else if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The command connection is not open.");
            }

            connection.ValidateCommandTransaction(_transaction);
            if (string.IsNullOrWhiteSpace(CommandText))
            {
                throw new InvalidOperationException("CommandText is required.");
            }

            var telemetry = StartTelemetry(connection);
            Exception? failure = null;
            Volatile.Write(ref _executingConnection, connection);
            try
            {
                using var timeoutTimer = _prepareRequested ? null : CreateTimeoutTimer();
                using var preparedTimeout = _prepareRequested
                    ? CreatePreparedCommandTimeout()
                    : default;
                var plan = GetCommandPlan();
                if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
                {
                    if (plan.Parameters.Count != 0 || _prepareRequested)
                    {
                        throw new InvalidOperationException(
                            "Simple execution mode cannot be used with parameters or a prepared command.");
                    }

                    return DecodeScalar<T>(
                        connection,
                        BlueTuskScalarQueryResult.FromQueryResult(
                            await connection.Session.ExecuteSimpleQueryAsync(
                                plan.Sql,
                                cancellationToken).ConfigureAwait(false)));
                }

                if (ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
                    plan.Parameters.Count == 0 &&
                    !_prepareRequested)
                {
                    return DecodeScalar<T>(
                        connection,
                        BlueTuskScalarQueryResult.FromQueryResult(
                            await connection.Session.ExecuteSimpleQueryAsync(
                                plan.Sql,
                                cancellationToken).ConfigureAwait(false)));
                }

                var parameters = EncodeParameters(plan, connection.TypeRegistry);
                var useBinaryResults = connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
                try
                {
                    BlueTuskScalarQueryResult result;
                    if (_prepareRequested)
                    {
                        var prepared = await EnsurePreparedAsync(
                            cancellationToken,
                            plan,
                            parameters).ConfigureAwait(false);
                        result = await connection.Session.ExecutePreparedScalarAsync(
                            prepared.Name,
                            parameters,
                            useBinaryResults,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await connection.Session.ExecuteExtendedScalarAsync(
                            plan.Sql,
                            parameters,
                            useBinaryResults,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return DecodeScalar<T>(connection, result);
                }
                catch (BlueTuskServerException exception) when (
                    useBinaryResults && IsMissingBinaryOutputFunction(exception))
                {
                    BlueTuskScalarQueryResult result;
                    if (_prepareRequested)
                    {
                        var prepared = await EnsurePreparedAsync(
                            cancellationToken,
                            plan,
                            parameters).ConfigureAwait(false);
                        result = await connection.Session.ExecutePreparedScalarAsync(
                            prepared.Name,
                            parameters,
                            useBinaryResults: false,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await connection.Session.ExecuteExtendedScalarAsync(
                            plan.Sql,
                            parameters,
                            useBinaryResults: false,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return DecodeScalar<T>(connection, result);
                }
            }
            catch (BlueTuskServerException exception)
            {
                if (exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
                {
                    var translated = new TimeoutException(
                        $"The command exceeded its {CommandTimeout}-second timeout.",
                        exception);
                    failure = translated;
                    throw translated;
                }

                if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
                {
                    var translated = new OperationCanceledException(
                        "The PostgreSQL command was cancelled.",
                        exception);
                    failure = translated;
                    throw translated;
                }

                var providerException = new BlueTuskException(exception);
                failure = providerException;
                throw providerException;
            }
            catch (OperationCanceledException exception)
            {
                failure = exception;
                throw;
            }
            catch (Exception exception) when (!connection.HasOpenSession)
            {
                failure = exception;
                connection.Close();
                throw;
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                telemetry.Complete(failure);
                if (ownedConnection is not null)
                {
                    await ownedConnection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    private static T? DecodeScalar<T>(
        BlueTuskConnection connection,
        BlueTuskScalarQueryResult result)
    {
        if (result is not { HasValue: true, Field: not null })
        {
            return default;
        }

        var value = BlueTuskValueDecoder.Decode(
            connection.TypeRegistry,
            result.Field,
            result.Value);
        if (value is null or DBNull)
        {
            return default;
        }

        return value is T typed
            ? typed
            : (T)Convert.ChangeType(
                value,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int GetRecordsAffected(BlueTuskQueryResult result)
    {
        var affected = 0;
        var found = false;
        foreach (var resultSet in result.ResultSets)
        {
            if (BlueTuskCommandTagParser.TryGetRecordsAffected(resultSet.CommandTag, out var count))
            {
                affected = checked(affected + count);
                found = true;
            }
        }

        return found ? affected : -1;
    }

    private static bool IsMissingBinaryOutputFunction(BlueTuskServerException exception) =>
        exception.SqlState == "42883" &&
        (exception.Message.StartsWith(
             "no binary output function available for type ",
             StringComparison.Ordinal) ||
         exception.Error.Fields.TryGetValue('R', out var routine) &&
         string.Equals(routine, "getTypeBinaryOutputInfo", StringComparison.Ordinal));

    private async ValueTask<PreparedStatementState> EnsurePreparedAsync(
        CancellationToken cancellationToken,
        BlueTuskCommandPlan? plan = null,
        IReadOnlyList<BlueTuskExtendedQueryParameter>? encodedParameters = null)
    {
        var connection = _connection ??
            throw new InvalidOperationException(
                "Explicit preparation requires a command associated with an open connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Explicit preparation requires an open command connection.");
        }

        connection.ValidateCommandTransaction(_transaction);
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
        {
            throw new InvalidOperationException(
                "A command in simple execution mode cannot be prepared.");
        }

        plan ??= GetCommandPlan();
        encodedParameters ??= EncodeParameters(plan, connection.TypeRegistry);
        var session = connection.Session;
        if (_preparedStatement is { } current &&
            ReferenceEquals(current.Session, session) &&
            string.Equals(current.Sql, plan.Sql, StringComparison.Ordinal) &&
            ParameterTypeOidsMatch(current.ParameterTypeOids, encodedParameters))
        {
            BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "reuse");
            return current;
        }

        var typeOids = encodedParameters.Select(static parameter => parameter.TypeOid).ToArray();

        var statementName = _preparedStatement?.Name ??
            $"bluetusk_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
        if (_preparedStatement is { } existing && ReferenceEquals(existing.Session, session))
        {
            await session.ClosePreparedStatementAsync(
                existing.Name,
                cancellationToken).ConfigureAwait(false);
        }

        await session.PrepareStatementAsync(
            statementName,
            plan.Sql,
            typeOids,
            cancellationToken).ConfigureAwait(false);
        BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "prepare");
        var prepared = new PreparedStatementState(
            statementName,
            plan.Sql,
            typeOids,
            session);
        _preparedStatement = prepared;
        return prepared;
    }

    private PreparedStatementState EnsurePrepared(
        BlueTuskCommandPlan? plan = null,
        IReadOnlyList<BlueTuskExtendedQueryParameter>? encodedParameters = null)
    {
        var connection = _connection ??
            throw new InvalidOperationException(
                "Explicit preparation requires a command associated with an open connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Explicit preparation requires an open command connection.");
        }

        connection.ValidateCommandTransaction(_transaction);
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        if (ExecutionMode == BlueTuskCommandExecutionMode.Simple)
        {
            throw new InvalidOperationException(
                "A command in simple execution mode cannot be prepared.");
        }

        plan ??= GetCommandPlan();
        encodedParameters ??= EncodeParameters(plan, connection.TypeRegistry);
        var session = connection.Session;
        if (_preparedStatement is { } current &&
            ReferenceEquals(current.Session, session) &&
            string.Equals(current.Sql, plan.Sql, StringComparison.Ordinal) &&
            ParameterTypeOidsMatch(current.ParameterTypeOids, encodedParameters))
        {
            BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "reuse");
            return current;
        }

        var typeOids = encodedParameters.Select(static parameter => parameter.TypeOid).ToArray();

        var statementName = _preparedStatement?.Name ??
            $"bluetusk_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
        if (_preparedStatement is { } existing && ReferenceEquals(existing.Session, session))
        {
            session.ClosePreparedStatement(existing.Name);
        }

        session.PrepareStatement(statementName, plan.Sql, typeOids);
        BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "prepare");
        var prepared = new PreparedStatementState(statementName, plan.Sql, typeOids, session);
        _preparedStatement = prepared;
        return prepared;
    }

    private void CancelForTimeout()
    {
        Interlocked.Exchange(ref _timeoutRequested, 1);
        Cancel();
    }

    private BlueTuskCommandPlan GetCommandPlan()
    {
        if (_commandPlan is { } cached &&
            _commandPlanParameterVersion == _parameters.Version &&
            string.Equals(_commandPlanText, CommandText, StringComparison.Ordinal))
        {
            return cached;
        }

        var plan = BlueTuskCommandTextRewriter.Rewrite(CommandText, _parameters);
        _commandPlan = plan;
        _commandPlanText = CommandText;
        _commandPlanParameterVersion = _parameters.Version;
        return plan;
    }

    private BlueTusk.TypeSystem.BlueTuskTypeRegistry GetTypeRegistry() =>
        _connection?.TypeRegistry ??
        _dataSource?.TypeRegistry ??
        throw new InvalidOperationException("The command has no connection or data source.");

    private BlueTuskCommandInstrumentation StartTelemetry(BlueTuskConnection connection)
    {
        var endpoint = connection.DiagnosticEndpoint;
        return BlueTuskDiagnostics.StartCommand(
            CommandText,
            connection.Database,
            endpoint.Host,
            endpoint.Port,
            connection.DiagnosticsOptions);
    }

    private IReadOnlyList<BlueTuskExtendedQueryParameter> EncodeParameters(
        BlueTuskCommandPlan plan,
        BlueTusk.TypeSystem.BlueTuskTypeRegistry types)
    {
        if (!_prepareRequested)
        {
            return BlueTuskParameterEncoder.Encode(plan.Parameters, types);
        }

        var count = plan.Parameters.Count;
        if (_preparedEncodedParameters is null || _preparedEncodedParameters.Length != count)
        {
            _preparedEncodedParameters = new BlueTuskExtendedQueryParameter[count];
            _preparedParameterBuffers = new byte[]?[count];
        }

        BlueTuskParameterEncoder.Encode(
            plan.Parameters,
            types,
            _preparedEncodedParameters,
            _preparedParameterBuffers!);
        return _preparedEncodedParameters;
    }

    private static bool ParameterTypeOidsMatch(
        ReadOnlySpan<uint> expected,
        IReadOnlyList<BlueTuskExtendedQueryParameter> actual)
    {
        if (expected.Length != actual.Count)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index] != actual[index].TypeOid)
            {
                return false;
            }
        }

        return true;
    }

    private readonly struct PreparedCommandTimeoutLease : IDisposable
    {
        private readonly ReusableCommandTimeout? _timeout;

        public PreparedCommandTimeoutLease(ReusableCommandTimeout timeout, TimeSpan dueTime)
        {
            _timeout = timeout;
            timeout.Start(dueTime);
        }

        public void Dispose() => _timeout?.Stop();
    }

    private sealed class ReusableCommandTimeout : IDisposable
    {
        private readonly BlueTuskCommand _command;
        private readonly Timer _timer;
        private long _dueTimestamp;
        private bool _active;
        private bool _disposed;

        public ReusableCommandTimeout(BlueTuskCommand command)
        {
            _command = command;
            _timer = new Timer(
                static state =>
                {
                    var weakTimeout = (WeakReference<ReusableCommandTimeout>)state!;
                    if (weakTimeout.TryGetTarget(out var timeout))
                    {
                        timeout.OnTimeout();
                    }
                },
                new WeakReference<ReusableCommandTimeout>(this),
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
        }

        public void Start(TimeSpan dueTime)
        {
            lock (this)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _dueTimestamp = checked(
                    Stopwatch.GetTimestamp() +
                    (long)(dueTime.TotalSeconds * Stopwatch.Frequency));
                _active = true;
                _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
            }
        }

        public void Stop()
        {
            lock (this)
            {
                if (_disposed)
                {
                    return;
                }

                _active = false;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _active = false;
                _timer.Dispose();
            }
        }

        private void OnTimeout()
        {
            lock (this)
            {
                if (_disposed || !_active)
                {
                    return;
                }

                var remainingTicks = _dueTimestamp - Stopwatch.GetTimestamp();
                if (remainingTicks > 0)
                {
                    _timer.Change(
                        TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency),
                        Timeout.InfiniteTimeSpan);
                    return;
                }

                _active = false;
                _command.CancelForTimeout();
            }
        }
    }

    private sealed record PreparedStatementState(
        string Name,
        string Sql,
        uint[] ParameterTypeOids,
        IBlueTuskPhysicalSession Session);
}
