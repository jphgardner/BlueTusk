using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using BlueTusk.Client;
using BlueTusk.Diagnostics;

namespace BlueTusk.Data;

/// <summary>Executes a group of PostgreSQL statements in one extended-protocol cycle.</summary>
public sealed class BlueTuskBatch : DbBatch
{
    private static long s_preparedStatementSequence;
    private readonly BlueTuskBatchCommandCollection _commands = new();
    private readonly BlueTuskDataSource? _dataSource;
    private BlueTuskConnection? _connection;
    private BlueTuskConnection? _executingConnection;
    private BlueTuskTransaction? _transaction;
    private PreparedBatchState? _preparedBatch;
    private int _timeout = 30;
    private int _executing;
    private int _cancellationRequested;
    private int _timeoutRequested;
    private bool _prepareRequested;

    public BlueTuskBatch()
    {
    }

    public BlueTuskBatch(BlueTuskConnection connection)
    {
        Connection = connection;
    }

    internal BlueTuskBatch(BlueTuskDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    public new BlueTuskBatchCommandCollection BatchCommands => _commands;

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => Connection = value switch
        {
            null => null,
            BlueTuskConnection connection => connection,
            _ => throw new ArgumentException("A BlueTuskBatch requires a BlueTuskConnection.", nameof(value)),
        };
    }

