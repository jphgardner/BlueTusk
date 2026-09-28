using System.Buffers.Binary;
using System.Buffers;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTusk.Data;
using BlueTusk.Replication;
using BlueTusk.Streams;
using BlueTusk.TypeSystem;

namespace BlueTusk.Projections;

/// <summary>Actual source/catalogue evidence for different-slot rebuilds on one immutable publication history.</summary>
public sealed class ProjectionSourceLineage
{
    internal ProjectionSourceLineage(ChangeSourceIdentity source, uint timeline, uint databaseOid,
        string publicationFingerprint, string fingerprint, IReadOnlyList<ChangeTable> tables)
    {
        Source = source; Timeline = timeline; DatabaseOid = databaseOid;
        PublicationFingerprint = publicationFingerprint; Fingerprint = fingerprint; Tables = tables;
        CapturedAt = DateTimeOffset.UtcNow;
        CaptureTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    public ChangeSourceIdentity Source { get; }
    public uint Timeline { get; }
    public uint DatabaseOid { get; }
    public string PublicationFingerprint { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<ChangeTable> Tables { get; }
    public DateTimeOffset CapturedAt { get; }
    internal long CaptureTimestamp { get; }
    internal string SnapshotContract => ProjectionLineageTables.Serialize(Tables);
}

public static class PostgreSqlProjectionLineage
{
    /// <summary>Captures fresh lineage and emits a verified transactional source barrier for a different-slot cutover.</summary>
    public static async ValueTask<ProjectionCutoverEvidence> CaptureForCutoverAsync(BlueTuskDataSource dataSource,
        ChangeSourceIdentity source, IReadOnlyList<string> publicationNames, CancellationToken cancellationToken = default)
    {
        var lineage = await CaptureAsync(dataSource, source, publicationNames, cancellationToken).ConfigureAwait(false);
        var barrier = await ProjectionSourceBarrier.EmitVerifiedAsync(dataSource, lineage, cancellationToken).ConfigureAwait(false);
        return new ProjectionCutoverEvidence(lineage, barrier);
    }

    /// <summary>
    /// Captures actual publication membership/flags/column/filter/type identity, database identity and
    /// timeline. Current Streams snapshots require full unfiltered publications with all DML operations.
    /// The deployment must keep source/publication DDL immutable throughout snapshot and retained WAL.
    /// </summary>
    public static async ValueTask<ProjectionSourceLineage> CaptureAsync(BlueTuskDataSource dataSource,
        ChangeSourceIdentity source, IReadOnlyList<string> publicationNames, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(publicationNames);
        if (publicationNames.Count != 1)
        {
            throw new ArgumentException("Different-slot rebuild evidence currently requires exactly one immutable publication.", nameof(publicationNames));
        }
        var publication = publicationNames[0];
        ArgumentException.ThrowIfNullOrWhiteSpace(publication);
        if (publication.Contains('\0') || Encoding.UTF8.GetByteCount(publication) > 63) { throw new ArgumentException("Invalid PostgreSQL publication identifier.", nameof(publicationNames)); }
        BlueTuskReplicationSystemIdentity system;
        await using (var replication = await BlueTuskLogicalReplicationConnection.OpenAsync(dataSource.CreateDedicatedSessionOptions(), cancellationToken).ConfigureAwait(false))
        {
            system = await replication.IdentifySystemAsync(cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(source.SystemIdentifier, system.SystemIdentifier, StringComparison.Ordinal) || !string.Equals(source.DatabaseName, system.DatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The lineage source differs from the connected PostgreSQL system/database.");
        }
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var command = ProjectionSql.Command(connection, transaction, 30, "SET LOCAL search_path=pg_catalog"))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var command = ProjectionSql.Command(connection, transaction, 30,
                         "SELECT s.system_identifier::text, c.timeline_id::bigint, pg_is_in_recovery() FROM pg_control_system() s CROSS JOIN pg_control_checkpoint() c"))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) != system.SystemIdentifier ||
                reader.GetInt64(1) != system.Timeline || reader.GetBoolean(2))
            {
                throw new InvalidOperationException("The SQL/control connection changed system/timeline or is in recovery. Cross-timeline/failover rebuild cutover is not certified.");
            }
        }
        uint databaseOid;
        using var publicationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var command = ProjectionSql.Command(connection, transaction, 30, """
            SELECT d.oid, current_setting('server_version_num')::integer / 10000, p.oid, p.pubname,
                p.puballtables, p.pubinsert, p.pubupdate, p.pubdelete, p.pubtruncate, p.pubviaroot
            FROM pg_database d CROSS JOIN pg_publication p WHERE d.datname=current_database() AND p.pubname=@publication
            """, ("publication", publication)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { throw new InvalidOperationException("The named source publication does not exist."); }
            databaseOid = reader.GetFieldValue<uint>(0);
            if (!reader.GetBoolean(5) || !reader.GetBoolean(6) || !reader.GetBoolean(7) || !reader.GetBoolean(8))
            {
                throw new InvalidOperationException("A rebuild publication must include inserts, updates, deletes and truncates for complete snapshot/WAL coverage.");
            }
            for (var ordinal = 1; ordinal < reader.FieldCount; ordinal++)
            {
                Append(publicationHash, Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)!);
            }
        }
        var tables = new List<ChangeTable>();
        long metadataBytes = 0;
        await using (var command = ProjectionSql.Command(connection, transaction, 30, """
            WITH table_metadata AS (
                SELECT c.oid, t.schemaname, t.tablename, c.relreplident::text, t.rowfilter,
                    (SELECT count(*) FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped) = cardinality(t.attnames) AS all_columns,
                    (SELECT jsonb_agg(jsonb_build_array(a.attname,a.attnum,a.atttypid::bigint,a.atttypmod,a.attgenerated,
                        EXISTS(SELECT 1 FROM pg_index i WHERE i.indrelid=c.oid AND i.indisprimary AND a.attnum=ANY(i.indkey))) ORDER BY a.attnum)::text
                    FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped AND a.attname=ANY(t.attnames)) AS columns
                FROM pg_publication_tables t JOIN pg_namespace n ON n.nspname=t.schemaname JOIN pg_class c ON c.relnamespace=n.oid AND c.relname=t.tablename
                WHERE t.pubname=@publication ORDER BY t.schemaname COLLATE "C", t.tablename COLLATE "C" LIMIT 4097)
            SELECT oid,schemaname,tablename,relreplident,rowfilter,all_columns,
                CASE WHEN octet_length(columns)<=1048576 THEN columns ELSE NULL END,octet_length(columns)
            FROM table_metadata ORDER BY schemaname COLLATE "C",tablename COLLATE "C"
            """, ("publication", publication)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (tables.Count == 4096 || reader.IsDBNull(6) || (metadataBytes += reader.GetInt32(7)) > 4_194_304)
                {
                    throw new ProjectionBoundExceededException("Publication lineage exceeds its 4096-table/4MiB metadata bound.");
                }
                if (!reader.IsDBNull(4) || !reader.GetBoolean(5))
                {
                    throw new InvalidOperationException("The current Streams snapshot seam requires unfiltered publications of every source column. Filtered/partial publication rebuilds fail closed.");
                }
                var oid = reader.GetFieldValue<uint>(0);
                var schema = reader.GetString(1);
                var name = reader.GetString(2);
                var replica = reader.GetString(3)[0];
                var json = reader.GetString(6);
                Append(publicationHash, oid.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Append(publicationHash, schema); Append(publicationHash, name); Append(publicationHash, replica.ToString()); Append(publicationHash, json);
                using var columns = JsonDocument.Parse(json);
                if (columns.RootElement.GetArrayLength() > 4096) { throw new ProjectionBoundExceededException("Publication lineage exceeds its column bound."); }
                var columnList = new List<ChangeColumn>();
                foreach (var column in columns.RootElement.EnumerateArray())
                {
                    columnList.Add(new ChangeColumn(columnList.Count, column[0].GetString()!, column[2].GetUInt32(), column[3].GetInt32(), column[5].GetBoolean()));
                }
                if (!columnList.Any(static column => column.IsKey)) { throw new InvalidOperationException("A rebuild snapshot table requires a stable primary key."); }
                tables.Add(new ChangeTable(oid, schema, name, replica, columnList));
            }
        }
        if (tables.Count == 0) { throw new InvalidOperationException("A rebuild publication cannot be empty."); }
        var publicationFingerprint = Convert.ToHexStringLower(publicationHash.GetHashAndReset());
        using var lineageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(lineageHash, "BlueTusk.ProjectionLineage.v1"); Append(lineageHash, system.SystemIdentifier); Append(lineageHash, system.DatabaseName!);
        Append(lineageHash, databaseOid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(lineageHash, system.Timeline.ToString(System.Globalization.CultureInfo.InvariantCulture)); Append(lineageHash, publicationFingerprint);
        var fingerprint = Convert.ToHexStringLower(lineageHash.GetHashAndReset());
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ProjectionSourceLineage(source, system.Timeline, databaseOid, publicationFingerprint, fingerprint, tables.AsReadOnly());
    }

    private static void Append(IncrementalHash hash, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        hash.AppendData(size); hash.AppendData(bytes);
    }
}

