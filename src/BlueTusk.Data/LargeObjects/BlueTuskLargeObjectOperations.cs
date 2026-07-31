namespace BlueTusk.Data.LargeObjects;

internal sealed class BlueTuskLargeObjectOperations : IBlueTuskLargeObjectOperations
{
    private readonly BlueTuskConnection _connection;
    private readonly BlueTuskTransaction _transaction;
    private readonly int _descriptor;
    private readonly bool _ownsTransaction;

    public BlueTuskLargeObjectOperations(
        BlueTuskConnection connection,
        BlueTuskTransaction transaction,
        int descriptor,
        bool ownsTransaction)
    {
        _connection = connection;
        _transaction = transaction;
        _descriptor = descriptor;
        _ownsTransaction = ownsTransaction;
    }

    public byte[] Read(int count) =>
        ExecuteScalar<byte[]>(
            _connection,
            _transaction,
            "SELECT pg_catalog.loread($1, $2)",
            [new BlueTuskParameter<int>(_descriptor), new BlueTuskParameter<int>(count)]);

    public ValueTask<byte[]> ReadAsync(int count, CancellationToken cancellationToken) =>
        ExecuteScalarAsync<byte[]>(
            _connection,
            _transaction,
            "SELECT pg_catalog.loread($1, $2)",
            [new BlueTuskParameter<int>(_descriptor), new BlueTuskParameter<int>(count)],
            cancellationToken);

    public int Write(ReadOnlySpan<byte> buffer) =>
        ExecuteScalar<int>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lowrite($1, $2)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<byte[]>(buffer.ToArray()),
            ]);

    public ValueTask<int> WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken) =>
        ExecuteScalarAsync<int>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lowrite($1, $2)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<byte[]>(buffer.ToArray()),
            ],
            cancellationToken);

    public long Seek(long offset, SeekOrigin origin) =>
        ExecuteScalar<long>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lo_lseek64($1, $2, $3)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<long>(offset),
                new BlueTuskParameter<int>((int)origin),
            ]);

    public ValueTask<long> SeekAsync(
        long offset,
        SeekOrigin origin,
        CancellationToken cancellationToken) =>
        ExecuteScalarAsync<long>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lo_lseek64($1, $2, $3)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<long>(offset),
                new BlueTuskParameter<int>((int)origin),
            ],
            cancellationToken);

    public void SetLength(long value)
    {
        _ = ExecuteScalar<int>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lo_truncate64($1, $2)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<long>(value),
            ]);
    }

    public async ValueTask SetLengthAsync(long value, CancellationToken cancellationToken)
    {
        _ = await ExecuteScalarAsync<int>(
            _connection,
            _transaction,
            "SELECT pg_catalog.lo_truncate64($1, $2)",
            [
                new BlueTuskParameter<int>(_descriptor),
                new BlueTuskParameter<long>(value),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CloseAsync(bool commit, CancellationToken cancellationToken)
    {
        try
        {
            if (commit &&
                !_transaction.IsCompleted &&
                _connection.State == System.Data.ConnectionState.Open)
            {
                _ = await ExecuteScalarAsync<int>(
                    _connection,
                    _transaction,
                    "SELECT pg_catalog.lo_close($1)",
                    [new BlueTuskParameter<int>(_descriptor)],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            commit = false;
            throw;
        }
        finally
        {
            if (_ownsTransaction)
            {
                await _connection.CompleteImplicitLargeObjectTransactionAsync(
                    _transaction,
                    commit,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Close(bool commit)
    {
        try
        {
            if (commit &&
                !_transaction.IsCompleted &&
                _connection.State == System.Data.ConnectionState.Open)
            {
                _ = ExecuteScalar<int>(
                    _connection,
                    _transaction,
                    "SELECT pg_catalog.lo_close($1)",
                    [new BlueTuskParameter<int>(_descriptor)]);
            }
        }
        catch
        {
            commit = false;
            throw;
        }
        finally
        {
            if (_ownsTransaction)
            {
                _connection.CompleteImplicitLargeObjectTransaction(_transaction, commit);
            }
        }
    }

    public void Abandon()
    {
        if (_ownsTransaction)
        {
            _connection.Close();
        }
    }

    internal static async ValueTask<T> ExecuteScalarAsync<T>(
        BlueTuskConnection connection,
        BlueTuskTransaction transaction,
        string sql,
        IReadOnlyList<BlueTuskParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = new BlueTuskCommand(sql, connection)
        {
            Transaction = transaction,
        };
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync<T>(cancellationToken).ConfigureAwait(false);
        return result is null
            ? throw new BlueTuskException("PostgreSQL returned null from a large-object operation.")
            : result;
    }

    internal static T ExecuteScalar<T>(
        BlueTuskConnection connection,
        BlueTuskTransaction transaction,
        string sql,
        IReadOnlyList<BlueTuskParameter> parameters)
    {
        using var command = new BlueTuskCommand(sql, connection)
        {
            Transaction = transaction,
        };
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull
            ? throw new BlueTuskException("PostgreSQL returned null from a large-object operation.")
            : result is T typed
                ? typed
                : (T)Convert.ChangeType(
                    result,
                    typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture);
    }
}
