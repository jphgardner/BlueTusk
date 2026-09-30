using System.Data.Common;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

public static class ProjectionSourceBarrier
{
    /// <summary>
    /// Emits a transactional logical message on the source database. Consumers must enable Streams
    /// logical messages and explicitly recognize this prefix. Its enclosing commit checkpoint covers this LSN.
    /// </summary>
    public static async ValueTask<BlueTuskLogSequenceNumber> EmitAsync(DbDataSource sourceDataSource, CancellationToken cancellationToken = default)
        => await EmitCoreAsync(sourceDataSource, null, cancellationToken).ConfigureAwait(false);

    internal static ValueTask<BlueTuskLogSequenceNumber> EmitVerifiedAsync(DbDataSource sourceDataSource, ProjectionSourceLineage expected,
        CancellationToken cancellationToken) => EmitCoreAsync(sourceDataSource, expected, cancellationToken);

    private static async ValueTask<BlueTuskLogSequenceNumber> EmitCoreAsync(DbDataSource sourceDataSource, ProjectionSourceLineage? expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDataSource);
        await using var connection = await sourceDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (expected is not null)
        {
            await using var identity = ProjectionSql.Command(connection, transaction, 30, """
                SELECT s.system_identifier::text,c.timeline_id::bigint,d.oid,pg_catalog.pg_is_in_recovery()
                FROM pg_catalog.pg_control_system() s CROSS JOIN pg_catalog.pg_control_checkpoint() c
                CROSS JOIN pg_catalog.pg_database d WHERE d.datname=pg_catalog.current_database() AND d.datname=@database
                """, ("database", expected.Source.DatabaseName));
            await using var identityReader = await identity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await identityReader.ReadAsync(cancellationToken).ConfigureAwait(false) || identityReader.GetString(0) != expected.Source.SystemIdentifier ||
                identityReader.GetInt64(1) != expected.Timeline || identityReader.GetFieldValue<uint>(2) != expected.DatabaseOid || identityReader.GetBoolean(3))
            {
                throw new InvalidOperationException("The source changed database/system/timeline before its cutover barrier. Rebuild across failover is not certified.");
            }
        }
        BlueTuskLogSequenceNumber position;
        await using (var command = ProjectionSql.Command(connection, transaction, 30, "SELECT pg_catalog.pg_logical_emit_message(true, 'bluetusk.projections.barrier', '')"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("The source did not return a projection barrier."); }
            position = reader.GetFieldValue<BlueTuskLogSequenceNumber>(0);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return position;
    }
}
