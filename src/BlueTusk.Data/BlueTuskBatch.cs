using System.Data;
using System.Data.Common;
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
            var commands = BuildExecutions(connection);
            _ = EnsurePrepared(connection, commands);
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
            var commands = BuildExecutions(connection);
            _ = await EnsurePreparedAsync(connection, commands, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<BatchResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The batch is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
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
        var commands = BuildExecutions(connection);
        startTelemetry(connection);
        Volatile.Write(ref _executingConnection, connection);
        using var timeoutTimer = Timeout > 0
            ? new Timer(
                static state => ((BlueTuskBatch)state!).CancelForTimeout(),
                this,
                TimeSpan.FromSeconds(Timeout),
                System.Threading.Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            var useBinaryResults = connection.Session.TransactionStatus ==
                BlueTusk.Protocol.BlueTuskTransactionStatus.Idle;
            BlueTuskQueryResult result;
            try
            {
                result = ExecuteCommands(connection, commands, useBinaryResults);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                result = ExecuteCommands(connection, commands, useBinaryResults: false);
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
        var commands = BuildExecutions(connection);
        startTelemetry(connection);
        using var timeoutSource = Timeout > 0
            ? new CancellationTokenSource(TimeSpan.FromSeconds(Timeout))
            : null;
        using var linkedSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Volatile.Write(ref _executingConnection, connection);
        try
        {
            var effectiveToken = linkedSource?.Token ?? cancellationToken;
            var useBinaryResults = connection.Session.TransactionStatus ==
                BlueTusk.Protocol.BlueTuskTransactionStatus.Idle;
            BlueTuskQueryResult result;
            try
            {
                result = await ExecuteCommandsAsync(
                    connection,
                    commands,
                    useBinaryResults,
                    effectiveToken).ConfigureAwait(false);
            }
            catch (BlueTuskServerException exception) when (
                useBinaryResults && IsMissingBinaryOutputFunction(exception))
            {
                result = await ExecuteCommandsAsync(
                    connection,
                    commands,
                    useBinaryResults: false,
                    effectiveToken).ConfigureAwait(false);
            }

            ApplyRecordsAffected(result);
            return new BatchResult(result, connection.TypeRegistry);
        }
        catch (BlueTuskServerException exception)
        {
            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL batch was cancelled.", exception);
            }

            throw new BlueTuskException(exception);
        }
        catch (OperationCanceledException exception) when (
            timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The batch exceeded its {Timeout}-second timeout.", exception);
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

    private async ValueTask<BlueTuskQueryResult> ExecuteCommandsAsync(
        BlueTuskConnection connection,
        IReadOnlyList<BatchCommandExecution> commands,
        bool useBinaryResults,
        CancellationToken cancellationToken)
    {
        if (_prepareRequested)
        {
            var prepared = await EnsurePreparedAsync(
                connection,
                commands,
                cancellationToken).ConfigureAwait(false);
            var queries = new BlueTuskPreparedBatchQuery[commands.Count];
            for (var index = 0; index < commands.Count; index++)
            {
                queries[index] = new BlueTuskPreparedBatchQuery(
                    prepared.StatementNames[index],
                    commands[index].Parameters,
                    useBinaryResults);
            }

            return await connection.Session.ExecutePreparedBatchAsync(
                queries,
                cancellationToken).ConfigureAwait(false);
        }

        return await connection.Session.ExecuteBatchAsync(
            commands.Select(command => new BlueTuskBatchQuery(
                    command.Sql,
                    command.Parameters,
                    useBinaryResults))
                .ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private BlueTuskQueryResult ExecuteCommands(
        BlueTuskConnection connection,
        IReadOnlyList<BatchCommandExecution> commands,
        bool useBinaryResults)
    {
        if (_prepareRequested)
        {
            var prepared = EnsurePrepared(connection, commands);
            var queries = new BlueTuskPreparedBatchQuery[commands.Count];
            for (var index = 0; index < commands.Count; index++)
            {
                queries[index] = new BlueTuskPreparedBatchQuery(
                    prepared.StatementNames[index],
                    commands[index].Parameters,
                    useBinaryResults);
            }

            return connection.Session.ExecutePreparedBatch(queries);
        }

        return connection.Session.ExecuteBatch(
            commands.Select(command => new BlueTuskBatchQuery(
                    command.Sql,
                    command.Parameters,
                    useBinaryResults))
                .ToArray());
    }

    private BatchCommandExecution[] BuildExecutions(BlueTuskConnection connection)
    {
        if (_commands.Count == 0)
        {
            throw new InvalidOperationException("A batch requires at least one command.");
        }

        var result = new BatchCommandExecution[_commands.Count];
        for (var index = 0; index < _commands.Count; index++)
        {
            var command = _commands.Items[index];
            command.SetRecordsAffected(-1);
            if (string.IsNullOrWhiteSpace(command.CommandText))
            {
                throw new InvalidOperationException($"Batch command {index} requires CommandText.");
            }

            var plan = BlueTuskCommandTextRewriter.Rewrite(
                command.CommandText,
                command.Parameters);
            result[index] = new BatchCommandExecution(
                plan.Sql,
                BlueTuskParameterEncoder.Encode(plan.Parameters, connection.TypeRegistry));
        }

        return result;
    }

    private async ValueTask<PreparedBatchState> EnsurePreparedAsync(
        BlueTuskConnection connection,
        IReadOnlyList<BatchCommandExecution> commands,
        CancellationToken cancellationToken)
    {
        if (_dataSource is not null)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires a batch associated with an open connection.");
        }

        var session = connection.Session;
        var sql = commands.Select(static command => command.Sql).ToArray();
        var typeOids = commands
            .Select(static command =>
                command.Parameters.Select(static parameter => parameter.TypeOid).ToArray())
            .ToArray();
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

        var statementNames = new string[commands.Count];
        var preparedCount = 0;
        try
        {
            for (var index = 0; index < commands.Count; index++)
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
        IReadOnlyList<BatchCommandExecution> commands)
    {
        if (_dataSource is not null)
        {
            throw new InvalidOperationException(
                "Explicit batch preparation requires a batch associated with an open connection.");
        }

        var session = connection.Session;
        var sql = commands.Select(static command => command.Sql).ToArray();
        var typeOids = commands
            .Select(static command =>
                command.Parameters.Select(static parameter => parameter.TypeOid).ToArray())
            .ToArray();
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

        var statementNames = new string[commands.Count];
        var preparedCount = 0;
        try
        {
            for (var index = 0; index < commands.Count; index++)
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

    private static bool IsMissingBinaryOutputFunction(BlueTuskServerException exception) =>
        exception.SqlState == "42883" &&
        (exception.Message.StartsWith(
             "no binary output function available for type ",
             StringComparison.Ordinal) ||
         exception.Error.Fields.TryGetValue('R', out var routine) &&
         string.Equals(routine, "getTypeBinaryOutputInfo", StringComparison.Ordinal));

    private sealed record BatchCommandExecution(
        string Sql,
        IReadOnlyList<BlueTuskExtendedQueryParameter> Parameters);

    private sealed record PreparedBatchState(
        IBlueTuskPhysicalSession Session,
        string[] StatementNames,
        string[] Sql,
        uint[][] ParameterTypeOids);

    private sealed record BatchResult(
        BlueTuskQueryResult Result,
        BlueTusk.TypeSystem.BlueTuskTypeRegistry Types);
}
