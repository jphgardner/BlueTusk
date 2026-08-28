using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using BlueTusk.Client;
using BlueTusk.Diagnostics;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

public sealed class BlueTuskCommand : DbCommand
{
    private static long s_preparedStatementSequence;
    private static readonly BlueTuskParameterCollection EmptyParameters = new();
    private BlueTuskParameterCollection? _parameters;
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
    private PreparedCommandState? _preparedState;
    private BlueTuskCommandPlan? _commandPlan;
    private string? _commandPlanText;
    private int _commandPlanParameterVersion = -1;
    private BlueTuskExtendedQueryParameter[]? _encodedParameters;
    private byte[]?[]? _parameterBuffers;
    private EncodedParameterList? _encodedParameterList;
    private string? _multiplexingClassificationText;
    private bool _multiplexingSessionNeutral;
    private BlueTuskCommandInstrumentation _multiplexedTelemetry;

    internal bool ForceBinaryResultsInTransaction { get; init; }

    public BlueTuskCommand()
    {
    }

    public BlueTuskCommand(string commandText, BlueTuskConnection connection)
    {
        CommandText = commandText;
        Connection = connection;
    }

    internal BlueTuskCommand(
        string commandText,
        BlueTuskDataSource dataSource,
        bool? multiplexingSessionNeutral = null)
    {
        CommandText = commandText;
        _dataSource = dataSource;
        if (multiplexingSessionNeutral is { } sessionNeutral)
        {
            _multiplexingClassificationText = commandText;
            _multiplexingSessionNeutral = sessionNeutral;
        }
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

    /// <summary>Gets or sets whether this command may use a multiplexed statement lane.</summary>
    public BlueTuskMultiplexingMode MultiplexingMode { get; set; }

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
                _preparedState?.Timeout?.Dispose();
                _preparedState = null;
            }

