using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace BlueTusk.Studio;

/// <summary>A nonqueued, cross-replica Studio gate. A null lease means capacity was exhausted; storage errors must throw.</summary>
public interface IStudioDistributedAdmission
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string scopeId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds PostgreSQL transaction-level advisory locks on a borrowed data source while a Studio operation runs.
/// Every replica in one admission domain must use the same namespace and limits.
/// </summary>
public sealed class PostgreSqlStudioAdmission : IStudioDistributedAdmission
{
    private readonly DbDataSource _dataSource;
    private readonly string _namespace;
    private readonly int _globalLimit;
    private readonly int _scopeLimit;
    private readonly int _timeout;

    public PostgreSqlStudioAdmission(DbDataSource dataSource, string admissionNamespace, int maximumConcurrentOperations,
        int maximumConcurrentPerScope, int commandTimeoutSeconds = 10)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (admissionNamespace is not { Length: > 0 and <= 128 } ||
            admissionNamespace.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("Admission namespace requires a stable non-sensitive identifier.", nameof(admissionNamespace));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentOperations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentOperations, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentPerScope, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumConcurrentPerScope, maximumConcurrentOperations);
        ArgumentOutOfRangeException.ThrowIfLessThan(commandTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(commandTimeoutSeconds, 30);
        _dataSource = dataSource;
        _namespace = admissionNamespace;
        _globalLimit = maximumConcurrentOperations;
        _scopeLimit = maximumConcurrentPerScope;
        _timeout = commandTimeoutSeconds;
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        if (scopeId is not { Length: > 0 and <= 128 } || scopeId == "legacy-unknown" ||
            scopeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not (':' or '.' or '-' or '_')))
        {
            throw new ArgumentException("Studio admission requires a stable audit scope identity.", nameof(scopeId));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_timeout));
        DbConnection? connection = null;
        DbTransaction? transaction = null;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(deadline.Token).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(deadline.Token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _timeout;
            command.CommandText = "SELECT pg_catalog.pg_try_advisory_xact_lock(@key)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            command.Parameters.Add(parameter);
            // Take the scope slot first so one scope cannot reserve the global pool
            // while waiting for its own cap. Each probe is immediate and never queues.
            if (!await TrySlotAsync(command, BaseKey("scope", scopeId), _scopeLimit, deadline.Token).ConfigureAwait(false) ||
                !await TrySlotAsync(command, BaseKey("global", string.Empty), _globalLimit, deadline.Token).ConfigureAwait(false))
            {
                return null;
            }
            var lease = new Lease(connection, transaction);
            connection = null;
            transaction = null;
            return lease;
        }
        finally
        {
            // Rolling back releases every transaction-level lock, including a
            // scope slot when the global pool was full or a probe failed.
            try
            {
                if (transaction is not null) { await transaction.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                if (connection is not null) { await connection.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }

    private static async ValueTask<bool> TrySlotAsync(DbCommand command, long baseKey, int limit, CancellationToken cancellationToken)
    {
        var first = Random.Shared.Next(limit);
        for (var offset = 0; offset < limit; offset++)
        {
            command.Parameters[0].Value = baseKey + (first + offset) % limit;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true) { return true; }
        }
        return false;
    }

    private long BaseKey(string kind, string scopeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("BlueTusk.Studio.Admission.v1\n" + _namespace + "\n" + kind + "\n" + scopeId));
        // Reserve six low bits for up to 64 slots. A hash collision can only
        // reject extra work, never let a domain exceed its configured limit.
        return BinaryPrimitives.ReadInt64BigEndian(bytes) & ~63L;
    }

    private sealed class Lease(DbConnection connection, DbTransaction transaction) : IAsyncDisposable
    {
        private DbConnection? _connection = connection;
        private DbTransaction? _transaction = transaction;

        public async ValueTask DisposeAsync()
        {
            var transaction = Interlocked.Exchange(ref _transaction, null);
            var connection = Interlocked.Exchange(ref _connection, null);
            try
            {
                if (transaction is not null) { await transaction.DisposeAsync().ConfigureAwait(false); }
            }
            finally
            {
                if (connection is not null) { await connection.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}
