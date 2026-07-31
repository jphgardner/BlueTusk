using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;
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
    private BlueTuskCommandExecutionMode _executionMode;
    private int _executing;
    private int _cancellationRequested;
    private bool _prepareRequested;
    private PreparedStatementState? _preparedStatement;

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

    public override int ExecuteNonQuery() =>
        throw new NotSupportedException("Synchronous command execution is not implemented yet. Use ExecuteNonQueryAsync.");

    public override object? ExecuteScalar() =>
        throw new NotSupportedException("Synchronous command execution is not implemented yet. Use ExecuteScalarAsync.");

    public override void Prepare() =>
        throw new NotSupportedException(
            "Synchronous preparation is not implemented yet. Use PrepareAsync.");

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

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        throw new NotSupportedException("Synchronous command execution is not implemented yet. Use ExecuteReaderAsync.");

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return new BlueTuskDataReader(
            result,
            behavior.HasFlag(CommandBehavior.CloseConnection) ? _connection : null,
            GetTypeRegistry());
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        return GetRecordsAffected(result);
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        var resultSet = result.FirstOrDefault;
        return resultSet is { Fields.Count: > 0, Rows.Count: > 0 }
            ? BlueTuskValueDecoder.Decode(GetTypeRegistry(), resultSet.Fields[0], resultSet.Rows[0].Values[0])
            : null;
    }

    public async Task<T?> ExecuteScalarAsync<T>(CancellationToken cancellationToken = default)
    {
        var value = await ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return default;
        }

        return value is T typed
            ? typed
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async ValueTask<BlueTuskQueryResult> ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
        {
            throw new InvalidOperationException("The command is already executing.");
        }

        Interlocked.Exchange(ref _cancellationRequested, 0);
        try
        {
            return await ExecuteCoreOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _executingConnection, null);
            Volatile.Write(ref _executing, 0);
        }
    }

    private async ValueTask<BlueTuskQueryResult> ExecuteCoreOnceAsync(CancellationToken cancellationToken)
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

        using var timeoutSource = CommandTimeout > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(CommandTimeout)) : null;
        using var linkedSource = timeoutSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        Volatile.Write(ref _executingConnection, connection);
        try
        {
            var effectiveToken = linkedSource?.Token ?? cancellationToken;
            var plan = BlueTuskCommandTextRewriter.Rewrite(CommandText, _parameters);
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

            var parameters = BlueTuskParameterEncoder.Encode(plan.Parameters, connection.TypeRegistry);
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
            if (exception.SqlState == "57014" && Volatile.Read(ref _cancellationRequested) != 0)
            {
                throw new OperationCanceledException("The PostgreSQL command was cancelled.", exception);
            }

            throw new BlueTuskException(exception);
        }
        catch (OperationCanceledException exception) when (
            timeoutSource?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The command exceeded its {CommandTimeout}-second timeout.", exception);
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

        plan ??= BlueTuskCommandTextRewriter.Rewrite(CommandText, _parameters);
        encodedParameters ??= BlueTuskParameterEncoder.Encode(
            plan.Parameters,
            connection.TypeRegistry);
        var typeOids = encodedParameters.Select(static parameter => parameter.TypeOid).ToArray();
        var session = connection.Session;
        if (_preparedStatement is { } current &&
            ReferenceEquals(current.Session, session) &&
            string.Equals(current.Sql, plan.Sql, StringComparison.Ordinal) &&
            current.ParameterTypeOids.AsSpan().SequenceEqual(typeOids))
        {
            return current;
        }

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
        var prepared = new PreparedStatementState(
            statementName,
            plan.Sql,
            typeOids,
            session);
        _preparedStatement = prepared;
        return prepared;
    }

    private BlueTusk.TypeSystem.BlueTuskTypeRegistry GetTypeRegistry() =>
        _connection?.TypeRegistry ??
        _dataSource?.TypeRegistry ??
        throw new InvalidOperationException("The command has no connection or data source.");

    private sealed record PreparedStatementState(
        string Name,
        string Sql,
        uint[] ParameterTypeOids,
        IBlueTuskPhysicalSession Session);
}