            _connection = value;
        }
    }

    protected override DbParameterCollection DbParameterCollection => Parameters;

    public new BlueTuskParameterCollection Parameters => _parameters ??= new BlueTuskParameterCollection();

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
            ValidatePreparationMultiplexing();
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
            ValidatePreparationMultiplexing();
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
            _preparedState?.Timeout?.Dispose();
            ReturnParameterBuffers();
        }

        base.Dispose(disposing);
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ShouldUseStreamingReader(ValidateCommandBehavior(behavior))
            ? ExecuteStreamingDataReaderAfterMultiplexingValidation(behavior)
            : new BlueTuskDataReader(
                ExecuteCore(),
                behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
                GetTypeRegistry(),
                behavior);

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateCommandBehavior(behavior);
        if (ShouldUseStreamingReader(behavior))
        {
            ValidateSequentialMultiplexing();
            return ExecuteStreamingDataReaderAsync(behavior, cancellationToken);
        }

        return ExecuteBufferedDataReaderAsync(behavior, cancellationToken);
    }

    private bool ShouldUseStreamingReader(CommandBehavior behavior)
    {
        if (behavior.HasFlag(CommandBehavior.SequentialAccess))
        {
            return true;
        }

        if (_connection is { BufferDataReaders: true } ||
            ExecutionMode == BlueTuskCommandExecutionMode.Simple ||
            string.IsNullOrWhiteSpace(CommandText) ||
            !BlueTuskCommandTextRewriter.CanUseExtendedProtocol(CommandText))
        {
            return false;
        }

        return ExecutionMode == BlueTuskCommandExecutionMode.Extended ||
            _prepareRequested ||
            Parameters.Count != 0 ||
            _connection is { PreferExtendedQueryProtocol: true };
    }

    private static CommandBehavior ValidateCommandBehavior(CommandBehavior behavior)
    {
        const CommandBehavior supported =
            CommandBehavior.SingleResult |
            CommandBehavior.SingleRow |
            CommandBehavior.SequentialAccess |
            CommandBehavior.CloseConnection;
        const CommandBehavior explicitlyUnsupported =
            CommandBehavior.SchemaOnly |
            CommandBehavior.KeyInfo;
        if ((behavior & explicitlyUnsupported) != 0)
        {
            throw new NotSupportedException(
                "BlueTusk does not support CommandBehavior.SchemaOnly or CommandBehavior.KeyInfo. " +
                "Execute the query normally and use GetColumnSchema for result metadata.");
        }

        if ((behavior & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(behavior),
                behavior,
                "The command behavior contains unknown flags.");
        }

        return behavior;
    }

    private BlueTuskDataReader ExecuteStreamingDataReaderAfterMultiplexingValidation(
        CommandBehavior behavior)
    {
        ValidateSequentialMultiplexing();
        return ExecuteStreamingDataReader(behavior);
    }

    private void ValidateSequentialMultiplexing()
    {
        if (MultiplexingMode != BlueTuskMultiplexingMode.Require)
        {
            return;
        }

        _ = ResolveMultiplexer();
        throw new InvalidOperationException(
            "The command cannot be multiplexed because sequential readers require session affinity.");
    }

    private void ValidatePreparationMultiplexing()
    {
        if (MultiplexingMode != BlueTuskMultiplexingMode.Require)
        {
            return;
        }

        _ = ResolveMultiplexer();
        throw new InvalidOperationException(
            "The command cannot be multiplexed because explicit preparation requires session affinity.");
    }

    private async Task<DbDataReader> ExecuteBufferedDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return new BlueTuskDataReader(
            result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
            GetTypeRegistry(),
            behavior);
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
        BlueTuskCommandTimeout? timeoutTimer = null;
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
            var portal = BeginStreamingPortal(connection, behavior);
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

    private Task<DbDataReader> ExecuteStreamingDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken) =>
        ExecuteStreamingDataReaderValueTaskAsync(behavior, cancellationToken).AsTask();

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<DbDataReader> ExecuteStreamingDataReaderValueTaskAsync(
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
        BlueTuskCommandTimeout? timeoutTimer = null;
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        try
        {
            var connection = _connection;
            if (connection is null)
            {
                if (_dataSource is null)
                {
                    throw new InvalidOperationException("The command has no connection or data source.");
                }

                ownedConnection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                connection = ownedConnection;
            }
            else if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The command connection is not open.");
            }

            connection.ValidateCommandTransaction(_transaction);
            ValidateStreamingCommand();
            telemetry = StartTelemetry(connection);
            Volatile.Write(ref _executingConnection, connection);
            timeoutTimer = CreateTimeoutTimer();
            var plan = GetCommandPlan();
            var parameters = EncodeParameters(plan, connection.TypeRegistry);
            var beginStatement = connection.PrepareCommandTransaction(_transaction);
            var useBinaryResults = ForceBinaryResultsInTransaction ||
                beginStatement is null &&
                connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
            BlueTuskPortal portal;
            try
            {
                if (_prepareRequested)
                {
                    if (connection.HasPendingPoolReset)
                    {
                        await connection.CompletePendingPoolResetAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (beginStatement is not null)
                    {
                        _ = await connection.Session.ExecuteSimpleQueryAsync(
                            beginStatement,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var prepared = await EnsurePreparedAsync(
                        cancellationToken,
                        plan,
                        parameters).ConfigureAwait(false);
                    portal = await connection.Session.BeginPreparedPortalAsync(
                        prepared.Name,
                        parameters,
                        useBinaryResults,
                        SequentialFetchSize,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (connection.HasPendingPoolReset)
                {
                    portal = await connection.BeginResetPortalAsync(
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        SequentialFetchSize,
                        beginStatement,
                        cancellationToken).ConfigureAwait(false);
                }
                else if (beginStatement is not null)
                {
                    portal = await connection.Session.BeginTransactionPortalAsync(
                        beginStatement,
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        SequentialFetchSize,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    portal = await connection.Session.BeginPortalAsync(
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        SequentialFetchSize,
                        cancellationToken).ConfigureAwait(false);
                }
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
                    portal = await connection.Session.BeginPreparedPortalAsync(
                        prepared.Name,
                        parameters,
                        useBinaryResults: false,
                        SequentialFetchSize,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    portal = await connection.Session.BeginPortalAsync(
                        plan.Sql,
                        parameters,
                        useBinaryResults: false,
                        SequentialFetchSize,
                        cancellationToken).ConfigureAwait(false);
                }
            }

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

    private BlueTuskPortal BeginStreamingPortal(
        BlueTuskConnection connection,
        CommandBehavior behavior)
    {
        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        var beginStatement = connection.PrepareCommandTransaction(_transaction);
        var useBinaryResults = ForceBinaryResultsInTransaction ||
            beginStatement is null &&
            connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
        try
        {
            if (_prepareRequested)
            {
                connection.CompletePendingPoolReset();
                if (beginStatement is not null)
                {
                    _ = connection.Session.ExecuteSimpleQuery(beginStatement);
                }

                var prepared = EnsurePrepared(plan, parameters);
                return connection.Session.BeginPreparedPortal(
                    prepared.Name,
                    parameters,
                    useBinaryResults,
                    SequentialFetchSize);
            }

            if (connection.HasPendingPoolReset)
            {
                return connection.BeginResetPortal(
                    plan.Sql,
                    parameters,
                    useBinaryResults,
                    SequentialFetchSize,
                    beginStatement);
            }

            if (beginStatement is not null)
            {
                _ = connection.Session.ExecuteSimpleQuery(beginStatement);
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

    private BlueTuskDataReader CreateStreamingReader(
        BlueTuskPortal portal,
        BlueTuskConnection connection,
        BlueTuskConnection? ownedConnection,
        CommandBehavior behavior,
        BlueTuskCommandTimeout? timeoutTimer)
    {
        var connectionToClose = ownedConnection ??
            (behavior.HasFlag(CommandBehavior.CloseConnection) ? connection : null);
        return new BlueTuskDataReader(
            portal,
            connection,
            connectionToClose,
            connection.TypeRegistry,
            behavior.HasFlag(CommandBehavior.SingleRow),
            behavior.HasFlag(CommandBehavior.SequentialAccess),
            this,
            timeoutTimer,
            connection.ResolveRowFields(portal.Fields));
    }

    private BlueTuskCommandTimeout? CreateTimeoutTimer() => CommandTimeout > 0
        ? BlueTuskCommandTimeout.Rent(this, TimeSpan.FromSeconds(CommandTimeout))
        : null;

    private PreparedCommandTimeoutLease CreatePreparedCommandTimeout() => CommandTimeout > 0
        ? new PreparedCommandTimeoutLease(
            Prepared.Timeout ??= new ReusableCommandTimeout(this),
            TimeSpan.FromSeconds(CommandTimeout))
        : default;

    internal void CompleteStreamingExecution(BlueTuskCommandTimeout? timeoutTimer = null)
    {
        timeoutTimer?.Dispose();
        Volatile.Write(ref _executingConnection, null);
        Volatile.Write(ref _executing, 0);
    }

    internal Exception TranslateReaderServerException(BlueTuskServerException exception)
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
        if (CanUseSimpleNonQueryFastPath())
        {
            return await ExecuteSimpleNonQueryFastAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return GetRecordsAffected(result);
    }

    private bool CanUseSimpleNonQueryFastPath()
    {
        if (_connection is not { State: ConnectionState.Open } connection ||
            _transaction is not null ||
            connection.HasPendingPoolReset ||
            _prepareRequested ||
            Parameters.Count != 0 ||
            string.IsNullOrWhiteSpace(CommandText))
        {
            return false;
        }

        return ExecutionMode == BlueTuskCommandExecutionMode.Simple ||
            ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
            (!connection.PreferExtendedQueryProtocol ||
                !BlueTuskCommandTextRewriter.CanUseExtendedProtocol(CommandText));
    }

    private async Task<int> ExecuteSimpleNonQueryFastAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        var telemetry = default(BlueTuskCommandInstrumentation);
        Exception? failure = null;
        var connection = _connection!;
        try
        {
            connection.ValidateCommandTransaction(_transaction);
            telemetry = StartTelemetry(connection);
            Volatile.Write(ref _executingConnection, connection);
            using var timeoutTimer = CreateTimeoutTimer();
            return await connection.Session.ExecuteSimpleNonQueryAsync(
                CommandText,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BlueTuskServerException exception)
        {
            failure = exception;
            if (exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
            {
                throw new TimeoutException(
                    $"The command exceeded its {CommandTimeout}-second timeout.",
                    exception);
            }

            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException(
                    "The PostgreSQL command was cancelled.",
                    exception);
            }

            throw new BlueTuskException(exception);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!connection.HasOpenSession)
            {
                connection.Close();
            }

            throw;
        }
        finally
        {
            telemetry.Complete(failure);
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
        ShouldUseMultiplexingScalarPath()
            ? ExecuteMultiplexedScalarAsync<object?>(cancellationToken)
            : ExecuteScalarCoreAsync<object?>(cancellationToken);

    public Task<T?> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default) =>
        ShouldUseMultiplexingScalarPath()
            ? ExecuteMultiplexedScalarAsync<T>(cancellationToken)
            : ExecuteScalarCoreAsync<T>(cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskQueryResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        var telemetry = default(BlueTuskCommandInstrumentation);
        var multiplexed = false;
        Exception? failure = null;
        try
        {
            if (ResolveMultiplexer() is { } multiplexer)
            {
                multiplexed = true;
                return await multiplexer.ExecuteAsync(
                    this,
                    cancellationToken).ConfigureAwait(false);
            }

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
            if (multiplexed)
            {
                CompleteMultiplexedTelemetry(failure);
            }
            else
            {
                telemetry.Complete(failure);
            }

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
        var multiplexed = false;
        Exception? failure = null;
        try
        {
            if (ResolveMultiplexer() is { } multiplexer)
            {
                multiplexed = true;
                return multiplexer.ExecuteAsync(
                        this,
                        CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }

            return ExecuteCoreOnce(connection => telemetry = StartTelemetry(connection));
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (multiplexed)
            {
                CompleteMultiplexedTelemetry(failure);
            }
            else
            {
                telemetry.Complete(failure);
            }

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

        var beginStatement = connection.PrepareCommandTransaction(_transaction);

        startTelemetry(connection);

        Volatile.Write(ref _executingConnection, connection);
        using var timeoutTimer = CreateTimeoutTimer();
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

                return connection.HasPendingPoolReset
                    ? connection.ExecuteResetAndSimpleQuery(plan.Sql, beginStatement)
                    : beginStatement is not null
                        ? connection.Session.ExecuteBeginAndSimpleQuery(beginStatement, plan.Sql)
                        : connection.Session.ExecuteSimpleQuery(plan.Sql);
            }

            if (ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
                plan.Parameters.Count == 0 &&
                !_prepareRequested &&
                (!connection.PreferExtendedQueryProtocol ||
                    !BlueTuskCommandTextRewriter.CanUseExtendedProtocol(plan.Sql)))
            {
                return connection.HasPendingPoolReset
                    ? connection.ExecuteResetAndSimpleQuery(plan.Sql, beginStatement)
                    : beginStatement is not null
                        ? connection.Session.ExecuteBeginAndSimpleQuery(beginStatement, plan.Sql)
                        : connection.Session.ExecuteSimpleQuery(plan.Sql);
            }

            var parameters = EncodeParameters(plan, connection.TypeRegistry);
            var useBinaryResults = beginStatement is null &&
                connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;

            try
            {
                if (_prepareRequested)
                {
                    connection.CompletePendingPoolReset();
                    if (beginStatement is not null)
                    {
                        _ = connection.Session.ExecuteSimpleQuery(beginStatement);
                    }

                    var prepared = EnsurePrepared(plan, parameters);
                    return connection.Session.ExecutePreparedStatement(
                        prepared.Name,
                        parameters,
                        useBinaryResults);
                }

                if (connection.HasPendingPoolReset)
                {
                    return connection.ExecuteResetAndExtendedQuery(
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        beginStatement);
                }

                if (beginStatement is not null)
                {
                    _ = connection.Session.ExecuteSimpleQuery(beginStatement);
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

                if (connection.HasPendingPoolReset)
                {
                    return connection.ExecuteResetAndExtendedQuery(
                        plan.Sql,
                        parameters,
                        useBinaryResults: false,
                        beginStatement);
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskQueryResult> ExecuteCoreOnceAsync(
        Action<BlueTuskConnection>? startTelemetry,
        CancellationToken cancellationToken,
        BlueTuskConnection? dispatchedConnection = null,
        bool startMultiplexedTelemetry = false)
    {
        if (_connection is null && _dataSource is null && dispatchedConnection is null)
        {
            throw new InvalidOperationException("The command has no connection or data source.");
        }

        BlueTuskConnection? ownedConnection = null;
        var connection = dispatchedConnection ?? _connection;
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

        var beginStatement = connection.PrepareCommandTransaction(_transaction);

        if (startMultiplexedTelemetry)
        {
            StartMultiplexedTelemetry(connection);
        }
        else
        {
            startTelemetry!(connection);
        }

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

                return connection.HasPendingPoolReset
                    ? await connection.ExecuteResetAndSimpleQueryAsync(
                        plan.Sql,
                        beginStatement,
                        effectiveToken).ConfigureAwait(false)
                    : beginStatement is not null
                    ? await connection.Session.ExecuteBeginAndSimpleQueryAsync(
                        beginStatement,
                        plan.Sql,
                        effectiveToken).ConfigureAwait(false)
                    : await connection.Session.ExecuteSimpleQueryAsync(plan.Sql, effectiveToken)
                        .ConfigureAwait(false);
            }

            if (ExecutionMode == BlueTuskCommandExecutionMode.Auto &&
                plan.Parameters.Count == 0 &&
                !_prepareRequested &&
                (!connection.PreferExtendedQueryProtocol ||
                    !BlueTuskCommandTextRewriter.CanUseExtendedProtocol(plan.Sql)))
            {
                return connection.HasPendingPoolReset
                    ? await connection.ExecuteResetAndSimpleQueryAsync(
                        plan.Sql,
                        beginStatement,
                        effectiveToken).ConfigureAwait(false)
                    : beginStatement is not null
                    ? await connection.Session.ExecuteBeginAndSimpleQueryAsync(
                        beginStatement,
                        plan.Sql,
                        effectiveToken).ConfigureAwait(false)
                    : await connection.Session.ExecuteSimpleQueryAsync(plan.Sql, effectiveToken)
                        .ConfigureAwait(false);
            }

            var parameters = EncodeParameters(plan, connection.TypeRegistry);
            var useBinaryResults = beginStatement is null &&
                connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle;
            try
            {
                if (_prepareRequested)
                {
                    await connection.CompletePendingPoolResetAsync(effectiveToken).ConfigureAwait(false);
                    if (beginStatement is not null)
                    {
                        _ = await connection.Session.ExecuteSimpleQueryAsync(
                            beginStatement,
                            effectiveToken).ConfigureAwait(false);
                    }

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

                if (connection.HasPendingPoolReset)
                {
                    return await connection.ExecuteResetAndExtendedQueryAsync(
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        beginStatement,
                        effectiveToken).ConfigureAwait(false);
                }

                if (beginStatement is not null)
                {
                    return await connection.Session.ExecuteBeginAndExtendedQueryAsync(
                        beginStatement,
                        plan.Sql,
                        parameters,
                        useBinaryResults,
                        effectiveToken).ConfigureAwait(false);
                }

                if (connection.HasPendingPoolReset)
                {
                    return await connection.ExecuteResetAndExtendedQueryAsync(
                        plan.Sql,
                        parameters,
                        useBinaryResults: false,
                        beginStatement,
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

    internal ValueTask<BlueTuskQueryResult> ExecuteDispatchedAsync(
        BlueTuskConnection connection,
        CancellationToken cancellationToken) =>
        ExecuteCoreOnceAsync(
            startTelemetry: null,
            cancellationToken: cancellationToken,
            dispatchedConnection: connection,
            startMultiplexedTelemetry: true);

    internal bool CanUseMultiplexedPipeline =>
        ExecutionMode != BlueTuskCommandExecutionMode.Simple &&
        !_prepareRequested;

    internal BlueTuskMultiplexedPipelineCommand CreateMultiplexedPipelineCommand(
        BlueTuskConnection connection,
        bool scalar)
    {
        connection.ValidateCommandTransaction(_transaction);
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText is required.");
        }

        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        return new BlueTuskMultiplexedPipelineCommand(
            plan.Sql,
            parameters,
            UseBinaryResults: false,
            scalar);
    }

    internal void SetMultiplexedPipelineActiveConnection(
        BlueTuskConnection connection) =>
        Volatile.Write(ref _executingConnection, connection);

    internal void StartMultiplexedTelemetry(BlueTuskConnection connection) =>
        _multiplexedTelemetry = StartTelemetry(connection);

    internal void CompleteMultiplexedPipelineExecution() =>
        Volatile.Write(ref _executingConnection, null);

    internal Exception TranslateMultiplexedPipelineError(
        BlueTuskServerException exception) =>
        TranslateReaderServerException(exception);

    private BlueTuskCommandMultiplexer? ResolveMultiplexer()
    {
        if (MultiplexingMode == BlueTuskMultiplexingMode.Disable)
        {
            return null;
        }

        if (_connection is not null)
        {
            if (MultiplexingMode == BlueTuskMultiplexingMode.Require)
            {
                throw new InvalidOperationException(
                    "The command cannot be multiplexed because commands on an explicit connection require session affinity.");
            }

            return null;
        }

        var multiplexer = _dataSource?.Multiplexer;
        if (multiplexer is null)
        {
            if (MultiplexingMode == BlueTuskMultiplexingMode.Require)
            {
                throw new InvalidOperationException(
                    "The command requires multiplexing, but its data source has not enabled it.");
            }

            return null;
        }

        var reason = _transaction is not null
            ? "commands enlisted in a transaction require session affinity"
            : _prepareRequested
                ? "explicitly prepared commands require session affinity"
                : !IsSessionNeutralCommandText()
                    ? "the SQL can change or depend on physical-session state"
                    : null;
        if (reason is null)
        {
            return multiplexer;
        }

        if (MultiplexingMode == BlueTuskMultiplexingMode.Require)
        {
            throw new InvalidOperationException($"The command cannot be multiplexed because {reason}.");
        }

        return null;
    }

    private bool IsSessionNeutralCommandText()
    {
        var commandText = CommandText;
        if (string.Equals(
                _multiplexingClassificationText,
                commandText,
                StringComparison.Ordinal))
        {
            return _multiplexingSessionNeutral;
        }

        var sessionNeutral = BlueTuskMultiplexingClassifier.IsSessionNeutral(commandText);
        _multiplexingClassificationText = commandText;
        _multiplexingSessionNeutral = sessionNeutral;
        return sessionNeutral;
    }

    private bool ShouldUseMultiplexingScalarPath() =>
        MultiplexingMode == BlueTuskMultiplexingMode.Require ||
        _connection is null &&
        _dataSource is { IsMultiplexingEnabled: true } &&
        MultiplexingMode != BlueTuskMultiplexingMode.Disable;

    private async Task<T?> ExecuteMultiplexedScalarAsync<T>(CancellationToken cancellationToken)
    {
        var multiplexer = ResolveMultiplexer();
        if (multiplexer is null)
        {
            return await ExecuteScalarCoreAsync<T>(cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        Interlocked.Exchange(ref _timeoutRequested, 0);
        Exception? failure = null;
        try
        {
            var result = await multiplexer.ExecuteScalarAsync(
                this,
                cancellationToken).ConfigureAwait(false);
            return DecodeScalar<T>(GetTypeRegistry(), result);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            CompleteMultiplexedTelemetry(failure);
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    private async Task<T?> ExecuteScalarCoreAsync<T>(CancellationToken cancellationToken)
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

            if (string.IsNullOrWhiteSpace(CommandText))
            {
                throw new InvalidOperationException("CommandText is required.");
            }

            var plan = GetCommandPlan();
            var canPrependPoolReset = !_prepareRequested &&
                ExecutionMode != BlueTuskCommandExecutionMode.Simple &&
                (ExecutionMode != BlueTuskCommandExecutionMode.Auto || plan.Parameters.Count != 0);

            BlueTuskConnection? ownedConnection = null;
            var connection = _connection;
            if (connection is null)
            {
                ownedConnection = canPrependPoolReset
                    ? await _dataSource!.OpenCommandConnectionAsync(cancellationToken).ConfigureAwait(false)
                    : await _dataSource!.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                connection = ownedConnection;
            }
            else if (connection.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The command connection is not open.");
            }

            connection.ValidateCommandTransaction(_transaction);
            var beginStatement = connection.PrepareCommandTransaction(_transaction);

            var telemetry = StartTelemetry(connection);
            Exception? failure = null;
            Volatile.Write(ref _executingConnection, connection);
            try
            {
                using var timeoutTimer = _prepareRequested ? null : CreateTimeoutTimer();
                using var preparedTimeout = _prepareRequested
                    ? CreatePreparedCommandTimeout()
                    : default;
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
                            connection.HasPendingPoolReset
                                ? await connection.ExecuteResetAndSimpleQueryAsync(
                                    plan.Sql,
                                    beginStatement,
                                    cancellationToken).ConfigureAwait(false)
                                : beginStatement is not null
                                ? await connection.Session.ExecuteBeginAndSimpleQueryAsync(
                                    beginStatement,
                                    plan.Sql,
                                    cancellationToken).ConfigureAwait(false)
                                : await connection.Session.ExecuteSimpleQueryAsync(
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
                            connection.HasPendingPoolReset
                                ? await connection.ExecuteResetAndSimpleQueryAsync(
                                    plan.Sql,
                                    beginStatement,
                                    cancellationToken).ConfigureAwait(false)
                                : beginStatement is not null
                                ? await connection.Session.ExecuteBeginAndSimpleQueryAsync(
                                    beginStatement,
                                    plan.Sql,
                                    cancellationToken).ConfigureAwait(false)
                                : await connection.Session.ExecuteSimpleQueryAsync(
                                    plan.Sql,
                                    cancellationToken).ConfigureAwait(false)));
                }

                var parameters = EncodeParameters(plan, connection.TypeRegistry);
                var session = connection.Session;
                var useBinaryResults = ForceBinaryResultsInTransaction ||
                    beginStatement is null &&
                    session.TransactionStatus == BlueTuskTransactionStatus.Idle;
                try
                {
                    BlueTuskScalarQueryResult result;
                    if (_prepareRequested)
                    {
                        await connection.CompletePendingPoolResetAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (beginStatement is not null)
                        {
                            _ = await session.ExecuteSimpleQueryAsync(
                                beginStatement,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var prepared = TryGetPreparedStatement(session, plan, parameters) ??
                            await EnsurePreparedAsync(
                                cancellationToken,
                                plan,
                                parameters).ConfigureAwait(false);
                        result = await session.ExecutePreparedScalarAsync(
                            prepared.Name,
                            parameters,
                            useBinaryResults,
                            Prepared.ParameterEncodingUnchanged,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = connection.HasPendingPoolReset
                            ? await connection.ExecuteResetAndExtendedScalarAsync(
                                plan.Sql,
                                parameters,
                                useBinaryResults,
                                beginStatement,
                                cancellationToken).ConfigureAwait(false)
                            : beginStatement is not null
                            ? await session.ExecuteBeginAndExtendedScalarAsync(
                                beginStatement,
                                plan.Sql,
                                parameters,
                                useBinaryResults,
                                cancellationToken).ConfigureAwait(false)
                            : await session.ExecuteExtendedScalarAsync(
                                plan.Sql,
                                parameters,
                                useBinaryResults,
                                cancellationToken).ConfigureAwait(false);
                    }

                    return _prepareRequested
                        ? DecodePreparedScalar<T>(connection, result)
                        : DecodeScalar<T>(connection, result);
                }
                catch (BlueTuskServerException exception) when (
                    useBinaryResults && IsMissingBinaryOutputFunction(exception))
                {
                    BlueTuskScalarQueryResult result;
                    if (_prepareRequested)
                    {
                        var prepared = TryGetPreparedStatement(session, plan, parameters) ??
                            await EnsurePreparedAsync(
                                cancellationToken,
                                plan,
                                parameters).ConfigureAwait(false);
                        result = await session.ExecutePreparedScalarAsync(
                            prepared.Name,
                            parameters,
                            useBinaryResults: false,
                            Prepared.ParameterEncodingUnchanged,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        result = await session.ExecuteExtendedScalarAsync(
                            plan.Sql,
                            parameters,
                            useBinaryResults: false,
                            cancellationToken).ConfigureAwait(false);
                    }

                    return _prepareRequested
                        ? DecodePreparedScalar<T>(connection, result)
                        : DecodeScalar<T>(connection, result);
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
        BlueTuskScalarQueryResult result) =>
        DecodeScalar<T>(connection.TypeRegistry, result);

    private static T? DecodeScalar<T>(
        BlueTusk.TypeSystem.BlueTuskTypeRegistry types,
        BlueTuskScalarQueryResult result)
    {
        if (result is not { HasValue: true, Field: not null })
        {
            return default;
        }

        var resolved = BlueTuskValueDecoder.Resolve(types, result.Field);
        return DecodeScalar<T>(resolved, result);
    }

    private T? DecodePreparedScalar<T>(
        BlueTuskConnection connection,
        BlueTuskScalarQueryResult result)
    {
        if (result is not { HasValue: true, Field: not null })
        {
            return default;
        }

        var types = connection.TypeRegistry;
        var prepared = Prepared;
        if (!ReferenceEquals(prepared.ScalarTypeRegistry, types) ||
            !ReferenceEquals(prepared.ScalarField, result.Field))
        {
            prepared.ScalarTypeRegistry = types;
            prepared.ScalarField = result.Field;
            prepared.ScalarResolvedField = BlueTuskValueDecoder.Resolve(types, result.Field);
        }

        return DecodeScalar<T>(prepared.ScalarResolvedField, result);
    }

    private static T? DecodeScalar<T>(
        in BlueTuskResolvedField resolved,
        BlueTuskScalarQueryResult result)
    {
        if (result.Value is not null &&
            resolved.Type is not null &&
            resolved.Codec is BlueTusk.TypeSystem.IBlueTuskCodec<T>)
        {
            return BlueTuskValueDecoder.DecodeTyped<T>(resolved, result.Value);
        }

        var value = BlueTuskValueDecoder.Decode(resolved, result.Value);
        if (value is null or DBNull)
        {
            return typeof(T) == typeof(object)
                ? (T)(object)DBNull.Value
                : default;
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

    internal bool TryRetryStreamingPortalWithTextResults(
        BlueTuskServerException exception,
        BlueTuskConnection connection,
        out BlueTuskPortal? portal)
    {
        portal = null;
        if (!IsMissingBinaryOutputFunction(exception) ||
            connection.Session.TransactionStatus != BlueTuskTransactionStatus.Idle)
        {
            return false;
        }

        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        if (_prepareRequested)
        {
            var prepared = EnsurePrepared(plan, parameters);
            portal = connection.Session.BeginPreparedPortal(
                prepared.Name,
                parameters,
                useBinaryResults: false,
                SequentialFetchSize);
        }
        else
        {
            portal = connection.Session.BeginPortal(
                plan.Sql,
                parameters,
                useBinaryResults: false,
                SequentialFetchSize);
        }

        return true;
    }

    internal async ValueTask<BlueTuskPortal?> RetryStreamingPortalWithTextResultsAsync(
        BlueTuskServerException exception,
        BlueTuskConnection connection,
        CancellationToken cancellationToken)
    {
        if (!IsMissingBinaryOutputFunction(exception) ||
            connection.Session.TransactionStatus != BlueTuskTransactionStatus.Idle)
        {
            return null;
        }

        var plan = GetCommandPlan();
        var parameters = EncodeParameters(plan, connection.TypeRegistry);
        if (_prepareRequested)
        {
            var prepared = await EnsurePreparedAsync(cancellationToken, plan, parameters)
                .ConfigureAwait(false);
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

    private ValueTask<PreparedStatementState> EnsurePreparedAsync(
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

        var resolvedPlan = plan ?? GetCommandPlan();
        encodedParameters ??= EncodeParameters(resolvedPlan, connection.TypeRegistry);
        var session = connection.Session;
        if (TryGetPreparedStatement(session, resolvedPlan, encodedParameters) is { } current)
        {
            return ValueTask.FromResult(current);
        }

        var typeOids = encodedParameters.Select(static parameter => parameter.TypeOid).ToArray();

        var statementName = Prepared.Statement?.Name ??
            $"bluetusk_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
        var existingStatementName =
            Prepared.Statement is { } existing && ReferenceEquals(existing.Session, session)
                ? existing.Name
                : null;
        return PrepareStatementAsync(
            session,
            statementName,
            resolvedPlan,
            typeOids,
            existingStatementName,
            cancellationToken);
    }

    private async ValueTask<PreparedStatementState> PrepareStatementAsync(
        IBlueTuskPhysicalSession session,
        string statementName,
        BlueTuskCommandPlan plan,
        uint[] typeOids,
        string? existingStatementName,
        CancellationToken cancellationToken)
    {
        if (existingStatementName is not null)
        {
            await session.ClosePreparedStatementAsync(
                existingStatementName,
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
            plan,
            typeOids,
            session);
        Prepared.Statement = prepared;
        return prepared;
    }

    private PreparedStatementState? TryGetPreparedStatement(
        IBlueTuskPhysicalSession session,
        BlueTuskCommandPlan plan,
        IReadOnlyList<BlueTuskExtendedQueryParameter> encodedParameters)
    {
        if (Prepared.Statement is not { } current ||
            !ReferenceEquals(current.Session, session) ||
            !current.Plan.Equals(plan) ||
            (!Prepared.ParameterEncodingUnchanged &&
                !ParameterTypeOidsMatch(current.ParameterTypeOids, encodedParameters)))
        {
            return null;
        }

        BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "reuse");
        return current;
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

        var resolvedPlan = plan ?? GetCommandPlan();
        encodedParameters ??= EncodeParameters(resolvedPlan, connection.TypeRegistry);
        var session = connection.Session;
        if (Prepared.Statement is { } current &&
            ReferenceEquals(current.Session, session) &&
            current.Plan.Equals(resolvedPlan) &&
            (Prepared.ParameterEncodingUnchanged ||
                ParameterTypeOidsMatch(current.ParameterTypeOids, encodedParameters)))
        {
            BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "reuse");
            return current;
        }

        var typeOids = encodedParameters.Select(static parameter => parameter.TypeOid).ToArray();

        var statementName = Prepared.Statement?.Name ??
            $"bluetusk_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
        if (Prepared.Statement is { } existing && ReferenceEquals(existing.Session, session))
        {
            session.ClosePreparedStatement(existing.Name);
        }

        session.PrepareStatement(statementName, resolvedPlan.Sql, typeOids);
        BlueTuskDiagnostics.RecordPreparedStatements(1, "explicit", "prepare");
        var prepared = new PreparedStatementState(statementName, resolvedPlan, typeOids, session);
        Prepared.Statement = prepared;
        return prepared;
    }

    internal void CancelForTimeout()
    {
        Interlocked.Exchange(ref _timeoutRequested, 1);
        Cancel();
    }

    private BlueTuskCommandPlan GetCommandPlan()
    {
        var parameters = _parameters ?? EmptyParameters;
        if (_commandPlan is { } cached &&
            _commandPlanParameterVersion == parameters.Version &&
            string.Equals(_commandPlanText, CommandText, StringComparison.Ordinal))
        {
            return cached;
        }

        var plan = BlueTuskCommandTextRewriter.Rewrite(CommandText, parameters);
        _commandPlan = plan;
        _commandPlanText = CommandText;
        _commandPlanParameterVersion = parameters.Version;
        return plan;
    }

    private BlueTusk.TypeSystem.BlueTuskTypeRegistry GetTypeRegistry() =>
        _connection?.TypeRegistry ??
        _dataSource?.TypeRegistry ??
        throw new InvalidOperationException("The command has no connection or data source.");

    private PreparedCommandState Prepared =>
        _preparedState ??= new PreparedCommandState();

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

    private void CompleteMultiplexedTelemetry(Exception? failure)
    {
        var telemetry = _multiplexedTelemetry;
        _multiplexedTelemetry = default;
        telemetry.Complete(failure);
    }

    private IReadOnlyList<BlueTuskExtendedQueryParameter> EncodeParameters(
        BlueTuskCommandPlan plan,
        BlueTusk.TypeSystem.BlueTuskTypeRegistry types)
    {
        if (_preparedState is not null)
        {
            _preparedState.ParameterEncodingUnchanged = false;
        }

        var count = plan.Parameters.Count;
        if (count == 0)
        {
            if (_prepareRequested)
            {
                Prepared.ParameterEncodingUnchanged = true;
            }

            return Array.Empty<BlueTuskExtendedQueryParameter>();
        }

        if (_encodedParameters is null || _encodedParameters.Length < count)
        {
            ReturnParameterStorage();
            _encodedParameters = ArrayPool<BlueTuskExtendedQueryParameter>.Shared.Rent(count);
            _parameterBuffers = ArrayPool<byte[]?>.Shared.Rent(count);
            Array.Clear(_parameterBuffers);
        }

        var encoded = (_encodedParameterList ??= new EncodedParameterList());
        encoded.Reset(_encodedParameters, count);

        if (!_prepareRequested)
        {
            BlueTuskParameterEncoder.Encode(
                plan.Parameters,
                types,
                _encodedParameters,
                _parameterBuffers!);
            return encoded;
        }

        var prepared = Prepared;
        if (prepared.ParameterSnapshots is null ||
            prepared.ParameterSnapshots.Length != count)
        {
            prepared.ParameterSnapshots = new PreparedParameterEncodingSnapshot[count];
        }

        var unchanged = true;
        for (var index = 0; index < count; index++)
        {
            var parameter = plan.Parameters[index];
            if (prepared.ParameterSnapshots[index].Matches(parameter))
            {
                continue;
            }

            _encodedParameters[index] = BlueTuskParameterEncoder.Encode(
                parameter,
                types,
                ref _parameterBuffers![index],
                rentBuffer: true);
            prepared.ParameterSnapshots[index] = new PreparedParameterEncodingSnapshot(parameter);
            unchanged = false;
        }

        prepared.ParameterEncodingUnchanged = unchanged;
        return encoded;
    }

    private void ReturnParameterBuffers()
    {
        ReturnParameterStorage();
        _encodedParameterList = null;
    }

    private void ReturnParameterStorage()
    {
        if (_parameterBuffers is not null)
        {
            foreach (var buffer in _parameterBuffers)
            {
                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }
            }

            ArrayPool<byte[]?>.Shared.Return(_parameterBuffers, clearArray: true);
            _parameterBuffers = null;
        }

        if (_encodedParameters is not null)
        {
            ArrayPool<BlueTuskExtendedQueryParameter>.Shared.Return(
                _encodedParameters,
                clearArray: true);
            _encodedParameters = null;
        }
    }

    private sealed class EncodedParameterList : IReadOnlyList<BlueTuskExtendedQueryParameter>
    {
        private BlueTuskExtendedQueryParameter[] _items = [];

        public int Count { get; private set; }

        public BlueTuskExtendedQueryParameter this[int index] =>
            (uint)index < (uint)Count
                ? _items[index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        internal void Reset(BlueTuskExtendedQueryParameter[] items, int count)
        {
            _items = items;
            Count = count;
        }

        public IEnumerator<BlueTuskExtendedQueryParameter> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return _items[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
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
        private int _callbackExecuting;
        private bool _active;
        private bool _scheduled;
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
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            Volatile.Write(
                ref _dueTimestamp,
                checked(
                    Stopwatch.GetTimestamp() +
                    (long)(dueTime.TotalSeconds * Stopwatch.Frequency)));
            Volatile.Write(ref _active, true);
            if (!Volatile.Read(ref _scheduled))
            {
                ScheduleActiveTimeout();
            }
        }

        public void Stop()
        {
            Volatile.Write(ref _active, false);
            var spinner = new SpinWait();
            while (Volatile.Read(ref _callbackExecuting) != 0)
            {
                spinner.SpinOnce();
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

                Volatile.Write(ref _disposed, true);
                Volatile.Write(ref _active, false);
                Volatile.Write(ref _scheduled, false);
                _timer.Dispose();
            }
        }

        private void ScheduleActiveTimeout()
        {
            lock (this)
            {
                if (_disposed || !_active || _scheduled)
                {
                    return;
                }

                ScheduleActiveTimeoutUnderLock();
            }
        }

        private void ScheduleActiveTimeoutUnderLock()
        {
            var remainingTicks = Volatile.Read(ref _dueTimestamp) - Stopwatch.GetTimestamp();
            var dueTime = remainingTicks > 0
                ? TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency)
                : TimeSpan.Zero;
            Volatile.Write(ref _scheduled, true);
            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }

        private void OnTimeout()
        {
            Interlocked.Increment(ref _callbackExecuting);
            try
            {
                lock (this)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    if (!Volatile.Read(ref _active))
                    {
                        Volatile.Write(ref _scheduled, false);
                        if (Volatile.Read(ref _active))
                        {
                            ScheduleActiveTimeoutUnderLock();
                        }

                        return;
                    }

                    var remainingTicks =
                        Volatile.Read(ref _dueTimestamp) - Stopwatch.GetTimestamp();
                    if (remainingTicks > 0)
                    {
                        _timer.Change(
                            TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency),
                            Timeout.InfiniteTimeSpan);
                        return;
                    }

                    // Stop can race the callback after the first active check. Its
                    // callback barrier prevents the command from being reused until
                    // this second check and any resulting cancellation are complete.
                    if (!Volatile.Read(ref _active))
                    {
                        Volatile.Write(ref _scheduled, false);
                        return;
                    }

                    Volatile.Write(ref _active, false);
                    Volatile.Write(ref _scheduled, false);
                    _command.CancelForTimeout();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _callbackExecuting);
            }
        }
    }

    private sealed class PreparedCommandState
    {
        internal PreparedStatementState? Statement;

        internal PreparedParameterEncodingSnapshot[]? ParameterSnapshots;

        internal bool ParameterEncodingUnchanged;

        internal BlueTusk.TypeSystem.BlueTuskTypeRegistry? ScalarTypeRegistry;

        internal BlueTuskFieldDescription? ScalarField;

        internal BlueTuskResolvedField ScalarResolvedField;

        internal ReusableCommandTimeout? Timeout;
    }

    private readonly struct PreparedParameterEncodingSnapshot
    {
        private readonly BlueTuskParameter? _parameter;
        private readonly object? _value;
        private readonly DbType _dbType;
        private readonly uint? _postgreSqlTypeOid;
        private readonly string? _postgreSqlTypeName;

        public PreparedParameterEncodingSnapshot(BlueTuskParameter parameter)
        {
            _parameter = parameter;
            _value = parameter.Value;
            _dbType = parameter.DbType;
            _postgreSqlTypeOid = parameter.PostgreSqlTypeOid;
            _postgreSqlTypeName = parameter.PostgreSqlTypeName;
        }

        public bool Matches(BlueTuskParameter parameter)
        {
            var value = parameter.Value;
            return ReferenceEquals(_parameter, parameter) &&
                ReferenceEquals(_value, value) &&
                parameter.PostgreSqlTypeName is null &&
                IsStableBuiltInValue(value) &&
                _dbType == parameter.DbType &&
                _postgreSqlTypeOid == parameter.PostgreSqlTypeOid &&
                string.Equals(
                    _postgreSqlTypeName,
                    parameter.PostgreSqlTypeName,
                    StringComparison.Ordinal);
        }

        private static bool IsStableBuiltInValue(object? value) => value is
            null or DBNull or string or char or bool or
            sbyte or byte or short or ushort or int or uint or long or ulong or
            float or double or decimal or Guid or
            DateOnly or TimeOnly or TimeSpan or DateTime or DateTimeOffset;
    }

    private sealed record PreparedStatementState(
        string Name,
        BlueTuskCommandPlan Plan,
        uint[] ParameterTypeOids,
        IBlueTuskPhysicalSession Session);
}

internal sealed class BlueTuskCommandTimeout : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<BlueTuskCommandTimeout> Pool = [];
    private readonly Timer _timer;
    private BlueTuskCommand? _command;
    private bool _leased;

    private BlueTuskCommandTimeout()
    {
        _timer = new Timer(
            static state => ((BlueTuskCommandTimeout)state!).OnTimeout(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public static BlueTuskCommandTimeout Rent(BlueTuskCommand command, TimeSpan dueTime)
    {
        if (!Pool.TryTake(out var timeout))
        {
            timeout = new BlueTuskCommandTimeout();
        }

        timeout.Start(command, dueTime);
        return timeout;
    }

    public void Dispose()
    {
        lock (this)
        {
            if (!_leased)
            {
                return;
            }

            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _command = null;
            _leased = false;
        }

        Pool.Add(this);
    }

    private void Start(BlueTuskCommand command, TimeSpan dueTime)
    {
        lock (this)
        {
            if (_leased)
            {
                throw new InvalidOperationException("A command timeout registration is already in use.");
            }

            _command = command;
            _leased = true;
            _timer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimeout()
    {
        lock (this)
        {
            if (_leased)
            {
                _command!.CancelForTimeout();
            }
        }
    }
}