public sealed class ProjectionCutoverEvidence
{
    internal ProjectionCutoverEvidence(ProjectionSourceLineage lineage, BlueTuskLogSequenceNumber barrierPosition)
    {
        Lineage = lineage; BarrierPosition = barrierPosition;
    }
    public ProjectionSourceLineage Lineage { get; }
    public BlueTuskLogSequenceNumber BarrierPosition { get; }
}

internal static class ProjectionLineageTables
{
    internal static string Serialize(IReadOnlyList<ChangeTable> tables)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var table in tables)
        {
            writer.WriteStartObject(); writer.WriteString("schema", table.Schema); writer.WriteString("name", table.Name);
            writer.WriteString("fingerprint", Fingerprint(table)); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static string Fingerprint(ChangeTable table)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray(); writer.WriteNumberValue(table.RelationId); writer.WriteStringValue(table.Schema); writer.WriteStringValue(table.Name);
        writer.WriteStringValue(table.ReplicaIdentity.ToString());
        foreach (var column in table.Columns)
        {
            writer.WriteStartArray(); writer.WriteNumberValue(column.Ordinal); writer.WriteStringValue(column.Name); writer.WriteNumberValue(column.TypeOid);
            writer.WriteNumberValue(column.TypeModifier); writer.WriteBooleanValue(column.IsKey); writer.WriteEndArray();
        }
        writer.WriteEndArray(); writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }
}