    public new BlueTuskConnection? Connection
    {
        get => _connection;
        set
        {
            if (!ReferenceEquals(_connection, value))
            {
                _preparedBatch = null;
            }

            _connection = value;
        }
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            BlueTuskTransaction transaction => transaction,
            _ => throw new ArgumentException("A BlueTuskBatch requires a BlueTuskTransaction.", nameof(value)),
        };
    }

    public new BlueTuskTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    public override int Timeout
    {
        get => _timeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _timeout = value;
        }
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

    public override int ExecuteNonQuery() => GetRecordsAffected(ExecuteCore().Result);

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        var execution = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return GetRecordsAffected(execution.Result);
    }

    public override object? ExecuteScalar()
    {
        var execution = ExecuteCore();
        var resultSet = execution.Result.FirstOrDefault;
        return resultSet is { Fields.Count: > 0, Rows.Count: > 0 }
            ? BlueTuskValueDecoder.Decode(
                execution.Types,
                resultSet.Fields[0],
                resultSet.Rows[0].Values[0])
            : null;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        var execution = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        var resultSet = execution.Result.FirstOrDefault;
        return resultSet is { Fields.Count: > 0, Rows.Count: > 0 }
            ? BlueTuskValueDecoder.Decode(
                execution.Types,
                resultSet.Fields[0],
                resultSet.Rows[0].Values[0])
            : null;
    }

    public override void Prepare()
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The batch is already executing.");
        }

        try
        {
            _prepareRequested = true;
            var connection = GetOpenConnectionForPreparation();
            using var commands = BuildExecutions(connection);
            _ = EnsurePrepared(connection, commands.Items, commands.Count);
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
            throw new InvalidOperationException("The batch is already executing.");
        }

        try
        {
            _prepareRequested = true;
            var connection = GetOpenConnectionForPreparation();
            using var commands = BuildExecutions(connection);
            _ = await EnsurePreparedAsync(
                connection,
                commands.Items,
                commands.Count,
                cancellationToken).ConfigureAwait(false);
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

    protected override DbBatchCommand CreateDbBatchCommand() => new BlueTuskBatchCommand();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ValidateCommandBehavior(behavior);
        var execution = ExecuteCore();
        return new BlueTuskDataReader(
            execution.Result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
            execution.Types,
            behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        ValidateCommandBehavior(behavior);
        var execution = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return new BlueTuskDataReader(
            execution.Result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
            execution.Types,
            behavior);
    }

    private static void ValidateCommandBehavior(CommandBehavior behavior)
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
                "BlueTusk does not support CommandBehavior.SchemaOnly or CommandBehavior.KeyInfo.");
        }

        if ((behavior & ~supported) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(behavior),
                behavior,
                "The batch behavior contains unknown flags.");
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BatchResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The batch is already executing.");
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

    private BatchResult ExecuteCore()
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The batch is already executing.");
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

    private BatchResult ExecuteCoreOnce(Action<BlueTuskConnection> startTelemetry)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The batch has no connection or data source.");
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
            throw new InvalidOperationException("The batch connection is not open.");
        }

        connection.ValidateCommandTransaction(_transaction);
        using var commands = BuildExecutions(connection);
        connection.CompletePendingPoolReset();
        var beginStatement = connection.PrepareCommandTransaction(_transaction);
        startTelemetry(connection);
        Volatile.Write(ref _executingConnection, connection);
        using var timeoutTimer = Timeout > 0
            ? BatchTimeout.Rent(this, TimeSpan.FromSeconds(Timeout))
            : null;
        try
        {
            if (beginStatement is not null)
            {
                _ = connection.Session.ExecuteSimpleQuery(beginStatement);
            }

            var useBinaryResults = connection.Session.TransactionStatus ==
                BlueTusk.Protocol.BlueTuskTransactionStatus.Idle;
            BlueTuskQueryResult result;
            try
            {
                result = ExecuteCommands(
                    connection,
                    commands.Items,
                    commands.Count,
                    useBinaryResults);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                result = ExecuteCommands(
                    connection,
                    commands.Items,
                    commands.Count,
                    useBinaryResults: false);
            }

            ApplyRecordsAffected(result);
            return new BatchResult(result, connection.TypeRegistry);
        }
        catch (BlueTuskServerException exception) when (
            exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
        {
            throw new TimeoutException($"The batch exceeded its {Timeout}-second timeout.", exception);
        }
        catch (BlueTuskServerException exception)
        {
            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL batch was cancelled.", exception);
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
    private async ValueTask<BatchResult> ExecuteCoreOnceAsync(
        Action<BlueTuskConnection> startTelemetry,
        CancellationToken cancellationToken)
    {
        if (_connection is null && _dataSource is null)
        {
            throw new InvalidOperationException("The batch has no connection or data source.");
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
            throw new InvalidOperationException("The batch connection is not open.");
        }

        connection.ValidateCommandTransaction(_transaction);
        using var commands = BuildExecutions(connection);
        startTelemetry(connection);
        using var timeoutTimer = Timeout > 0
            ? BatchTimeout.Rent(this, TimeSpan.FromSeconds(Timeout))
            : null;
        Volatile.Write(ref _executingConnection, connection);
        try
        {
            var effectiveToken = cancellationToken;
            await connection.CompletePendingPoolResetAsync(effectiveToken).ConfigureAwait(false);
            var beginStatement = connection.PrepareCommandTransaction(_transaction);
            if (beginStatement is not null)
            {
                _ = await connection.Session.ExecuteSimpleQueryAsync(
                    beginStatement,
                    effectiveToken).ConfigureAwait(false);
            }

            var useBinaryResults = connection.Session.TransactionStatus ==
                BlueTusk.Protocol.BlueTuskTransactionStatus.Idle;
            BlueTuskQueryResult result;
            try
            {
                result = await ExecuteCommandsAsync(
                    connection,
                    commands.Items,
                    commands.Count,
                    useBinaryResults,
                    effectiveToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                result = await ExecuteCommandsAsync(
                    connection,
                    commands.Items,
                    commands.Count,
                    useBinaryResults: false,
                    effectiveToken).ConfigureAwait(false);
            }

            ApplyRecordsAffected(result);
            return new BatchResult(result, connection.TypeRegistry);
        }
        catch (BlueTuskServerException exception)
        {
            if (exception.SqlState == "57014" && Volatile.Read(ref _timeoutRequested) != 0)
            {
                throw new TimeoutException($"The batch exceeded its {Timeout}-second timeout.", exception);
            }

            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL batch was cancelled.", exception);
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
            if (ownedConnection is not null)
            {
                await ownedConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<BlueTuskQueryResult> ExecuteCommandsAsync(
        BlueTuskConnection connection,
        BlueTuskBatchCommandExecution[] commands,
        int commandCount,
        bool useBinaryResults,
        CancellationToken cancellationToken)
    {
        if (_prepareRequested)
        {
            var prepared = await EnsurePreparedAsync(
                connection,
                commands,
                commandCount,
                cancellationToken).ConfigureAwait(false);
            var queries = new BlueTuskPreparedBatchQuery[commandCount];
            for (var index = 0; index < commandCount; index++)
            {
                queries[index] = new BlueTuskPreparedBatchQuery(
                    prepared.StatementNames[index],
                    commands[index].MaterializeParameters(),
                    useBinaryResults);
            }

            return await connection.Session.ExecutePreparedBatchAsync(
                queries,
                cancellationToken).ConfigureAwait(false);
        }

        return await connection.Session.ExecuteBatchAsync(
            commands,
            commandCount,
            useBinaryResults,
            cancellationToken).ConfigureAwait(false);
    }

    private BlueTuskQueryResult ExecuteCommands(
        BlueTuskConnection connection,
        BlueTuskBatchCommandExecution[] commands,
        int commandCount,
        bool useBinaryResults)
    {
        if (_prepareRequested)
        {
            var prepared = EnsurePrepared(connection, commands, commandCount);
            var queries = new BlueTuskPreparedBatchQuery[commandCount];
            for (var index = 0; index < commandCount; index++)
            {
                queries[index] = new BlueTuskPreparedBatchQuery(
                    prepared.StatementNames[index],
                    commands[index].MaterializeParameters(),
                    useBinaryResults);
            }

            return connection.Session.ExecutePreparedBatch(queries);
        }

        var batchQueries = new BlueTuskBatchQuery[commandCount];
        for (var index = 0; index < commandCount; index++)
        {
            batchQueries[index] = new BlueTuskBatchQuery(
                commands[index].Sql,
                commands[index].MaterializeParameters(),
                useBinaryResults);
        }

        return connection.Session.ExecuteBatch(batchQueries);
    }

    private PooledBatchCommandExecutions BuildExecutions(BlueTuskConnection connection)
    {
        if (_commands.Count == 0)
        {
            throw new InvalidOperationException("A batch requires at least one command.");
        }

        var count = _commands.Count;
        var result = ArrayPool<BlueTuskBatchCommandExecution>.Shared.Rent(count);
        try
        {
            for (var index = 0; index < count; index++)
            {
                var command = _commands.Items[index];
                command.SetRecordsAffected(-1);
                if (string.IsNullOrWhiteSpace(command.CommandText))
                {
                    throw new InvalidOperationException($"Batch command {index} requires CommandText.");
                }

                var sql = command.CommandText;
                IReadOnlyList<BlueTuskParameter> parameters = command.Parameters.Items;
                if (BlueTuskCommandTextRewriter.MightContainNamedParameters(sql))
                {
                    var plan = BlueTuskCommandTextRewriter.Rewrite(sql, command.Parameters);
                    sql = plan.Sql;
                    parameters = plan.Parameters;
                }

                result[index] = parameters.Count == 1
                    ? new BlueTuskBatchCommandExecution(
                        sql,
                        BlueTuskParameterEncoder.Encode(parameters[0], connection.TypeRegistry))
                    : new BlueTuskBatchCommandExecution(
                        sql,
                        BlueTuskParameterEncoder.Encode(parameters, connection.TypeRegistry));
            }

            return new PooledBatchCommandExecutions(result, count);
        }
        catch
        {
            Array.Clear(result, 0, count);
            ArrayPool<BlueTuskBatchCommandExecution>.Shared.Return(result);
            throw;
        }
    }

    private async ValueTask<PreparedBatchState> EnsurePreparedAsync(
        BlueTuskConnection connection,
        BlueTuskBatchCommandExecution[] commands,
        int commandCount,
        CancellationToken cancellationToken)
    {
        if (_dataSource is not null)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires a batch associated with an open connection.");
        }

        var session = connection.Session;
        var sql = new string[commandCount];
        var typeOids = new uint[commandCount][];
        for (var index = 0; index < commandCount; index++)
        {
            sql[index] = commands[index].Sql;
            typeOids[index] = CreateParameterTypeOids(commands[index]);
        }
        if (_preparedBatch is { } current &&
            ReferenceEquals(current.Session, session) &&
            current.Sql.SequenceEqual(sql, StringComparer.Ordinal) &&
            ParameterTypesEqual(current.ParameterTypeOids, typeOids))
        {
            BlueTuskDiagnostics.RecordPreparedStatements(
                current.StatementNames.Length,
                "batch",
                "reuse");
            return current;
        }

        if (_preparedBatch is { } existing && ReferenceEquals(existing.Session, session))
        {
            foreach (var statementName in existing.StatementNames)
            {
                await session.ClosePreparedStatementAsync(
                    statementName,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var statementNames = new string[commandCount];
        var preparedCount = 0;
        try
        {
            for (var index = 0; index < commandCount; index++)
            {
                var statementName =
                    $"bluetusk_batch_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
                statementNames[index] = statementName;
                await session.PrepareStatementAsync(
                    statementName,
                    sql[index],
                    typeOids[index],
                    cancellationToken).ConfigureAwait(false);
                preparedCount++;
            }
        }
        catch
        {
            for (var index = 0; index < preparedCount; index++)
            {
                try
                {
                    await session.ClosePreparedStatementAsync(
                        statementNames[index],
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // The original preparation failure remains authoritative.
                }
            }

            throw;
        }

        var prepared = new PreparedBatchState(
            session,
            statementNames,
            sql,
            typeOids);
        BlueTuskDiagnostics.RecordPreparedStatements(preparedCount, "batch", "prepare");
        _preparedBatch = prepared;
        return prepared;
    }

    private PreparedBatchState EnsurePrepared(
        BlueTuskConnection connection,
        BlueTuskBatchCommandExecution[] commands,
        int commandCount)
    {
        if (_dataSource is not null)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires a batch associated with an open connection.");
        }

        var session = connection.Session;
        var sql = new string[commandCount];
        var typeOids = new uint[commandCount][];
        for (var index = 0; index < commandCount; index++)
        {
            sql[index] = commands[index].Sql;
            typeOids[index] = CreateParameterTypeOids(commands[index]);
        }
        if (_preparedBatch is { } current &&
            ReferenceEquals(current.Session, session) &&
            current.Sql.SequenceEqual(sql, StringComparer.Ordinal) &&
            ParameterTypesEqual(current.ParameterTypeOids, typeOids))
        {
            BlueTuskDiagnostics.RecordPreparedStatements(
                current.StatementNames.Length,
                "batch",
                "reuse");
            return current;
        }

        if (_preparedBatch is { } existing && ReferenceEquals(existing.Session, session))
        {
            foreach (var statementName in existing.StatementNames)
            {
                session.ClosePreparedStatement(statementName);
            }
        }

        var statementNames = new string[commandCount];
        var preparedCount = 0;
        try
        {
            for (var index = 0; index < commandCount; index++)
            {
                var statementName =
                    $"bluetusk_batch_{Interlocked.Increment(ref s_preparedStatementSequence):x}";
                statementNames[index] = statementName;
                session.PrepareStatement(statementName, sql[index], typeOids[index]);
                preparedCount++;
            }
        }
        catch
        {
            for (var index = 0; index < preparedCount; index++)
            {
                try
                {
                    session.ClosePreparedStatement(statementNames[index]);
                }
                catch
                {
                    // The original preparation failure remains authoritative.
                }
            }

            throw;
        }

        var prepared = new PreparedBatchState(session, statementNames, sql, typeOids);
        BlueTuskDiagnostics.RecordPreparedStatements(preparedCount, "batch", "prepare");
        _preparedBatch = prepared;
        return prepared;
    }

    private void CancelForTimeout()
    {
        Interlocked.Exchange(ref _timeoutRequested, 1);
        Cancel();
    }

    private BlueTuskConnection GetOpenConnectionForPreparation()
    {
        if (_dataSource is not null || _connection is null)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires a batch associated with an open connection.");
        }

        if (_connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires an open batch connection.");
        }

        _connection.ValidateCommandTransaction(_transaction);
        return _connection;
    }

    private static BlueTuskCommandInstrumentation StartTelemetry(BlueTuskConnection connection)
    {
        var endpoint = connection.DiagnosticEndpoint;
        return BlueTuskDiagnostics.StartBatch(
            connection.Database,
            endpoint.Host,
            endpoint.Port,
            connection.DiagnosticsOptions);
    }

    private void ApplyRecordsAffected(BlueTuskQueryResult result)
    {
        if (result.ResultSets.Count != _commands.Count)
        {
            throw new BlueTuskException(
                "PostgreSQL returned a result count that does not match the batch command count.");
        }

        for (var index = 0; index < _commands.Count; index++)
        {
            _commands.Items[index].SetRecordsAffected(
                BlueTuskCommandTagParser.TryGetRecordsAffected(
                    result.ResultSets[index].CommandTag,
                    out var count)
                    ? count
                    : -1);
        }
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

    private static bool ParameterTypesEqual(uint[][] left, uint[][] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var index = 0; index < left.Length; index++)
        {
            if (!left[index].AsSpan().SequenceEqual(right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static uint[] CreateParameterTypeOids(BlueTuskBatchCommandExecution command)
    {
        if (command.ParameterCount == 0)
        {
            return [];
        }

        var result = new uint[command.ParameterCount];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = command.GetParameter(index).TypeOid;
        }

        return result;
    }

    private static bool IsMissingBinaryOutputFunction(BlueTuskServerException exception) =>
        exception.SqlState == "42883" &&
        (exception.Message.StartsWith(
             "no binary output function available for type ",
             StringComparison.Ordinal) ||
         exception.Error.Fields.TryGetValue('R', out var routine) &&
         string.Equals(routine, "getTypeBinaryOutputInfo", StringComparison.Ordinal));

    private sealed record PreparedBatchState(
        IBlueTuskPhysicalSession Session,
        string[] StatementNames,
        string[] Sql,
        uint[][] ParameterTypeOids);

    private sealed record BatchResult(
        BlueTuskQueryResult Result,
        BlueTusk.TypeSystem.BlueTuskTypeRegistry Types);

    private readonly struct PooledBatchCommandExecutions(
        BlueTuskBatchCommandExecution[] items,
        int count) : IDisposable
    {
        internal BlueTuskBatchCommandExecution[] Items { get; } = items;

        internal int Count { get; } = count;

        public void Dispose()
        {
            Array.Clear(Items, 0, Count);
            ArrayPool<BlueTuskBatchCommandExecution>.Shared.Return(Items);
        }
    }

    private sealed class BatchTimeout : IDisposable
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<BatchTimeout> Pool = [];
        private readonly Timer _timer;
        private BlueTuskBatch? _batch;
        private TimeSpan _dueTime;
        private long _startedTimestamp;
        private bool _leased;

        private BatchTimeout()
        {
            _timer = new Timer(
                static state => ((BatchTimeout)state!).OnTimeout(),
                this,
                System.Threading.Timeout.InfiniteTimeSpan,
                System.Threading.Timeout.InfiniteTimeSpan);
        }

        public static BatchTimeout Rent(BlueTuskBatch batch, TimeSpan dueTime)
        {
            if (!Pool.TryTake(out var timeout))
            {
                timeout = new BatchTimeout();
            }

            timeout.Start(batch, dueTime);
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

                _timer.Change(
                    System.Threading.Timeout.InfiniteTimeSpan,
                    System.Threading.Timeout.InfiniteTimeSpan);
                _batch = null;
                _leased = false;
            }

            Pool.Add(this);
        }

        private void Start(BlueTuskBatch batch, TimeSpan dueTime)
        {
            lock (this)
            {
                if (_leased)
                {
                    throw new InvalidOperationException("A batch timeout registration is already in use.");
                }

                _batch = batch;
                _dueTime = dueTime;
                _startedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _leased = true;
                _timer.Change(dueTime, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        private void OnTimeout()
        {
            lock (this)
            {
                if (_leased &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(_startedTimestamp) >= _dueTime)
                {
                    _batch!.CancelForTimeout();
                }
            }
        }
    }
}
