using System.Data.Common;
using System.Net.Sockets;

namespace BlueTusk.Sync.PostgreSql;

/// <summary>
/// Separates PostgreSQL failures that a fresh attempt can succeed after (lost connections,
/// serialization conflicts, deadlocks, shutdowns and resource exhaustion) from permanent
/// failures (constraint, permission, schema and data errors, and Sync durability or identity
/// violations), which must stop the pipeline for an operator.
/// </summary>
internal static class PostgreSqlSyncRetryClassification
{
    // https://www.postgresql.org/docs/current/errcodes-appendix.html
    private static readonly HashSet<string> TransientSqlStates = new(StringComparer.Ordinal)
    {
        "08000", // connection_exception
        "08001", // sqlclient_unable_to_establish_sqlconnection
        "08003", // connection_does_not_exist
        "08004", // sqlserver_rejected_establishment_of_sqlconnection
        "08006", // connection_failure
        "08007", // transaction_resolution_unknown: the retry is idempotent
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "53000", // insufficient_resources
        "53200", // out_of_memory
        "53300", // too_many_connections
        "53400", // configuration_limit_exceeded
        "55006", // object_in_use
        "55P03", // lock_not_available
        "57P01", // admin_shutdown
        "57P02", // crash_shutdown
        "57P03", // cannot_connect_now
        "57P05", // idle_session_timeout
        "58030", // io_error
    };

    internal static bool IsTransient(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException:
                case SyncDestinationDurabilityException:
                case SyncTransformVersionMismatchException:
                case PostgreSqlSyncException:
                    return false;
                case DbException database when database.SqlState is { Length: 5 } sqlState:
                    return TransientSqlStates.Contains(sqlState) || database.IsTransient;
                case DbException { IsTransient: true }:
                case TimeoutException:
                case IOException:
                case SocketException:
                    return true;
            }
        }

        return false;
    }
}
