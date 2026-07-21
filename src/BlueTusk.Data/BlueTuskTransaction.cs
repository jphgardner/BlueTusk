using System.Data;
using System.Data.Common;
using BlueTusk.Client;
using BlueTusk.Protocol;

namespace BlueTusk.Data;

/// <summary>Represents a PostgreSQL transaction block on one physical connection.</summary>
public sealed class BlueTuskTransaction : DbTransaction
{
    private BlueTuskConnection? _connection;
    private int _completed;

    internal BlueTuskTransaction(BlueTuskConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection? DbConnection => _connection;

    public new BlueTuskConnection? Connection => _connection;

    internal bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public override void Commit() =>
        throw new NotSupportedException("Synchronous transaction completion is not implemented yet. Use CommitAsync.");

    public override Task CommitAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync("COMMIT", cancellationToken);

    public override void Rollback() =>
        throw new NotSupportedException("Synchronous transaction completion is not implemented yet. Use RollbackAsync.");

    public override Task RollbackAsync(CancellationToken cancellationToken = default) =>
        CompleteAsync("ROLLBACK", cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !IsCompleted && _connection is { State: ConnectionState.Open } connection)
        {
            // Closing is a genuine synchronous operation and PostgreSQL rolls the transaction back on disconnect.
            connection.Close();
        }

        MarkCompleted();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!IsCompleted && _connection is { State: ConnectionState.Open })
        {
            await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            MarkCompleted();
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal void ConnectionClosed()
    {
        Interlocked.Exchange(ref _completed, 1);
        _connection = null;
    }

    internal static string GetBeginStatement(IsolationLevel isolationLevel) => isolationLevel switch
    {
        IsolationLevel.Unspecified => "BEGIN",
        IsolationLevel.ReadUncommitted => "BEGIN ISOLATION LEVEL READ UNCOMMITTED",
        IsolationLevel.ReadCommitted => "BEGIN ISOLATION LEVEL READ COMMITTED",
        IsolationLevel.RepeatableRead => "BEGIN ISOLATION LEVEL REPEATABLE READ",
        IsolationLevel.Serializable => "BEGIN ISOLATION LEVEL SERIALIZABLE",
        IsolationLevel.Chaos or IsolationLevel.Snapshot => throw new ArgumentOutOfRangeException(
            nameof(isolationLevel),
            isolationLevel,
            "PostgreSQL does not support this ADO.NET isolation level."),
        _ => throw new ArgumentOutOfRangeException(nameof(isolationLevel), isolationLevel, "Unknown isolation level."),
    };

    private async Task CompleteAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = GetActiveConnection();
        try
        {
            _ = await connection.Session.ExecuteSimpleQueryAsync(sql, cancellationToken).ConfigureAwait(false);
        }
        catch (BlueTuskServerException exception)
        {
            throw new BlueTuskException(exception);
        }
        finally
        {
            if (connection.Session.TransactionStatus == BlueTuskTransactionStatus.Idle)
            {
                MarkCompleted();
            }
        }
    }

    private BlueTuskConnection GetActiveConnection()
    {
        if (IsCompleted || _connection is not { State: ConnectionState.Open } connection)
        {
            throw new InvalidOperationException("The transaction has already completed or its connection is closed.");
        }

        if (!ReferenceEquals(connection.CurrentTransaction, this))
        {
            throw new InvalidOperationException("The transaction is no longer active on its connection.");
        }

        return connection;
    }

    private void MarkCompleted()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            var connection = _connection;
            _connection = null;
            connection?.CompleteTransaction(this);
        }
    }
}
