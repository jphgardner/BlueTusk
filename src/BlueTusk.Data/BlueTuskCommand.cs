using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using BlueTusk.Client;

namespace BlueTusk.Data;

public sealed class BlueTuskCommand : DbCommand
{
    private readonly BlueTuskParameterCollection _parameters = new();
    private BlueTuskConnection? _connection;
    private readonly BlueTuskDataSource? _dataSource;
    private BlueTuskTransaction? _transaction;
    private BlueTuskConnection? _executingConnection;
    private int _commandTimeout = 30;
    private int _executing;
    private int _cancellationRequested;

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
        set => _connection = value switch
        {
            null => null,
            BlueTuskConnection connection => connection,
            _ => throw new ArgumentException("A BlueTuskCommand requires a BlueTuskConnection.", nameof(value)),
        };
    }

    public new BlueTuskConnection? Connection
    {
        get => _connection;
        set => _connection = value;
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
        throw new NotSupportedException("Explicit prepared statements are not implemented yet.");

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
            return _parameters.Count == 0
                ? await connection.Session.ExecuteSimpleQueryAsync(CommandText, effectiveToken).ConfigureAwait(false)
                : await connection.Session.ExecuteExtendedQueryAsync(
                    CommandText,
                    BlueTuskParameterEncoder.Encode(_parameters),
                    effectiveToken).ConfigureAwait(false);
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

    private BlueTusk.TypeSystem.BlueTuskTypeRegistry GetTypeRegistry() =>
        _connection?.TypeRegistry ??
        _dataSource?.TypeRegistry ??
        throw new InvalidOperationException("The command has no connection or data source.");
}
